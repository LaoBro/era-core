using System;
using System.IO;

namespace MinorShift.Emuera.Files;

internal sealed class GamePaths
{
    /// <summary>
    /// 游戏根的<b>只读视图</b>（issue-007）——不再是可写的进程级单例字段。
    /// 值来自 <see cref="GameRootEnvironment"/>：本执行作用域建立的根优先，
    /// 其次装配期最后一次提交的根。
    /// <para>保留它是为了给尚未迁移的读者（MAUI UI 线程、既有测试脚手架）一个不断档的读法；
    /// 引擎与服务端调用点已在 issue-008 分批改为显式根，issue-009 删除装配期视图。</para>
    /// </summary>
    public static GamePaths Current => GameRootEnvironment.Current!;

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

    /// <summary>
    /// 构造根并<b>提交到根环境</b>（<see cref="ResolvePreview"/> 与它的差异只剩这一步）。
    /// issue-007 起提交动作写的是 <see cref="GameRootEnvironment"/>（作用域层 + 装配期视图），
    /// 不再存在「改写一个进程级单例字段」这回事。
    /// </summary>
    public static GamePaths Resolve(string? exeDirArg, IGameDirAccessor dirAccessor)
    {
        var paths = ResolvePreview(exeDirArg, dirAccessor);
        GameRootEnvironment.Commit(paths);
        return paths;
    }

    /// <summary>
    /// 只构造、可随后 <see cref="Validate"/>，**不提交**给根环境。
    ///
    /// issue-001：/load-game 需在「拆旧会话」之前完成路径校验（拆旧前拦路径级错误）。
    /// issue-007 起这条顺序不再是必需的安全措施——会话作用域的根环境建立后，
    /// 后续提交改写不了正在拆除的旧循环读到的根；但校验阶段确实还没有根要提交，
    /// 所以本方法仍是校验的唯一入口（两者的差异只剩「是否提交」）。
    /// </summary>
    public static GamePaths ResolvePreview(string? exeDirArg, IGameDirAccessor dirAccessor)
    {
        var baseDirectory = exeDirArg ?? DetectDefault();
        var paths = new GamePaths(baseDirectory, dirAccessor);
        paths.DirAccessor = dirAccessor;
        return paths;
    }

    /// <summary>
    /// 旧 API：提交一个已构造好的根给根环境（等价于 <see cref="Resolve"/> 的提交那一步，
    /// 不重新构造）。issue 05 /load-game 校验失败时用它回滚装配期视图；
    /// 迁移完成的 issue-009 删除。
    /// </summary>
    internal static void SetCurrent(GamePaths paths) => GameRootEnvironment.Commit(paths);

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
    /// <c>Directory.Exists</c>（已收敛），使「 existence 判定走哪套语义」
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
