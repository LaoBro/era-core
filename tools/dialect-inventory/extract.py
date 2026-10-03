#!/usr/bin/env python3
"""fork 参数化上游 delta 工具（实现票 17 → 26，spec §6 / §7 静态层「可选」项）。

定位
----
**opt-in 维护工具，不进硬 CI**。仓库克隆后不存在各 fork 的上游目录，因此显式调用时
必须能解析出 fork 的 ``sourceRoot``；解析失败即 fail-fast。

设计（决策票 12/13）
-------------------
- 每个 fork 一份 ``dialects/<forkId>.json`` 描述符：``{id, displayName, sourceRoot,
  base, registryFiles{instructions, methods, argBuilders}, additions[], exclusions[],
  ownerNotes}``。``sourceRoot`` 支持 ``$ENV`` 展开（如 ``$EMUERA_SKIA_REF``）。
- ``base`` 统一为仓库内 **era-core-baseline 快照**（``dialects/era-core-baseline.json``
  描述布局，冻结产物 ``tools/dialect-inventory/base.era-core-baseline.txt``）。
  因共享祖先，所有 fork 复用同一 base。
- 工具**零 fork 专属逻辑**：路径、布局、增量全部来自描述符。新增 fork = 一份描述符 +
  跑一次 ``--update``。

做什么
------
1. 读描述符 → 解析 ``sourceRoot`` → 按 ``registryFiles`` 抽取上游公开键
   （指令名 = ``FunctionCode`` 枚举成员；式中函数名 = ``Function/Creator.cs`` 的
   ``["NAME"] = new ...`` 字典初始化项）。
2. 读 base 快照 → 求 delta（上游独有键）+ 确定清单（四组已排序 ``name<TAB>kind`` 行，
   UTF-8 / LF，末尾换行）＋ SHA-256。
3. ``--check`` 逐位比对冻结产物与描述符 ``additions``：漂移即出 diff 并以非零码退出，
   **禁止静默合并**。``--update`` 重写清单 / hash / 描述符 ``additions``。

口径来源：01 asset 的 ``_extract.py`` 原型。清单 = fork 指令 → base 指令 → fork 函数 →
base 函数；hash 不含时间与绝对路径，重复运行逐位相等。

用法
----
    export EMUERA_SKIA_REF=/path/to/skia-checkout

    python tools/dialect-inventory/extract.py --fork skia --check      # 校验漂移
    python tools/dialect-inventory/extract.py --fork skia --update     # 重写冻结产物
    python tools/dialect-inventory/extract.py --fork skia --print-delta
    python tools/dialect-inventory/extract.py --update-base            # 重写 base 快照
    python tools/dialect-inventory/extract.py --list-forks

退出码：0 = 一致/成功；1 = 上游或基线漂移；2 = 前置条件缺失（描述符 / sourceRoot / base 缺）。
"""

from __future__ import annotations

import argparse
import dataclasses
import difflib
import hashlib
import io
import json
import os
import re
import sys
from pathlib import Path

DIRECTIVE = "directive"
FUNCTION = "function"

BASE_ID = "era-core-baseline"

_ENV_TOKEN = re.compile(r"\$\{?([A-Za-z_][A-Za-z0-9_]*)\}?")
_enum_head = re.compile(r"^enum\s+FunctionCode")
_enum_member = re.compile(r"^([A-Za-z_][A-Za-z0-9_]*)\s*(=.*)?,?$")
_func_entry = re.compile(r'^\["([A-Za-z0-9_]+)"\]\s*=\s*new\s+([A-Za-z0-9_]+)')


# --------------------------------------------------------------------------- #
# 布局
# --------------------------------------------------------------------------- #
@dataclasses.dataclass(frozen=True)
class Layout:
    """工具涉及的全部路径（可注入，便于自测指向临时目录）。"""

    repo_root: Path
    dialects_dir: Path
    inventory_dir: Path

    @classmethod
    def default(cls) -> "Layout":
        repo = Path(__file__).resolve().parents[2]
        return cls(repo, repo / "dialects", repo / "tools" / "dialect-inventory")

    def descriptor(self, fork_id: str) -> Path:
        return self.dialects_dir / f"{fork_id}.json"

    def base_snapshot(self, base_id: str = BASE_ID) -> Path:
        return self.inventory_dir / f"base.{base_id}.txt"

    def inventory(self, fork_id: str) -> Path:
        return self.inventory_dir / f"{fork_id}-delta-inventory.txt"

    def digest(self, fork_id: str) -> Path:
        return self.inventory_dir / f"{fork_id}-delta.sha256"


