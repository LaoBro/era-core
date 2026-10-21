using MinorShift.Emuera.Files;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MinorShift.Emuera.Runtime.Utils;
using Xunit;

namespace MinorShift.Emuera.Tests;

/// <summary>
/// S0-M3（diagnostic-knobs issues/11，2026-09-05）：<see cref="Preload"/> 轮内全树枚举 memo 契约。
/// 命中轮 ERB 全树被枚举 4 次（IR 预判定 / IndexTree / ErhLoader / LoadErbDir.BuildPendingList），
/// memo 把它们折叠为 1 次冷枚举 + 3 次命中。本测试钉住：同根去重、Clear 失效、扩展名子过滤复用 memo。
/// 注：与其余 Preload/Loader 测试同属 <c>LoaderTests</c> 串行集合——本类 Clear/种子静态 Preload，
/// 若落在默认集合会与 LoaderTests 并行执行并清掉对方在途缓存（语料未装载 / label not found）。
/// </summary>
[Collection("LoaderTests")]
public class PreloadTreeMemoTests
{
    private const string ErbRoot = "C:/game/ERB";
    private static readonly string[] ErbFiles =
    [
        ErbRoot + "/TOP.ERB",
        ErbRoot + "/SUB/NESTED.ERB",
        ErbRoot + "/IGNORED.csv",
    ];

    [Fact]
    public void Two_enumerations_of_same_root_trigger_one_accessor_query()
    {
        var accessor = new CountingTreeAccessor(ErbFiles);
        Preload.Clear();
        try
        {
            var first = Preload.EnumerateFilesFromTree(ErbRoot, accessor, ".ERB");
            var second = Preload.EnumerateFilesFromTree(ErbRoot, accessor, ".ERB");

            Assert.Equal(2, first.Count);
            Assert.Equal(2, second.Count);
            Assert.Equal(1, accessor.GetFilesCalls); // 第二次命中 memo，不再走 accessor
            Assert.Equal(first.Select(kv => kv.Key).OrderBy(x => x), second.Select(kv => kv.Key).OrderBy(x => x));
        }
        finally
        {
            Preload.Clear();
        }
    }

    [Fact]
    public void Extension_subfilter_reuses_memo_from_other_extension()
    {
        var accessor = new CountingTreeAccessor(ErbFiles);
        Preload.Clear();
        try
        {
            var erb = Preload.EnumerateFilesFromTree(ErbRoot, accessor, ".ERB");
            var csv = Preload.EnumerateFilesFromTree(ErbRoot, accessor, ".CSV");

            Assert.Equal(2, erb.Count);
            Assert.Single(csv);
            Assert.Equal("IGNORED.csv", csv[0].Key); // memo 存的是目标扩展名全量过滤清单，子过滤复用（Key=相对路径）
            Assert.Equal(1, accessor.GetFilesCalls);
        }
        finally
        {
            Preload.Clear();
        }
    }

    [Fact]
    public void Clear_invalidates_memo_and_forces_fresh_enumeration()
    {
        var accessor = new CountingTreeAccessor(ErbFiles);
        Preload.Clear();
        try
        {
            Preload.EnumerateFilesFromTree(ErbRoot, accessor, ".ERB");
            Assert.Equal(1, accessor.GetFilesCalls);

            Preload.Clear(); // 轮边界：下轮必须重新冷枚举
            Preload.EnumerateFilesFromTree(ErbRoot, accessor, ".ERB");

            Assert.Equal(2, accessor.GetFilesCalls);
        }
        finally
        {
            Preload.Clear();
        }
    }

    [Fact]
    public void Different_roots_do_not_share_memo_entries()
    {
        var erbAccessor = new CountingTreeAccessor(ErbFiles);
        var csvAccessor = new CountingTreeAccessor(["C:/game/CSV/DATA.CSV"]);
        Preload.Clear();
        try
        {
            Preload.EnumerateFilesFromTree(ErbRoot, erbAccessor, ".ERB");
            Preload.EnumerateFilesFromTree("C:/game/CSV", csvAccessor, ".CSV");
            Preload.EnumerateFilesFromTree(ErbRoot, erbAccessor, ".ERB");

            Assert.Equal(1, erbAccessor.GetFilesCalls); // 各自根各枚举一次
            Assert.Equal(1, csvAccessor.GetFilesCalls);
        }
        finally
        {
            Preload.Clear();
        }
    }

