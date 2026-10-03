#!/usr/bin/env python3
"""上游 delta 工具（实现票 17，spec §7 静态层「可选」项）。

定位
----
**opt-in 维护工具，不进硬 CI**。仓库克隆后不存在 Skia 上游目录，因此本工具显式调用时
必须经环境变量 ``EMUERA_SKIA_REF`` 指向 Skia checkout；缺失即 fail-fast。

做什么
------
1. 从上游 Skia checkout 抽取公开键：指令名（``FunctionCode`` 枚举成员）+ 式中函数名
   （``Function/Creator.cs`` 的 ``["NAME"] = new ...`` 字典初始化项）。
2. 从 era-core 基线源码抽取同名两类键，作为 ``base``。
3. 求 delta（Skia-only）并生成**确定**的清单文本（四组已排序 ``name<TAB>kind`` 行，
   UTF-8 / LF，末尾换行）＋ SHA-256。
4. ``--check`` 与冻结产物逐位比对：漂移即输出 diff 并以非零码退出，**禁止静默合并**。

口径来源：01 asset 的 ``_extract.py`` 原型。清单 = Skia 指令 → era-core 指令 →
Skia 函数 → era-core 函数，共四组；hash 不含时间与绝对路径，重复运行逐位相等。

用法
----
    # 冻结/更新（写入 tools/dialect-inventory/skia-delta-inventory.txt + .sha256）
    EMUERA_SKIA_REF=/path/to/skia-checkout python tools/dialect-inventory/extract.py --update

    # 校验漂移（CI 之外手动跑）
    EMUERA_SKIA_REF=/path/to/skia-checkout python tools/dialect-inventory/extract.py --check

    # 只打印 Skia-only delta（供选择 P0 参考）
    EMUERA_SKIA_REF=/path/to/skia-checkout python tools/dialect-inventory/extract.py --print-delta

退出码：0 = 一致/成功；1 = 上游或基线漂移；2 = 前置条件缺失（未设/无效 EMUERA_SKIA_REF）。
"""

from __future__ import annotations

import argparse
import difflib
import hashlib
import io
import os
import re
import sys
from pathlib import Path

DIRECTIVE = "directive"
FUNCTION = "function"

SKIA_REF_ENV = "EMUERA_SKIA_REF"

# 相对各自 Statements 根的固定相对路径（两库同构，见 asset 01 元信息）。
ENUM_REL = "BuiltInFunctionCode.cs"
FUNC_REL = "Function/Creator.cs"

INVENTORY_NAME = "skia-delta-inventory.txt"
HASH_NAME = "skia-delta.sha256"

_enum_head = re.compile(r"^enum\s+FunctionCode")
_enum_member = re.compile(r"^([A-Za-z_][A-Za-z0-9_]*)\s*(=.*)?,?$")
_func_entry = re.compile(r'^\["([A-Za-z0-9_]+)"\]\s*=\s*new\s+([A-Za-z0-9_]+)')


def enum_members(path: Path) -> set[str]:
    """``enum FunctionCode`` 成员名集合（去 ``__NULL__``）。"""
    out: set[str] = set()
    inside = False
    for line in io.open(path, encoding="utf-8-sig").read().splitlines():
        s = line.strip()
        if not inside:
            if _enum_head.match(s):
                inside = True
            continue
        if s.startswith("#"):
            continue
        if s == "}":
            break
        code = s.split("//")[0].strip()
        if not code:
            continue
        m = _enum_member.match(code)
        if m:
            out.add(m.group(1))
    out.discard("__NULL__")
    return out


def expression_functions(path: Path) -> set[str]:
    """``Function/Creator.cs`` 静态字典初始化的键集合。"""
    out: set[str] = set()
    for line in io.open(path, encoding="utf-8-sig").read().splitlines():
        s = line.strip()
        if s.startswith("//"):
            continue
        m = _func_entry.match(s)
        if m:
            out.add(m.group(1))
    return out


def _kind_lines(names: set[str], kind: str) -> list[str]:
    return [f"{name}\t{kind}" for name in sorted(names)]


def build_inventory(
    skia_directives: set[str],
    base_directives: set[str],
    skia_functions: set[str],
    base_functions: set[str],
) -> str:
    """四组已排序行 → 规范清单文本（UTF-8 / LF，末尾换行）。"""
    lines = (
        _kind_lines(skia_directives, DIRECTIVE)
        + _kind_lines(base_directives, DIRECTIVE)
        + _kind_lines(skia_functions, FUNCTION)
        + _kind_lines(base_functions, FUNCTION)
    )
    return "\n".join(lines) + "\n"


def sha256_hex(text: str) -> str:
    return hashlib.sha256(text.encode("utf-8")).hexdigest()


