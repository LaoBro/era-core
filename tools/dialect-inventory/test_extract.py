#!/usr/bin/env python3
"""extract.py 自测（实现票 26 验收 1/2）。

覆盖：
- 新增一个 fork 时不改任何工具代码（纯描述符驱动）；
- `additions` 由 extractor 生成并与描述符项比对（漂移 fail-fast）；
- 冻结清单 / hash 漂移 fail-fast；重复运行逐位确定；
- 缺 sourceRoot 前置条件即 fail-fast（ToolError → 退出码 2）。

运行：``python tools/dialect-inventory/test_extract.py``
"""

from __future__ import annotations

import contextlib
import io
import json
import sys
import tempfile
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))

import extract  # noqa: E402  (需先注入本目录到 sys.path)
from extract import Layout, ToolError  # noqa: E402

_FORK_REGISTRY = {
    "instructions": "Emuera/Runtime/Script/Statements/BuiltInFunctionCode.cs",
    "methods": "Emuera/Runtime/Script/Statements/Function/Creator.cs",
}


def _write_source(root: Path, directives, functions) -> None:
    """合成一份最小 Emuera 源码布局，供抽取器读取。"""
    statements = root / "Emuera" / "Runtime" / "Script" / "Statements"
    (statements / "Function").mkdir(parents=True, exist_ok=True)
    (statements / "BuiltInFunctionCode.cs").write_text(
        "enum FunctionCode\n{\n" + "".join(f"\t{d},\n" for d in directives) + "}\n",
        encoding="utf-8",
    )
    (statements / "Function" / "Creator.cs").write_text(
        "internal static class FunctionMethodCreator\n{\n\tstatic FunctionMethodCreator()\n\t{\n"
        "\t\tmethodList = new()\n\t\t{\n"
        + "".join(f'\t\t\t["{f}"] = new {f}Method(),\n' for f in functions)
        + "\t\t};\n\t}\n}\n",
        encoding="utf-8",
    )


