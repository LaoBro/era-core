#!/usr/bin/env python3
"""fork 键分档器：静态扫描上游实现体，判定 A/B/C（无 LLM 依赖、仅标准库）。

用途
----
回答「这个 fork-only 键能不能机械搬进 era-core」：
  A = 结构同构，可机械搬 + 编译错误修复
  B = 结构可搬，但语义/执行需人工映射（通常落到既有语义接缝）
  C = 宿主能力 / 语言运行时缺失，脚本无能为力

判定依据 = 上游实现体里出现的**符号信号**，不看键名、不做语义猜测。
每个结论都附带证据（命中的信号名 + 文件:行）。

口径（与 .scratch/2026.10.31.方言移植元数据框架 的分析一致）
--------------------------------------------------------------
1. 类型系统级信号（Float 族）          → C  （era-core 无 Float 类型）
2. 语言运行时级信号（ExecutionContext）→ C  （era-core 无栈式函数上下文 / VARIADIC）
3. 渲染/音频/反射 API：
     键是**指令**  → B（参数骨架可搬，执行体降级到既有接缝）
     键是**函数**  → C（无法返回正确值）
4. 变量反射/自定义参数校验（无上述信号）→ B
5. 其余                                → A

用法
----
    export EMUERA_SKIA_REF=/path/to/skia-checkout
    python tools/dialect-inventory/classify.py --fork skia
    python tools/dialect-inventory/classify.py --fork skia --key MATCHALL
    python tools/dialect-inventory/classify.py --fork skia --json

退出码：0 成功；2 前置条件缺失（描述符 / sourceRoot / 注册表）。
"""

from __future__ import annotations

import argparse
import json
import os
import re
import sys
from dataclasses import dataclass, field
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
from extract import Layout, ToolError, load_descriptor, resolve_source_root  # noqa: E402

DIRECTIVE = "directive"
FUNCTION = "function"

# --------------------------------------------------------------------------- #
# 信号表：name -> (类别, 说明)。类别用于优先级（type > runtime > host）。
# --------------------------------------------------------------------------- #
FLOAT_SIGNALS = [
    "EraType.Float",
    "ArgType.Float",
]

RUNTIME_SIGNALS = [
    "ExecutionContext",
    "CurrentVariadicArgCount",
    "exm.Process.State",
    "Process.State",
]

# 图像层：指令可搬骨架→B，函数无法返回正确值→C
IMAGE_LAYER_SIGNALS = [
    "ImageLayerManager",
    "ImageLayer",
    "ExistsImageLayer",
]

# 渲染/字体/音频后端：era-core 无对应实现，连"存状态"都无消费者 → 一律 C
RENDER_CONFIG_SIGNALS = [
    "Config.ImageQuality",
    "Config.FontHinting",
    "Config.FontEdging",
    "Config.TextDrawingMode",
    "TextDrawingMode",
    "StrictFontFallback",
    "strictFontFallback",
    "BitmapCache",
    "bitmapCache",
    "BitmapCachemEnabled",
    "bitmapCacheEnabled",
    "AnimeTimer",
    "AnimeTimerInterval",
    "setRedrawTimer",
    "SetRedrawTimer",
    "GetLinePointY",
    "GetLineY",
    "ImageQuality",
    "SkiaSharp",
    "SKBitmap",
    "SKImage",
    "SKCanvas",
    "SKPaint",
    "SKColor",
    "FontStyle",
]

AUDIO_SIGNALS = [
    "SoundTouch",
    "WaveAudio",
    "GetSound",
    "PlaySound",
    "PlayBGM",
    "GlobalStatic.Bgm",
    "GlobalStatic.Sound",
    "BGM",
    "Bgm",
    "Sound",
]

# 输入宏 / 序列输入：era-core 无对应输入注入机制 → C
INPUT_MACRO_SIGNALS = [
    "inputMacroEnabled",
    "InputMacroEnabled",
    "sequenceInputValue",
    "hasSequenceInput",
]

# 仅状态开关：era-core 有同名字段可承接，执行降级 → B
STATE_ONLY_SIGNALS = [
    "TextBackgroundColor",
]

# 变量反射 / 自定义参数校验：可搬但需人工核对语义
REFLECTION_SIGNALS = [
    "GlobalStatic.IdentifierDictionary.GetVariableToken",
    "GetVariableToken",
    "VariableToken",
    "GetOperandType",
    "CheckArgumentType",
]

