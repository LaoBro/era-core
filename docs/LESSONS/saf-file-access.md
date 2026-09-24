# Android SAF 文件访问 失败教训

> **TL;DR**：SAF content:// URI 不是文件系统路径——凡是"文件名/相对路径/扩展名/前缀过滤/路径拼接"都走 documentId 语义（SafPath），禁止 `Path.GetFileName*`、`File.Exists`、字符串拼接；目录/文档 ID 提取口径全库统一；缓存/预取/失效与性能（IPC N+1）一起设计；写路径必须走 DirAccessor + 缓冲；**真实路径 accessor 必须镜像 SAF 的通配语义**（大小写不敏感、按条目名）——托管 glob 在 Android 上大小写敏感，「切模式」= 换语义；**精确名读取同样须两端一致**——「今天能工作」可能只是挂载（FUSE）碰巧不敏感，换挂载即静默全丢；**改 accessor 语义后必须审计所有直连 `File.*`/`Directory.*` 的调用点**——绕过抽象的那部分不在修复范围内。

## 教训清单

| # | 标题 | 一句话 | 日期 |
|---|---|---|---|
| 1 | BuildChildDocumentsUriUsingTree 第一个参数必须是原始树 URI | 子文档 URI 会丢路径；缓存 `_treeAndroidUri` | — |
| 2 | DirAccessor 必须按路径选择 | content:// 用 SAF，本地路径用 FileSystem，不能全局替换 | — |
| 3 | Preload 编码检测必须字节级 | `ReadAllBytes` → BOM/UTF-8/SJIS 级联，不能 `ReadAllText`+Split | — |
| 4 | GetDocumentId vs GetTreeDocumentId 不能混用 | 统一 `ResolveDocId`（/document/ 后整段 + Unescape） | — |
| 5 | CombinePath 不能字符串拼接 content URI | 用 `BuildDocumentUriUsingTree(treeUri, docId + "/" + filename)` | — |
| 6 | EraStreamReader.Open 必须走 DirAccessor | 中心入口层切换，不在调用方逐个修补 | — |
| 7 | ResolveSubPath 必须大小写不敏感 | Android ext4 敏感；`OrdinalIgnoreCase` | — |
| 8 | %2F 编码导致 StartsWith 前缀失效 | SAF 分隔符是 `%2F` 不是 `/`；相对路径走 documentId 语义 | — |
| 9 | 禁止对 content URI 用 Path.GetFileName* | 逻辑文件名/相对路径必须经 SafPath | — |
| 10 | emuera.config 不能用 File.Exists | 字符串拼接 + `File.Exists(content://)` 恒 false | — |
| 11 | CombinePath 必须用 ResolveDocId | 同 accessor 内 docId 提取口径要统一 | — |
| 12 | FileStream 存档在 SAF 上必炸 | 初始化只读 ≠ 运行时可写；验收到自动存档 | — |
| 13 | 每次写读 Preferences 慢 100× | 静态配置缓存，热路径禁止反复读偏好 | 2026-07-31 |
| 14 | 存档卡死根因链 | 慢存档 + 无限循环检测 + StepAsync 误处理四环 | 2026-07-31 |
| 15 | 跨进程 IPC 被当 syscall 用 | SAF 慢 = N+1 + 无缓存；先取证量化再优化 | 2026-08-03 |
| 16 | 缓存 key 必须统一由父文档 URI 推导 | EnumerateUri 与 FindChildDocument 的 key 一致 | 2026-08-03 |
| 17 | 缓存失效方向陷阱 | 建目录失效"目录自身"，删文件失效"父目录" | 2026-08-04 |
| 18 | 精确失效优先于 TTL | 写/删收敛入口拿失效信号；TTL 兜底进程外 | 2026-08-03 |
| 19 | 后台预取时机不能早于游戏加载完成 | sav 目录由游戏逻辑创建；预取窗口在标题停留期 | 2026-08-04 |
| 20 | Task.Run 后台线程丢 AsyncLocal scope | 进 Task.Run 前捕获字符串/实例值 | 2026-08-04 |
| 21 | 覆盖写不失效父目录 | 只有新建文档才失效；过度失效抵消预取 | 2026-08-04 |
| 22 | **ImageNameTable sprite 名解析对 content:// 失效（2026-08-08）** | `Path.GetRelativePath` 对 content:// 无效；改 documentId 前缀比较 | 2026-08-08 |
| 23 | **枚举输出作字典键必须 OrdinalIgnoreCase——displayName 是磁盘真实大小写（2026-09-09）** | 引擎固定名请求恒大写 `GAMEBASE.CSV`，provider 返回 `GameBase.csv`；懒读被 SAF 大小写不敏感解析掩盖，精确字典 miss | 2026-09-09 |
| 24 | **真实路径 accessor 必须镜像 SAF 通配语义（2026-09-12）** | 托管 glob 在 Android 大小写敏感，`CHARA*.CSV` 匹配不到 `Chara1 霊夢.csv`；别把 searchPattern 透传给 Directory.* | 2026-09-12 |
| 25 | **修好 accessor 语义 ≠ 修好所有枚举（2026-09-12）** | 绕过 accessor 的直连 `Directory.GetFiles` 不在上一轮修复范围内；改抽象实现后必须审计直连调用点 | 2026-09-12 |
| 26 | **「今天能工作」可能只是挂载碰巧不敏感（2026-09-12）** | 精确名读取（GAMEBASE.CSV vs 磁盘 Abl.csv）依赖 FUSE 容错；换挂载即静默全丢，且缺失是 early-return、错误在别处显形 | 2026-09-12 |

---

## 1. SAF——`BuildChildDocumentsUriUsingTree` 第一个参数必须是原始树 URI

**场景**：`SafGameDirAccessor` 中把任意传入的路径（可能是子文档 URI）当树 URI 传给 `DocumentsContract.BuildChildDocumentsUriUsingTree(firstParam, parentDocId)`。

**结果**：子目录导航失败，`ResolveSubPath` 退化为 `basePath`（返原值），`Validate()` 校验失败。