class ExtractToolTests(unittest.TestCase):
    def setUp(self) -> None:
        # 工具本体刻意向 stdout / stderr 打日志；自测期静音，保持结果可读。
        self._stdout = contextlib.redirect_stdout(io.StringIO())
        self._stderr = contextlib.redirect_stderr(io.StringIO())
        self._stdout.__enter__()
        self._stderr.__enter__()

        self._tmp = tempfile.TemporaryDirectory()
        root = Path(self._tmp.name)
        self.layout = Layout(root, root / "dialects", root / "tools" / "dialect-inventory")

        base_src = root / "base_src"
        _write_source(base_src, ["__NULL__", "BASE_A", "BASE_B"], ["BASE_FN"])
        self.fork_src = root / "fork_src"
        _write_source(self.fork_src, ["__NULL__", "BASE_A", "BASE_B", "FORK_ONLY"], ["BASE_FN", "FORK_FN"])

        self._descriptor("era-core-baseline", {
            "id": "era-core-baseline",
            "sourceRoot": str(base_src),
            "registryFiles": _FORK_REGISTRY,
        })
        self._descriptor("fork", {
            "id": "fork",
            "sourceRoot": str(self.fork_src),
            "base": "era-core-baseline",
            "registryFiles": _FORK_REGISTRY,
            "additions": [],
        })

        self.assertEqual(extract.update_base(self.layout, {}), 0)
        self.assertEqual(extract.update_fork(self.layout, "fork", {}), 0)

    def tearDown(self) -> None:
        self._tmp.cleanup()
        self._stderr.__exit__(None, None, None)
        self._stdout.__exit__(None, None, None)

    def _descriptor(self, fork_id: str, data: dict) -> None:
        path = self.layout.descriptor(fork_id)
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(json.dumps(data, ensure_ascii=False), encoding="utf-8")

    def _read_descriptor(self, fork_id: str) -> dict:
        return json.loads(self.layout.descriptor(fork_id).read_text(encoding="utf-8"))

    # ---------- 正例 ----------

    def test_check_passes_after_update(self) -> None:
        self.assertEqual(extract.check_fork(self.layout, "fork", {}), 0)

    def test_additions_are_generated_from_delta(self) -> None:
        additions = {(a["name"], a["kind"]) for a in self._read_descriptor("fork")["additions"]}
        self.assertEqual({("FORK_ONLY", "directive"), ("FORK_FN", "function")}, additions)
        # owner 默认取描述符 id。
        self.assertTrue(all(a["owner"] == "fork" for a in self._read_descriptor("fork")["additions"]))

    def test_repeat_run_is_bit_identical(self) -> None:
        inventory_before = self.layout.inventory("fork").read_bytes()
        digest_before = self.layout.digest("fork").read_bytes()
        self.assertEqual(extract.update_fork(self.layout, "fork", {}), 0)
        self.assertEqual(inventory_before, self.layout.inventory("fork").read_bytes())
        self.assertEqual(digest_before, self.layout.digest("fork").read_bytes())

    def test_second_fork_needs_no_tool_change(self) -> None:
        # 新增 fork = 一份描述符 + 一次 --update，不动 extract.py 一行。
        second_src = Path(self._tmp.name) / "second_src"
        _write_source(second_src, ["__NULL__", "BASE_A", "SECOND_ONLY"], ["BASE_FN", "SECOND_FN"])
        self._descriptor("second", {
            "id": "second",
            "sourceRoot": str(second_src),
            "base": "era-core-baseline",
            "registryFiles": _FORK_REGISTRY,
            "additions": [],
        })
        self.assertEqual(extract.update_fork(self.layout, "second", {}), 0)
        self.assertEqual(extract.check_fork(self.layout, "second", {}), 0)
        additions = {(a["name"], a["kind"]) for a in self._read_descriptor("second")["additions"]}
        self.assertEqual({("SECOND_ONLY", "directive"), ("SECOND_FN", "function")}, additions)

    def test_source_root_supports_env_expansion(self) -> None:
        self._descriptor("envfork", {
            "id": "envfork",
            "sourceRoot": "$TEST_FORK_REF",
            "base": "era-core-baseline",
            "registryFiles": _FORK_REGISTRY,
            "additions": [],
        })
        self.assertEqual(extract.update_fork(self.layout, "envfork", {"TEST_FORK_REF": str(self.fork_src)}), 0)
        self.assertEqual(extract.check_fork(self.layout, "envfork", {"TEST_FORK_REF": str(self.fork_src)}), 0)

    # ---------- fail-fast ----------

    def test_missing_source_root_env_fails_fast(self) -> None:
        self._descriptor("nofork", {
            "id": "nofork",
            "sourceRoot": "$TEST_FORK_REF",
            "base": "era-core-baseline",
            "registryFiles": _FORK_REGISTRY,
            "additions": [],
        })
        with self.assertRaises(ToolError):
            extract.check_fork(self.layout, "nofork", {})

    def test_upstream_drift_is_red(self) -> None:
        # 上游新增一个键但未重新固化 → 清单漂移。
        _write_source(self.fork_src, ["__NULL__", "BASE_A", "BASE_B", "FORK_ONLY", "NEW_KEY"], ["BASE_FN", "FORK_FN"])
        self.assertEqual(extract.check_fork(self.layout, "fork", {}), 1)

    def test_additions_drift_is_red(self) -> None:
        descriptor = self._read_descriptor("fork")
        descriptor["additions"] = [a for a in descriptor["additions"] if a["name"] != "FORK_ONLY"]
        self._descriptor("fork", descriptor)
        self.assertEqual(extract.check_fork(self.layout, "fork", {}), 1)

    def test_missing_frozen_artifact_is_red(self) -> None:
        self.layout.inventory("fork").unlink()
        self.assertEqual(extract.check_fork(self.layout, "fork", {}), 1)


if __name__ == "__main__":
    unittest.main(verbosity=2)