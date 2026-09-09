#if ANDROID
using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Text;
using Android.Content;
using Android.Provider;
using AndroidX.Activity.Result;
using MinorShift.Emuera.GameView;

namespace MinorShift.Emuera;

/// <summary>
/// <see cref="IGameDirAccessor"/> 的 Android SAF 实现。
/// 使用 <c>ACTION_OPEN_DOCUMENT_TREE</c> 选目录，
/// <see cref="DocumentsContract"/> + <see cref="ContentResolver"/> 列举/读/写文件。
/// </summary>
internal sealed class SafGameDirAccessor : IGameDirAccessor
{
    private readonly Context _context;
    private readonly ActivityResultLauncher _launcher;
    private TaskCompletionSource<Android.Net.Uri?>? _tcs;
    private string? _treeUri;
    private Android.Net.Uri? _treeAndroidUri;

    /// <summary>ADR-0019：MainActivity 创建的全局实例，供 OnReloadGame 在 SAF 路径时使用。</summary>
    internal static SafGameDirAccessor? Instance { get; private set; }

    private const string PrefKey = "saf_tree_uri";
    private const string DeferredWriteLimitPrefKey = "saf_deferred_write_limit_bytes";
    private const string WriteProbeFilePrefix = "_emuera_write_probe_";
    private const string OctetStreamMime = "application/octet-stream";
    private const long DefaultDeferredWriteLimitBytes = 64L * 1024 * 1024;

    /// <summary>
    /// Deferred SAF writes are bounded before allocating unbounded memory.
    /// Read **once** at first use, not per write: calling Preferences.Get on every
    /// WriteByte of an uncompressed multi-MB save made autosave take ~5s and froze
    /// the game. Advanced deployments may still override this with the app
    /// preference key above; it takes effect on next app start.
    /// </summary>
    private static readonly long s_deferredWriteLimit = LoadDeferredWriteLimit();

    private static long LoadDeferredWriteLimit()
    {
        var configured = Microsoft.Maui.Storage.Preferences.Get(
            DeferredWriteLimitPrefKey, DefaultDeferredWriteLimitBytes);
        return configured > 0 ? configured : DefaultDeferredWriteLimitBytes;
    }

    internal static long DeferredWriteLimitBytes => s_deferredWriteLimit;

    // O1 目录级子项缓存（saf-accel 计划）：目录 docId → 子项列表。
    // 命中零 IPC；写路径（OpenWrite/Delete/CreateDirectory）精确失效；切换树根全清。
    private readonly DirectoryChildCache _childCache = new();

    /// <summary>
    /// issues/14 A1+A2：basePath → 基座解析与快路径凭据。<see cref="CombinePath"/> 对
    /// 同一 basePath 的 <c>Uri.Parse</c> + <see cref="ResolveDocId"/> 结果恒同（纯函数），真机
    /// 命中轮曾对同一目录重复解析 7308 次 ≈ 2.90s。键 = basePath 原串；条目数 = 不同目录数
    /// （游戏目录树内数十级），无需上限。树根切换无需失效——缓存键是完整 basePath 而非树根。
    /// A2：<see cref="CombinePaths"/> 快路径凭据——ChildPrefix（Android 构建一次的子 URI 基座）
    /// + CharEncodings（逐字符编码表，值 = Android 对该字符的实测编码输出；null = 结构异常 →
    /// 含它的文件名走逐条 JNI）。
    /// </summary>
    private sealed class BaseResolution
    {
        public Android.Net.Uri BaseUri = null!;
        public string DocId = "";
        public string? ChildPrefix;
        public Dictionary<char, string?> CharEncodings = new();
    }

    private readonly ConcurrentDictionary<string, BaseResolution> _baseResolutionCache =
        new(StringComparer.Ordinal);

    /// <summary>
    /// S0-M1（2026-09-05）：SAF 元数据探针已执行标志。AgentLog 关闭（Android 默认）时探针零成本
    /// （只查一次布尔）；开启时沿首个枚举路径向下 ≤3 层做一次 5 列 Query（SIZE/LAST_MODIFIED）。
    /// 树根切换（PickDirectory）复位——新 provider 需重新取证。
    /// </summary>
    private bool _metadataProbed;

    /// <summary>父目录 docId（docId 最后一个 '/' 之前；单段 → 树根）。</summary>
    private string ParentDocIdOf(string docId)
        => docId.LastIndexOf('/') is var s && s < 0
            ? DocumentsContract.GetTreeDocumentId(_treeAndroidUri!)! // API 标注 nullable；树根存在时恒非 null
            : docId[..s];

    /// <summary>失效 docId 所在父目录。防御性：失效失败不影响写路径结果。</summary>
    private void InvalidateParentOf(string docId)
    {
        try { _childCache.Invalidate(ParentDocIdOf(docId)); }
        catch { /* 失效失败仅损失一次缓存命中，不改变写操作成败 */ }
    }

    // ── 辅助 ─────────────────────────────────────

    /// <summary>根据 URI 类型选择正确的文档 ID 提取方法——树 URI 用 GetTreeDocumentId，文档 URI 手动从路径提取。</summary>
    private static string ResolveDocId(Android.Net.Uri docUri)
    {
        var uriStr = docUri.ToString() ?? string.Empty;
        var posDoc = uriStr.LastIndexOf("/document/", StringComparison.Ordinal);
        // 文档 URI（.../document/...）：手动从路径提取完整文档 ID（GetDocumentId 对 %2F 编码只返回第一段）
        if (posDoc >= 0)
        {
            var encodedDocId = uriStr[(posDoc + 10)..]; // "/document/" = 10 chars
            return Uri.UnescapeDataString(encodedDocId);
        }
        // 树 URI（.../tree/...不含 /document/）：用标准的 GetTreeDocumentId
        if (uriStr.Contains("/tree/", StringComparison.Ordinal))
            return DocumentsContract.GetTreeDocumentId(docUri)!; // API 标注 nullable；已解析为树 URI 时恒非 null
        // 回退
        return DocumentsContract.GetDocumentId(docUri)!; // 同上
    }

    // ── A1 取证日志 ────────────────────────────────
    /// <summary>
    /// SAF 操作耗时日志（A1，saf-accel 计划）——每次 ContentResolver IPC 包装点记录
    /// 操作名 + 逻辑路径 + 结果 + 耗时。走 AgentLog（A0 开关控制，默认关，取证时设置页开启）。
    /// <para>
    /// <b>调用约定</b>：调用方必须<b>先 sw.Stop() 再调用本方法</b>——日志写入的微秒级
    /// 开销不得污染 ms 测量（计划原则：先停表再写日志）。
    /// </para>
    /// <para>
    /// 格式：<c>[saf] &lt;op&gt; &lt;path&gt; &lt;detail&gt; ms=&lt;N&gt;[ FAIL &lt;msg&gt;]</c>。
    /// path = docId 去 <c>primary:</c> 前缀（如 <c>emuera/TK/sav/global.sav</c>），
    /// 可读且能区分目录层级。
    /// </para>
    /// </summary>
    private static void LogSaf(string op, string docId, string detail, long ms, bool ok = true, string? failMsg = null)
    {
        // 原则 2「常态零成本」：开关关闭（Android 默认）时热路径零分配零加锁——
        // 必须先查 Enabled 再拼字符串，避免每条 IPC 1~2 次分配。
        if (!AgentLog.Instance.Enabled) return;
        var path = docId.StartsWith("primary:", StringComparison.Ordinal) ? docId["primary:".Length..] : docId;
        var tail = ok ? $"ms={ms}" : $"ms={ms} FAIL {failMsg}";
        AgentLog.Instance.Write($"[saf] {op} {path} {detail} {tail}");
    }

    public SafGameDirAccessor(Context context, ActivityResultLauncher launcher)
    {
        _context = context;
        _launcher = launcher;
        _treeUri = Microsoft.Maui.Storage.Preferences.Get(PrefKey, null);
        if (_treeUri != null) _treeAndroidUri = Android.Net.Uri.Parse(_treeUri);
        Instance = this;
    }

    // ── 目录选择 ──────────────────────────────────