**原因**：`BuildChildDocumentsUriUsingTree` 的第一个参数**必须是 `ACTION_OPEN_DOCUMENT_TREE` 返回的原始树 URI**（如 `content://.../tree/primary%3Aemuera`），不能是子文档 URI（如 `content://.../tree/primary%3Aemuera/document/primary%3Aemuera%2Fcsv`）。

**解决**：缓存 `_treeAndroidUri` 字段（原始树 URI），所有 `BuildChildDocumentsUriUsingTree` 调用统一用它做第一个参数，第二个参数从传入路径提取文档 ID。

## 2. SAF——`DirAccessor` 必须按路径选择，不能全局替换

**场景**：`MainActivity.OnCreate` 无条件将 `GamePaths.Current.DirAccessor` 替换为 `SafGameDirAccessor`。

**结果**：内置 `test_game`（本地文件路径）走 `SafGameDirAccessor` → 所有目录存在性检查返回 false → `Preload.Load` 认为目录不存在 → 文件逐 I/O 慢路径 → 极其缓慢 + GAMEBASE.CSV 被跳过 → 空壳游戏。

**解决**：`OnReloadGame` 和 `ScanAndPushGames` 等所有使用 DirAccessor 的地方**按路径判断**：
```csharp
var dirAccessor = path.StartsWith("content://")
    ? (IGameDirAccessor?)SafGameDirAccessor.Instance
    : new FileSystemGameDirAccessor();
```
`SafGameDirAccessor` 只用于 SAF content URI 路径，本地文件路径始终走 `FileSystemGameDirAccessor`。

**教训**：DirAccessor 的选择是**路径属性**，不是**全局属性**。一个应用中可能同时存在本地测试游戏和 SAF 外部游戏，不能全局替换。

## 3. `Preload` 文件编码检测——必须用字节级 BOM 检测，不能直接 `ReadAllText` + `Split`

**场景**：`Preload.ReadAllLinesViaAccessor` 初版用 `dirAccessor.ReadAllText(path)` + `Split('\n')`。

**结果**：ERA 游戏的 ERB 文件通常是 SHIFT-JIS 编码。UTF-8 `StreamReader`（默认）解码 SHIFT-JIS 字节流时，`\r`/`\n` 字节可能被当作多字节日文字符的一部分被"吞掉"，换行符丢失，两行合成一行，解析器报错"无法解析的行"。

**解决**：加 `ReadAllBytes` 到 `IGameDirAccessor`，`Preload` 调用 `dirAccessor.ReadAllBytes` 获取原始字节数组 → 传入 `EncodingHandler.ReadAllLinesFromBytes`（BOM 检测 → UTF-8 尝试 → SHIFT-JIS 回退 → `StringReader.ReadLine` 分行）。

**教训**：文件读取必须保留原始字节级别的编码检测路径。`ReadAllText` 预设单一编码不够，必须以字节数组为中介，复用原有的 `BOM → UTF-8 → SHIFT-JIS` 级联检测。

## 4. SAF——`GetDocumentId` vs `GetTreeDocumentId` 不能混用

**场景**：在 `SafGameDirAccessor` 中统一用 `DocumentsContract.GetDocumentId(docUri)` 提取文档 ID。

**结果**：
- 对树 URI（`.../tree/...`）抛异常——`GetDocumentId` 只对文档 URI 有效
- 对含 `%2F`（编码的 `/`）的文档 URI（`.../document/...%2Ftest_game`），只返回第一段（如 `...:emuera`，丢掉了 `/test_game`）
- `DirectoryExists` 检查了错误的目录但返回 True（因为根目录存在）→ 假阳性

**解决**：
```csharp
private static string ResolveDocId(Android.Net.Uri docUri)
{
    var uriStr = docUri.ToString();
    // 文档 URI（.../document/...）：手动从路径提取完整文档 ID
    var posDoc = uriStr.LastIndexOf("/document/");
    if (posDoc >= 0)
        return Uri.UnescapeDataString(uriStr[(posDoc + 10)..]);
    // 树 URI（.../tree/...）：用标准的 GetTreeDocumentId
    if (uriStr.Contains("/tree/"))
        return DocumentsContract.GetTreeDocumentId(docUri);
    return DocumentsContract.GetDocumentId(docUri);
}
```

**教训**：
- `GetDocumentId` 只适用于文档 URI，对树 URI 无效
- `GetTreeDocumentId` 对文档 URI 返回树根的文档 ID（不是你想要的子文档 ID）
- 含 `%2F` 的文档 URI 不能依赖 `GetDocumentId`——它只返回编码 `%2F` 之前的部分
- 路径中同时含 `/tree/` 和 `/document/` 的 URI（SAF 常见格式），优先检查 `/document/`

## 5. SAF——`CombinePath` 不能字符串拼接 content URI

**场景**：`SafGameDirAccessor.CombinePath` 实现为 `$"{basePath.TrimEnd('/')}/{filename}"`。

**结果**：`content://.../document/...:emuera/test_game/csv/ABL.CSV` → Android 解析时忽略 `/ABL.CSV`（因为文档 URI 只识别 `/document/` 后的第一个路径段），实际打开的是 csv 目录本身 → `OpenInputStream` 抛 `EISDIR (Is a directory)`。

**解决**：用 `DocumentsContract.BuildDocumentUriUsingTree(treeUri, docId + "/" + filename)`：
```csharp
var baseUri = Android.Net.Uri.Parse(basePath);
var docId = ResolveDocId(baseUri);
var childUri = DocumentsContract.BuildDocumentUriUsingTree(_treeAndroidUri!, $"{docId}/{filename}");
return childUri.ToString();
```

**教训**：SAF content URI 不是文件系统路径——不能用字符串拼接构造子文件路径。必须用 `BuildDocumentUriUsingTree` 把父文档 ID 和文件名组合成完整子文档 ID 再构造 URI。

## 6. `EraStreamReader.Open` 必须走 DirAccessor，不能直接 `File.ReadAllBytes`

