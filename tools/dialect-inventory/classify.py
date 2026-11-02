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
# 保留判据：该键在自动流程中**无法**得到当前档位——要么无 C 类信号可命中（直接走 A），
# 要么信号语义与 era-core 无强关联。**能被探测稳定复现的条目应删除**（先例：GETANIMETIMER）。
KNOWN_OVERRIDES: dict[str, tuple[str, str]] = {
    # NF 族：上游仅多一个构造参数（noFocus），实现体里没有任何可探测符号；
    # era-core 无「焦点」概念，必须人工适配。
    "TINPUTNF": ("B", "noFocus 语义在 era-core 无焦点概念，需人工适配"),
    "TINPUTSNF": ("B", "noFocus 语义在 era-core 无焦点概念，需人工适配"),
    "TONEINPUTNF": ("B", "noFocus 语义在 era-core 无焦点概念，需人工适配"),
    "TONEINPUTSNF": ("B", "noFocus 语义在 era-core 无焦点概念，需人工适配"),
    # CSV / ERD 预设名反查：上游实现体**没有** C 类信号（直接走 A 分支），
    # 而探测只在 C 分支生效；override 是「跨仓 API 名不同、需人工映射」的唯一来源。
    # 实测删除会退化为 A（丢失准确档位），故保留。
    "EXIST_IN_CSV": ("B", "映射到 era-core ConstantData.isDefined/TryKeywordToInteger，需人工对齐语义"),
    "EXIST_IN_ERD": ("B", "映射到 era-core ConstantData.isDefinedErd，需人工对齐语义"),
    "GETCSVNOBYNAME": ("B", "映射到 era-core ConstantData.TryKeywordToInteger，需人工对齐语义"),
    "GETCSVNOBYCALLNAME": ("B", "映射到 era-core ConstantData 关键字系统，需人工对齐语义"),
    "GETCSVNOBYMASTERNAME": ("B", "映射到 era-core ConstantData 关键字系统，需人工对齐语义"),
    "GETCSVNOBYNICKNAME": ("B", "映射到 era-core ConstantData 关键字系统，需人工对齐语义"),
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


# --------------------------------------------------------------------------- #
# era-core 符号探测（修正「只读上游」盲区）
# --------------------------------------------------------------------------- #
# 通用词黑名单：单独出现绝不作为探测候选，避免 Sound / Length 之类误配。
GENERIC_TOKENS = {
    "ToString", "GetType", "Equals", "GetHashCode", "Clone",
    "Length", "Count", "Value", "Name", "Item", "Keys", "Values",
    "Get", "Set", "Add", "Remove", "Contains", "Clear", "RemoveAt",
    "Math", "Convert", "Array", "List", "String", "Object",
    "Print", "Input", "True", "False", "Null", "This", "New",
    # 宿主容器/中介属性：单独命中没有任何语义信息，绝不作为降级依据。
    "Console", "Process", "Exm", "GlobalStatic", "Config", "Arguments", "Term",
    # 表达式/变量体系通用方法：几乎每个 FunctionMethod 都会调用，绝不算「能力证据」。
    "GetIntValue", "GetStrValue", "GetFloatValue", "SetValue", "GetValue",
    "GetOperandType", "GetEraType", "GetLength", "GetArray", "SetArray",
    "Restructure", "IsConst", "ConstInt", "ConstStr", "Identifier",
    "Reduce", "ReduceExpressionTerm", "Calc",
    # 领域枚举值与参数构造通用调用：单次出现不足以证明能力等价。
    "NAME", "NICKNAME", "CALLNAME", "MASTERNAME",
    "GetArgumentBuilder", "ArgBuilder", "GetMethodName",
    # 指令行/求值器的通用宿主属性：任何指令实现都触碰，不构成能力证据。
    "Argument", "VEvaluator", "Constant", "State", "Line", "LineNo",
    "Message", "Target", "Index", "RandomSeed",
}

# 近似匹配只允许这些**词形变换**（前后缀），不做任意模糊。
STRIP_PREFIXES = ("Get", "Set", "Is", "Has", "Try")
STRIP_SUFFIXES = ("Method", "Instruction", "Manager", "Info")

# 人工确认的同义映射（上游符号 → era-core 候选）。只收已核对的条目，
# 命中记为 weak（等价性仍需人工确认）。
SYNONYM_HINTS: dict[str, list[str]] = {
    "ExistPresetNameInCsv": ["isDefined", "TryKeywordToInteger"],
    "ExistPresetNameInErd": ["isDefinedErd"],
    "GetCsvNoByName": ["TryKeywordToInteger", "KeywordToInteger"],
    "GetCsvNoByCallName": ["TryKeywordToInteger", "KeywordToInteger"],
    "GetCsvNoByMasterName": ["TryKeywordToInteger", "KeywordToInteger"],
    "GetCsvNoByNickName": ["TryKeywordToInteger", "KeywordToInteger"],
    # era-core 把重绘周期暴露为 getRedrawTimer() 方法，上游是 AnimeTimer 属性。
    "AnimeTimer": ["getRedrawTimer", "GetRedrawTimer"],
    # 上游角色名反查读 Constant.NameToTemplateMap；era-core 等价于 ConstantData 关键字系统。
    "NameToTemplateMap": ["TryKeywordToInteger", "KeywordToInteger"],
    "NicknameToTemplateMap": ["TryKeywordToInteger", "KeywordToInteger"],
    "CallnameToTemplateMap": ["TryKeywordToInteger", "KeywordToInteger"],
    "MasternameToTemplateMap": ["TryKeywordToInteger", "KeywordToInteger"],
}

# 纯语法结构类：其成员（Def / Argument / Line 等）是任何实现都会触碰的容器，
# 命中这些类不算「era-core 具备该能力」。
SYNTAX_OWNER_BLACKLIST = {
    "LogicalLine", "FunctionLabelLine", "InstructionLine", "ExpressionMediator",
    "ProcessState", "Process", "GlobalStatic", "ArgumentBuilder", "ArgumentParser",
}

# 已人工确认的探测符号：只有这些命中的 override 才算「可自动复现」，
# 避免 exact 匹配的泛型命中（MouseInput / Argument 之类）制造清理噪音。
KNOWN_PROBE_SYMBOLS = {
    "getRedrawTimer", "GetRedrawTimer", "setRedrawTimer", "SetRedrawTimer",
    "isDefined", "isDefinedErd", "TryKeywordToInteger", "KeywordToInteger",
}

# 渲染「绘制原语/配置字段」是硬墙：即使 era-core 有同名字段也不算具备管线能力。
# 只有定时器接缝（setRedrawTimer / getRedrawTimer）允许探测降级。
RENDER_HARD_SIGNALS = [
    "Config.ImageQuality", "Config.FontHinting", "Config.FontEdging",
    "Config.TextDrawingMode", "TextDrawingMode", "StrictFontFallback",
    "strictFontFallback", "BitmapCache", "bitmapCache", "ImageQuality",
    "GetLinePointY", "GetLineY", "SkiaSharp", "SKBitmap", "SKImage",
    "SKCanvas", "SKPaint", "SKColor", "FontStyle", "G_POLYGON",
]
TIMER_SIGNALS = ["setRedrawTimer", "SetRedrawTimer", "getRedrawTimer", "GetRedrawTimer", "AnimeTimer"]


@dataclass
class EraSymbol:
    name: str
    owner: str
    kind: str          # method | property | field | class
    visibility: str    # public | internal | protected | private
    file: Path
    line: int
    is_empty: bool = False

    def where(self) -> str:
        return f"{self.file.name}:{self.line}"


_MEMBER_DECL = re.compile(
    r"^\s*(?P<vis>public|internal|protected|private)?\s*"
    r"(?:(?:static|virtual|override|sealed|async|readonly|const|new|partial|extern|unsafe|abstract)\s+)*"
    r"(?P<type>[A-Za-z_][\w<>\[\],.?\s]*)\s+"
    r"(?P<name>[A-Za-z_][A-Za-z0-9_]*)\s*(?P<tail>\(|\{|=>|;|=)"
)
_CLASS_DECL_ERACORE = re.compile(r"\b(?:class|record|struct|interface|enum)\s+([A-Za-z_][A-Za-z0-9_]*)")
_EMPTY_METHOD = re.compile(r"\(\s*[^)]*\)\s*(?:=>\s*(?:null|default|0|\"\"|false|true)\s*;|\{\s*\})")


class EraCoreIndex:
    """扫描 era-core 源码，建立 符号名 → 声明条目 索引（供探测引用证据）。"""

    def __init__(self, root: Path):
        self.root = root
        self.by_name: dict[str, list[EraSymbol]] = {}
        self.by_lower: dict[str, list[EraSymbol]] = {}
        self._built = False

    def build(self):
        if self._built:
            return
        for path in self.root.rglob("*.cs"):
            self._scan_file(path)
        self._built = True

    def _scan_file(self, path: Path):
        try:
            raw = path.read_text(encoding="utf-8-sig", errors="replace")
        except OSError:
            return
        lines = raw.splitlines()
        owner = ""
        for idx, line in enumerate(lines):
            cm = _CLASS_DECL_ERACORE.search(line)
            if cm and "(" not in line.split(cm.group(1))[0][-1:]:
                owner = cm.group(1)
            m = _MEMBER_DECL.match(line)
            if not m:
                continue
            name = m.group("name")
            if name in {"if", "for", "while", "switch", "using", "return", "new", "var", "get", "set"}:
                continue
            vis = (m.group("vis") or "private").strip()
            tail = m.group("tail")
            kind = "method" if tail == "(" else ("property" if tail == "{" else "field")
            is_empty = False
            if kind == "method":
                snippet = " ".join(l.strip() for l in lines[idx: idx + 4])
                is_empty = bool(_EMPTY_METHOD.search(snippet))
            sym = EraSymbol(name, owner, kind, vis, path, idx + 1, is_empty)
            self.by_name.setdefault(name, []).append(sym)
            self.by_lower.setdefault(name.lower(), []).append(sym)

    def _usable(self, syms: list[EraSymbol]) -> list[EraSymbol]:
        # 只有可见成员、实现非空、且不属于纯语法容器类，才算「能力」。
        return [
            s for s in syms
            if s.visibility in ("public", "internal", "protected")
            and not s.is_empty
            and s.owner not in SYNTAX_OWNER_BLACKLIST
        ]

    def exact(self, name: str) -> EraSymbol | None:
        self.build()
        usable = self._usable(self.by_name.get(name, []))
        return usable[0] if usable else None

    def case_insensitive(self, name: str) -> EraSymbol | None:
        self.build()
        usable = self._usable(self.by_lower.get(name.lower(), []))
        return usable[0] if usable else None

    def any(self, name: str) -> EraSymbol | None:
        return self.exact(name) or self.case_insensitive(name)


_MEMBER_REF = re.compile(r"[.)]\s*([A-Za-z_][A-Za-z0-9_]*)")
_UPSTREAM_TIMER = re.compile(r"(getRedrawTimer|setRedrawTimer)\s*\(")


def _upstream_member_names(body: str) -> list[str]:
    seen: set[str] = set()
    out: list[str] = []
    for m in _MEMBER_REF.finditer(body):
        name = m.group(1)
        if not name or name in GENERIC_TOKENS or name in seen:
            continue
        seen.add(name)
        out.append(name)
    return out


def _morph_candidates(name: str) -> list[str]:
    out: list[str] = []
    for prefix in STRIP_PREFIXES:
        if name.startswith(prefix) and len(name) > len(prefix):
            out.append(name[len(prefix):])
    for suffix in STRIP_SUFFIXES:
        if name.endswith(suffix) and len(name) > len(suffix):
            out.append(name[: -len(suffix)])
    return out


def probe_eracore(body: str, eracore: EraCoreIndex | None) -> dict | None:
    """在上游实现体里找 era-core 已有等价符号。命中返回证据，否则 None。

    保守规则：exact → probable（仅白名单词形变换）→ weak（仅人工同义词表）。
    private / 空实现一律不算能力。
    """
    if eracore is None:
        return None

    timer = _UPSTREAM_TIMER.search(body)
    if timer:
        name = timer.group(1)
        hit = eracore.any(name)
        if hit:
            return {"upstream": name, "candidate": hit, "confidence": "exact"}

    for name in _upstream_member_names(body):
        hit = eracore.exact(name)
        if hit:
            return {"upstream": name, "candidate": hit, "confidence": "exact"}
        for cand in _morph_candidates(name):
            hit = eracore.any(cand)
            if hit:
                return {"upstream": name, "candidate": hit, "confidence": "probable"}
        for cand in SYNONYM_HINTS.get(name, []):
            hit = eracore.any(cand)
            if hit:
                return {"upstream": name, "candidate": hit, "confidence": "weak"}
    return None


def _evidence(probe: dict, category: str) -> dict:
    sym: EraSymbol = probe["candidate"]
    return {
        "signals": [],
        "category": category,
        "downgrade": {
            "upstream": probe["upstream"],
            "candidate": f"{sym.owner}.{sym.name}",
            "visibility": sym.visibility,
            "site": sym.where(),
            "confidence": probe["confidence"],
        },
    }


def classify(key: str, kind: str, impl: KeyImpl, eracore: EraCoreIndex | None = None) -> tuple[str, str, dict]:
    """返回 (档位, 一句话理由, 证据 dict)。"""
    override = KNOWN_OVERRIDES.get(key)

    if impl.class_name is None:
        return "C", "无法定位上游实现类（注册表未登记或类缺失）", {"signals": []}
    if not impl.body.strip():
        return "C", "实现体为空/未找到类体", {"signals": []}

    body, file, line = impl.body, impl.file, impl.line

    # ---- 硬墙：Float / 语言运行时，永不走探测降级 ----
    float_hits = _scan(body, file, line, FLOAT_SIGNALS)
    if float_hits:
        if "ReturnType = EraType.Float" in body or "EraType[] { EraType.Float" in body \
                or "ArgTypes = { ArgType.Float" in body or "ArgType.Float }" in body:
            return "C", f"Float 专用签名（{float_hits[0][0]}），era-core 无 Float 类型", {
                "signals": [f"{k} @ {v}" for k, v in float_hits], "category": "float-only"}
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

    # ---- 渲染硬墙（绘制原语 / 配置字段）：不探测 ----
    render_hard = _scan(body, file, line, RENDER_HARD_SIGNALS)
    if render_hard:
        return "C", f"依赖渲染/字体后端（{render_hard[0][0]}），era-core 无对应管线", {
            "signals": [f"{k} @ {v}" for k, v in render_hard], "category": "render"}

    # ---- 音频硬墙：不探测 ----
    audio_hits = _scan(body, file, line, AUDIO_SIGNALS)
    if audio_hits:
        return "C", f"依赖音频后端（{audio_hits[0][0]}），era-core 无对应实现", {
            "signals": [f"{k} @ {v}" for k, v in audio_hits], "category": "audio"}

    macro_hits = _scan(body, file, line, INPUT_MACRO_SIGNALS)
    if macro_hits:
        return "C", f"依赖输入宏/序列输入机制（{macro_hits[0][0]}），era-core 无对应注入路径", {
            "signals": [f"{k} @ {v}" for k, v in macro_hits], "category": "input-macro"}

    # ---- 图像层：指令骨架可搬（B）；函数需探测，空接缝不算能力 ----
    image_hits = _scan(body, file, line, IMAGE_LAYER_SIGNALS)
    if image_hits:
        if kind == DIRECTIVE:
            return "B", f"执行体依赖图像层管线（{image_hits[0][0]}）；参数骨架可搬，执行降级到既有接缝", {
                "signals": [f"{k} @ {v}" for k, v in image_hits], "category": "image-layer"}
        probe = probe_eracore(body, eracore)
        if probe:
            return "B", (
                f"图像层管线缺失，但 era-core 有候选符号 "
                f"{probe['candidate'].owner}.{probe['candidate'].name}"
                f"（{probe['confidence']}）——需人工确认等价性"
            ), _evidence(probe, "image-layer")
        return "C", f"函数实现依赖图像层管线（{image_hits[0][0]}），era-core 无可用后端", {
            "signals": [f"{k} @ {v}" for k, v in image_hits], "category": "image-layer"}

    # ---- 定时器接缝：探测优先（era-core 有 getRedrawTimer / setRedrawTimer）----
    timer_hits = _scan(body, file, line, TIMER_SIGNALS)
    if timer_hits:
        probe = probe_eracore(body, eracore)
        if probe:
            return "B", (
                f"渲染管线缺失，但 era-core 有定时器候选符号 "
                f"{probe['candidate'].owner}.{probe['candidate'].name}"
                f"（{probe['confidence']}）——需人工确认等价性"
            ), _evidence(probe, "timer")
        return "C", f"依赖渲染定时器（{timer_hits[0][0]}），era-core 无对应接缝", {
            "signals": [f"{k} @ {v}" for k, v in timer_hits], "category": "timer"}

    state_hits = _scan(body, file, line, STATE_ONLY_SIGNALS)
    if state_hits:
        return "B", f"仅保存状态（{state_hits[0][0]}）；era-core 有同名字段可承接，消费线未接", {
            "signals": [f"{k} @ {v}" for k, v in state_hits], "category": "state-only"}

    refl_hits = _scan(body, file, line, REFLECTION_SIGNALS)
    if refl_hits:
        return "B", f"含变量反射/自定义参数校验（{refl_hits[0][0]}），语义需人工核对", {
            "signals": [f"{k} @ {v}" for k, v in refl_hits], "category": "reflection"}

    base: tuple[str, str, dict] = ("A", "结构同构，可机械搬 + 编译修复", {"signals": []})

    # ---- KNOWN_OVERRIDES 兜底（人工裁决覆盖自动结果）----
    if override:
        grade, reason = override
        return grade, f"{reason}（已知边界表）", {"signals": [], "category": "known-override"}

    return base


# --verify 的自检期望：硬墙必须保持 C，已有等价能力的必须降为 B。
VERIFY_EXPECTATIONS = {
    "G_POLYGON_DRAW": "C",
    "SET_SKIA_QUALITY": "C",
    "BGMCONTROL": "C",
    "EVALF": "C",
    "SQL_EXECUTE_SCALAR_FLOAT": "C",
    "ARGLEN": "C",
    "GETANIMETIMER": "B",
    "EXIST_IN_CSV": "B",
    "GETCSVNOBYNAME": "B",
}


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description="fork 键分档器（A/B/C 静态判定 + era-core 符号探测）")
    parser.add_argument("--fork", required=True, help="fork id（dialects/<id>.json）")
    parser.add_argument("--key", help="只分析单个键")
    parser.add_argument("--json", action="store_true", help="输出 JSON")
    parser.add_argument("--quiet", action="store_true", help="只输出分类计数")
    parser.add_argument("--verify", action="store_true",
                        help="内置断言自检（硬墙保持 C / 等价能力降 B），失败返回非零")
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

    # era-core 符号索引：用于把「上游有信号但 era-core 已有等价能力」从 C 降为 B
    eracore = EraCoreIndex(layout.repo_root / "EraCore.Core")

    items = descriptor.get("additions") or []
    if args.key and not args.verify:
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
        grade, why, ev = classify(name, kind, impl, eracore)
        # override 键额外跑一次探测：用于「可清理」提示（即使结果一致）。
        override_probe = None
        if impl.body.strip():
            p = probe_eracore(impl.body, eracore)
            if p:
                s: EraSymbol = p["candidate"]
                override_probe = {
                    "upstream": p["upstream"],
                    "candidate": f"{s.owner}.{s.name}",
                    "symbol": s.name,
                    "site": s.where(),
                    "confidence": p["confidence"],
                }
        results.append({
            "key": name,
            "kind": kind,
            "grade": grade,
            "reason": why,
            "class": class_name,
            "site": f"{impl.file.name}:{impl.line}" if impl.file else None,
            "evidence": ev,
            "override_probe": override_probe,
        })

    counts = {"A": 0, "B": 0, "C": 0}
    for r in results:
        counts[r["grade"]] += 1

    # 冗余检测：override 声明了，但自动分类已能复现/取代它 ⇒ 提示可清理。
    redundant: list[str] = []
    by_key = {r["key"]: r for r in results}
    for key, (override_grade, _) in KNOWN_OVERRIDES.items():
        result = by_key.get(key)
        if not result:
            continue
        probe = result.get("override_probe")
        # 只有探测在**当前流程中真正生效**（非 override 兜底）才算可清理；
        # 走 A 分支的键（CSV 反查）虽然能探测到候选，但删 override 会退化，不能提示。
        if (probe and probe["symbol"] in KNOWN_PROBE_SYMBOLS
                and result["evidence"].get("category") != "known-override"):
            redundant.append(
                f"{key}: override={override_grade} 已被探测复现 "
                f"（{probe['confidence']} {probe['candidate']} @ {probe['site']}）→ 可核对清理"
            )
            continue
        if result["grade"] != override_grade:
            redundant.append(
                f"{key}: override={override_grade} 实际={result['grade']}"
                f"（{result['reason']}）→ 可清理"
            )

    if args.verify:
        failures: list[str] = []
        for key, expected in VERIFY_EXPECTATIONS.items():
            result = by_key.get(key)
            if result is None:
                failures.append(f"{key}: 不在 fork 清单中")
            elif result["grade"] != expected:
                failures.append(f"{key}: 期望 {expected} 实际 {result['grade']}（{result['reason']}）")

        if failures:
            print("verify FAILED:", file=sys.stderr)
            for line in failures:
                print(f"  - {line}", file=sys.stderr)
            if redundant:
                print("redundant overrides:", file=sys.stderr)
                for line in redundant:
                    print(f"  ~ {line}", file=sys.stderr)
            return 1

        if redundant:
            print("redundant overrides (可清理):")
            for line in redundant:
                print(f"  ~ {line}")
        print(f"verify OK: {len(VERIFY_EXPECTATIONS)} 条期望全部满足；"
              f"A={counts['A']} B={counts['B']} C={counts['C']} (n={len(results)})")
        return 0

    if args.json:
        print(json.dumps({"counts": counts, "total": len(results),
                          "redundant_overrides": redundant, "items": results},
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
            downgrade = r["evidence"].get("downgrade")
            if downgrade:
                print(f"      downgrade: {downgrade['confidence']} "
                      f"{downgrade['upstream']} → {downgrade['candidate']} "
                      f"({downgrade['visibility']}) @ {downgrade['site']}")
    print(f"\nTOTAL A={counts['A']} B={counts['B']} C={counts['C']} (n={len(results)})")
    if redundant:
        print("redundant overrides (可清理):")
        for line in redundant:
            print(f"  ~ {line}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