class ToolError(Exception):
    """前置条件缺失（→ 退出码 2），与漂移（→ 1）区分。"""


# --------------------------------------------------------------------------- #
# 抽取
# --------------------------------------------------------------------------- #
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


def _kind_lines(names, kind: str) -> list[str]:
    return [f"{name}\t{kind}" for name in sorted(names)]


def build_inventory(fork_directives, base_directives, fork_functions, base_functions) -> str:
    """四组已排序行 → 规范清单文本（UTF-8 / LF，末尾换行）。"""
    lines = (
        _kind_lines(fork_directives, DIRECTIVE)
        + _kind_lines(base_directives, DIRECTIVE)
        + _kind_lines(fork_functions, FUNCTION)
        + _kind_lines(base_functions, FUNCTION)
    )
    return "\n".join(lines) + "\n"


def sha256_hex(text: str) -> str:
    return hashlib.sha256(text.encode("utf-8")).hexdigest()


def keys_of(fork_directives, base_directives, fork_functions, base_functions):
    """delta：(name, kind) 的上游独有集合 + 排序后的两族名单。"""
    only_directives = sorted(fork_directives - base_directives)
    only_functions = sorted(fork_functions - base_functions)
    delta = {(n, DIRECTIVE) for n in only_directives} | {(n, FUNCTION) for n in only_functions}
    return delta, only_directives, only_functions


# --------------------------------------------------------------------------- #
# 描述符 / 路径解析
# --------------------------------------------------------------------------- #
def _read(path: Path) -> str:
    return path.read_text(encoding="utf-8")


def _write(path: Path, text: str) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    with io.open(path, "w", encoding="utf-8", newline="\n") as handle:
        handle.write(text)


def load_descriptor(layout: Layout, fork_id: str) -> dict:
    path = layout.descriptor(fork_id)
    if not path.is_file():
        raise ToolError(f"缺少描述符 {path}（新增 fork = 一份 dialects/<forkId>.json）。")
    try:
        return json.loads(_read(path))
    except json.JSONDecodeError as exc:
        raise ToolError(f"描述符 {path} 不是合法 JSON：{exc}") from exc


def resolve_source_root(layout: Layout, descriptor: dict, env: dict) -> Path:
    raw = descriptor.get("sourceRoot")
    if not raw or not isinstance(raw, str):
        raise ToolError(f"描述符「{descriptor.get('id')}」缺 sourceRoot。")
    expanded = _ENV_TOKEN.sub(lambda m: env.get(m.group(1), ""), raw).strip()
    if not expanded:
        raise ToolError(
            f"sourceRoot「{raw}」未解析到路径（检查其引用的环境变量是否已设置）。"
        )
    root = Path(expanded)
    if not root.is_absolute():
        root = layout.repo_root / root
    if not root.is_dir():
        raise ToolError(f"sourceRoot「{raw}」→ {root} 不是目录。")
    return root


def registry_paths(descriptor: dict, source_root: Path) -> tuple[Path, Path]:
    registry = descriptor.get("registryFiles") or {}
    instructions = registry.get("instructions")
    methods = registry.get("methods")
    if not instructions or not methods:
        raise ToolError(f"描述符「{descriptor.get('id')}」的 registryFiles 缺 instructions / methods。")
    return source_root / instructions, source_root / methods


def extract_keys(instructions_path: Path, methods_path: Path) -> tuple[set[str], set[str]]:
    for path in (instructions_path, methods_path):
        if not path.is_file():
            raise ToolError(f"registryFiles 指向的文件不存在：{path}")
    return enum_members(instructions_path), expression_functions(methods_path)