**场景**：`EraStreamReader.Open(path)` → `EncodingHandler.ReadAllLinesWithDetection(filepath)` → `File.ReadAllBytes(path)`。对 SAF content URI 路径，`File.ReadAllBytes` 阻塞或失败。

**解决**：让 `Open` 委托给 `OpenOnCache`（缓存未命中时走 `DirAccessor.ReadAllBytes`→`EncodingHandler.ReadAllLinesFromBytes`），从内容源级别统一所有文件读取路径：
```csharp
public bool Open(string path, string name)
{
    return OpenOnCache(path, name);
}
public bool OpenOnCache(string path, string name)
{
    filepath = path; filename = name;
    curNo = 0; nextNo = 0;
    // 先查 Preload 缓存
    var cached = Preload.TryGetFileLines(path);
    if (cached != null) { _fileLines = cached; return true; }
    // 缓存未命中：走 DirAccessor（SAF content URI）
    var dirAccessor = GamePaths.Current?.DirAccessor;
    if (dirAccessor != null)
    {
        var bytes = dirAccessor.ReadAllBytes(path);
        if (bytes != null)
        { _fileLines = EncodingHandler.ReadAllLinesFromBytes(bytes); return true; }
    }
    // 最后 fallback 原始 File.ReadAllBytes
    try { _fileLines = EncodingHandler.ReadAllLinesWithDetection(filepath); return true; }
    catch { Dispose(); return false; }
}
```

**教训**：`EncodingHandler.ReadAllLinesWithDetection(path)` 是原始 I/O 漏斗——任何地方传入 content URI 都会阻塞。`EraStreamReader` 是 ERA 引擎所有文件读取的中心入口，必须在**这一层**切换到 DirAccessor，而不是在调用方逐个修补。

## 7. SAF `ResolveSubPath` 必须大小写不敏感（Android ext4）

**场景**：`ResolveSubPath(gamePath, "csv")` 用 `string.Equals(name, subDir, StringComparison.Ordinal)` 进行大小写敏感匹配。

**结果**：真实游戏目录名是 `CSV`（大写），`ResolveSubPath` 返回 fallback `gamePath`，`GamePaths.CsvDir` 被设为游戏根目录而非 `.../CSV` → 所有 CSV 文件查不到 → 空壳游戏。

**修复**：改用 `OrdinalIgnoreCase`：
```csharp
if (string.Equals(name, subDir, StringComparison.OrdinalIgnoreCase))
```

**教训**：Android 文件系统（ext4/f2fs）是大小写敏感的，ERA 游戏目录命名约定不统一——有的用 `CSV/ERB`（大写），有的用 `csv/erb`（小写）。SAF 路径下的子目录查找必须大小写不敏感。

## 8. SAF URI——URL 编码 `%2F` 导致 `StartsWith` 前缀匹配失效

**场景**：`GetErbFilesFromCache` 用 `dirPath.TrimEnd('/') + "/"` 构造前缀，再用 `cacheKey.StartsWith(prefix)` 过滤 Preload 缓存中的文件。

**结果**：永远匹配到 0 个文件。`ResolveSubPath` 返回的 URI 中 `/` 编码为 `%2F`（如 `...%2FERB`），而构造的前缀用字面 `/`（`...%2FERB/`）。缓存键中的路径分隔符也编码为 `%2F`（如 `...%2FERB%2Ffile.ERB`），所以 `StartsWith("...%2FERB/")` 永远不匹配 `"...%2FERB%2Ffile.ERB"`。

**解决（初版，已过时）**：不依赖 raw URI 前缀匹配，改用扩展名过滤缓存键。  
**后续修正**：`Path.GetFileName(contentUri)` 会得到**整段 documentId**，不能当相对路径 Key（见下文「逻辑文件名 / SafPath」）。正确做法是 Unescape documentId 后做前缀比较与相对路径：
```csharp
// SafPath.IsUnderRoot(dir, k) + SafPath.GetRelativePathFromRoot(dir, k)
var erbFiles = Preload.GetAllCachedKeys()
    .Where(k => k.EndsWith(".ERB", StringComparison.OrdinalIgnoreCase)
        && SafPath.IsUnderRoot(dirPath, k))
    .Select(k => new KeyValuePair<string, string>(
        SafPath.GetRelativePathFromRoot(dirPath, k), k))
    .ToList();
```

**教训**：SAF URI 中的路径分隔符是 `%2F`（URL 编码的 `/`），不是字面 `/`。不要用 `TrimEnd('/')` + `"/"` 构造 SAF URI 的目录前缀，更不要用 `StartsWith` 做目录范围过滤。扩展名过滤只能解决「找得到文件」，**相对路径 Key 仍须走 documentId 语义**。

## 9. SAF——禁止对 content URI 使用 `Path.GetFileName*`（逻辑文件名 / SafPath）

**场景（2026-07）**：TK 在 SAF 下加载后，警告路径是整段 documentId；`PrepareERDFileNames` 用 `Path.GetFileNameWithoutExtension(path)` 作 EE_ERD 键。

**结果**：
- Win：`...\DVAR.erd` → key=`DVAR`
- SAF：`content://.../document/...%2FDVAR.erd` → key≈**整段 URL-encoded documentId**
- `erdFileNames.TryGetValue("DVAR")` 永远 miss；警告文件名不可读

实测（.NET）：
```text
Path.GetFileName(contentUri)           → 整段 document 段
Path.GetFileNameWithoutExtension(...)  → 去掉 .erd 后的整段 documentId
Path.GetExtension(contentUri)          → 往往仍是 .erd（末尾扩展名碰巧可用，不可依赖）
```

**解决**：Core 侧 `SafPath`：
1. 取 `/document/` 之后整段并 `Uri.UnescapeDataString` 得到 documentId  
2. 按 `/` 取最后一段 → 逻辑短名（`DVAR.erd`）  
3. `GetRelativePathFromRoot` 用 documentId 前缀差 → `SYSTEM\...\a.ERB`（对齐 `Config.getFiles`）  
4. **禁止**再对完整 content URI 调用 `Path.GetFileName*` 当游戏逻辑文件名

