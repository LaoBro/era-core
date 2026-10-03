"""DoD 单入口：一致性套件（实现票 25）。

build（可选）→ [面快照] → 以目标 profile 加载 → 断言零识别错误/零未定义函数/无未预期 Lv2 → 到输入循环。

    # DoD（eraTW 画蛇添足版，自带 build 与面快照）
    python tests/test_conformance_kit.py --game-dir D:\\era\\eratw-sub-modding-develop --build

    # 仓库内自检（test_game → v24pure，run_all 用）
    python tests/test_conformance_kit.py --game-dir test_game --no-surface

profile 解析：`--profile` 优先；否则按 `tests/games.json` 的 `matchHints` 显式映射（不建 resolver）。
"""
import argparse
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))

import conformance_kit as kit

passed = [0]
failed = [0]


def check(ok, message):
    if ok:
        passed[0] += 1
        print(f"  PASS: {message}")
    else:
        failed[0] += 1
        print(f"  FAIL: {message}")


def main():
    parser = argparse.ArgumentParser(description="一致性套件 + DoD 单入口（实现票 25）")
    parser.add_argument("--game-dir", default="test_game", help="游戏目录")
    parser.add_argument("--profile", help="显式 profile id（v24pure/snake/eratw）；缺省按 games.json 解析")
    parser.add_argument("--binary", help="EraCore.Cli 二进制路径（缺省自动查找）")
    parser.add_argument("--games-json", help="fixture 映射文件（缺省 tests/games.json）")
    parser.add_argument("--build", action="store_true", help="先 dotnet build EraCore.Cli")
    parser.add_argument("--no-surface", dest="surface", action="store_false", default=True,
                        help="跳出面快照门禁（run_all 已跑 dotnet test 时可关）")
    parser.add_argument("--no-playtest", dest="playtest", action="store_false", default=True,
                        help="只做解析 smoke，不做 playtest 状态断言")
    args = parser.parse_args()

    game_dir = Path(args.game_dir)
    if not game_dir.is_dir():
        print(f"  FAIL: 游戏目录不存在: {game_dir}")
        return 1

    if args.build:
        print("\n=== build ===")
        ok, note = kit.run_build()
        check(ok, f"dotnet build EraCore.Cli（{note}）")
        if not ok:
            print(f"\n=== conformance kit: {passed[0]} passed, {failed[0]} failed ===")
            return 1

    if args.surface:
        print("\n=== 面快照（票 17 DialectSurfaceSnapshotTests）===")
        ok, note = kit.run_surface_snapshot()
        if ok is None:
            print(f"  WARN: {note}")
        else:
            check(ok, f"面快照断言（{note}）")

    # ---- game→profile 显式映射 + 锚点冒烟 ----
    config = kit.load_config(args.games_json)
    profile, fixture, notes = kit.resolve_profile(config, game_dir, args.profile)
    fixture_id = fixture.get("id") if fixture else "(未映射)"
    print(f"\n=== game→profile: {fixture_id} → {profile} ===")
    for note in notes:
        print(f"  WARN: {note}")

    check(profile is not None, f"解析得到 profile（{profile}）")
    if profile is None:
        print(f"\n=== conformance kit: {passed[0]} passed, {failed[0]} failed ===")
        return 1

    if fixture is not None:
        anchor_problems = kit.check_anchors(game_dir, fixture)
        check(not anchor_problems,
              "内容锚点冒烟通过（防 profile 误配）" if not anchor_problems
              else "内容锚点冒烟: " + "; ".join(anchor_problems))

    # ---- 解析 smoke + playtest 冒烟 ----
    print(f"\n=== 解析 smoke + playtest（profile={profile}）===")
    smoke = kit.run_game_smoke(game_dir, profile, args.binary)
    check(smoke["load_status"] in (200, 409), f"POST /load-game 返回 {smoke['load_status']}")

    lines = smoke["lines"]
    known_lv2 = fixture.get("knownLv2", []) if fixture else []
    unrecognized, undefined_func, unexpected_lv2, known_hit = kit.analyze_warnings(lines, known_lv2)

    check(not unrecognized, f"零「无法解析的标识符/行」（命中 {len(unrecognized)}）")
    for line in unrecognized[:5]:
        print(f"    unrecognized: {line}")
    check(not undefined_func, f"零未定义函数警告（命中 {len(undefined_func)}）")
    for line in undefined_func[:5]:
        print(f"    undefined-func: {line}")
    check(not unexpected_lv2, f"无未预期 warnLevel≥2（命中 {len(unexpected_lv2)}）")
    for line in unexpected_lv2[:5]:
        print(f"    lv2: {line}")
    if known_hit:
        print(f"  WARN: 已知游戏数据 Lv2（fixture knownLv2，不计入 DoD）: {len(known_hit)}")
        for line in known_hit[:5]:
            print(f"    known-lv2: {line}")

    if args.playtest:
        expect_state = (fixture.get("expectState") if fixture else None) or "WaitInput"
        check(smoke["state"] == expect_state,
              f"进入输入循环（state={smoke['state']}，期望 {expect_state}）")

    print(f"\n=== conformance kit: {passed[0]} passed, {failed[0]} failed ===")
    return 0 if failed[0] == 0 else 1


if __name__ == "__main__":
    sys.exit(main())
