using MinorShift.Emuera.Files;
using System;
using System.IO;

namespace MinorShift.Emuera.GameView
{
    internal sealed class AgentLog : IDisposable
    {
        /// <summary>
        /// <see cref="Configure(bool)"/> 记录的初值——null 表示从未调用（走环境变量语义）。
        /// 静态字段：CLI/Server 不调 Configure，Lazy 构造时按环境变量（默认开）；MAUI 启动早期
        /// 调 Configure(false) 覆盖（A0：Android 默认关闭，UI 开关按需开启）。
        /// </summary>
        private const string LogFileName = "agent.log";
        private const string PrevLogName = "agent.prev.log";

        private static bool? s_configuredEnabled;

        private static readonly Lazy<AgentLog> s_instance = new(() => new AgentLog());

        public static AgentLog Instance => s_instance.Value;

        /// <summary>
        /// 启动早期调用（<b>须在首次 <see cref="Instance"/> 访问之前</b>）——决定 Lazy 初值。
        /// <para>
        /// 时序要求：Lazy 单例只在第一次访问 <see cref="Instance"/> 时构造，构造时读此字段；
        /// 之后调用无效（实例已按旧初值创建）。MAUI 在 <c>MauiProgram.CreateMauiApp</c> 最早期
        /// （任何 BridgeHost 访问 Instance 之前）读 Preferences 后调用。
        /// </para>
        /// <para>
        /// 韧性兜底：若 Instance 已被访问（Lazy 已初始化，如并行单测场景），立即把值应用到
        /// 实例（等价运行时切换）——保证任何调用时序下 Configure 都生效。
        /// </para>
        /// <para>
        /// CLI / Server 不调用——保留 <c>EMUERA_AGENT_LOG</c> 环境变量语义（未设默认开），
        /// 调试与 CI 能力不降级。
        /// </para>
        /// </summary>
        public static void Configure(bool enabled)
        {
            s_configuredEnabled = enabled;
            if (s_instance.IsValueCreated)
                s_instance.Value.Enabled = enabled;
        }

        private readonly object _lock = new();
        private bool _enabled;
        private StreamWriter? _writer;
        private bool _disposed;
        /// <summary>
        /// 当前日志的路径凭据。持凭据而非字符串的原因：<see cref="AppDataPaths.Directory"/> 是可变全局状态
        /// （并行测试 / 宿主可重配），writer 建立时取到的路径必须在后续读写中原样复用。
        /// </summary>
        private AppDataPath? _logPath;

        /// <summary>
        /// 是否启用。UI 开关可运行时切换（即时生效，无需重启）——A0。
        /// <para>
        /// 开启时惰性创建 writer（若尚未创建）；关闭时不销毁 writer（再次开启复用，
        /// 避免反复创建文件句柄）。进程正常退出（<see cref="OnProcessExit"/>）才 flush。
        /// </para>
        /// </summary>
        public bool Enabled
        {
            get
            {
                lock (_lock)
                    return _enabled;
            }
            set
            {
                lock (_lock)
                {
                    if (_disposed) return;
                    _enabled = value;
                    if (_enabled)
                        EnsureWriterLocked();
                }
            }
        }

        /// <summary>日志文件路径——未启用或初始化失败时为 null。</summary>
        public string? FilePath { get; private set; }

        private AgentLog()
        {
            try
            {
                if (s_configuredEnabled is { } cfg)
                {
                    _enabled = cfg;
                }
                else
                {
                    string? v = Environment.GetEnvironmentVariable("EMUERA_AGENT_LOG");
                    // 默认启用（v == null）；设 EMUERA_AGENT_LOG=0/false 可关闭
                    _enabled = v == null || v == "1" || v == "true";
                }
            }
            catch (Exception ex) { _enabled = false; Console.Error.WriteLine("[agent-log] env read failed: " + ex.Message); }

            if (_enabled)
                EnsureWriterLocked();
        }