`SafGameDirAccessor.GetFileName` / `Config.getFiles` / `EraStreamReader` 默认名 / Preload 扩展名过滤一律走逻辑短名。

**教训**：SAF 的路径分隔符活在 documentId 的 `%2F` 里，不在 URI path 的 `/` 上。`System.IO.Path` 按最后一个 `/` 切片，对 document URI 永远错。凡「文件名 / 相对路径 / 扩展名 / ERD 键」都要先解出逻辑路径。

## 10. SAF——`emuera.config` 不能用 `File.Exists` / 字符串拼接

**场景**：`ConfigData` 用 `ExeDir + "emuera.config"` 与 `File.Exists(confPath)` 加载配置。

**结果（Android SAF + TK）**：
1. 字符串拼接把 `emuera.config` 粘进 documentId（缺 `%2F` 分隔）或路径对 `File.*` 无效  
2. `File.Exists(content://...)` **恒 false** → 配置从未读入  
3. `SystemSaveInBinary` 保持默认 **false**  
4. 全部 `#DIM ... SAVEDATA CHARADATA` 抛 CodeEE（需二进制存档）→ `LoadHeaderFiles noError=False` → `soft-return reason=ErhLoadHeaderFilesFalse`（加载「提前失败、输出变短」）

Windows 同游戏正常，是因为本地 `File.Exists` 能读到 `emuera.config`。

**解决**：
- 路径：`DirAccessor.CombinePath(exeDir, "emuera.config")`（内部 `ResolveDocId`，见 CombinePath 课）  
- 存在性：`SafCompat.FileExists` / DirAccessor，禁止对 content URI 用 `File.Exists`  
- 缺失时不要在 content URI 上 `SaveConfig()`（写路径未支持）  
- 验收日志：`[Config] LoadConfig: main=True, SystemSaveInBinary=True`（以**游戏目录那一次**为准；启动时内置 `files/emuera` 可能先 Load 一次且 Binary=False）

**教训**：配置加载与脚本加载是同一 SAF 语义问题。默认配置「看起来能跑」会在真正解析大游戏 `#DIM SAVEDATA` 时集中爆雷。桌面能过 ≠ Android 读到了同一份 config。

## 11. `CombinePath` 必须用 `ResolveDocId`，不能用 `GetDocumentId`

**场景**：`SafGameDirAccessor.CombinePath` 一度写 `DocumentsContract.GetDocumentId(baseUri)`。

**结果**：含 `%2F` 的子目录 document URI 只得到第一段 ID，拼出的子文件 URI 落在错误层级（与「GetDocumentId vs GetTreeDocumentId」课同一根因）。读 `emuera.config`、拼 sav 路径都会错。

**解决**：与枚举/DirectoryExists 一致，一律 `ResolveDocId`（手动取 `/document/` 后整段并 Unescape）。

**教训**：同一 accessor 里「有的 API 用 ResolveDocId、有的用 GetDocumentId」是隐性回归源。路径构造入口应只保留一种 documentId 提取方式。

## 12. 加载通了不等于可玩——`FileStream` 存档在 SAF 上必炸

**场景（2026-07-30）**：TK 已进标题、开局选项正常；点「接受」触发 `@SYSTEM_AUTOSAVE`：
```
保存全局数据时发生错误 / SAVEDATA命令在存档时发生了意料之外的错误
```

**原因**：`VariableEvaluator.SaveGlobal` / `SaveTo` 仍：
```csharp
Config.CreateSavDir(); // Directory.CreateDirectory(content://…) 无效
new FileStream(SavDir + "global.sav", FileMode.Create, FileAccess.Write); // 打不开 content URI
```
`SavDir` 派生自 `Program.ExeDir`（SAF 下为 content URI）。选目录权限目前只有 `GrantReadUriPermission`，即便走 DocumentsContract 写也未接通。

**状态**：已知未完成项（方案 A：App 私有目录重定向 SavDir；方案 B：完整 SAF 写 API）。  
加载修复 **不会** 自动修好存档。

**教训**：
- 初始化只读路径与运行时写路径是两层；只测到标题会漏掉自动存档  
- `SafCompat` 对写「静默跳过」与 `SaveGlobal` catch 转 CodeEE 表现不同——用户看到的是硬错误  
- 验收清单应含：标题 → 开局接受/自动存档 → 继续遊戲读档

## 13. 每次写入读 Preferences 会让存档慢 100×（热路径别碰偏好存储）

**场景（2026-07-31）**：`DeferredSafWriteStream` 加 64MB 上限检查时，`DeferredWriteLimitBytes` 属性**每次访问都执行 `Preferences.Get`**，而 `EnsureWriteWithinLimit` 在每次 `WriteByte`/`Write` 上调用。

**结果**：478KB 未压缩存档从 ~50ms 变成 **4.9s**（几十万次 WriteByte × 每次 SharedPreferences 查询）。logcat 时间线证据：`defer buffer →`（打开流）到 `flush OK`（写出）间隔 4.9s，而 `flush OK ... ms=5` 说明 ContentResolver 实际写出只要 5ms——4.9s 全在**写内存缓冲**阶段，唯一非纯内存操作就是每次写入读偏好。

**原因**：`Preferences.Get`（Android SharedPreferences）虽有进程内缓存，但每次仍有 AppContext 解析 + 方法调用开销；放在逐字节热路径 = 存档字节数 × 单次开销。

**解决**：首次访问读一次进 static 字段，属性只返回缓存。安全上限 / 失败清理 / 权限逻辑原样保留。

**教训**：
- 静态配置读取值必须缓存，禁止在逐元素/逐字节热路径上反复读偏好
- 意图正确的健壮性提交也可能带性能 bug——保留其安全功能，只修性能实现
- 判定"慢在哪"要拆时间线：`defer buffer →` 与 `flush OK` 之间是写缓冲（CPU/每写开销），`flush OK ... ms=` 才是 ContentResolver 实际写

## 14. 存档卡死根因链——慢存档撞上无限循环检测，再被当致命错误吞掉

