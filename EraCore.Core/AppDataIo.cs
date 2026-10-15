using System;
using System.IO;

namespace MinorShift.Emuera;

/// <summary>
/// <see cref="AppDataPaths"/>（应用私有域）的 IO 窄口子 —— 不变量 F-4「<b>唯一豁免域</b>」的可实例化形态。
/// <para>
/// <b>为什么不是第二个 accessor</b>：私有域不是 <see cref="IGameDirAccessor"/> 的第二适配器——
/// 它与平台无关，恒为真实本地文件系统路径，不存在「隐藏平台差异」这一层收益。
/// 它的价值只在一点：把「可以不走 accessor」这件事<b>收进一处</b>，而不是散落成若干句
/// <c>File.*</c> / <c>Directory.*</c> 各自援引一条无人守着的规矩。
/// </para>
/// <para>
/// <b>接缝：路径凭据</b>。本类所有 IO 方法收 <see cref="AppDataPath"/> 而非字符串路径，
/// 而 <see cref="AppDataPath"/> 只能经 <see cref="Resolve"/> 从「相对私有根的名字」取得 ——
/// 「越过豁免域」在调用方就不可表达。
/// </para>
/// <para>
/// 凭据的另一个作用是<b>固定时序</b>：<see cref="AppDataPaths.Directory"/> 是可变全局状态
/// （宿主/测试可重配），因此在写句柄时取到的凭据必须被持有并复用；
/// 反复调用 <see cref="Resolve"/> 会把 IO 导向「当下的根」而非当初那份路径
/// （T-027 教训：并行切换私有根时读到了别处的文件）。这也解释了为何这里<b>不做</b>
/// 「每次 IO 前复查 IsUnder」——复查会同样错判「创建时合法、之后根变了」的旧凭据。
/// </para>
/// <para>
/// <b>不为游戏内容服务</b>：这里不承载任何 CSV / ERB / 资源的 IO —— 那部分恒经
/// <see cref="IGameDirAccessor"/>（F-1）。混淆二者会让 SAF 下重新静默失败。
/// </para>
/// </summary>
internal static class AppDataIo
{
    /// <summary>
    /// 把相对名解析为私有域内的路径凭据。拒绝绝对路径 / content URI / 空串——
    /// 三者都意味着调用方试图越过本域。
    /// </summary>
    public static AppDataPath Resolve(string relativeName)
    {
        if (string.IsNullOrWhiteSpace(relativeName))
            throw new ArgumentException("App-data relative name is required.", nameof(relativeName));
        if (relativeName.StartsWith("content://", StringComparison.Ordinal))
            throw new ArgumentException("App-data names must be relative, not a SAF content URI.", nameof(relativeName));
        if (Path.IsPathRooted(relativeName))
            throw new ArgumentException("App-data paths must be relative to the private root.", nameof(relativeName));
        return new AppDataPath(AppDataPaths.CombinePath(relativeName));
    }

    internal static bool FileExists(AppDataPath path) => File.Exists(path.Value);

    internal static void Copy(AppDataPath source, AppDataPath destination, bool overwrite)
        => File.Copy(source.Value, destination.Value, overwrite);

    /// <summary>确保凭据所在目录存在（幂等）。</summary>
    internal static void CreateDirectoryOf(AppDataPath path)
    {
        var dir = Path.GetDirectoryName(path.Value);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
    }

    internal static void WriteAllText(AppDataPath path, string contents)
    {
        CreateDirectoryOf(path);
        File.WriteAllText(path.Value, contents);
    }

    internal static string ReadAllText(AppDataPath path) => File.ReadAllText(path.Value);

    /// <summary>
    /// 共享读打开：<c>FileShare.ReadWrite</c>。
    /// 写入方以 <c>FileAccess.Write</c> 长期持有句柄时，读取方必须允许「他人写」，
    /// 否则两个句柄的共享模式冲突 → <c>IOException: file in use</c>。
    /// </summary>
    internal static Stream OpenReadShared(AppDataPath path)
        => new FileStream(path.Value, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
}

/// <summary>
/// 应用私有域内的一条路径凭据。构造函数为 <c>internal</c>，外部唯一来源是
/// <see cref="AppDataIo.Resolve"/> —— 拿不到域外路径，也改不了里面的值。
/// </summary>
internal readonly struct AppDataPath
{
    internal AppDataPath(string value) => Value = value;

    /// <summary>凭据对应的绝对路径。</summary>
    public string Value { get; }

    public override string ToString() => Value;
}
