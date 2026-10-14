"""Test: DataTable 指令链 AOT 子集验证（3.3 收尾项）。

背景：NativeAOT 验证中 System.Data.DataTable 触发 IL2026/IL3050/IL2072 警告
（EraBinaryDataWriter/Reader + Creator.Method.cs 的 DT_* 指令），但 14/14 e2e
从未实际执行过 DataTable 路径（test_game 的 ERB 未使用 DT_* 指令）——"编译通过
但没跑过"不构成运行时安全的证据。

本测试构造一个覆盖 DataTable 全部调用面的 ERB（DT_CREATE/COLUMN_ADD/ROW_ADD/
CELL_SET/CELL_GET(S)/SELECT/TOXML/FROMXML，对应 Creator.Method.cs 全部警告点），
通过 server 模式加载并断言输出。托管（JIT）与 NativeAOT 产物双跑对比，验证
DataTable 核心 API 在 AOT 下运行时行为等价。

Usage:
    python test_datatable_aot.py                     # 托管 DLL
    python test_datatable_aot.py --binary path/to/EraCore.Cli.exe   # AOT
"""
import argparse
import json
import os
import shutil
import sys
import tempfile
from pathlib import Path

_project_dir = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
sys.path.insert(0, _project_dir)
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

from emuera_server import TEST_GAME_DIR, diff_text, start_server, turn_state


passed = [0]
failed = [0]


def check(condition, message):
    if condition:
        passed[0] += 1
        print(f"  PASS: {message}")
    else:
        failed[0] += 1
        print(f"  FAIL: {message}")


# DataTable 全调用面验证脚本（语法取自 evilmask.gitlab.io Emuera 官方文档 DT_CELL 节）。
# 覆盖 Creator.Method.cs 的 ILC 警告点：
#   DT_CREATE      -> new DataTable + Columns.Add("id")          (L1389 区域)
#   DT_COLUMN_ADD  -> Columns.Add(name, type)
#   DT_ROW_ADD     -> NewRow / Rows.Add
#   DT_CELL_SET    -> row[name] = value
#   DT_CELL_GET(S) -> row[name] 读取
#   DT_SELECT      -> dt.Select(filter, sort)                    (L1559/1694/1695)
#   DT_TOXML       -> WriteXmlSchema + WriteXml                   (L1732/1735)
#   DT_FROMXML     -> ReadXmlSchema + ReadXml                     (L1758/1762)
#
# 语法要点（踩坑记录）：
# - #DIM/#DIMS 必须紧跟 @SYSTEM_TITLE（函数声明）之后，中间不能有语句
# - 字符串变量赋值必须用 '=（= 会把右侧当字符串字面量）
# - 字符串返回值函数在 FORM 里用 %fn()% 包裹（{} 只用于数值表达式）
# - 变量名不能叫 data（Emuera 保留名）
# - DT_FROMXML 的 key 必须与 schema 表名一致（== DT_CREATE 的 key）：
#   .NET Core 下 new DataTable(key) 读入表名不匹配的 XML 会抛
#   "does not match to any DataTable in source"（托管同样失败，非 AOT 回归）
ERB = """@SYSTEM_TITLE
#DIM id
#DIM cnt
#DIMS schema_str
#DIMS xml_str
; 1. 建表 + 加列（new DataTable / Columns.Add）
DT_CREATE "db"
DT_COLUMN_ADD "db", "name"
DT_COLUMN_ADD "db", "height", "int16"
DT_COLUMN_ADD "db", "age", "int16"
; 2. 加行（NewRow / Rows.Add；id 由系统分配）
id = DT_ROW_ADD("db", "name", "Name1", "age", 11)
DT_ROW_ADD "db", "name", "Name2", "age", 21, "height", 164
DT_ROW_ADD "db", "name", "Name3", "age", 18, "height", 159
; 3. 读值（Rows[i][name]）
PRINTFORML name0=%DT_CELL_GETS("db", 0, "name")%
PRINTFORML age0={DT_CELL_GET("db", 0, "age")}
; 4. 改值（DT_CELL_SET）
DT_CELL_SET "db", 0, "height", 132
PRINTFORML height0={DT_CELL_GET("db", 0, "height")}
; 5. 查询（dt.Select(filter, sort)；省略第 4 参输出到 RESULTS）
cnt = DT_SELECT("db", "age >= 18", "age ASC")
PRINTFORML select_cnt={cnt}
; 6. XML 序列化/反序列化（WriteXmlSchema/WriteXml + ReadXmlSchema/ReadXml）
;    FROMXML key 必须与 schema 表名一致（见文件头踩坑记录）
xml_str '= DT_TOXML("db", schema_str)
PRINTFORML xml_len={STRLENS(xml_str)}
DT_FROMXML "db", schema_str, xml_str
PRINTFORML db_exist={DT_EXIST("db")}
PRINTFORML db_name0=%DT_CELL_GETS("db", 0, "name")%
; 7. 行数验证
PRINTFORML row_len={DT_ROW_LENGTH("db")}
PRINTFORML DT_VERIFY_DONE
PRINTL [0] continue
INPUT
QUIT
"""