**场景（2026-07-31）**：点「接受」触发自动存档后游戏冻结，前端停在 `state=Running` + "游戏运行中…"，画面不变；Debug 帧 error=`"script requested exit (QUIT/EXIT)"`；logcat 末尾 `game loop completed normally`。

**原因链**（四环）：
1. **慢存档**（见上条）4.9s
2. **无限循环检测误报**：`checkInfiniteLoop` 每 10000 行检查 `checkInfiniteLoopStopwatch >= InfiniteLoopAlertTime`（默认 5000ms），慢存档让累计脚本运行时间超时，HEADLESS 分支直接 `throw GameExitException`
3. **StepAsync catch-all 吞掉**：`catch (Exception ex)` 把 `GameExitException` 当致命错误，构造 `state: console.State.ToString()`（脚本中断时仍是 "Running"）+ error 文本的回合并 `Stop()`
4. **前端假死**：state=Running + diff=null → 画面不变、无可见错误

**与既往提交的关系**：80ed962 修的是"存档写失败"（FileStream 打不开 content URI）；**620c1f6 引入"存档写太慢"**。同一可见症状（点按钮→冻结）、两批不同根因——80ed962 之后的"新冻结"由 620c1f6 引起，80ed962 本身不是问题。

**教训**：
- 「点按钮就冻结」先拿 Debug 帧的 `error` 字段定性——它是卡死是"什么异常"的第一手证据
- `game loop completed normally` 不代表一切正常——RunLoopAsync 正常返回可能是协议 Stop / 通道被关
- 修复"存档失败"后必须回归测"存档成功后脚本继续"的路径——修复会「揭开」下一层（此处暴露 QUIT 误处理）

## 15. 跨进程 IPC 被当 syscall 用——SAF 慢的根因是 N+1 + 无缓存（2026-08-03）

**场景**：Android 存档界面打开 5s+，uemuera（本地文件系统）对照瞬时。
**根因**：SAF 每次 `ContentResolver.Query`/`OpenInputStream` 是跨进程 Binder IPC（单次 50~150ms 常见）。存档界面每槽位 4 次 IPC（TryQueryDocument×2 + GetMimeType + OpenRead），几百槽 ≈ 2~3s；启动加载 ~8000 次 IPC 累计 165s。问题在「把 IPC 当 syscall 用」（N+1 + 无缓存），不在架构——ADR-0019 SAF 迁移不推翻。
**取证方法（A1）**：给全部 ContentResolver 包装点加耗时日志（`[saf] op path detail ms`，先 `sw.Stop()` 再写，失败记 FAIL），聚合分析脚本统计「次数 × 单次耗时」+ 时间窗口 + N+1 检测，判定「单次慢（Provider 实现）」vs「次数多（调用模式）」——两者优化手段完全不同。
**解决**：O1 目录级子项缓存（一份枚举喂 GetFiles/GetDirectories/FileExists）+ O4 后台预取。
**教训**：性能优化前必须先取证量化「总 IPC 次数 × 单次耗时」区分两类根因；命中路径保持零 IPC、零分配、零日志（常态零成本）。优化后同样用日志前后对照验收（agent2/3/4.log）。

## 16. SAF 缓存 key 必须统一由父文档 URI 推导（2026-08-03）

**场景**：O1 缓存 key 用目录 docId。`EnumerateUri` 原实现 `ResolveDocId(childrenUri)` 对 `.../tree/.../document/<id>/children` 取 `/document/` 后整段，得到 `父id/children`（带后缀）；而 `FindChildDocument`/`ResolveSubPath` 用父文档 URI 得到真 `父id`——两个 key 不一致，缓存永不命中。
**解决**：key 一律由「父文档 URI 的 ResolveDocId」推导，EnumerateUri 改签名收父 docId（不再自推）。
**教训**：SAF URI 形态多（tree/document/children），docId 提取必须统一口径；缓存 key 的一致性要跨方法核对，不能只看单方法。

## 17. 缓存失效方向陷阱：建目录失效「目录自身」，删文件失效「父目录」（2026-08-04）

**场景**：`EnsureRealDirectoryUri` 创建新目录后，失效写成 `InvalidateParentOf(新目录)`（失效新目录的父），而新目录创建在其父下，应失效**父自身**——key 错位导致新建目录在缓存中持续不可见（FileExists 误报 false）。
**原因**：建目录（父的子项列表 +1 → 失效父自身）与删文件（父的子项列表 -1 → 失效父自身）失效点其实相同（都失效「子项列表被改动的那层」）；误用「失效新实体的父」方向就反了。
**教训**：写失效信号前先想清楚「谁的子项列表变了」，而不是「哪个实体变了」。此类 off-by-one 由 code-review 的 Spec 轴对照计划逐行比对抓到。

## 18. 精确失效优先于 TTL——写/删收敛入口拿失效信号（2026-08-03）

**场景**：缓存一致性靠什么保证。
**解决**：引擎内所有文件写/删收敛在 `OpenWrite`（真正落盘在 `DeferredSafWriteStream.Dispose`）/`Delete`/`CreateDirectory` 三个入口，失效信号从 C# 直接拿到（写档成功→失效父目录、删除成功→失效父目录、建目录成功→失效父自身、切换树根→全清）；TTL 仅兜底进程外修改（文件管理器/USB），可先不设。
**教训**：能拿到精确失效信号就不要用 TTL；失效是 O(1) 字典操作，命中路径零成本。验证失效是否生效：写档后立即枚举/FileExists 应看到新档（agent.log 时间线实证 +31ms 重枚举即见）。

## 19. 后台预取时机不能早于「游戏加载完成」（2026-08-04）