        /// <summary>
        /// 创建/复用 writer（调用方须已持有 <see cref="_lock"/>，或处于构造单线程期）。
        /// 幂等：writer 已存在直接返回。
        /// </summary>
        private void EnsureWriterLocked()
        {
            if (_writer != null) return;
            try
            {
                // F-4：进程日志恒落应用私有域（唯一豁免域），经 AppDataIo 窄口子而非裸 File.* / Directory.*。
                AppDataPaths.EnsureDirectory();
                var logPath = AppDataIo.Resolve(LogFileName);
                // 诊断保留策略：agent.log 仍覆盖式（进程启动清空，只保留最近一次运行），
                // 但截断前先把旧文件拷为 agent.prev.log——崩溃现场不至于随下次启动被抹掉。
                // 文件数上限 2（本次 + 上次），有界。拷贝失败不阻断启动（目录可能被非本进程独占）。
                if (AppDataIo.FileExists(logPath))
                {
                    try { AppDataIo.Copy(logPath, AppDataIo.Resolve(PrevLogName), overwrite: true); }
                    catch (Exception) { /* 保留失败静默：不因日志保留打断启动 */ }
                }
                _logPath = logPath;
                FilePath = logPath.Value;
                _writer = new StreamWriter(logPath.Value, append: false) { AutoFlush = false };
                AppDomain.CurrentDomain.ProcessExit += OnProcessExit;
            }
            catch (Exception ex) { FilePath = null; _writer = null; _logPath = null; Console.Error.WriteLine("[agent-log] file init failed: " + ex.Message); }
        }

        public void Write(string message)
        {
            lock (_lock)
            {
                if (_disposed || !_enabled || _writer == null) return;
                try
                {
                    _writer.WriteLine($"{DateTime.Now:HH:mm:ss.fff} {message}");
                }
                catch (Exception) { /* 写日志失败不能再调 AgentLog，避免递归 */ }
            }
        }

        /// <summary>
        /// 强制落盘缓冲日志（AutoFlush=false，平时积在缓冲里）。app 内日志查看器读取前调用，
        /// 无需退出进程即可拿到最新日志（A0 补充：真机无 adb 场景的取回路径）。
        /// </summary>
        public void Flush()
        {
            lock (_lock)
            {
                if (_disposed || _writer == null) return;
                try
                {
                    _writer.Flush();
                }
                catch (Exception) { /* 同上，静默 */ }
            }
        }

        /// <summary>
        /// 读取当前完整日志文本——先 <see cref="Flush"/> 再读文件，保证包含缓冲中的最新行。
        /// 未启用 / writer 未创建 / 文件不存在 / 读取失败时返回 null（调用方自行处理）。
        /// 供 app 内日志查看器使用（A0 补充）。
        /// <para>
        /// <b>必须用 FileShare.ReadWrite</b>：writer 以 FileAccess.Write 持有文件，读取句柄的
        /// FileShare 需允许「他人写」（否则 File.ReadAllText 的 FileShare.Read 与 writer 的
        /// Write 访问冲突 → IOException: file in use）。
        /// </para>
        /// </summary>
        public string? ReadAllText()
        {
            AppDataPath? logPath;
            lock (_lock)
            {
                if (_disposed || _writer == null) return null;
                try
                {
                    _writer.Flush();
                }
                catch (Exception)
                {
                    return null;
                }
                logPath = _logPath;
            }
            try
            {
                if (logPath is not { } path || !AppDataIo.FileExists(path))
                    return null;
                using var fs = AppDataIo.OpenReadShared(path);
                using var reader = new StreamReader(fs);
                return reader.ReadToEnd();
            }
            catch (Exception)
            {
                return null;
            }
        }

        private void OnProcessExit(object? sender, EventArgs e)
        {
            try
            {
                lock (_lock)
                {
                    if (_disposed) return;
                    _writer?.Flush();
                    _writer?.Dispose();
                    _disposed = true;
                }
            }
            catch (Exception) { /* ProcessExit 里调日志/Console 不安全，吞掉 */ }
        }

        public void Dispose()
        {
            OnProcessExit(this, EventArgs.Empty);
            AppDomain.CurrentDomain.ProcessExit -= OnProcessExit;
        }
    }
}