# 静态扫描无法捕获的语义映射边界（人工维护，每月复核）。
# 这些键结构同构、无缺失符号，但上游语义在 era-core 无对应概念，必须人工改写。
KNOWN_OVERRIDES: dict[str, tuple[str, str]] = {
    "TINPUTNF": ("B", "noFocus 语义在 era-core 无焦点概念，需人工适配"),
    "TINPUTSNF": ("B", "noFocus 语义在 era-core 无焦点概念，需人工适配"),
    "TONEINPUTNF": ("B", "noFocus 语义在 era-core 无焦点概念，需人工适配"),
    "TONEINPUTSNF": ("B", "noFocus 语义在 era-core 无焦点概念，需人工适配"),
    # CSV / ERD 预设名反查：上游方法名（ExistPresetNameInCsv 等）与 era-core
    # ConstantData 的关键字系统（isDefined / TryKeywordToInteger）不同名但语义等价，
    # 静态扫描无法跨仓比对，人工确认后归 B（需人工映射 API 名）。
    "EXIST_IN_CSV": ("B", "映射到 era-core ConstantData.isDefined/TryKeywordToInteger，需人工对齐语义"),
    "EXIST_IN_ERD": ("B", "映射到 era-core ConstantData.isDefinedErd，需人工对齐语义"),
    "GETCSVNOBYNAME": ("B", "映射到 era-core ConstantData.TryKeywordToInteger，需人工对齐语义"),
    "GETCSVNOBYCALLNAME": ("B", "映射到 era-core ConstantData 关键字系统，需人工对齐语义"),
    "GETCSVNOBYMASTERNAME": ("B", "映射到 era-core ConstantData 关键字系统，需人工对齐语义"),
    "GETCSVNOBYNICKNAME": ("B", "映射到 era-core ConstantData 关键字系统，需人工对齐语义"),
    # GETANIMETIMER 静态扫描会命中 AnimeTimer 而误判 C；era-core 已有等价接缝
    # （EmueraConsole.getRedrawTimer，且该键已移植），故归 A。
    "GETANIMETIMER": ("A", "era-core 已有 getRedrawTimer 等价接缝，可复用（已移植先例）"),
}

SIGNAL_LABELS = {
    "type": "类型系统（Float）",
    "runtime": "语言运行时上下文",
    "host": "宿主 API",
    "reflection": "变量反射/自定义参数校验",
}


class ClassIndex:
    """一次性扫描全部 .cs，建立 类名 -> (文件, 起行, 起止偏移) 索引。"""

    _CLASS_DECL = re.compile(r"\b(?:class|record|struct|interface)\s+([A-Za-z_][A-Za-z0-9_]*)")

    def __init__(self, files: list[Path]):
        self.files = files
        self.entries: dict[str, list[tuple[Path, int, int, int]]] = {}
        self._texts: dict[Path, tuple[str, list[int]]] = {}
        self._built = False

    def _text(self, path: Path) -> tuple[str, list[int]]:
        if path not in self._texts:
            raw = path.read_text(encoding="utf-8-sig", errors="replace")
            lines = raw.splitlines(keepends=True)
            offsets = [0]
            for line in lines:
                offsets.append(offsets[-1] + len(line))
            self._texts[path] = (raw, offsets)
        return self._texts[path]

    @staticmethod
    def _strip_code(raw: str) -> str:
        """把注释/字符串替换为等长空白，供大括号配平使用。"""
        out = list(raw)
        i, n = 0, len(raw)
        while i < n:
            c = raw[i]
            if c == "/" and i + 1 < n and raw[i + 1] == "/":
                while i < n and raw[i] != "\n":
                    out[i] = " "
                    i += 1
            elif c == "/" and i + 1 < n and raw[i + 1] == "*":
                out[i] = out[i + 1] = " "
                i += 2
                while i + 1 < n and not (raw[i] == "*" and raw[i + 1] == "/"):
                    if raw[i] != "\n":
                        out[i] = " "
                    i += 1
                if i + 1 < n:
                    out[i] = out[i + 1] = " "
                    i += 2
            elif c == '"':
                j = i + 1
                while j < n:
                    if raw[j] == "\\":
                        j += 2
                        continue
                    if raw[j] == '"':
                        j += 1
                        break
                    if raw[j] != "\n":
                        out[j] = " "
                    j += 1
                out[i] = " "
                if j <= n:
                    if j - 1 < n:
                        out[j - 1] = " "
                i = j
            else:
                i += 1
        return "".join(out)

    def build(self):
        if self._built:
            return
        for path in self.files:
            raw, offsets = self._text(path)
            clean = self._strip_code(raw)
            for m in self._CLASS_DECL.finditer(clean):
                name = m.group(1)
                brace = clean.find("{", m.end())
                if brace < 0:
                    continue
                depth = 0
                end = -1
                for k in range(brace, len(clean)):
                    ch = clean[k]
                    if ch == "{":
                        depth += 1
                    elif ch == "}":
                        depth -= 1
                        if depth == 0:
                            end = k
                            break
                if end < 0:
                    continue
                line = clean.count("\n", 0, m.start()) + 1
                self.entries.setdefault(name, []).append((path, line, m.start(), end))
        self._built = True

    def body(self, class_name: str) -> tuple[Path, int, str] | None:
        self.build()
        candidates = self.entries.get(class_name)
        if not candidates:
            return None
        # 多个同名类：取 body 最长的（真正的实现，不是接口/桩）
        path, line, start, end = max(candidates, key=lambda e: e[3] - e[2])
        raw, _ = self._text(path)
        return path, line, raw[start: end + 1]