**场景**：O4 预取 sav/根目录缓存，为什么不在加载早期（Preload 阶段）就做。
**原因**：
1. sav 目录由游戏逻辑在启动流程某刻创建（实测删 sav 后 31 秒才重建）——过早预取大概率白跑（目录不存在→Query 空/异常→不缓存）
2. 加载早期钩子在共享 Core 层（CLI/Server/MAUI 共用），预取是 Android 专属，提前触发要侵入共享层或加事件
3. 与加载阶段（IPC 密集）并发抢 Provider 资源，可能拖慢启动本身
**解决**：runLoop 回调开头（`console.Initialize` 完成后 = 游戏状态就绪、sav 已建好），标题画面停留期就是天然预取窗口；异步预取是尽力而为——用户秒点未完成时退回缓存 miss 路径，无正确性问题。
**教训**：预取/预热类优化，时机选择看「数据是否已就绪 + 是否与主流程抢资源 + 架构是否隔离」，不是越早越好；收益相同前提下选「数据准 + 窗口够 + 零污染」的时机。

## 20. Task.Run 后台线程丢失 AsyncLocal scope——先捕获字符串再传（2026-08-04）

**场景**：runLoop lambda 在 `GlobalStatic.OpenScope` 的 AsyncLocal scope 内（Config.Current 可用），`_ = Task.Run(...)` 启动的后台线程拿不到 scope。
**解决**：lambda 内先同步取 `GamePaths.Current.ExeDir`（纯字符串 getter，无 IPC），再传进 Task.Run；后台线程里只访问实例字段/已捕获值，不依赖 AsyncLocal 上下文。
**教训**：fire-and-forget 后台任务若依赖 scope/上下文，必须在进入 Task.Run 之前完成取值捕获；`_ = Task.Run` + 异常全吞 + 日志留痕是项目约定。

## 21. 覆盖写不失效父目录——失效也要分场景（2026-08-04）

**场景**：每次写档（Dispose 落盘）都无条件失效父目录，把 O4 预取的 sav 缓存清掉，下次进存档界面又首枚举。
**分析**：覆盖写（已存在文件）不改变子项名/mime，子项列表无需更新；只有「新建文档」才需要失效（且 OpenWrite 的 created 分支 `CreateDocument` 成功后已失效过父目录）。
**解决**：Dispose 成功路径失效改为仅 `_createdDocument` 时执行；Delete/建目录失效保留。
**教训**：不是所有写操作都改变目录子项集合——过度失效会抵消缓存/预取收益。失效信号与缓存/预取是同一系统的两面，一起设计。

## 22. ImageNameTable sprite 名解析对 content:// 失效——Path API 不是 SAF（2026-08-08）

**场景**：Android SAF 加载游戏后，**所有无扩展名 sprite 名（`Border_Normal`、`Image_F女`）图片全空**。`edge://inspect` Network 面板这些请求 `ERR_NAME_NOT_RESOLVED`（~2 秒 DNS 超时）——走真实网络。Windows 同游戏正常。

**根因**：`ImageNameTable.GetRelativeDir` 用 `Path.GetDirectoryName` + `Path.GetRelativePath`（Windows 文件路径语义）计算 csv 所在目录相对游戏根的路径。对 `content://` URI 完全无效（抛异常被 `EnsureLoaded` 的 catch 吞掉）→ 整个 sprite 表加载为空 → 所有 sprite 名查不到映射 → `AssetChannel.TryGetImage` 的 `ImageNameTable.TryResolve` fallback 失败 → PathHandler reject → WebView 走默认网络 → `game.local` DNS 失败。这是 class remarks 标注过的"安卓 sprite 名解析待 URI 形态适配"未实现项。

