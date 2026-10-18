using System;
using System.IO;

namespace MinorShift.Emuera;

internal sealed class GamePaths
{
    public static GamePaths Current { get; private set; } = null!;

    public string ExeDir { get; }
    public string CsvDir { get; }
    public string ErbDir { get; }
    public string DebugDir { get; }
    public string DatDir { get; }
    public string ContentDir { get; }
    public string SoundDir { get; }
    public string FontDir { get; }
    /// <summary>ADR-0019：游戏目录访问抽象。必传——调用方负责注入平台实现。</summary>
    public IGameDirAccessor DirAccessor { get; set; } = null!;

    private GamePaths(string exeDir, IGameDirAccessor dirAccessor)
    {
        bool isContentUri = exeDir.StartsWith("content://", StringComparison.Ordinal);

        if (isContentUri)
        {
            ExeDir = exeDir;
            CsvDir = dirAccessor?.ResolveSubPath(exeDir, "csv") ?? exeDir;
            ErbDir = dirAccessor?.ResolveSubPath(exeDir, "erb") ?? exeDir;
            DebugDir = dirAccessor?.ResolveSubPath(exeDir, "debug") ?? exeDir;
            DatDir = dirAccessor?.ResolveSubPath(exeDir, "dat") ?? exeDir;
            ContentDir = dirAccessor?.ResolveSubPath(exeDir, "resources") ?? exeDir;
            SoundDir = dirAccessor?.ResolveSubPath(exeDir, "sound") ?? exeDir;
            FontDir = dirAccessor?.ResolveSubPath(exeDir, "font") ?? exeDir;
        }
        else
        {
            ExeDir = Path.GetFullPath(new DirectoryInfo(exeDir).FullName + Path.DirectorySeparatorChar);
            CsvDir = Path.Combine(ExeDir, "csv") + Path.DirectorySeparatorChar;
            ErbDir = Path.Combine(ExeDir, "erb") + Path.DirectorySeparatorChar;
            DebugDir = Path.Combine(ExeDir, "debug") + Path.DirectorySeparatorChar;
            DatDir = Path.Combine(ExeDir, "dat") + Path.DirectorySeparatorChar;
            ContentDir = Path.Combine(ExeDir, "resources") + Path.DirectorySeparatorChar;
            SoundDir = Path.Combine(ExeDir, "sound") + Path.DirectorySeparatorChar;
            FontDir = Path.Combine(ExeDir, "font") + Path.DirectorySeparatorChar;
        }
    }

    public static GamePaths Resolve(string? exeDirArg, IGameDirAccessor dirAccessor)
    {
        var paths = ResolvePreview(exeDirArg, dirAccessor);
        Current = paths;
        return paths;
    }

    /// <summary>
    /// 只构造并可随后 <see cref="Validate"/>，**不写** <see cref="Current"/>。
    ///
    /// issue-001：/load-game 需在「拆旧会话」之前完成路径校验（拆旧前拦路径级错误），
    /// 而 <see cref="Resolve"/> 会把 <see cref="Current"/> 指向新目录。旧游戏循环仍在运行时
    /// （拆除超时、尚未退出）读到被改写的 <see cref="Current"/> 会去新目录找 ERB/CSV——
    /// 故校验阶段必须用本方法，<see cref="Resolve"/> 推迟到旧循环确实退出之后再提交。
    /// </summary>
    public static GamePaths ResolvePreview(string? exeDirArg, IGameDirAccessor dirAccessor)
    {
        var baseDirectory = exeDirArg ?? DetectDefault();
        var paths = new GamePaths(baseDirectory, dirAccessor);
        paths.DirAccessor = dirAccessor;
        return paths;
    }

    /// <summary>
    /// 显式重设静态 <see cref="Current"/>——issue 05 /load-game 校验失败时回滚到旧值。
    /// 仅 server 内部使用，CLI / 启动期不调。
    /// </summary>
    internal static void SetCurrent(GamePaths paths)
    {
        Current = paths;
    }

    private static string DetectDefault()
    {
        var baseDirectory = AppContext.BaseDirectory;
        if (Directory.Exists(Path.Combine(baseDirectory, "Data", "erb")))
            baseDirectory = Path.Combine(baseDirectory, "Data");
        return baseDirectory;
    }

    /// <summary>
    /// 校验游戏目录结构。失败时抛 <see cref="GamePathValidationException"/>（issue 05）——
    /// 旧实现调 <c>Environment.Exit(1)</c>，会让 /load-game 校验失败时杀死整个 server 进程，
    /// 故改为抛异常：CLI 启动路径在 Program.Main 捕获后退出，server /load-game 路径捕获后返 400。
    /// <para>
    /// <b>不变量 F-1</b>：真实路径分支同样经 <see cref="DirAccessor"/>——过去这里裸调
    /// <c>Directory.Exists</c>（已收敛，<b>不再</b>计入 ARCHITECTURE.md §4.3 例外清单），使「 existence 判定走哪套语义」
    /// 取决于路径形态而非访问器。本地访问器本来就是 <c>Directory.Exists</c> 的超集
    /// （多一层大小写容错，归约后与原语义一致）。
    /// </para>
    /// </summary>
    public void Validate()
    {
        if (!DirAccessor.DirectoryExists(ExeDir))
            throw new GamePathValidationException("DIR_NOT_FOUND", $"目录不存在: {ExeDir}");
        if (!DirAccessor.DirectoryExists(CsvDir))
            throw new GamePathValidationException("MISSING_CSV", $"缺少 csv 目录: {CsvDir}");
        if (!DirAccessor.DirectoryExists(ErbDir))
            throw new GamePathValidationException("MISSING_ERB", $"缺少 erb 目录: {ErbDir}");
    }
}

/// <summary>
/// 游戏目录校验失败异常（issue 05）。<see cref="Code"/> 与前端错误契约对称：
/// <c>DIR_NOT_FOUND</c> / <c>MISSING_CSV</c> / <c>MISSING_ERB</c>。
/// </summary>
internal sealed class GamePathValidationException : Exception
{
    public string Code { get; }
    public GamePathValidationException(string code, string message) : base(message)
    {
        Code = code;
    }
}