@dataclass
class KeyImpl:
    key: str
    kind: str
    class_name: str | None = None
    file: Path | None = None
    line: int = 0
    body: str = ""
    missing_reason: str | None = None


_reg_func = re.compile(r'\["([A-Za-z0-9_]+)"\]\s*=\s*new\s+([A-Za-z0-9_]+)')
_reg_instr = re.compile(r'addFunction\s*\(\s*FunctionCode\.([A-Za-z0-9_]+)\s*,\s*new\s+([A-Za-z0-9_]+)')


def load_registrations(files: list[Path]) -> dict[tuple[str, str], str]:
    """(kind, key) -> 实现类名。"""
    out: dict[tuple[str, str], str] = {}
    for path in files:
        text = path.read_text(encoding="utf-8-sig", errors="replace")
        for m in _reg_func.finditer(text):
            out[(FUNCTION, m.group(1))] = m.group(2)
        for m in _reg_instr.finditer(text):
            out[(DIRECTIVE, m.group(1))] = m.group(2)
    return out


def _scan(body: str, file: Path, base_line: int, signals: list[str]) -> list[tuple[str, str]]:
    hits: list[tuple[str, str]] = []
    for sig in signals:
        idx = body.find(sig)
        if idx >= 0:
            line = base_line + body.count("\n", 0, idx)
            hits.append((sig, f"{file.name}:{line}"))
    return hits