**解决**：`GetRelativeDir` 重写，双分支：
- content://：`SafPath.TryGetDocumentId(gameRoot)` / `TryGetDocumentId(csvPath)` 前缀比较（含 Unescape，正确处理 `%3A`/`%2F`），去掉 gameRoot 前缀后取父目录段
- 普通路径：`Path.GetFullPath` + 前缀剥离（与原 `Path.GetRelativePath` 语义等价）
- 统一把 `\` 转 `/` 后去掉末段（csv 文件名），返回目录相对路径（如 `resources/sub`）

**教训**：
- **SAF URI 的"目录相对路径"同样不能走 `System.IO.Path`**——与既有「禁止 Path.GetFileName*」教训同源。凡是"相对路径/目录"计算，content:// 一律走 `SafPath`（documentId 语义）。
- **共享层代码的"已知未实现"标记会跨平台爆雷**：ImageNameTable 长期只被 Windows 路径触发，Android 接入资源通道后才暴露。改动平台接入时，要顺带审计共享层里标注"待适配"的路径处理。
- 截图/Network 面板的 `ERR_NAME_NOT_RESOLVED` 是"拦截通道放行到网络层"的统一信号（PathHandler 拒 或 拦截未生效都会走到这），需配 logcat 的 `GameAssetPathHandler: serve|reject` 区分。

## 23. 枚举输出作字典键必须 OrdinalIgnoreCase——displayName 是磁盘真实大小写（2026-09-09）

**场景**：10-B（issues/10）CSV/ERH 内容缓存（`SrcContentCache`）：建库轮把 provider children 枚举的 displayName（`GameBase.csv`）连同解码行写入 era_src.dat 快照；命中轮引擎按固定名请求 `CombinePath(csvDir, "GAMEBASE.CSV")` 查快照。字典用 `StringComparer.Ordinal`。

**结果**：快照报告 hit（327 条全载入内存），但 16 个固定名 CSV（GAMEBASE/ABL/FLAG…）全部查不到 → 退回 SAF 懒读，CSV 段从 54ms 回潮到 ~10.7s。而 CHARA*.CSV / .ERH（路径来自枚举输出本身）全部命中——同一份快照一半命中一半 miss，极具迷惑性。

**根因**：ExternalStorageProvider 的 displayName 返回**磁盘真实大小写**（`GameBase.csv`、`VariableSize.csv`），而 ERA 引擎对固定名 CSV 恒用全大写硬编码（`loadDataTo(CombinePath(csvDir, "GAMEBASE.CSV"))`）。懒读时代这条差异被 SAF 的**大小写不敏感解析**（OpenInputStream 按 displayName 不区分大小写匹配）完全掩盖——任何"枚举输出 → 精确字符串字典 → 按引擎请求串查表"的新缓存层都会在固定名上 miss。与教训 7（目录名 `csv`/`CSV`）同族但方向相反：7 是"我们的查找要忽略大小写"，23 是"**provider 给的名字与引擎要的名字大小写不一致，且运行时解析不报错**"。

**解决**：快照字典 `StringComparer.OrdinalIgnoreCase`（镜像 SAF 解析语义）。排障手法值得复用：在快照命中日志里打 2 个**键样例**、在首个查表 miss 时打**请求串样例**——两串字符级对照一轮定位（真机 R11，s10b-r9.txt）。

**教训**：
- **SAF 把"名字不一致"这类错误静默吞掉**：底层解析大小写不敏感意味着错误配置照常工作，只有当新缓存层引入精确匹配时才爆。给 SAF 数据建精确索引前，先问"这个键的书写者有几种"。
- **引擎硬编码大写 + 磁盘任意大小写**是 ERA 游戏生态的既成事实（Windows 不区分大小写惯出来的），任何平台的缓存/索引键都必须镜像目标文件系统解析语义，而不是镜像写入方的字节。
- "一半命中一半 miss"的快照 → 按键**来源**分组排查：来自枚举输出的键（真实名）命中，来自引擎字面量的键（约定名）miss——差异即线索。

## 24. 真实路径 accessor 必须镜像 SAF 的通配语义——托管 glob 在 Android 上大小写敏感（2026-09-12）

**场景**：issues/15 落地后，Android 游戏可走「真实路径 + `FileSystemGameDirAccessor`」（legacy；`BridgeHost.ResolveDirAccessor` 按路径形态分流）。eraTK（TK1.29.3）在 legacy 下**能加载**，但点「重新開始」报 `SYSTEM\NEWGAME.ERB:283` `CFLAG` 第 1 引数 -1；同一游戏切回 SAF 完全正常。

**结果**：legacy 下角色 CSV 枚举到 **0 个**（`[ErhLoader] PrepareERDFileNames: count=19`，SAF 为 `20`——唯一缺失项是 CSV 根目录里唯一的全大写扩展名 `CFLAG.CSV`），角色模板表为空 → `EXISTCSV()` 全 0 → 新游戏不加任何角色 → `NAME_TO_CHARA("アリス") = -1`。加载期不碰角色模板，所以只有运行期入口暴露。

**根因**：`FileSystemGameDirAccessor.GetFiles(path, searchPattern, option)` 原实现直接把 pattern 透传给 `Directory.GetFiles`。.NET 在 Unix/Android 上由**托管代码**做 glob（`EnumerationOptions.MatchCasing` 默认 `PlatformDefault` = 大小写敏感），而 SAF accessor 用仓库自研 `Wildcard.Matches`（`Core/Wildcard.cs`）是**大小写不敏感**。引擎的硬编码大写 glob（`Config.GetFiles(csvDir, "CHARA*.CSV")`）对磁盘真名 `csv/Chara/Chara1 霊夢.csv` 因此在 legacy 全部失配。与教训 23 同源（引擎大写约定 vs 磁盘真实大小写），方向相反：23 是缓存字典键，24 是 accessor 的 glob。

**解决**：两个 pattern 重载改为「OS 层 catch-all 枚举（`GetFiles(path, "*", option)`，递归仍交给 `Directory.*`）+ 托管侧 `Wildcard.Matches(Path.GetFileName(entry), pattern)` 过滤」，`"*"` 走快路径保持 `Preload` 全树枚举零额外开销。精确名读取（`File.Exists`/`OpenRead`/`ReadAllText`）**不动**——Android 外置存储 FUSE 层本就不区分大小写，真机探针确认 `ls .../csv/ABL.CSV` 解析到 `Abl.csv`（对照 `NOPE.csv` 报 No such file）。

**教训**：
- **accessor 的契约必须两端一致**：SAF 与真实路径必须是同一套匹配语义（大小写不敏感、按**条目名**、`*`/`?`）。否则「切模式」等于悄悄换语义——同一份游戏数据在两种模式下结果不同，且症状只在切换后出现。
- **永远不要把 `searchPattern` 透传给 `Directory.GetFiles/GetDirectories`**：其大小写语义随 OS 变（Windows 不敏感 / Unix 敏感），而引擎的模式是 Windows 遗产（全大写）。托管侧用 `Wildcard` 过滤才跨平台可控。注意 `?*.` 等语义也随 MatchType（Win32 vs Simple）漂移，统一到 `Wildcard` 还顺带消除这类分叉。
- **判「枚举到 0 个文件」先分层**：精确路径（OS/FUSE 解析，本机不敏感）vs 托管 glob（大小写敏感）——同一次诊断里两层结论可能相反，对应完全不同的修复（前者不该动，后者才是根因）。
- 同族的真实路径坑：本地分支的相对路径重建必须归一分隔符（`SafPath.RelToPhysical`），IR/sidecar 的每个重建点都不能漏（本次顺带补上 `ErbLoader.ResolveFirstDirPath` 的 `\` → `/` 归一）。
- **从「存储契约的相对名」取短名，别用 `Path.GetFileName`**：契约是 `\` 分隔，Windows 上 `Path.GetFileName` 恰好对，**Android/Linux 上 `\` 不是分隔符** → 返回整串（含目录段）。取末段要显式切 `\` 与 `/`（`LastIndexOfAny`）；`SafPath.GetLogicalFileName` 内部也走 `Path.GetFileName`，对相对名同样不安全（只对完整物理路径 / content URI 安全）。实例：issues/18 的 csvNo 解析命中目录段 `Chara` → csvNo 恒 0 → `_csvNoMap` 除 0 外全 miss。

## 25. 修好 accessor 的通配语义 ≠ 修好所有枚举——绕过 accessor 的直连调用点不在修复范围内（2026-09-12）

**场景**：issues/15 legacy（真实路径）落地后，按教训 24 修好 `FileSystemGameDirAccessor` 的 pattern 重载（提交 `9f6b1b8`）。随后逐个调用点排查，发现 `ConfigData.getUpdateKey` 的本地路径分支是直连的：

```csharp
if (SafPath.IsContentUri(Program.ExeDir))
    return getSafUpdateKey(option);                                    // accessor + Wildcard，语义正确
