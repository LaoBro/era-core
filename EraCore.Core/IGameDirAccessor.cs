using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace MinorShift.Emuera;

/// <summary>
/// 游戏目录访问抽象——隐藏平台文件系统差异（SAF content:// URI vs 传统文件路径）。
/// 所有文件 IO 必须通过此接口，禁止直接调用 File.* / Directory.*。
/// </summary>
public interface IGameDirAccessor
{
    // ── 目录选择 ──────────────────────────────────
    Task<string?> PickDirectoryAsync(CancellationToken ct = default);

    // ── 目录枚举 ──────────────────────────────────
    bool DirectoryExists(string path);
    string[] GetDirectories(string path);
    string[] GetDirectories(string path, string searchPattern, SearchOption searchOption);

    // ── 文件枚举 ──────────────────────────────────
    bool FileExists(string path);
    string[] GetFiles(string path);
    string[] GetFiles(string path, string searchPattern, SearchOption searchOption);

    // ── 文件读取 ──────────────────────────────────
    string ReadAllText(string path);
    byte[]? ReadAllBytes(string path);
    Stream? OpenRead(string path);

    // ── 文件/目录写入（方案 B：SAF 存档写回游戏树）──────────────────
    /// <summary>确保目录存在（本地 mkdir；SAF 下对父目录 CreateDocument DIR）。</summary>
    void CreateDirectory(string path);

    /// <summary>
    /// 创建或截断写入（对齐 <see cref="FileMode.Create"/>）。
    /// 调用方负责 <see cref="IDisposable.Dispose"/>。
    /// SAF 上必须按「父目录 + displayName」查找/创建，不能假设理论 URI 已在 provider 注册。
    /// </summary>
    Stream OpenWrite(string path);

    /// <summary>删除文件；不存在则 no-op。目录删除行为由实现定义（SAF 仅删文件）。</summary>
    void Delete(string path);

    /// <summary>
    /// 当前游戏根是否具备可写能力。
    /// 本地文件系统恒为 true；SAF 检查持久化 URI 是否含 Write（缺写时需用户重新选目录）。
    /// </summary>
    bool HasWriteAccess();

    // ── 路径操作 ──────────────────────────────────
    string ResolveSubPath(string basePath, string subDir);
    string CombinePath(string basePath, string filename);
    string GetParentPath(string path);
    string GetFileName(string path);

    /// <summary>
    /// issues/14 A1：批量路径拼接——「同一 basePath + N 条相对名」的调用形态
    ///（清单 sidecar 回放的 BuildPendingList replay / Preload.SeedTreeMemo）
    /// 在 SAF 上每条 <see cref="CombinePath"/> 都重复 Uri.Parse + ResolveDocId（同一 basePath
    /// 结果恒同）+ 2 次 JNI，真机 7308 次 ≈ 2.90s/命中轮。批量实现可把基座解析提为一次。
    /// 默认实现 = 逐条 <see cref="CombinePath"/> 循环（语义与逐条严格一致），各实现按需重写；
    /// 返回顺序与 <paramref name="filenames"/> 一一对应。
    /// </summary>
    IReadOnlyList<string> CombinePaths(string basePath, IReadOnlyList<string> filenames)
    {
        var result = new string[filenames.Count];
        for (int i = 0; i < filenames.Count; i++)
            result[i] = CombinePath(basePath, filenames[i]);
        return result;
    }
}