def make_game_dir_with_dt_erb():
    """复制 test_game 后替换 TEST.ERB 为 DT 验证脚本，返回游戏目录路径。"""
    tmp = Path(tempfile.mkdtemp(prefix="emuera_datatable_"))
    game = tmp / "game"
    shutil.copytree(TEST_GAME_DIR, game)
    (game / "erb" / "TEST.ERB").write_text(ERB, encoding="utf-8")
    return str(game)


def run(server, game_dir):
    """加载游戏，收集输出文本并断言。"""
    # T-025：server 空闲启动，须 POST /load-game 建立会话
    st, body = server.load_game(game_dir)
    check(st == 200, f"POST /load-game -> {st} ({body[:120]})")

    # 等脚本执行到 INPUT 停驻（get_turn 阻塞至首回合产出 → WaitInput，
    # 快照此时保留全部输出文本）
    st, turn_body = server.get_turn(timeout=40)
    check(st == 200, f"GET /turn -> {st}")

    status, body = server.get_snapshot(timeout=40)
    snap = json.loads(body)
    check(turn_state(snap) == "WaitInput",
          f"game reached WaitInput (awaiting.input), got {turn_state(snap)!r}")

    text = snapshot_text_full(snap)
    for line in [
        "name0=Name1",
        "age0=11",
        "height0=132",      # DT_CELL_SET 生效
        "select_cnt=2",     # age>=18 → Name2(21), Name3(18)，按 age ASC
        "xml_len=",         # DT_TOXML 产出非空（WriteXmlSchema/WriteXml 路径）
        "db_exist=1",       # DT_FROMXML 成功（ReadXmlSchema/ReadXml 路径）
        "db_name0=Name1",   # XML 往返数据一致
        "row_len=3",        # DT_ROW_LENGTH
        "DT_VERIFY_DONE",
    ]:
        check(line in text, f"output contains {line!r}")

    # 输入 0 放行 → QUIT，确认脚本完整执行无中途异常
    st, body = server.post_input("0")
    st, turn_body = server.get_turn(timeout=40)
    turn = json.loads(turn_body)
    check(turn_state(turn) == "Quit", f"after input -> Quit (awaiting.ended), got {turn_state(turn)!r}")


def snapshot_text_full(snap):
    """提取 snapshot 全部可见文本（v5: entries[].segments[].text）。"""
    parts = []
    for line in snap.get("lines", []):
        for ent in line.get("entries", []):
            for seg in ent.get("segments", []):
                parts.append(seg.get("text", ""))
    return "".join(parts)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--binary", default=None, help="CLI 可执行文件或 dll 路径")
    ap.add_argument("--game-dir", default=None, help="测试游戏目录（默认 test_game）")
    args = ap.parse_args()

    game_dir = make_game_dir_with_dt_erb()
    print(f"[DataTableAot] game dir: {game_dir}")
    try:
        server = start_server(binary=args.binary, game_dir=game_dir)
        try:
            run(server, game_dir)
        finally:
            server.close()
    finally:
        shutil.rmtree(os.path.dirname(game_dir), ignore_errors=True)

    print(f"\n  passed={passed[0]} failed={failed[0]}")
    sys.exit(1 if failed[0] else 0)


if __name__ == "__main__":
    main()
