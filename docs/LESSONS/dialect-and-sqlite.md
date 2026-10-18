# 方言移植与 SQLite 教训

> 场景：为运行 eraTW 画蛇添足版，移植 Emuera Skia 的 P0 指令/函数，并引入 SQLite。
> 时间：2026-10-03 ～ 2026-10-04。相关指南：[方言移植](../guides/dialect-porting.md)、[运行 eraTW](../guides/running-eratw.md)。

## TL;DR

1. **profile 必须在 `GlobalStatic.OpenScope` 之后解析**——surface 构建依赖 ambient `Config`，提前 resolve 会读到错误配置。
2. **`methodList` 会以 METHOD 指令并入 `funcDic`**——登记为式中函数后，无括号命令形自动成立；不要再登记同名指令。
3. **键级 delta 捕获不到签名增量**——`EXISTVAR` 这类“键存在但参数变了”必须单独裁定，不能只看面快照。
4. **`.als` 加载器的静默异常会伪装成“游戏数据缺陷”**——746 条中文标识符告警的根因是 `string.Format` 参数数不匹配。
5. **同名 `@EVENTLOAD` 是合法的**——lazy 物化按“名+行号”双匹配，IR 索引键必须含 `LineNo`。
6. **SQLite 原生库的位置随发布形态变**——CLI 在 `runtimes/win-x64/native/`，MAUI 被压平到 `win-x64/`；只拷 exe 会 `DllNotFoundException`。
7. **no-op 必须显式登记**——参数解析成功不等于语义完成；不登记就是隐藏的兼容性债务。
8. **复用 server 时 profile 不能改**——agent 必须显式报不一致，不能静默用错误可见面加载游戏。

## 教训清单

| # | 主题 | 一句话 |
|---|---|---|
| 1 | profile 解析时机 | surface 构建依赖 ambient Config，必须先开 scope |
| 2 | 命令形来源 | methodList 并入 funcDic，函数自动获得无括号语句形 |
| 3 | 签名增量 | 面快照只比 name/kind，参数变化不可见 |
| 4 | `.als` 静默中断 | 一个 `FormatException` 可丢掉整张别名表 |
| 5 | 同名多定义 | 事件函数可重复定义；定位与索引都要带行号 |
| 6 | IR 索引 | `(File, Label)` 会后者覆盖前者 |
| 7 | SQLite 分发 | 原生库在 `runtimes/<rid>/native/`，拷贝分发要带树或平铺 |
| 8 | 语义状态 | no-op / 仅状态 / 未实现必须写进清单 |
| 9 | agent 复用 | profile 一致性守卫，禁止静默降级 |
| 10 | 面快照 | 只经环境变量重写，行尾/BOM 归一化 |

---

## 1. profile 解析时机：不能在 OpenScope 之前 Resolve

**场景**：产品路径只传 profile id，首次读取 `GlobalStatic.DialectProfile` 时才 `DialectRegistry.Resolve`。

**结果**：如果在 `OpenScope` 之前解析，`FunctionIdentifier` / `FunctionMethodCreator` 的静态构造器读到的 `Config.IgnoreCase` 等 ambient 值是错的，`funcDic` 比较器与键大小写行为可能不一致。

**原因**：surface 构建会触发注册表静态构造，而注册表静态构造依赖 ambient `Config`；`GlobalStatic.OpenScope` 才绑定当前 `ConfigData`。

**解决**：`OpenScope(configData, string? dialectProfileId)` 只登记 id；`DialectProfile` 属性首次访问时才在 scope 内 `Resolve`。未知 id 由启动期 `DialectRegistry.IsKnownProfile` fail-fast，不静默回落。

**教训**：**“按 id 延迟解析”不是性能优化，是正确性要求**。测试里也一样：先开 scope，再 `Resolve`。

---

## 2. 命令形来自 methodList 并入 funcDic

**场景**：eraTW 的 `SQL_CONNECT "QOL_DISH_DB", "Data Source=plugins/qol_data.db"` 没有括号。

**结果**：只把 `SQL_CONNECT` 登记为式中函数后，该语句行也能解析，`@SYSTEM_TITLE` 不再 fatal。

**原因**：`FunctionIdentifier` 静态构造器在 `AddInstructionDescriptors` 之后遍历 `methodList`，把每个函数以 `METHOD_Instruction` 形式补进 `funcDic`。

**解决**：SQL 22 键全部只登记 `FunctionMethodDescriptor`，不再登记同名指令。

**教训**：**同名重复登记会撞 `funcDic.Add`**；命令形是既有机制的自然结果，不要额外造一条指令。

---

## 3. 键级 delta 捕获不到签名增量

**场景**：`EXISTVAR(VAR_STR,1)` 报“需要 1 个参数，但传入了 2 个”。

**结果**：`EXISTVAR` 在 baseline 和 Skia 都存在，面快照（`name<TAB>kind`）无差异，但 Skia 已把签名扩为 `(String, Int=0)`、`mode≠0` 时把字符串当表达式解析。

**原因**：方言层是**键可见面**模型，只表达“这个键对当前 profile 是否可见”，不表达“同一个键的参数/返回/副作用差异”。

**解决**：按“向后兼容则并入 baseline”处理——只新增可选尾参、旧调用逐位不变、键可见面不变，因此不加 dialect 标签，面快照无 diff；`v24pure` 也获得 `mode≠0`，对既有脚本不可观测。