def read_base(path: Path) -> tuple[set[str], set[str]]:
    if not path.is_file():
        raise ToolError(f"缺少 base 快照 {path}；先跑 --update-base 固化。")
    directives: set[str] = set()
    functions: set[str] = set()
    for line in _read(path).splitlines():
        if not line.strip():
            continue
        name, tab, kind = line.partition("\t")
        if not tab or kind not in (DIRECTIVE, FUNCTION):
            raise ToolError(f"base 快照 {path} 行格式非法：{line!r}")
        (directives if kind == DIRECTIVE else functions).add(name)
    return directives, functions


def write_base(path: Path, directives: set[str], functions: set[str]) -> None:
    text = "\n".join(_kind_lines(directives, DIRECTIVE) + _kind_lines(functions, FUNCTION)) + "\n"
    _write(path, text)


# --------------------------------------------------------------------------- #
# 描述符 additions
# --------------------------------------------------------------------------- #
def descriptor_additions(descriptor: dict) -> set[tuple[str, str]]:
    out: set[tuple[str, str]] = set()
    for item in descriptor.get("additions") or []:
        out.add((item["name"], item["kind"]))
    return out


def _merge_additions(descriptor: dict, delta: set[tuple[str, str]]) -> list[dict]:
    """按 delta 重排 additions；同 (name,kind) 保留既有 owner 等自定义字段。"""
    existing: dict[tuple[str, str], dict] = {}
    for item in descriptor.get("additions") or []:
        existing[(item["name"], item["kind"])] = item
    ordered = sorted(delta, key=lambda nk: (nk[1], nk[0]))  # 先 directive 后 function
    merged: list[dict] = []
    for name, kind in ordered:
        entry = existing.get((name, kind))
        if entry is None:
            entry = {"name": name, "kind": kind, "owner": descriptor.get("id")}
        merged.append(entry)
    return merged


# --------------------------------------------------------------------------- #
# 命令
# --------------------------------------------------------------------------- #
def update_base(layout: Layout, env: dict) -> int:
    descriptor = load_descriptor(layout, BASE_ID)
    source_root = resolve_source_root(layout, descriptor, env)
    directives, functions = extract_keys(*registry_paths(descriptor, source_root))
    path = layout.base_snapshot()
    write_base(path, directives, functions)
    print(f"updated {path.relative_to(layout.repo_root)}")
    print(f"  directives={len(directives)} functions={len(functions)}")
    return 0


def _collect(layout: Layout, descriptor: dict, env: dict):
    source_root = resolve_source_root(layout, descriptor, env)
    fork_directives, fork_functions = extract_keys(*registry_paths(descriptor, source_root))
    base_directives, base_functions = read_base(layout.base_snapshot(descriptor.get("base", BASE_ID)))
    return fork_directives, fork_functions, base_directives, base_functions


def update_fork(layout: Layout, fork_id: str, env: dict) -> int:
    descriptor = load_descriptor(layout, fork_id)
    fork_d, fork_f, base_d, base_f = _collect(layout, descriptor, env)
    inventory = build_inventory(fork_d, base_d, fork_f, base_f)
    digest = sha256_hex(inventory)
    _write(layout.inventory(fork_id), inventory)
    _write(layout.digest(fork_id), digest + "\n")

    delta, _, _ = keys_of(fork_d, base_d, fork_f, base_f)
    descriptor["additions"] = _merge_additions(descriptor, delta)
    _write(layout.descriptor(fork_id), json.dumps(descriptor, ensure_ascii=False, indent=2) + "\n")

    print(f"updated {layout.inventory(fork_id).relative_to(layout.repo_root)}")
    print(f"updated {layout.digest(fork_id).relative_to(layout.repo_root)}")
    print(f"updated {layout.descriptor(fork_id).relative_to(layout.repo_root)}")
    print(f"  additions={len(delta)} sha256={digest}")
    return 0