    public async Task<string?> PickDirectoryAsync(CancellationToken ct = default)
    {
        _tcs?.TrySetCanceled();
        _tcs = new TaskCompletionSource<Android.Net.Uri?>();
        ct.Register(() => _tcs.TrySetCanceled(ct), useSynchronizationContext: false);

        _launcher!.Launch(null);
        var uri = await _tcs.Task;
        if (uri != null)
        {
            // 方案 B：存档写回游戏树，优先持久化读+写权限。
            // 某些 DocumentsProvider 只返回读授权；保留 URI 并继续走探针，
            // 让上层给出“请重新选择目录”的可诊断提示，而不是在这里吞掉选择结果。
            var flags = ActivityFlags.GrantReadUriPermission | ActivityFlags.GrantWriteUriPermission;
            try
            {
                _context.ContentResolver!.TakePersistableUriPermission(uri, flags);
            }
            catch (Exception ex)
            {
                Android.Util.Log.Warn("EmueraMaui",
                    $"PickDirectory: persist read/write permission failed: {ex.Message}");
                try
                {
                    _context.ContentResolver!.TakePersistableUriPermission(
                        uri, ActivityFlags.GrantReadUriPermission);
                }
                catch (Exception readEx)
                {
                    Android.Util.Log.Warn("EmueraMaui",
                        $"PickDirectory: persist read-only permission failed: {readEx.Message}");
                }
            }
            _treeUri = uri.ToString();
            _treeAndroidUri = uri;
            _childCache.Clear(); // 树根切换：旧 docId 前缀全部作废，全清缓存
            _metadataProbed = false; // S0-M1：树根切换 → 新 provider 需重新取证
            Microsoft.Maui.Storage.Preferences.Set(PrefKey, _treeUri);
            Android.Util.Log.Info("EmueraMaui",
                $"PickDirectory: took r/w persistable uri, hasWrite={HasWriteAccess()}, uri={_treeUri}");
        }
        return uri?.ToString();
    }

    /// <summary>ActivityResultCallback 入口——由 MainActivity 调用。</summary>
    public void OnTreeResult(Android.Net.Uri? uri)
    {
        _tcs?.TrySetResult(uri);
    }

    // ── 目录/文件枚举 ─────────────────────────────

    public bool DirectoryExists(string path)
    {
        try
        {
            if (_treeAndroidUri == null)
            {
                Android.Util.Log.Warn("EmueraMaui", $"DirectoryExists: _treeAndroidUri null for {path}");
                return false;
            }
            if (!TryParseUri(path, out var docUri))
            {
                Android.Util.Log.Warn("EmueraMaui", $"DirectoryExists: TryParseUri failed for {path}");
                return false;
            }
            // 不调用 EnsureRealDirectoryUri（会 Create）；只做存在性查询
            if (IsTreeRootUri(docUri)) return true;

            // O1 快速路径：父目录已缓存 → 从缓存按名查子目录（0 IPC 确定性判定；
            // 未命中走原 TryQueryDocument / FindChildDocument 路径，后者会回填缓存）
            if (TryGetParentAndName(path, out var cacheParentUri, out var cacheName)
                && cacheParentUri != null && cacheName != null
                && _childCache.Get(ResolveDocId(cacheParentUri)) is { } cachedChildren)
            {
                foreach (var c in cachedChildren)
                {
                    if (c.Mime == DocumentsContract.Document.MimeTypeDir && NameMatches(c, cacheName))
                        return true;
                }
                return false; // 父目录已缓存且无此子目录 → 确定性 false
            }

            if (TryQueryDocument(docUri, out var mime) && mime == DocumentsContract.Document.MimeTypeDir)
                return true;
            if (TryGetParentAndName(path, out var parentUri, out var name) && parentUri != null && name != null)
            {
                Android.Net.Uri? realParent = null;
                if (IsTreeRootUri(parentUri))
                {
                    realParent = DocumentsContract.BuildDocumentUriUsingTree(
                        _treeAndroidUri, DocumentsContract.GetTreeDocumentId(_treeAndroidUri));
                }
                else if (TryQueryDocument(parentUri, out var pm) && pm == DocumentsContract.Document.MimeTypeDir)
                {
                    realParent = parentUri;
                }
                else
                {
                    // 父亦为理论路径时：仅支持「树根下一级」查找（不创建）
                    realParent = null;
                    if (TryGetParentAndName(parentUri.ToString()!, out var grand, out var parentName)
                        && grand != null && parentName != null && IsTreeRootUri(grand))
                    {
                        var grandReal = DocumentsContract.BuildDocumentUriUsingTree(
                            _treeAndroidUri, DocumentsContract.GetTreeDocumentId(_treeAndroidUri));
                        if (grandReal != null)
                            realParent = FindChildDocument(grandReal, parentName, wantDir: true);
                    }
                }
                if (realParent != null && FindChildDocument(realParent, name, wantDir: true) != null)
                    return true;
            }
            return false;
        }
        catch (Exception ex)
        {
            Android.Util.Log.Warn("EmueraMaui", $"DirectoryExists exception: {path} → {ex.Message}");
            return false;
        }
    }

    public string[] GetDirectories(string path)
    {
        return EnumerateChildren(path, isDir: true, pattern: null, recursive: false);
    }

    public string[] GetDirectories(string path, string searchPattern, SearchOption searchOption)
    {
        return EnumerateChildren(path, isDir: true, pattern: searchPattern, recursive: searchOption == SearchOption.AllDirectories);
    }

    public string[] GetFiles(string path)
    {
        return EnumerateChildren(path, isDir: false, pattern: null, recursive: false);
    }

    public string[] GetFiles(string path, string searchPattern, SearchOption searchOption)
    {
        return EnumerateChildren(path, isDir: false, pattern: searchPattern, recursive: searchOption == SearchOption.AllDirectories);
    }

    public string ResolveSubPath(string basePath, string subDir)
    {
        if (_treeAndroidUri == null)
        {
            Android.Util.Log.Warn("EmueraMaui", $"ResolveSubPath: _treeAndroidUri null, base={basePath}, sub={subDir}");
            return basePath;
        }
        if (!TryParseUri(basePath, out var docUri))
        {
            Android.Util.Log.Warn("EmueraMaui", $"ResolveSubPath: TryParseUri failed for {basePath}");
            return basePath;
        }
        var docId = ResolveDocId(docUri);
        try
        {
            // O1：父目录缓存命中 → 零 IPC；未命中 QueryChildren（op=EnumerateUri）并回填。
            // 仅按 Name 匹配（原语义），无 documentId 末段回退。
            foreach (var child in GetOrQueryChildren(docId))
            {
                if (!string.Equals(child.Name, subDir, StringComparison.OrdinalIgnoreCase)) continue;
                var result = DocumentsContract.BuildDocumentUriUsingTree(_treeAndroidUri!, child.DocId)!.ToString()!;
                Android.Util.Log.Info("EmueraMaui", $"ResolveSubPath: found {subDir} → {result}");
                return result;
            }
        }
        catch (Exception ex)
        {
            // Query 异常由 QueryChildren 记录（op=EnumerateUri）并重抛；保持原传播行为（上层有 catch）
            Android.Util.Log.Warn("EmueraMaui", $"ResolveSubPath query-ex: {subDir} → {ex.Message}");
            throw;
        }

        // 子目录尚不存在时返回理论 document URI，供 CreateDirectory / OpenWrite 按父+名创建
        // （旧实现返回 basePath 会导致 sav/ 永远建在错误层级）
        var theoretical = DocumentsContract.BuildDocumentUriUsingTree(_treeAndroidUri, $"{docId}/{subDir}")!.ToString()!;
        Android.Util.Log.Info("EmueraMaui", $"ResolveSubPath: {subDir} not found, theoretical → {theoretical}");
        return theoretical;
    }

    // ── 文件读取 ──────────────────────────────────

    public bool FileExists(string path)
    {
        try
        {
            return ResolveExistingFileUri(path) != null;
        }
        catch { return false; }
    }

