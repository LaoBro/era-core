# 方言描述符（fork descriptor）

本目录为每个上游 Emuera fork 保存一份 JSON 描述符，供 `tools/dialect-inventory/extract.py` 做**上游增量抽取**。它不是运行期配置；运行期 profile 闭包在 `DialectRegistry` 中。

## 文件

| 文件 | 作用 |
|---|---|
| `era-core-baseline.json` | 所有 fork 共享的 base（v24 祖先）描述 |
| `skia.json` | Emuera Skia fork 的键面描述 |

## schema

```json
{
  "id": "skia",
  "displayName": "Emuera Skia",
  "sourceRoot": "$EMUERA_SKIA_REF",
  "base": "era-core-baseline",
  "registryFiles": {
    "instructions": "Emuera/Runtime/Script/Statements/BuiltInFunctionCode.cs",
    "methods": "Emuera/Runtime/Script/Statements/Function/Creator.cs",
    "argBuilders": null
  },
  "additions": [],
  "exclusions": [],
  "ownerNotes": "..."
}
```

| 字段 | 说明 |
|---|---|
| `sourceRoot` | 上游 checkout 路径；支持 `$ENV` 展开，缺失即 fail-fast |
| `base` | 统一为 `era-core-baseline`（共享祖先） |
| `registryFiles` | 上游指令/函数的登记文件，工具按此抽取键 |
| `additions` | fork-only 键及 `owner`；由 `--update` 维护 |
| `exclusions` | 明确排除的键，需在 `ownerNotes` 说明理由 |
| `ownerNotes` | 归属判据与范围外说明 |

`owner` 是**库存元数据**，用于跨 fork 冲突评审；运行期 `DialectModule.Owner` 默认取模块名（`Snake`/`Eratw`），两者不是同一字段。

## 用法

```bash
# 需要先指向上游 checkout
export EMUERA_SKIA_REF=/path/to/skia-checkout

python tools/dialect-inventory/extract.py --list-forks
python tools/dialect-inventory/extract.py --fork skia --check      # 漂移检查
python tools/dialect-inventory/extract.py --fork skia --update     # 确认后重写 additions/hash
python tools/dialect-inventory/extract.py --fork skia --print-delta
python tools/dialect-inventory/extract.py --update-base            # 重写 base 快照
```

退出码：`0` 一致；`1` 漂移；`2` 前置条件缺失（描述符 / sourceRoot / base）。

## 新增 fork

1. 复制一份描述符，改 `id` / `sourceRoot` / `registryFiles`；
2. 跑 `extract.py --fork <id> --update` 生成 `additions`；
3. 在 `DialectRegistry` 增加模块与 profile 闭包（保持由窄到宽的单调链）；
4. 生成 `surface.<profile>.txt`；
5. 在 `tests/games.json` 增加 fixture。

完整步骤见 [`docs/guides/dialect-porting.md`](../docs/guides/dialect-porting.md)。

## 当前范围

`skia.json` 列出的是 **fork 全量键面**，不等于 era-core 已移植的键。已移植 66 键（17 指令 + 49 函数）见 [`docs/reference/dialect-surface.md`](../docs/reference/dialect-surface.md)；渲染/性能层与未引用键明确不在当前范围。