    /// <summary>
    /// 2026-09-05 回归钉（SelectCase/UncalledSweep 语料 0 装载）：LoaderTestHarness 先在空目录跑
    /// Initialize（引擎就绪加载），后写语料再加载。空目录枚举结果【不得缓存】，否则后写入的文件
    /// 被陈旧空缓存吞掉 → label not found / 签名 0 条。
    /// </summary>
    [Fact]
    public async Task Empty_dir_enumeration_is_not_cached_so_later_files_are_seen()
    {
        var accessor = new CountingTreeAccessor([]);
        Preload.Clear();
        try
        {
            await Preload.Load(ErbRoot, accessor); // 空目录：枚举 [] → 不缓存
            Assert.Equal(1, accessor.GetFilesCalls);
            Assert.Empty(Preload.GetAllCachedKeys());

            accessor.Add(ErbRoot + "/MAIN.ERB"); // 语料在初始化轮之后写入
            await Preload.Load(ErbRoot, accessor);

            Assert.Equal(2, accessor.GetFilesCalls); // 空缓存未投毒 → 重新枚举看到新文件
            Assert.Contains(ErbRoot + "/MAIN.ERB", Preload.GetAllCachedKeys());
        }
        finally
        {
            Preload.Clear();
        }
    }

    /// <summary>
    /// 2026-09-05：Load（内容装载入口，= harness 的 PreloadAsync）必须失效该根 memo——
    /// 非空目录两次 Load 之间新增文件也要被看到（写文件→Load→再写→再 Load 流程）。
    /// </summary>
    [Fact]
    public async Task Load_invalidates_memo_so_files_added_between_loads_are_seen()
    {
        var accessor = new CountingTreeAccessor([ErbRoot + "/A.ERB"]);
        Preload.Clear();
        try
        {
            await Preload.Load(ErbRoot, accessor); // 缓存 [A.ERB]
            Assert.Equal(1, accessor.GetFilesCalls);

            accessor.Add(ErbRoot + "/B.ERB");
            await Preload.Load(ErbRoot, accessor); // Load 失效 memo → 重枚举

            Assert.Equal(2, accessor.GetFilesCalls);
            Assert.Contains(ErbRoot + "/B.ERB", Preload.GetAllCachedKeys());
        }
        finally
        {
            Preload.Clear();
        }
    }

    /// <summary>计数 GetFiles(path, "*", AllDirectories) 的 stub；DirectoryExists 恒真，其余成员不参与。</summary>
    private sealed class CountingTreeAccessor : IGameDirAccessor
    {
        private readonly List<string> _files;

        public CountingTreeAccessor(IEnumerable<string> files) => _files = files.ToList();

        public int GetFilesCalls { get; private set; }

        public void Add(string path) => _files.Add(path);

        public bool DirectoryExists(string path) => true;

        public string[] GetFiles(string path, string searchPattern, SearchOption searchOption)
        {
            GetFilesCalls++;
            Assert.Equal("*", searchPattern);
            Assert.Equal(SearchOption.AllDirectories, searchOption);
            return _files.ToArray();
        }

        public byte[]? ReadAllBytes(string path) => [0x40, 0x4D, 0x0A, 0x52, 0x45, 0x54, 0x55, 0x52, 0x4E, 0x0A]; // "@M\nRETURN\n"

        public Task<string?> PickDirectoryAsync(CancellationToken ct = default) => Task.FromResult<string?>(null);
        public string[] GetDirectories(string path) => throw new NotSupportedException();
        public string[] GetDirectories(string path, string searchPattern, SearchOption searchOption) => throw new NotSupportedException();
        public bool FileExists(string path) => throw new NotSupportedException();
        public string[] GetFiles(string path) => throw new NotSupportedException();
        public string ReadAllText(string path) => throw new NotSupportedException();
        public Stream? OpenRead(string path) => throw new NotSupportedException();
        public void CreateDirectory(string path) => throw new NotSupportedException();
        public Stream OpenWrite(string path) => throw new NotSupportedException();
        public void Delete(string path) => throw new NotSupportedException();
        public bool HasWriteAccess() => throw new NotSupportedException();
        public string CombinePath(string basePath, string fileName) => throw new NotSupportedException();
        public string ResolveSubPath(string basePath, string subDir) => throw new NotSupportedException();
        public string GetParentPath(string path) => throw new NotSupportedException();
        public string GetFileName(string path) => throw new NotSupportedException();
    }
}