**教训**：**每次移植都要问“这是新键还是签名增量”**。不兼容的签名变化才需要把投影扩展为“同名多 variant 择一”，不要为单个键造第二套解析器。

---

## 4. `.als` 加载器的静默异常会伪装成游戏数据缺陷

**场景**：eraTW 首次以 `--profile eratw` 加载，出现 746 条 `warnLevel=2` 中文标识符告警。

**结果**：最初怀疑是游戏数据缺陷；同游戏在 Skia 下零错误。最终定位为 era-core `.als` 别名加载器在**重复索引**处 `string.Format` 参数数不匹配抛 `FormatException`，异常被吞掉，整个 `.als` 加载静默中断。

**原因**：画蛇添足版的 `.als` 大量“多名一索引”；加载器没有区分“重复项”与“格式错误”，一个异常终止整张表。

**解决**：修 baseline 加载器；修复后告警 746 → 56，剩余才是真正的方言缺口。

**教训**：**“大量同款告警”先怀疑加载器，不要先怀疑游戏数据**。静默 catch 会把配置/格式错误伪装成内容问题。

---

## 5. 同名 `@EVENTLOAD` 是合法的，定位必须带行号

**场景**：`SYSTEM.ERB` 同一文件二次定义 `@EVENTLOAD`（事件函数合法叠加），lazy 物化后 19 条 `#DIM DYNAMIC` 告警。

**结果**：物化命中了错误的 body。

**原因**：lazy 物化只按 `@名` 定位，同名多定义时命中第一个/错误的一个。

**解决**：源文件流改为“名 + 行号”双匹配；`ErbMaterializer` 用 `stub.Position.LineNo` 定位。

**教训**：**只要语言允许同名定义，任何按 name 建的索引都不够**。

---

## 6. IR 索引 `(File, Label)` 会后者覆盖前者

**场景**：修完 §5 后，IR 命中轮仍只看到一个 `@EVENTLOAD`。

**原因**：`ErbIrCache` 以 `(File, Label)` 为键，同名后者覆盖前者。

**解决**：键升级为 `(File, Label, LineNo)`；物化/签名查询按 `stub.Position.LineNo` 查，并修正 IR 桩 `Position` 行号 +1 的偏差。

**教训**：**缓存键要覆盖所有“合法重复”的维度**；行号不是装饰，是身份的一部分。

---

## 7. SQLite 原生库的位置随发布形态变

**场景**：CLI 启动即 `DllNotFoundException: e_sqlite3`；MAUI Windows 正常。

**结果**：

- CLI 框架依赖输出：`bin/<Config>/net10.0/runtimes/win-x64/native/e_sqlite3.dll`；
- MAUI Windows RID 输出：`bin/<Config>/net10.0-windows10.0.19041.0/win-x64/e_sqlite3.dll`（已压平）；
- 只拷 `EraCore.Cli.exe` + 托管 dll 到别处 → 原生库丢失。

**原因**：`Microsoft.Data.Sqlite` → `SQLitePCLRaw.bundle_e_sqlite3` 以 RID 资产形式携带原生库；框架依赖输出保留 `runtimes/` 结构。

**解决**：分发时带 `runtimes/` 整树，或把 `runtimes/win-x64/native/e_sqlite3.dll` 平铺到 exe 同目录。Android 尚未接入 `SqliteRuntime` 式定位，不在桌面闭环内。

**教训**：**“能 build 能 run”不等于“能分发”**；原生依赖要单独写进发布 checklist。

---

## 8. no-op 必须显式登记

**场景**：`SETIMAGELAYER*` / `CLEARIMAGELAYER*` 参数完整解析、`ColorMatrix` 5×5 校验通过，但控制台接缝为空实现；`TEXT_BGC_ON/OFF` 只保存状态，渲染线不消费。

**结果**：脚本不报错，但观感不追平。如果只在 commit message 里写“已移植”，后续会被误认为完全兼容。

**解决**：在 [`dialect-surface.md`](../reference/dialect-surface.md) 标注“语义接受（no-op）”“仅状态”；在测试里锁定参数校验，不锁定观感。

**教训**：**兼容性有三层：解析、语义、观感**。只完成前两层时必须写明。

---

## 9. agent 复用 server 时 profile 不能改

**场景**：agent `start --profile eratw`，但本地已有以 `v24pure` 启动的 server。

**结果**：如果不校验，会静默复用错误可见面，游戏加载失败且难以归因。

**解决**：`eracore_agent` 记录 server 的 profile；请求与记录不一致时直接报错，要求先 `stop` 再以目标 profile 重新 `start`；未知记录也报错。

**教训**：**会话级配置不能跨进程“猜”**；复用边界要 fail-fast。

---

## 10. 面快照只经环境变量重写

**场景**：新增方言键后 `DialectSurfaceSnapshotTests` 变红。

**解决**：

```powershell
$env:ERACORE_DIALECT_SURFACE_UPDATE=1
dotnet test EraCore.Tests/EraCore.Tests.csproj --filter FullyQualifiedName~DialectSurfaceSnapshotTests
Remove-Item Env:ERACORE_DIALECT_SURFACE_UPDATE
dotnet test EraCore.Tests/EraCore.Tests.csproj --filter FullyQualifiedName~DialectSurfaceSnapshotTests
```

**教训**：快照是**评审产物**，不是生成物。不要手改；不要在没有确认 diff 的情况下批量重写。比对已归一化 CRLF/BOM，但文件本身应保持 UTF-8 / LF。