def print_delta(layout: Layout, fork_id: str, env: dict) -> int:
    descriptor = load_descriptor(layout, fork_id)
    fork_d, fork_f, base_d, base_f = _collect(layout, descriptor, env)
    _, only_directives, only_functions = keys_of(fork_d, base_d, fork_f, base_f)
    print(f"{fork_id}-only directives ({len(only_directives)}):")
    for name in only_directives:
        print(f"  {name}")
    print(f"{fork_id}-only functions ({len(only_functions)}):")
    for name in only_functions:
        print(f"  {name}")
    return 0


def check_fork(layout: Layout, fork_id: str, env: dict) -> int:
    descriptor = load_descriptor(layout, fork_id)
    fork_d, fork_f, base_d, base_f = _collect(layout, descriptor, env)
    inventory = build_inventory(fork_d, base_d, fork_f, base_f)
    digest = sha256_hex(inventory)

    inventory_path = layout.inventory(fork_id)
    hash_path = layout.digest(fork_id)
    if not inventory_path.is_file() or not hash_path.is_file():
        print(f"error: 缺少冻结产物 {inventory_path} / {hash_path}；先跑 --update。", file=sys.stderr)
        return 1

    failed = False

    frozen = _read(inventory_path)
    stored_digest = _read(hash_path).strip()
    if inventory != frozen or digest != stored_digest:
        failed = True
        print(f"drift[{fork_id}]: 清单与冻结产物不一致——禁止静默合并，请评审后 --update。", file=sys.stderr)
        print(f"  frozen sha256: {sha256_hex(frozen)}", file=sys.stderr)
        print(f"  actual sha256: {digest}", file=sys.stderr)
        for line in difflib.unified_diff(
            frozen.splitlines(), inventory.splitlines(),
            fromfile=f"frozen/{inventory_path.name}", tofile=f"actual/{inventory_path.name}", lineterm="",
        ):
            print(line, file=sys.stderr)

    delta, _, _ = keys_of(fork_d, base_d, fork_f, base_f)
    declared = descriptor_additions(descriptor)
    if delta != declared:
        failed = True
        print(f"drift[{fork_id}]: 描述符 additions 与抽取结果不一致——请评审后 --update。", file=sys.stderr)
        for name, kind in sorted(delta - declared):
            print(f"  + {name}\t{kind}", file=sys.stderr)
        for name, kind in sorted(declared - delta):
            print(f"  - {name}\t{kind}", file=sys.stderr)

    if failed:
        return 1
    print(f"ok[{fork_id}]: 清单 / additions 与冻结产物一致（sha256={digest}, additions={len(delta)}）。")
    return 0


def list_forks(layout: Layout) -> int:
    ids = sorted(p.stem for p in layout.dialects_dir.glob("*.json"))
    for fork_id in ids:
        print(fork_id)
    return 0


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description="fork 参数化上游 delta 工具：抽取公开键、求 delta、冻结/校验稳定 hash。")
    parser.add_argument("--fork", help="fork id（对应 dialects/<id>.json）。--check/--update/--print-delta 必需。")
    mode = parser.add_mutually_exclusive_group()
    mode.add_argument("--check", action="store_true", help="与冻结产物及描述符 additions 比对，漂移则非零退出（默认）。")
    mode.add_argument("--update", action="store_true", help="重写冻结清单 / hash / 描述符 additions。")
    mode.add_argument("--print-delta", action="store_true", help="只打印 fork-only delta。")
    mode.add_argument("--update-base", action="store_true", help="重写 era-core-baseline 快照。")
    mode.add_argument("--list-forks", action="store_true", help="列出已知 fork id。")
    args = parser.parse_args(argv)

    layout = Layout.default()

    try:
        if args.list_forks:
            return list_forks(layout)
        if args.update_base:
            return update_base(layout, os.environ)
        if not args.fork:
            parser.error("--check/--update/--print-delta 需要 --fork <id>（或使用 --update-base / --list-forks）。")
        if args.update:
            return update_fork(layout, args.fork, os.environ)
        if args.print_delta:
            return print_delta(layout, args.fork, os.environ)
        return check_fork(layout, args.fork, os.environ)
    except ToolError as exc:
        print(f"error: {exc}", file=sys.stderr)
        return 2


if __name__ == "__main__":
    sys.exit(main())