string[] csvFiles = Directory.GetFiles(Program.CsvDir, "*.CSV", option); // ← 根本没走 accessor
```

**结果**：上一轮修复**覆盖不到它**。legacy 下 `*.CSV` 由托管 glob 大小写敏感匹配，只命中根目录里唯一的全大写扩展名 `CFLAG.CSV`，漏掉 19 个固定 CSV 与 `Chara/` 下 165 个 `.csv` → legacy 与 SAF 算出**不同的 update key**（影响 `ReduceArgumentOnLoad=ONCE` 的参数缩减与 IR 指纹）。不崩、不影响默认配置（默认 `NO`）的玩法，属**跨模式一致性 / 缓存效率**缺陷 → 没有可见症状、CI 也不会红。

**原因**：修 accessor 的**实现**只覆盖「经由 accessor 的调用」。而 `IGameDirAccessor` 的契约（「所有文件 IO 必须通过此接口，禁止直接调用 `File.*`/`Directory.*`」）早就有违反者；这些违反者在两种模式下都「能工作」——Windows 的 glob 不敏感、SAF 走 accessor 分支——只在「**legacy + 大小写敏感 glob**」这一第三种组合下分叉。

**解决**：本地分支改走 `GamePaths.Current.DirAccessor.GetFiles(...)`（保留 `File.GetLastWriteTime` 的 mtime 语义）。同类剩余（`SafCompat.GetFiles/EnumerateFiles` 的本地分支，3 个调用点）另立 issues/21，不扩大本次范围。

**后续（2026-09-25）**：`ConfigData.getUpdateKey`/`getSafUpdateKey` 已随「配置写盘政策」一并删除——`ReduceArgumentOnLoad=ONCE` 的变更判据改寄生到 IR 指纹（`ErbIrCache.ComputeContentFingerprint` + `ErbLoader.BuildPendingList`），本教训的两条守卫由 `ErbIrCacheTests` / `FileSystemGameDirAccessorPatternTests` / `WildcardTests` 承接。

**教训**：
- **改抽象层实现后，必须 grep 该抽象对应原始 API 的直连调用点**（`Directory.GetFiles`/`File.Exists`/`Path.GetFileName`…），逐个确认是否真在抽象内——否则修的是「抽象的效果」，不是「行为」。`grep -rn --include=*.cs 'Directory\.GetFiles' EraCore.Core` 一行就能列全。
- 违反契约的调用点常常**在两种模式下都「能工作」**（被平台的大小写不敏感掩盖），只在第三种组合下分叉 → 缺陷不报错、不崩、CI 绿，只能靠调用点审计发现，不能靠症状定位。
- 这类「跨模式一致性」缺陷没有单一可观测症状，测试要拆两轴：**结构守卫**（调用必须落到抽象上——用转发式 spy accessor 记录调用，任意平台改前可红）+ **行为守卫**（大小写敏感 FS 上的真红，WSL）。


## 26. 精确名读取的容错也是 accessor 契约——「今天能工作」可能只是挂载碰巧不敏感（2026-09-12）

**场景**：issues/19/21 收敛完通配枚举后继续审计精确名读取：引擎对固定名资源恒用硬编码大写（`GAMEBASE.CSV`/`ABL.CSV`/`VariableSize.CSV`…，`loadDataTo` 22 处 + `VariableSize.CSV` + `GAMEBASE.CSV`），而 eraTK 磁盘真名是 `Abl.csv`/`GameBase.csv`——根目录 20 个 CSV 里只有 `CFLAG.CSV` 全大写。真实路径 accessor 的 `FileExists`/`OpenRead` 直接交给 OS 解析，今天能工作只因为 Android 外置主存储的 FUSE 挂载不区分大小写（2026-09-11 真机探针证实：`ls .../csv/ABL.CSV` 解析到 `Abl.csv`）。

**结果**：换到任何大小写敏感挂载（app 私有 `/data`、部分 SD/OTG、桌面 Linux 的 CLI 部署）就**静默全丢**固定名 CSV。更麻烦的是缺失文件走 early-return（`loadDataTo`/`LoadGameBaseCsv`），而 `names[]` 已预分配——症状是「一堆未知标识符 / 角色名变量名全空」而不是「文件打不开」，按错误信息找根因会走错方向。

**解决**：拍板取方案 A（推翻原 issue 倾向的 B）——accessor 精确 miss 时按父目录「OrdinalIgnoreCase 名映射」重试（`ConcurrentDictionary` 惰性缓存 + 写路径失效父目录），一处修完全部消费点。拍板依据（实施前逐点核对得出）：
- 全部固定名消费点**本就经 accessor**（漏斗唯一）——「B 要动 ~24 个调用点」是在引擎层重复 accessor 的职责；
- 容错是 **miss 驱动**：精确命中零开销（与改前逐位一致），miss 集中在加载期 ~20 个固定名 CSV，每目录枚举一次后全字典命中；
- 「掩盖大小写错误」不是新增面——SAF accessor 与 Windows 挂载本就掩盖，掩盖是引擎的既成契约；
- B 会让 accessor 契约继续分裂（SAF 容错 / legacy 严格），违背 LESSONS #24「两端一致」。

**教训**：
- **「今天能工作」要问是哪一层在工作**：OS 挂载的容错（FUSE）不是代码的容错。可移植性硬化类缺陷的触发条件是「换挂载」，真机验收与现有环境都覆盖不到——只能靠审计「谁在替谁容错」发现。
- **修这类缺陷先画漏斗**：先核对消费点是否收敛在抽象内（本例全在 accessor），再决定修在哪层。漏斗唯一时修底层（一处、契约对齐）；调用点分散时才值得修上层。原 issue 因没先核对漏斗而倾向了错的方向，实施前的逐点核对把它纠正了。
- **缺失 ≠ 报错**：`loadDataTo` 对缺失文件 early-return，错误在别处显形（未知标识符）——「静默降级」类代码路径是这类缺陷的放大器，验收断言要打在数据载入结果上，不是异常上。