def _extract(statements_root: Path) -> tuple[set[str], set[str]]:
    return (
        enum_members(statements_root / ENUM_REL),
        expression_functions(statements_root / FUNC_REL),
    )


def _require_skia_ref() -> Path:
    raw = os.environ.get(SKIA_REF_ENV)
    if not raw:
        print(
            f"error: 未设置 {SKIA_REF_ENV}（指向 Skia checkout 根目录）。本工具显式调用时必需，缺失即 fail-fast。",
            file=sys.stderr,
        )
        sys.exit(2)
    root = Path(raw)
    statements = root / "Emuera/Runtime/Script/Statements"
    if not statements.is_dir():
        print(
            f"error: {SKIA_REF_ENV}={raw} 下未找到 Emuera/Runtime/Script/Statements。",
            file=sys.stderr,
        )
        sys.exit(2)
    return statements


def _repo_root() -> Path:
    # tools/dialect-inventory/extract.py → repo root
    return Path(__file__).resolve().parents[2]


def _read(path: Path) -> str:
    return path.read_text(encoding="utf-8")


def _write(path: Path, text: str) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    with io.open(path, "w", encoding="utf-8", newline="\n") as handle:
        handle.write(text)


def _print_delta(
    skia_directives: set[str],
    base_directives: set[str],
    skia_functions: set[str],
    base_functions: set[str],
) -> None:
    only_directives = sorted(skia_directives - base_directives)
    only_functions = sorted(skia_functions - base_functions)
    print(f"Skia-only directives ({len(only_directives)}):")
    for name in only_directives:
        print(f"  {name}")
    print(f"Skia-only functions ({len(only_functions)}):")
    for name in only_functions:
        print(f"  {name}")


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(
        description="Skia 上游 delta 工具：抽取公开键、求 delta、冻结/校验稳定 hash。"
    )
    mode = parser.add_mutually_exclusive_group()
    mode.add_argument("--check", action="store_true", help="与冻结产物比对，漂移则非零退出（默认）。")
    mode.add_argument("--update", action="store_true", help="重写冻结清单与 hash。")
    mode.add_argument("--print-delta", action="store_true", help="只打印 Skia-only delta。")
    args = parser.parse_args(argv)

    skia_statements = _require_skia_ref()
    base_statements = _repo_root() / "EraCore.Core/Shared/Runtime/Script/Statements"
    if not base_statements.is_dir():
        print(f"error: 未找到 era-core 基线源码目录：{base_statements}", file=sys.stderr)
        return 2

    skia_directives, skia_functions = _extract(skia_statements)
    base_directives, base_functions = _extract(base_statements)

    if args.print_delta:
        _print_delta(skia_directives, base_directives, skia_functions, base_functions)
        return 0

    inventory = build_inventory(
        skia_directives, base_directives, skia_functions, base_functions
    )
    digest = sha256_hex(inventory)

    out_dir = _repo_root() / "tools/dialect-inventory"
    inventory_path = out_dir / INVENTORY_NAME
    hash_path = out_dir / HASH_NAME

    if args.update:
        _write(inventory_path, inventory)
        _write(hash_path, digest + "\n")
        print(f"updated {inventory_path.relative_to(_repo_root())}")
        print(f"updated {hash_path.relative_to(_repo_root())}")
        print(f"sha256: {digest}")
        return 0

    # --check（默认）
    if not inventory_path.is_file() or not hash_path.is_file():
        print(
            f"error: 缺少冻结产物 {inventory_path} / {hash_path}；先跑 --update 固化。",
            file=sys.stderr,
        )
        return 1

    frozen = _read(inventory_path)
    frozen_digest = sha256_hex(frozen)
    stored_digest = _read(hash_path).strip()
    if inventory == frozen and digest == stored_digest:
        print(f"ok: 上游/基线清单与冻结产物一致（sha256={digest}）。")
        return 0

    print("drift: 抽取结果与冻结产物不一致——禁止静默合并，请评审后 --update 重新固化。", file=sys.stderr)
    print(f"  frozen sha256: {frozen_digest}", file=sys.stderr)
    print(f"  actual sha256: {digest}", file=sys.stderr)
    if stored_digest != frozen_digest:
        print(f"  note: {HASH_NAME} 与清单本身不同步（stored={stored_digest}）。", file=sys.stderr)
    diff = difflib.unified_diff(
        frozen.splitlines(),
        inventory.splitlines(),
        fromfile=f"frozen/{INVENTORY_NAME}",
        tofile=f"actual/{INVENTORY_NAME}",
        lineterm="",
    )
    for line in diff:
        print(line, file=sys.stderr)
    return 1


if __name__ == "__main__":
    sys.exit(main())