    public string ReadAllText(string path)
    {
        using var stream = OpenRead(path);
        if (stream == null) throw new FileNotFoundException($"SAF file not found: {path}");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    public byte[]? ReadAllBytes(string path)
    {
        using var stream = OpenRead(path);
        if (stream == null) return null;
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        return ms.ToArray();
    }

    public Stream? OpenRead(string path)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            if (_treeAndroidUri == null)
            {
                Android.Util.Log.Warn("EmueraMaui", $"OpenRead: _treeAndroidUri null for {path}");
                return null;
            }

            var docUri = ResolveExistingFileUri(path);
            if (docUri == null)
            {
                Android.Util.Log.Info("EmueraMaui", $"OpenRead: file not found: {path}");
                return null;
            }

            var mime = GetMimeType(docUri);
            if (mime == null || mime == DocumentsContract.Document.MimeTypeDir)
            {
                Android.Util.Log.Info("EmueraMaui", $"OpenRead: not a file: {path} (mime={mime ?? "null"})");
                return null;
            }

            // 与写路径对称：一次 CopyTo 进 MemoryStream。
            // 原因：
            // 1) SAF InputStream 常不可 Seek / Length 不可靠 → EraBinaryDataReader.CreateReader
            //    的 `fs.Length < 16` 会误判为坏档或走文本路径；
            // 2) BinaryReader 对数十万字节存档逐字段读 ContentResolver 极慢（与写假死同因）。
            var docId = ResolveDocId(docUri);
            using var input = _context.ContentResolver!.OpenInputStream(docUri);
            if (input == null)
            {
                sw.Stop();
                LogSaf("OpenRead", docId, "open-null", sw.ElapsedMilliseconds);
                Android.Util.Log.Warn("EmueraMaui", $"OpenRead: OpenInputStream null for {path}");
                return null;
            }
            var ms = new MemoryStream();
            input.CopyTo(ms);
            ms.Position = 0;
            sw.Stop();
            LogSaf("OpenRead", docId, $"bytes={ms.Length} mime={mime}", sw.ElapsedMilliseconds);
            Android.Util.Log.Info("EmueraMaui",
                $"OpenRead buffered OK path={path} bytes={ms.Length} ms={sw.ElapsedMilliseconds} mime={mime}");
            return ms;
        }
        catch (Exception ex)
        {
            sw.Stop();
            var failDocId = TryParseUri(path, out var failUri) ? ResolveDocId(failUri) : path;
            LogSaf("OpenRead", failDocId, "read-ex", sw.ElapsedMilliseconds, ok: false, failMsg: ex.Message);
            Android.Util.Log.Warn("EmueraMaui", $"OpenRead exception: {path} → {ex.Message}");
            return null;
        }
    }

    // ── 文件/目录写入 ─────────────────────────────

    public bool HasWriteAccess()
    {
        if (_treeAndroidUri == null) return false;
        try
        {
            var treeStr = _treeAndroidUri.ToString();
            foreach (var perm in _context.ContentResolver!.PersistedUriPermissions)
            {
                if (perm?.Uri == null || !perm.IsWritePermission) continue;
                var permStr = perm.Uri.ToString();
                // 持久化的是 tree URI；比较时兼容 document 形态
                if (string.Equals(permStr, treeStr, StringComparison.Ordinal)
                    || (treeStr != null && permStr != null && treeStr.StartsWith(permStr, StringComparison.Ordinal))
                    || (treeStr != null && permStr != null && permStr.StartsWith(treeStr, StringComparison.Ordinal)))
                {
                    return true;
                }
            }
        }
        catch (Exception ex)
        {
            Android.Util.Log.Warn("EmueraMaui", $"HasWriteAccess exception: {ex.Message}");
        }
        return false;
    }

    public void CreateDirectory(string path)
    {
        if (_treeAndroidUri == null)
            throw new InvalidOperationException("SAF tree URI not set");
        if (!TryParseUri(path, out var dirUri))
            throw new IOException($"CreateDirectory: invalid path {path}");

        var real = EnsureRealDirectoryUri(dirUri)
            ?? throw new IOException($"CreateDirectory failed: {path}");
        Android.Util.Log.Info("EmueraMaui", $"CreateDirectory OK: {path} → {real}");
    }

    public Stream OpenWrite(string path)
    {
        if (_treeAndroidUri == null)
            throw new InvalidOperationException("SAF tree URI not set");
        if (!HasWriteAccess())
        {
            Android.Util.Log.Error("EmueraMaui",
                "OpenWrite: no persisted write permission — re-pick game directory to grant write access");
            throw new UnauthorizedAccessException(
                "SAF write permission not granted. Please re-select the game directory to allow saving.");
        }

        var displayName = GetFileName(path);
        if (string.IsNullOrEmpty(displayName))
            throw new IOException($"OpenWrite: empty display name for {path}");

        // 先解析/创建 document，但**不**立刻挂 OpenOutputStream。
        // TK 存档可达数十 MB；经 ContentResolver 逐块写极慢甚至表现为卡死。
        // 改为有上限的可 Seek 内存缓冲，Dispose 时一次 CopyTo 写出。
        // 旧文档只有在输出流成功打开后才会被 provider 截断；打开失败时旧内容保持不变。
        var existing = ResolveExistingFileUri(path);
        Android.Net.Uri docUri;
        var createdDocument = false;
        if (existing != null)
        {
            docUri = existing;
            Android.Util.Log.Info("EmueraMaui", $"OpenWrite: defer buffer → existing {path}");
        }
        else
        {
            if (!TryGetParentAndName(path, out var parentUri, out var name))
                throw new IOException($"OpenWrite: cannot resolve parent for {path}");

            parentUri = EnsureRealDirectoryUri(parentUri)
                ?? throw new IOException($"OpenWrite: parent directory missing or unwritable for {path}");

            var created = DocumentsContract.CreateDocument(
                _context.ContentResolver!,
                parentUri,
                GuessMime(displayName),
                name);
            if (created == null)
                throw new IOException($"OpenWrite: CreateDocument returned null for {path}");
            docUri = created;
            createdDocument = true;
            // O1：新文档此刻已存在（空文档），父目录列表立即过期——不能等 Dispose
            InvalidateParentOf(ResolveDocId(created));
            Android.Util.Log.Info("EmueraMaui", $"OpenWrite: defer buffer → created {path} → {created}");
        }

        return new DeferredSafWriteStream(_context, docUri, path, createdDocument);
    }

    public void Delete(string path)
    {
        try
        {
            var docUri = ResolveExistingFileUri(path);
            if (docUri == null)
            {
                Android.Util.Log.Info("EmueraMaui", $"Delete: not found {path}");
                return;
            }
            var ok = DocumentsContract.DeleteDocument(_context.ContentResolver!, docUri);
            Android.Util.Log.Info("EmueraMaui", $"Delete: {path} → {ok}");
            if (ok)
            {
                // O1：删除成功 → 失效所在目录（ok=false 仅记录，不失效——文档可能仍存在）
                try { InvalidateParentOf(ResolveDocId(docUri)); }
                catch { /* 失效失败仅损失一次缓存命中 */ }
            }
        }
        catch (Exception ex)
        {
            Android.Util.Log.Warn("EmueraMaui", $"Delete exception: {path} → {ex.Message}");
            throw;
        }
    }

    /// <summary>
    /// B1 真机闸门：在树根写/读/删探针文件。成功返回 true 并写 log。
    /// </summary>
    internal bool TryWriteProbe(out string detail)
    {
        detail = "";
        try
        {
            if (_treeAndroidUri == null)
            {
                detail = "tree URI null";
                return false;
            }
            if (!HasWriteAccess())
            {
                detail = "no persisted write permission — re-pick directory";
                Android.Util.Log.Warn("EmueraMaui", $"WriteProbe FAIL: {detail}");
                return false;
            }

            var rootPath = TreeRootDocumentPath();
            var probeName = $"{WriteProbeFilePrefix}{Guid.NewGuid():N}.tmp";
            var probePath = CombinePath(rootPath, probeName);
            var payload = Encoding.UTF8.GetBytes("emuera-write-probe-ok");

            try
            {
                using (var ws = OpenWrite(probePath))
                    ws.Write(payload, 0, payload.Length);

                using (var rs = OpenRead(probePath)
                    ?? throw new IOException("probe OpenRead returned null"))
                {
                    using var ms = new MemoryStream();
                    rs.CopyTo(ms);
                    var read = ms.ToArray();
                    if (read.Length != payload.Length || !read.AsSpan().SequenceEqual(payload))
                    {
                        detail = $"readback mismatch len={read.Length}";
                        Android.Util.Log.Error("EmueraMaui", $"WriteProbe FAIL: {detail}");
                        return false;
                    }
                }

                detail = $"ok path={probePath}";
                Android.Util.Log.Info("EmueraMaui", $"WriteProbe OK: {detail}");
                return true;
            }
            finally
            {
                try
                {
                    Delete(probePath);
                }
                catch (Exception cleanupEx)
                {
                    Android.Util.Log.Warn("EmueraMaui",
                        $"WriteProbe cleanup failed path={probePath} ex={cleanupEx.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            detail = ex.ToString();
            Android.Util.Log.Error("EmueraMaui", $"WriteProbe FAIL: {detail}");
            return false;
        }
    }

    // ── 路径操作 ──────────────────────────────────

    public string CombinePath(string basePath, string filename)
    {
        // SAF content URI 不能用简单的字符串拼接——必须用 BuildDocumentUriUsingTree
        // 否则 content://.../dir/file.csv 会被 Android 解析为 content://.../dir（目录），
        // /file.csv 被忽略，OpenInputStream 读到目录本身 → EISDIR
        // documentId 含 %2F 时必须用 ResolveDocId，不能用 GetDocumentId（只返回第一段）。
        if (basePath.StartsWith("content://", StringComparison.Ordinal) && _treeAndroidUri != null)
        {
            try
            {
                var entry = ResolveBaseCached(basePath);
                var childUri = DocumentsContract.BuildDocumentUriUsingTree(_treeAndroidUri!, $"{entry.DocId}/{filename}");
                return childUri!.ToString()!;
            }
            catch
            {
                // 回退到字符串拼接（可能失败，但至少不抛）
            }
        }
        var trimmed = basePath.TrimEnd('/');
        return $"{trimmed}/{filename}";
    }

    /// <summary>
    /// issues/14 A1+A2：批量拼接。基座解析整批一次（A1）；快路径 = <c>prefix + "%2F" + 逐字符编码</c>
    /// （A2，0 JNI）——字符编码表由 <see cref="ResolveBaseCached"/> 前缀探针 + <see cref="EnsureCharsVerified"/>
    /// 逐字符探针（每新字符 1 次 JNI，终身缓存）向 Android 实测填充，**任何字符核验不过 → 含它的
    /// 文件名退回逐条 JNI**；批次内首个快路径条目再与 Android 实建结果逐字节比对一次（邻接性 sanity），
    /// 不等整批退回逐条。失败语义与 <see cref="CombinePath"/> 一致：单条异常回退字符串拼接。
    /// </summary>
    public IReadOnlyList<string> CombinePaths(string basePath, IReadOnlyList<string> filenames)
    {
        // 非 content 路径（混合场景兜底）：语义 = 逐条 CombinePath，直接走 DIM 默认。
        if (!basePath.StartsWith("content://", StringComparison.Ordinal) || _treeAndroidUri == null)
            return CombinePathsFallback(basePath, filenames);

        BaseResolution entry;
        try
        {
            entry = ResolveBaseCached(basePath);
        }
        catch
        {
            return CombinePathsFallback(basePath, filenames);
        }

        var result = new string[filenames.Count];
        if (entry.ChildPrefix != null)
        {
            try
            {
                EnsureCharsVerified(entry, filenames);
                int fastCount = 0;
                int firstFastIdx = -1;
                string? firstFastValue = null;
                for (int i = 0; i < filenames.Count; i++)
                {
                    var encoded = TryFastEncode(entry.ChildPrefix, entry, filenames[i]);
                    if (encoded != null)
                    {
                        result[i] = encoded;
                        if (firstFastIdx < 0)
                        {
                            firstFastIdx = i;
                            firstFastValue = encoded;
                        }
                        fastCount++;
                    }
                    else
                    {
                        result[i] = BuildViaJni(entry.DocId, filenames[i]) ?? FastFallback(basePath, filenames[i]);
                    }
                }
                // 邻接性 sanity：首个快路径条目与 Android 实建结果逐字节比对——
                // 不等说明存在未预期的上下文相关编码，整批退回逐条 JNI（宁可慢不可错）。
                if (firstFastIdx >= 0)
                {
                    var jni = BuildViaJni(entry.DocId, filenames[firstFastIdx]);
                    if (jni != firstFastValue)
                    {
                        entry.ChildPrefix = null;
                        entry.CharEncodings.Clear();
                        return CombinePathsFallback(basePath, filenames);
                    }
                }
                Android.Util.Log.Info("EmueraMaui",
                    $"[saf] combinePaths n={filenames.Count} fast={fastCount} jni={filenames.Count - fastCount} " +
                    $"prefix=ok chars={entry.CharEncodings.Count}");
                return result;
            }
            catch
            {
                // 落入下方逐条兜底
            }
        }

        for (int i = 0; i < filenames.Count; i++)
            result[i] = BuildViaJni(entry.DocId, filenames[i]) ?? FastFallback(basePath, filenames[i]);
        return result;
    }

    /// <summary>快路径编码：前缀 + "%2F" + 逐字符编码（"/" 由前缀探针 ③ 核验为恒定 "%2F"）；
    /// 含未捕获/捕获失败字符 → null（JNI 兜底）。</summary>
    private static string? TryFastEncode(string prefix, BaseResolution entry, string name)
    {
        if (name.Length == 0)
            return null;
        var sb = new System.Text.StringBuilder(prefix.Length + name.Length * 4 + 8);
        sb.Append(prefix).Append("%2F");
        foreach (var c in name)
        {
            // issues/14 A2："/" 不入字符表（前缀探针 ③ 已核验其编码恒为 "%2F"），拼接侧直接替换。
            if (c == '/')
            {
                sb.Append("%2F");
                continue;
            }
            if (!entry.CharEncodings.TryGetValue(c, out var enc) || enc == null)
                return null;
            sb.Append(enc);
        }
        return sb.ToString();
    }

    /// <summary>
    /// 逐字符捕获：每个新字符用 1 次 JNI 取 Android 对它的**实测编码输出**（剥掉前缀 + "%2F"
    /// 后原样缓存）——不做任何托管预测，复刻对错的问题不存在。拼接结果的正确性由
    /// <see cref="CombinePaths"/> 的批次首条实核（邻接性 sanity）兜底。代理对（非 BMP）不捕获
    /// （Java 单代理编码行为实现细节敏感）→ null → 含它的文件名走逐条 JNI。
    /// </summary>
    private void EnsureCharsVerified(BaseResolution entry, IReadOnlyList<string> filenames)
    {
        var prefix = entry.ChildPrefix!;
        lock (entry)
        {
            foreach (var name in filenames)
            {
                foreach (var c in name)
                {
                    if (c == '/' || entry.CharEncodings.ContainsKey(c))
                        continue; // "/" 由前缀探针 ③ 核验（拼接侧确定性替换）
                    if (c >= 0xD800 && c <= 0xDFFF)
                    {
                        entry.CharEncodings[c] = null;
                        continue;
                    }
                    string? captured = null;
                    try
                    {
                        var probe = DocumentsContract.BuildDocumentUriUsingTree(
                            _treeAndroidUri!, $"{entry.DocId}/{c}")?.ToString();
                        var head = prefix + "%2F";
                        if (probe != null && probe.StartsWith(head, StringComparison.Ordinal)
                            && probe.Length > head.Length)
                            captured = probe[head.Length..];
                    }
                    catch
                    {
                        captured = null;
                    }
                    entry.CharEncodings[c] = captured;
                }
            }
        }
    }

    /// <summary>A1 逐条 JNI 构建；失败返回 null（调用方回退字符串拼接）。</summary>
    private string? BuildViaJni(string docId, string filename)
    {
        try
        {
            return DocumentsContract.BuildDocumentUriUsingTree(
                _treeAndroidUri!, $"{docId}/{filename}")?.ToString();
        }
        catch
        {
            return null;
        }
    }

    private static string FastFallback(string basePath, string filename)
    {
        var trimmed = basePath.TrimEnd('/');
        return $"{trimmed}/{filename}";
    }

    /// <summary>
    /// 基座解析记忆化查询点——未命中才做 Parse + ResolveDocId（纯函数，结果可缓存）+ 一次
    /// A2 前缀探针（2 次 JNI，每目录终身一次）：用只含白名单字符的探针名让 Android 构建子 URI，
    /// 核验 (a) 输出以 "/探针名" 结尾、(b) 树 URI 前缀吻合、(c) 全字符集探针名与直拼预测逐字节
    /// 一致。任一不过 → ChildPrefix = null，快路径整目录禁用（A1 逐条 JNI 兜底）。
    /// </summary>
    private BaseResolution ResolveBaseCached(string basePath)
    {
        return _baseResolutionCache.GetOrAdd(basePath, static (path, self) =>
        {
            var baseUri = Android.Net.Uri.Parse(path)
                ?? throw new ArgumentException($"Uri.Parse returned null: {path}");
            var docId = ResolveDocId(baseUri);

            // A2 前缀探针——快路径的唯一正确性来源：Android 自己编码 documentId，托管侧零猜测。
            // BuildDocumentUriUsingTree 输出形态（实测）：.../document/<encode(docId)>%2F<encode(name)>——
            // documentId 整段编码，段内 "/" → %2F。三条探针：①纯 "0" 定位前缀；②全白名单字符集
            // 名核验「白名单字符编码恒等」；③含 "/" 的名核验分隔符编码（快路径要做 "/"→%2F 替换）。
            // 任一不满足 → ChildPrefix null → 快路径整目录禁用（A1 逐条 JNI 兜底）。
            const string Sep = "%2F";
            const string ProbeName1 = "0";
            const string ProbeName2 = "aA0-_.~!()*'";
            const string ProbeName3 = "S/0";
            string? prefix = null;
            try
            {
                var probe1 = DocumentsContract.BuildDocumentUriUsingTree(
                    self._treeAndroidUri!, $"{docId}/{ProbeName1}")?.ToString();
                var probe2 = DocumentsContract.BuildDocumentUriUsingTree(
                    self._treeAndroidUri!, $"{docId}/{ProbeName2}")?.ToString();
                var probe3 = DocumentsContract.BuildDocumentUriUsingTree(
                    self._treeAndroidUri!, $"{docId}/{ProbeName3}")?.ToString();
                if (probe1 != null && probe2 != null && probe3 != null
                    && probe1.EndsWith(Sep + ProbeName1, StringComparison.Ordinal))
                {
                    var candidate = probe1[..^((Sep + ProbeName1).Length)];
                    if (probe2 == candidate + Sep + ProbeName2
                        && probe3 == candidate + Sep + ProbeName3.Replace("/", Sep))
                        prefix = candidate;
                }
            }
            catch
            {
                prefix = null;
            }
            return new BaseResolution { BaseUri = baseUri, DocId = docId, ChildPrefix = prefix };
        }, this);
    }

    /// <summary>issues/14 A1：逐条兜底循环——与 IGameDirAccessor.CombinePaths 默认实现同语义。</summary>
    private IReadOnlyList<string> CombinePathsFallback(string basePath, IReadOnlyList<string> filenames)
    {
        var result = new string[filenames.Count];
        for (int i = 0; i < filenames.Count; i++)
            result[i] = CombinePath(basePath, filenames[i]);
        return result;
    }

    public string GetParentPath(string path)
    {
        if (_treeAndroidUri == null || !TryParseUri(path, out var docUri))
            return path;

        var docId = ResolveDocId(docUri);
        var lastSlash = docId.LastIndexOf('/');
        if (lastSlash < 0)
            return TreeRootDocumentPath();

        var parentId = docId[..lastSlash];
        var parentUri = DocumentsContract.BuildDocumentUriUsingTree(_treeAndroidUri, parentId);
        return parentUri?.ToString() ?? TreeRootDocumentPath();
    }

    public string GetFileName(string path)
    {
        // document 段内路径分隔符是 %2F：不能取 URI 最后一个 / 之后整段。
        // 与 Core SafPath 对齐，得到逻辑短名（如 xxx.ERB）。
        try
        {
            return MinorShift.Emuera.Runtime.Utils.SafPath.GetLogicalFileName(path);
        }
        catch { return path; }
    }

    // ── 内部实现 ──────────────────────────────────

    private string[] EnumerateChildren(string path, bool isDir, string? pattern, bool recursive)
    {
        var result = new List<string>();
        if (_treeAndroidUri == null || !TryParseUri(path, out var docUri)) return [];

        // 缓存 key 一律用父文档 URI 的 docId——ResolveDocId 对 children URI（尾带 /children）取到的
        // 是「父id/children」，与 FindChildDocument/ResolveSubPath 的「父id」不一致，不能作 key。
        var docId = ResolveDocId(docUri);
        ProbeMetadataColumns(path); // S0-M1：命中轮首个全树枚举即触发（AgentLog 关 = 零成本布尔检查）
        EnumerateUri(docId, result, isDir, pattern, recursive);
        return result.ToArray();
    }

    private static bool TryParseUri(string path, [NotNullWhen(true)] out Android.Net.Uri? uri)
    {
        if (string.IsNullOrEmpty(path)) { uri = null; return false; }
        try { uri = Android.Net.Uri.Parse(path)!; return true; }
        catch { uri = null; return false; }
    }

    private string? GetMimeType(Android.Net.Uri uri)
    {
        // docId 计算放 try 内——ResolveDocId 对异常 URI 可能抛，须保持原 catch-all 语义（返回 null）
        string? docId;
        try { docId = ResolveDocId(uri); }
        catch { return null; }

        // O1 快速路径：父目录已缓存 → 从缓存查 mime（0 IPC 零分配零日志；与 children Query 同列同源同值）
        if (docId.LastIndexOf('/') is var slash && slash >= 0
            && _childCache.Get(docId[..slash]) is { } cachedChildren)
        {
            foreach (var child in cachedChildren)
                if (string.Equals(child.DocId, docId, StringComparison.Ordinal))
                    return child.Mime;
            // 父缓存有但无此子项 → 缓存过期，回落原 Query
        }

        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            using var cursor = _context.ContentResolver!.Query(
                uri, new[] { DocumentsContract.Document.ColumnMimeType }, null, null, null);
            var mime = cursor?.MoveToFirst() == true ? cursor.GetString(0) : null;
            sw.Stop();
            LogSaf("GetMimeType", docId, $"mime={mime ?? "null"}", sw.ElapsedMilliseconds);
            return mime;
        }
        catch (Exception ex)
        {
            sw.Stop();
            LogSaf("GetMimeType", docId ?? uri.ToString() ?? "?", "query-ex", sw.ElapsedMilliseconds, ok: false, failMsg: ex.Message);
            return null;
        }
    }

    /// <summary>
    /// 枚举目录子项（O1 缓存优先）：命中缓存零 IPC 零日志；miss 时一次 Query 拉全量子项并回填缓存，
    /// 再从缓存做 isDir / pattern 过滤。递归时子目录走同一缓存（每目录首访一次 Query）。
    /// </summary>
    private void EnumerateUri(string parentDocId, List<string> result, bool isDir, string? pattern, bool recursive)
    {
        foreach (var child in GetOrQueryChildren(parentDocId))
        {
            var childIsDir = child.Mime == DocumentsContract.Document.MimeTypeDir;
            if (childIsDir == isDir && (pattern == null || Wildcard.Matches(DisplayNameForMatch(child, pattern), pattern)))
            {
                var childDocUri = DocumentsContract.BuildDocumentUriUsingTree(_treeAndroidUri!, child.DocId);
                result.Add(childDocUri!.ToString()!);
            }
            if (recursive && childIsDir)
                EnumerateUri(child.DocId, result, isDir, pattern, true);
        }
    }

    // ── S0-M1 SAF 元数据探针（2026-09-05，diagnostic-knobs issues/11）─────────────────────

    /// <summary>
    /// S0-M1：验证 SAF provider 是否对文件行提供 SIZE / LAST_MODIFIED 元数据列——10-B 内容缓存
    /// 失效检测的前置（现有 IR 指纹在 Android 上内容盲，stat 恒 0）。只探一次（<see cref="_metadataProbed"/>）；
    /// AgentLog 关闭（Android 默认）时零成本（单次布尔检查）。沿 sampleRootPath 向下（用既有子项缓存，
    /// 不新增枚举 IPC）找第一个含文件子项的目录，对其做一次 5 列 Query；日志输出 size/mtime 可读比例
    /// 与前 3 个样例。真机判读：mtime 可读且随内容变化 → 10-B 放行；缺失/恒 0 → 10-B 不立项。
    /// </summary>
    private void ProbeMetadataColumns(string sampleRootPath)
    {
        if (_metadataProbed || !AgentLog.Instance.Enabled) return;
        _metadataProbed = true; // 无论成败只探一次
        if (!TryParseUri(sampleRootPath, out var docUri)) return;
        try
        {
            var docId = ResolveDocId(docUri);
            string? sampleDir = null;
            int visited = 0;
            var queue = new Queue<string>();
            queue.Enqueue(docId);
            while (queue.Count > 0 && visited++ < 200 && sampleDir == null)
            {
                var dir = queue.Dequeue();
                foreach (var child in GetOrQueryChildren(dir))
                {
                    if (child.Mime == DocumentsContract.Document.MimeTypeDir)
                    {
                        if (queue.Count < 200) queue.Enqueue(child.DocId);
                    }
                    else if (sampleDir == null)
                    {
                        sampleDir = dir; // 该目录含文件行 → 元数据采样点
                    }
                }
            }
            if (sampleDir == null)
            {
                EmueraLog.Info("preload", "S0M1 metadata: no file-bearing dir found, skipped");
                return;
            }
            var childrenUri = DocumentsContract.BuildChildDocumentsUriUsingTree(_treeAndroidUri!, sampleDir);
            using var cursor = _context.ContentResolver!.Query(childrenUri!,
                new[]
                {
                    DocumentsContract.Document.ColumnDocumentId,
                    DocumentsContract.Document.ColumnDisplayName,
                    DocumentsContract.Document.ColumnMimeType,
                    DocumentsContract.Document.ColumnSize,
                    DocumentsContract.Document.ColumnLastModified
                }, null, null, null);
            if (cursor == null)
            {
                EmueraLog.Info("preload", "S0M1 metadata: cursor null (provider rejected extended projection)");
                return;
            }
            int files = 0, sizeOk = 0, mtimeOk = 0;
            var samples = new List<string>();
            while (cursor.MoveToNext())
            {
                if (cursor.GetString(2) == DocumentsContract.Document.MimeTypeDir) continue;
                files++;
                bool hasSize = !cursor.IsNull(3);
                bool hasMtime = !cursor.IsNull(4);
                if (hasSize) sizeOk++;
                if (hasMtime) mtimeOk++;
                if (samples.Count < 3)
                    samples.Add($"{cursor.GetString(1) ?? "?"}={(hasSize ? cursor.GetLong(3).ToString() : "-")}B@{(hasMtime ? cursor.GetLong(4).ToString() : "-")}");
            }
            EmueraLog.Info("preload",
                $"S0M1 metadata: dir={sampleDir} files={files} sizeAvail={sizeOk}/{files} mtimeAvail={mtimeOk}/{files} samples=[{string.Join(", ", samples)}]");
        }
        catch (Exception ex)
        {
            // 探针失败不阻断加载：provider 不支持扩展投影 / 查询异常都只留一行证据。
            EmueraLog.Info("preload", $"S0M1 metadata: probe-ex {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>取目录子项：缓存命中直接返回（零 IPC 零日志零分配）；未命中则 Query 并回填。</summary>
    private IReadOnlyList<ChildEntry> GetOrQueryChildren(string parentDocId)
    {
        if (_childCache.Get(parentDocId) is { } cached) return cached;
        var children = QueryChildren(parentDocId);
        if (children == null) return Array.Empty<ChildEntry>(); // cursor null：按空处理，不缓存（防瞬时故障污染）
        _childCache.Put(parentDocId, children);
        return children;
    }

    /// <summary>
    /// O4 后台预取：预热指定路径目录的子项缓存（内部走 <see cref="GetOrQueryChildren"/>——已缓存则跳过）。
    /// 减少后续对该目录的枚举/解析 IPC（典型用法：游戏加载完成后后台预热 sav 与游戏根目录）。
    /// 失败静默（仅损失一次预取，不影响功能）；不写日志（预取产生的 Query 由 QueryChildren 正常记录）。
    /// </summary>
    internal void WarmDirectoryCache(string path)
    {
        try
        {
            if (_treeAndroidUri == null || !TryParseUri(path, out var docUri)) return;
            GetOrQueryChildren(ResolveDocId(docUri));
        }
        catch { /* 预取失败静默 */ }
    }

    /// <summary>唯一的 children Query 点：一次拉全量三列不过滤；null / 异常语义与原 EnumerateUri 一致。</summary>
    private List<ChildEntry>? QueryChildren(string parentDocId)
    {
        var childrenUri = DocumentsContract.BuildChildDocumentsUriUsingTree(_treeAndroidUri!, parentDocId);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            using var cursor = _context.ContentResolver!.Query(
                childrenUri!,
                new[]
                {
                    DocumentsContract.Document.ColumnDocumentId,
                    DocumentsContract.Document.ColumnDisplayName,
                    DocumentsContract.Document.ColumnMimeType
                },
                null, null, null);
            if (cursor == null)
            {
                sw.Stop();
                LogSaf("EnumerateUri", parentDocId, "children=0", sw.ElapsedMilliseconds);
                return null;
            }
            var children = new List<ChildEntry>();
            while (cursor.MoveToNext())
            {
                var docId = cursor.GetString(0);
                var name = cursor.GetString(1);
                var mime = cursor.GetString(2);
                children.Add(new ChildEntry(docId!, name!, mime!));
            }
            sw.Stop();
            LogSaf("EnumerateUri", parentDocId, $"children={children.Count}", sw.ElapsedMilliseconds);
            return children;
        }
        catch (Exception ex)
        {
            // A1：Query 异常也记录（失败路径同样有取证价值），保持原传播行为（上层有 catch）
            sw.Stop();
            LogSaf("EnumerateUri", parentDocId, "query-ex", sw.ElapsedMilliseconds, ok: false, failMsg: ex.Message);
            throw;
        }
    }

    /// <summary>
    /// 通配匹配用名：优先 displayName；若 displayName 为 null/空或无扩展名，回退 documentId 最后一段
    /// （部分 Provider 只给短名/空名）。原 EnumerateUri 内联逻辑（O1 改造原样保留）。
    /// </summary>
    private static string DisplayNameForMatch(ChildEntry child, string pattern)
        => !string.IsNullOrEmpty(child.DocId)
            && (child.Name == null || child.Name.Length == 0
                || (pattern.Contains('.') && child.Name.IndexOf('.') < 0))
                ? (child.DocId.LastIndexOf('/') is var s && s >= 0 ? child.DocId[(s + 1)..] : child.DocId)
                : child.Name;

    private string TreeRootDocumentPath()
    {
        if (_treeAndroidUri == null) return _treeUri ?? "";
        // 树 URI 本身可作为 base；CombinePath/ResolveDocId 能处理
        var docId = DocumentsContract.GetTreeDocumentId(_treeAndroidUri);
        var docUri = DocumentsContract.BuildDocumentUriUsingTree(_treeAndroidUri, docId);
        return docUri?.ToString() ?? _treeUri ?? "";
    }

    private bool IsTreeRootUri(Android.Net.Uri uri)
    {
        if (_treeAndroidUri == null) return false;
        try
        {
            var treeId = DocumentsContract.GetTreeDocumentId(_treeAndroidUri);
            var docId = ResolveDocId(uri);
            return string.Equals(treeId, docId, StringComparison.Ordinal);
        }
        catch { return false; }
    }

    private bool DirectoryDocumentExists(string path)
    {
        try
        {
            if (_treeAndroidUri == null || !TryParseUri(path, out var docUri)) return false;
            return EnsureRealDirectoryUri(docUri) != null;
        }
        catch { return false; }
    }

    /// <summary>
    /// 将可能为「理论」的目录 URI 解析为 provider 中真实存在的目录 URI；
    /// 若不存在则在真实父下 CreateDocument(DIR)。
    /// </summary>
    private Android.Net.Uri? EnsureRealDirectoryUri(Android.Net.Uri dirUri)
    {
        if (_treeAndroidUri == null) return null;
        if (IsTreeRootUri(dirUri))
            return DocumentsContract.BuildDocumentUriUsingTree(
                _treeAndroidUri, DocumentsContract.GetTreeDocumentId(_treeAndroidUri));

        if (TryQueryDocument(dirUri, out var mime) && mime == DocumentsContract.Document.MimeTypeDir)
            return dirUri;

        var path = dirUri.ToString()!;
        if (!TryGetParentAndName(path, out var parentUri, out var name) || name == null || parentUri == null)
            return null;

        // 父目录：树根 / 已存在 / 再对父做一级 Ensure（仅支持「根下一级」，如 sav/dat）
        Android.Net.Uri? effectiveParent;
        if (IsTreeRootUri(parentUri))
        {
            effectiveParent = DocumentsContract.BuildDocumentUriUsingTree(
                _treeAndroidUri, DocumentsContract.GetTreeDocumentId(_treeAndroidUri));
        }
        else if (TryQueryDocument(parentUri, out var parentMime)
                 && parentMime == DocumentsContract.Document.MimeTypeDir)
        {
            effectiveParent = parentUri;
        }
        else
        {
            // 父仍是理论路径：若父的父是树根，则在树根下创建/查找父名
            if (!TryGetParentAndName(parentUri.ToString()!, out var grand, out var parentName)
                || grand == null || parentName == null)
                return null;
            if (!IsTreeRootUri(grand)
                && !(TryQueryDocument(grand, out var gm) && gm == DocumentsContract.Document.MimeTypeDir))
            {
                Android.Util.Log.Warn("EmueraMaui",
                    $"EnsureRealDirectoryUri: parent depth >1 not supported yet: {path}");
                return null;
            }
            var grandReal = IsTreeRootUri(grand)
                ? DocumentsContract.BuildDocumentUriUsingTree(
                    _treeAndroidUri, DocumentsContract.GetTreeDocumentId(_treeAndroidUri))
                : grand;
            if (grandReal == null) return null;
            effectiveParent = FindChildDocument(grandReal, parentName, wantDir: true);
            if (effectiveParent == null)
            {
                effectiveParent = DocumentsContract.CreateDocument(
                    _context.ContentResolver!,
                    grandReal,
                    DocumentsContract.Document.MimeTypeDir,
                    parentName);
                if (effectiveParent != null)
                {
                    // O1：新目录已创建在 grandReal 下 → 失效 grandReal 自身缓存
                    // （FindChildDocument 可能刚回填了不含新名的列表，key 错位会留下陈旧视图）
                    try { _childCache.Invalidate(ResolveDocId(grandReal)); }
                    catch { /* 失效失败仅损失一次缓存命中 */ }
                }
            }
        }

        if (effectiveParent == null) return null;

        var existing = FindChildDocument(effectiveParent, name, wantDir: true);
        if (existing != null) return existing;

        var created = DocumentsContract.CreateDocument(
            _context.ContentResolver!,
            effectiveParent,
            DocumentsContract.Document.MimeTypeDir,
            name);
        if (created != null)
        {
            Android.Util.Log.Info("EmueraMaui", $"EnsureRealDirectoryUri created {name} → {created}");
            // O1：新目录已创建在 effectiveParent 下 → 失效 effectiveParent 自身缓存
            // （FindChildDocument 可能刚回填了不含新名的列表，key 错位会留下陈旧视图）
            try { _childCache.Invalidate(ResolveDocId(effectiveParent)); }
            catch { /* 失效失败仅损失一次缓存命中 */ }
        }
        return created;
    }

    private bool TryQueryDocument(Android.Net.Uri docUri, out string? mime)
    {
        mime = null;
        var docId = ResolveDocId(docUri);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            using var cursor = _context.ContentResolver!.Query(
                docUri,
                new[] { DocumentsContract.Document.ColumnMimeType },
                null, null, null);
            if (cursor?.MoveToFirst() != true)
            {
                sw.Stop();
                LogSaf("TryQueryDocument", docId, "hit=0", sw.ElapsedMilliseconds);
                return false;
            }
            mime = cursor.GetString(0);
            sw.Stop();
            LogSaf("TryQueryDocument", docId, $"hit=1 mime={mime}", sw.ElapsedMilliseconds);
            return true;
        }
        catch (Exception ex)
        {
            sw.Stop();
            LogSaf("TryQueryDocument", docId, "query-ex", sw.ElapsedMilliseconds, ok: false, failMsg: ex.Message);
            return false;
        }
    }

    /// <summary>解析已存在文件的真实 document URI；不存在返回 null。</summary>
    private Android.Net.Uri? ResolveExistingFileUri(string path)
    {
        if (_treeAndroidUri == null || !TryParseUri(path, out var docUri)) return null;

        // O1 快速路径：父目录已缓存 → 从缓存按名查文件（0 IPC 确定性判定；
        // FileExists / OpenRead / OpenWrite / Delete 统一受益：每文件 3 次 IPC → 0 次）
        var docId = ResolveDocId(docUri);
        var slash = docId.LastIndexOf('/');
        var parentId = slash < 0 ? DocumentsContract.GetTreeDocumentId(_treeAndroidUri)! : docId[..slash];
        var name = slash < 0 ? docId : docId[(slash + 1)..];
        if (_childCache.Get(parentId) is { } children)
        {
            foreach (var child in children)
            {
                if (child.Mime == DocumentsContract.Document.MimeTypeDir) continue;
                if (NameMatches(child, name))
                    return DocumentsContract.BuildDocumentUriUsingTree(_treeAndroidUri, child.DocId);
            }
            return null; // 缓存确认不存在
        }

        if (TryQueryDocument(docUri, out var mime)
            && mime != null
            && mime != DocumentsContract.Document.MimeTypeDir)
            return docUri;

        if (!TryGetParentAndName(path, out var parent, out var parentName)) return null;
        return FindChildDocument(parent, parentName, wantDir: false);
    }

    private Android.Net.Uri? FindChildDocument(Android.Net.Uri parentUri, string displayName, bool wantDir)
    {
        if (_treeAndroidUri == null) return null;
        var parentId = ResolveDocId(parentUri);
        try
        {
            // O1：父目录缓存命中 → 零 IPC；未命中走 QueryChildren（op=EnumerateUri）并顺手回填缓存。
            // 命中路径不打 [saf] 日志（零日志原则）；异常由本 catch 吞掉（原语义）。
            foreach (var child in GetOrQueryChildren(parentId))
            {
                if ((child.Mime == DocumentsContract.Document.MimeTypeDir) != wantDir) continue;
                if (NameMatches(child, displayName))
                    return DocumentsContract.BuildDocumentUriUsingTree(_treeAndroidUri, child.DocId);
            }
        }
        catch (Exception ex)
        {
            Android.Util.Log.Warn("EmueraMaui", $"FindChildDocument({displayName}) → {ex.Message}");
        }
        return null;
    }

    /// <summary>子项名两级匹配（原 FindChildDocument 语义）：displayName 未中 → documentId 末段。</summary>
    private static bool NameMatches(ChildEntry child, string displayName)
        => string.Equals(child.Name, displayName, StringComparison.OrdinalIgnoreCase)
            || (!string.IsNullOrEmpty(child.DocId)
                && string.Equals(
                    child.DocId.LastIndexOf('/') is var s && s >= 0 ? child.DocId[(s + 1)..] : child.DocId,
                    displayName, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// 从 document/tree 路径拆出父目录 URI 与逻辑短名。
    /// documentId 形如 primary:Download/game/sav/global.sav → parent=.../sav, name=global.sav
    /// </summary>
    private bool TryGetParentAndName(string path, [NotNullWhen(true)] out Android.Net.Uri? parentUri, [NotNullWhen(true)] out string? name)
    {
        parentUri = null;
        name = null;
        if (_treeAndroidUri == null || !TryParseUri(path, out var docUri)) return false;

        var docId = ResolveDocId(docUri);
        var lastSlash = docId.LastIndexOf('/');
        if (lastSlash < 0)
        {
            // 单段 documentId：父为树根
            name = docId;
            var treeId = DocumentsContract.GetTreeDocumentId(_treeAndroidUri);
            parentUri = DocumentsContract.BuildDocumentUriUsingTree(_treeAndroidUri, treeId);
            return parentUri != null && !string.IsNullOrEmpty(name);
        }

        var parentId = docId[..lastSlash];
        name = docId[(lastSlash + 1)..];
        if (string.IsNullOrEmpty(name)) return false;
        parentUri = DocumentsContract.BuildDocumentUriUsingTree(_treeAndroidUri, parentId);
        return parentUri != null;
    }

    private Stream? TryOpenOutputStream(Android.Net.Uri docUri, string mode)
    {
        try
        {
            return _context.ContentResolver!.OpenOutputStream(docUri, mode);
        }
        catch (Exception ex)
        {
            Android.Util.Log.Warn("EmueraMaui", $"OpenOutputStream({mode}) failed: {ex.Message}");
            return null;
        }
    }

    private static string GuessMime(string displayName)
    {
        var ext = Path.GetExtension(displayName);
        if (ext.Equals(".txt", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".csv", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".erb", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".erh", StringComparison.OrdinalIgnoreCase))
            return "text/plain";
        return OctetStreamMime;
    }

    /// <summary>
    /// SAF 写缓冲流：引擎侧写内存（可 Seek），Dispose 时一次 OpenOutputStream + CopyTo。
    /// 避免 BinaryWriter 对数十 MB 存档逐字段打 ContentResolver 导致假死。
    /// </summary>
    private sealed class DeferredSafWriteStream : Stream
    {
        private readonly Context _context;
        private readonly Android.Net.Uri _docUri;
        private readonly string _pathForLog;
        private readonly bool _createdDocument;
        private readonly MemoryStream _buffer = new();
        private bool _disposed;
        private bool _writeFailed;

        public DeferredSafWriteStream(
            Context context, Android.Net.Uri docUri, string pathForLog, bool createdDocument)
        {
            _context = context;
            _docUri = docUri;
            _pathForLog = pathForLog;
            _createdDocument = createdDocument;
        }

        public override bool CanRead => false;
        public override bool CanSeek => true;
        public override bool CanWrite => true;
        public override long Length => _buffer.Length;
        public override long Position
        {
            get => _buffer.Position;
            set => _buffer.Position = value;
        }

        public override void Flush() => _buffer.Flush();

        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException("DeferredSafWriteStream is write-only");

        public override long Seek(long offset, SeekOrigin origin) => _buffer.Seek(offset, origin);

        public override void SetLength(long value)
        {
            try
            {
                EnsureWithinLimit(value);
                _buffer.SetLength(value);
            }
            catch
            {
                _writeFailed = true;
                throw;
            }
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            try
            {
                EnsureWriteWithinLimit(count);
                _buffer.Write(buffer, offset, count);
            }
            catch
            {
                _writeFailed = true;
                throw;
            }
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            try
            {
                EnsureWriteWithinLimit(buffer.Length);
                _buffer.Write(buffer);
            }
            catch
            {
                _writeFailed = true;
                throw;
            }
        }

        public override void WriteByte(byte value)
        {
            try
            {
                EnsureWriteWithinLimit(1);
                _buffer.WriteByte(value);
            }
            catch
            {
                _writeFailed = true;
                throw;
            }
        }

        private void EnsureWriteWithinLimit(int count)
        {
            var limit = DeferredWriteLimitBytes;
            if (count < 0 || _buffer.Position > limit - count)
                throw new IOException(
                    $"DeferredSafWriteStream limit exceeded for {_pathForLog}; limit={limit} bytes");
        }

        private void EnsureWithinLimit(long value)
        {
            var limit = DeferredWriteLimitBytes;
            if (value < 0 || value > limit)
                throw new IOException(
                    $"DeferredSafWriteStream limit exceeded for {_pathForLog}; limit={limit} bytes");
        }

        protected override void Dispose(bool disposing)
        {
            if (_disposed) return;
            _disposed = true;
            if (disposing)
            {
                // sw 声明在 try 外——catch 里也要访问（记 FAIL 耗时）
                var sw = System.Diagnostics.Stopwatch.StartNew();
                try
                {
                    if (_writeFailed)
                    {
                        DeleteCreatedDocument();
                        base.Dispose(disposing);
                        return;
                    }
                    var bytes = _buffer.Length;
                    _buffer.Position = 0;
                    using var output = _context.ContentResolver!.OpenOutputStream(_docUri, "wt")
                        ?? _context.ContentResolver.OpenOutputStream(_docUri, "w")
                        ?? throw new IOException($"DeferredSafWriteStream: OpenOutputStream null for {_docUri}");
                    _buffer.CopyTo(output);
                    output.Flush();
                    sw.Stop();
                    // O4 微优化：覆盖写（已存在文件）不改变子项名/mime，父目录缓存无需失效——
                    // 每次写档清父缓存会抵消 O4 预取收益（写档后 sav 缓存被清，下次进存档界面又重枚举）。
                    // 仅新建文档需要：OpenWrite created 分支（CreateDocument 后）已失效过父目录，
                    // 此处保留 createdDocument 失效为双重保险（防 CreateDocument 与 Dispose 间被枚举回填）。
                    if (_createdDocument)
                    {
                        try { SafGameDirAccessor.Instance?.InvalidateParentOf(ResolveDocId(_docUri)); }
                        catch { /* 失效失败仅损失一次缓存命中，不影响写路径结果 */ }
                    }
                    LogSaf("WriteDispose", ResolveDocId(_docUri), $"bytes={bytes}", sw.ElapsedMilliseconds);
                    Android.Util.Log.Info("EmueraMaui",
                        $"OpenWrite flush OK path={_pathForLog} bytes={bytes} ms={sw.ElapsedMilliseconds}");
                }
                catch (Exception ex)
                {
                    sw.Stop();
                    LogSaf("WriteDispose", ResolveDocId(_docUri), "flush-ex", sw.ElapsedMilliseconds, ok: false, failMsg: ex.Message);
                    Android.Util.Log.Error("EmueraMaui",
                        $"OpenWrite flush FAIL path={_pathForLog} ex={ex}");
                    DeleteCreatedDocument();
                    throw;
                }
                finally
                {
                    _buffer.Dispose();
                }
            }
            base.Dispose(disposing);
        }

        private void DeleteCreatedDocument()
        {
            if (!_createdDocument) return;
            try
            {
                DocumentsContract.DeleteDocument(_context.ContentResolver!, _docUri);
                // O1：失败清理删掉了刚建的空文档 → 失效其父目录
                try { SafGameDirAccessor.Instance?.InvalidateParentOf(ResolveDocId(_docUri)); }
                catch { /* 失效失败仅损失一次缓存命中 */ }
                Android.Util.Log.Info("EmueraMaui",
                    $"OpenWrite cleanup removed incomplete document path={_pathForLog}");
            }
            catch (Exception cleanupEx)
            {
                Android.Util.Log.Warn("EmueraMaui",
                    $"OpenWrite cleanup failed path={_pathForLog} ex={cleanupEx.Message}");
            }
        }
    }
}
#endif
