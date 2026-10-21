using MinorShift.Emuera.Files;
using MinorShift.Emuera.Terminal.Platform;
using MinorShift.Emuera.Runtime.Utils;
using MinorShift.Emuera.UI.Game.Image;

namespace MinorShift.Emuera.Assets;

/// <summary>
/// 05 — 平台无关的资源通道：消毒 → 白名单 → 读字节 → MIME 四步收敛。
/// spec Q5「校验函数 C# 侧单点实现」——03 Kestrel /assets handler 的内联逻辑共享化，
/// 05 双平台（Windows WebResourceRequested 拦截 / 安卓 WebViewAssetLoader PathHandler）复用。
/// </summary>
internal static class AssetChannel
{
    /// <summary>
    /// 图片响应头（三处共用：Kestrel /assets、Windows 拦截、安卓 PathHandler）——
    /// 改缓存策略只动这里。缓存 1 天：游戏目录为只读资产，但 /load-game 换目录后
    /// 同 URL 可能对应新文件，不用 immutable。
    /// </summary>
    public const string CacheControlHeader = "public, max-age=86400";

    /// <summary>srcm canvas 读像素需要 CORS 允许；图片 GET 无凭据，* 安全。</summary>
    public const string CorsAllowOriginHeader = "*";

    /// <summary>
    /// 尝试从游戏目录读取一张图片。失败（消毒拒绝 / 白名单外 / 不存在 / 读失败）返回 false。
    /// </summary>
    /// <param name="dirAccessor">游戏目录访问抽象（FileSystem / SAF）。</param>
    /// <param name="gameRoot">游戏根目录——普通路径或 content:// 树 URI（GamePaths.ExeDir 形态）。</param>
    /// <param name="urlPath">URL 相对路径（如 <c>img/portrait.png</c>）。</param>
    /// <param name="bytes">图片字节。</param>
    /// <param name="mimeType">MIME 类型（如 <c>image/png</c>）。</param>
    public static bool TryGetImage(
        IGameDirAccessor dirAccessor,
        string gameRoot,
        string urlPath,
        out byte[] bytes,
        out string mimeType)
    {
        bytes = null!;
        mimeType = null!;
        if (dirAccessor == null || string.IsNullOrEmpty(gameRoot) || string.IsNullOrEmpty(urlPath))
            return false;

        // 1. 直接相对路径（test_game 的 img/test.png 形态）
        if (TryReadImage(dirAccessor, gameRoot, urlPath, out bytes, out mimeType))
            return true;

        // 2. sprite 名表回退（era 的 <img src='Face_1'>：src 是 sprite 名，
        //    映射在 resources/*.csv，真实文件如 resources/1_Face.png）
        if (ImageNameTable.TryResolve(dirAccessor, gameRoot, urlPath, out var rel)
            && rel != null
            && TryReadImage(dirAccessor, gameRoot, rel, out bytes, out mimeType))
            return true;

        return false;
    }

    /// <summary>按相对路径执行完整四步：消毒 → 白名单 → CombinePath 重建读取 → MIME。</summary>
    private static bool TryReadImage(
        IGameDirAccessor dirAccessor,
        string gameRoot,
        string urlPath,
        out byte[] bytes,
        out string mimeType)
    {
        bytes = null!;
        mimeType = null!;

        // 1. 路径消毒：TryResolve 产出 fullPath 只用于校验（落根内 + 白名单）。
        //    注意：content 分支的 fullPath 是裸拼接形态（content://.../tree/<id>/img/x.png），
        //    不是合法 document URI（03 注释预警的坑）——**不能**直接喂 ReadAllBytes。
        if (!AssetPathValidator.TryResolve(gameRoot, urlPath, out var fullPath))
            return false;

        // 2. 扩展名白名单：只服务图片类型，其他一律拒绝（ERB/CSV/HTML 源文件不泄露）
        if (!AssetPathValidator.IsAllowedImage(fullPath))
            return false;

        // 3. 读字节——读取路径必须经 CombinePath 重建：SAF 实现用
        //    BuildDocumentUriUsingTree 正确编码（裸拼接会被 Android 解析成目录本身，
        //    ResolveDocId 只取 tree 第一段、后续路径被丢弃 → 恒 404）；
        //    FileSystem 实现是 Path.Combine（与 TryResolve 规范化产物一致）。
        //    urlPath 已通过 TryResolve 消毒（无 .. / 绝对路径 / 开头斜杠），CombinePath 产物必然落根内。
        var readPath = dirAccessor.CombinePath(gameRoot, urlPath);
        var data = dirAccessor.ReadAllBytes(readPath);
        if (data == null || data.Length == 0)
            return false;

        // 4. MIME（白名单内必然命中映射）
        mimeType = AssetPathValidator.GetMimeType(fullPath);
        bytes = data;
        return true;
    }
}