def classify(key: str, kind: str, impl: KeyImpl) -> tuple[str, str, dict]:
    """返回 (档位, 一句话理由, 证据 dict)。"""
    if key in KNOWN_OVERRIDES:
        grade, reason = KNOWN_OVERRIDES[key]
        return grade, f"{reason}（已知边界表）", {"signals": [], "category": "known-override"}

    if impl.class_name is None:
        return "C", "无法定位上游实现类（注册表未登记或类缺失）", {"signals": []}
    if not impl.body.strip():
        return "C", "实现体为空/未找到类体", {"signals": []}

    body, file, line = impl.body, impl.file, impl.line

    float_hits = _scan(body, file, line, FLOAT_SIGNALS)
    if float_hits:
        # 返回值/参数规则本身是 Float ⇒ 纯 Float 专用（C）
        if "ReturnType = EraType.Float" in body or "EraType[] { EraType.Float" in body \
                or "ArgTypes = { ArgType.Float" in body or "ArgType.Float }" in body:
            return "C", f"Float 专用签名（{float_hits[0][0]}），era-core 无 Float 类型", {
                "signals": [f"{k} @ {v}" for k, v in float_hits], "category": "float-only"}
        # 仅内部有 Float 分支、返回/参数仍是 Integer/String ⇒ 可裁剪（B）
        trimmable = ("EraType.Integer" in body) or ("EraType.String" in body)
        if trimmable:
            return "B", f"含 Float 分支（{float_hits[0][0]}）但可裁剪，需人工移除 Float 路径", {
                "signals": [f"{k} @ {v}" for k, v in float_hits], "category": "float-branch"}
        return "C", f"纯 Float 专用（{float_hits[0][0]}），era-core 无 Float 类型", {
            "signals": [f"{k} @ {v}" for k, v in float_hits], "category": "float-only"}

    runtime_hits = _scan(body, file, line, RUNTIME_SIGNALS)
    if runtime_hits:
        return "C", f"依赖语言运行时上下文（{runtime_hits[0][0]}），era-core 无 ExecutionContext/VARIADIC", {
            "signals": [f"{k} @ {v}" for k, v in runtime_hits], "category": "runtime"}

    render_hits = _scan(body, file, line, RENDER_CONFIG_SIGNALS)
    if render_hits:
        return "C", f"依赖渲染/字体后端（{render_hits[0][0]}），era-core 无对应管线", {
            "signals": [f"{k} @ {v}" for k, v in render_hits], "category": "render"}

    audio_hits = _scan(body, file, line, AUDIO_SIGNALS)
    if audio_hits:
        return "C", f"依赖音频后端（{audio_hits[0][0]}），era-core 无对应实现", {
            "signals": [f"{k} @ {v}" for k, v in audio_hits], "category": "audio"}

    macro_hits = _scan(body, file, line, INPUT_MACRO_SIGNALS)
    if macro_hits:
        return "C", f"依赖输入宏/序列输入机制（{macro_hits[0][0]}），era-core 无对应注入路径", {
            "signals": [f"{k} @ {v}" for k, v in macro_hits], "category": "input-macro"}

    image_hits = _scan(body, file, line, IMAGE_LAYER_SIGNALS)
    if image_hits:
        if kind == DIRECTIVE:
            return "B", f"执行体依赖图像层管线（{image_hits[0][0]}）；参数骨架可搬，执行降级到既有接缝", {
                "signals": [f"{k} @ {v}" for k, v in image_hits], "category": "image-layer"}
        return "C", f"函数实现依赖图像层管线（{image_hits[0][0]}），era-core 无对应后端", {
            "signals": [f"{k} @ {v}" for k, v in image_hits], "category": "image-layer"}

    state_hits = _scan(body, file, line, STATE_ONLY_SIGNALS)
    if state_hits:
        return "B", f"仅保存状态（{state_hits[0][0]}）；era-core 有同名字段可承接，消费线未接", {
            "signals": [f"{k} @ {v}" for k, v in state_hits], "category": "state-only"}

    refl_hits = _scan(body, file, line, REFLECTION_SIGNALS)
    if refl_hits:
        return "B", f"含变量反射/自定义参数校验（{refl_hits[0][0]}），语义需人工核对", {
            "signals": [f"{k} @ {v}" for k, v in refl_hits], "category": "reflection"}

    return "A", "结构同构，可机械搬 + 编译修复", {"signals": []}


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description="fork 键分档器（A/B/C 静态判定）")
    parser.add_argument("--fork", required=True, help="fork id（dialects/<id>.json）")
    parser.add_argument("--key", help="只分析单个键")
    parser.add_argument("--json", action="store_true", help="输出 JSON")
    parser.add_argument("--quiet", action="store_true", help="只输出分类计数")
    args = parser.parse_args(argv)

    layout = Layout.default()
    try:
        descriptor = load_descriptor(layout, args.fork)
        source_root = resolve_source_root(layout, descriptor, os.environ)
    except ToolError as exc:
        print(f"error: {exc}", file=sys.stderr)
        return 2

    registry = descriptor.get("registryFiles") or {}
    reg_files = []
    for field_name in ("instructions", "methods"):
        rel = registry.get(field_name)
        if rel:
            p = source_root / rel
            if p.is_file():
                reg_files.append(p)
    if not reg_files:
        print("error: registryFiles 未指向任何存在的文件", file=sys.stderr)
        return 2

    # 类体索引：扫 Emuera 下全部 .cs（只扫一次）
    emuera_root = source_root / "Emuera"
    cs_files = list(emuera_root.rglob("*.cs"))
    index = ClassIndex(cs_files)
    # 指令注册在 FunctionIdentifier.cs（不在 registryFiles 指向的枚举文件），故扫全量 .cs
    regs = load_registrations(cs_files)

    items = descriptor.get("additions") or []
    if args.key:
        items = [i for i in items if i["name"] == args.key]
        if not items:
            print(f"error: 键 {args.key} 不在 {args.fork} 的 additions 中", file=sys.stderr)
            return 2

    results = []
    for item in items:
        name, kind = item["name"], item["kind"]
        class_name = regs.get((kind, name))
        impl = KeyImpl(key=name, kind=kind, class_name=class_name)
        if class_name:
            found = index.body(class_name)
            if found:
                impl.file, impl.line, impl.body = found
        grade, why, ev = classify(name, kind, impl)
        results.append({
            "key": name,
            "kind": kind,
            "grade": grade,
            "reason": why,
            "class": class_name,
            "site": f"{impl.file.name}:{impl.line}" if impl.file else None,
            "evidence": ev,
        })

    counts = {"A": 0, "B": 0, "C": 0}
    for r in results:
        counts[r["grade"]] += 1

    if args.json:
        print(json.dumps({"counts": counts, "total": len(results), "items": results},
                         ensure_ascii=False, indent=2))
        return 0

    if args.quiet:
        print(f"A={counts['A']} B={counts['B']} C={counts['C']} total={len(results)}")
        return 0

    for grade in ("A", "B", "C"):
        group = [r for r in results if r["grade"] == grade]
        print(f"\n[{grade}] {len(group)} keys")
        for r in group:
            print(f"  {r['key']:<28} {r['kind']:<9} {r['reason']}")
            print(f"      class={r['class']} site={r['site']}")
            for sig in r["evidence"].get("signals", [])[:3]:
                print(f"      signal: {sig}")
    print(f"\nTOTAL A={counts['A']} B={counts['B']} C={counts['C']} (n={len(results)})")
    return 0


if __name__ == "__main__":
    sys.exit(main())
