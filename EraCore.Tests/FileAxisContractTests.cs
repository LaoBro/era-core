using MinorShift.Emuera.Files;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Xunit;

namespace MinorShift.Emuera.Tests;

/// <summary>
/// issue-005 步骤 0：文件轴行为锁。
///
/// <para>同一套契约必须同时成立在两种路径语义上：</para>
/// <list type="bullet">
/// <item><b>本地文件系统</b>——<see cref="FileSystemGameDirAccessor"/> 封装 <see cref="File"/> / <see cref="Directory"/>；</item>
/// <item><b>模拟 SAF content URI</b>——<see cref="InMemoryContentDirAccessor"/> 只认 document URI，不解析本地路径。</item>
/// </list>
///
/// <para>扫描语义单独钉住：只认同时含 <c>csv/</c> 与 <c>erb/</c> 的直接子目录，按名称<b>序数</b>排序，
/// 不递归。搬入 <c>Files/</c> 之前先让本文件在旧实现上通过，之后才动生产代码。</para>
/// </summary>
public sealed class FileAxisContractTests
{
    public static TheoryData<string> AccessorKinds => new() { "local", "content-uri" };

    [Theory]
    [MemberData(nameof(AccessorKinds))]
    public void Accessor_contract_is_identical_for_local_and_content_uri(string kind)
    {
        using var fixture = Fixture.Create(kind);
        var accessor = fixture.Accessor;
        var root = fixture.Root;

        // 目录：创建、存在性、父/名
        var sav = accessor.CombinePath(root, "sav");
        accessor.CreateDirectory(sav);
        Assert.True(accessor.DirectoryExists(root));
        Assert.True(accessor.DirectoryExists(sav));
        Assert.False(accessor.DirectoryExists(accessor.CombinePath(root, "missing")));
        Assert.Equal("sav", accessor.GetFileName(sav));
        Assert.Equal(root, accessor.GetParentPath(sav));

        // 写 → 读（字节 + 流 + 文本）
        var save = accessor.CombinePath(sav, "global.sav");
        byte[] payload = [0x01, 0x02, 0x03, 0xFE];
        using (var w = accessor.OpenWrite(save))
            w.Write(payload);

        Assert.True(accessor.FileExists(save));
        Assert.Equal(payload, accessor.ReadAllBytes(save)!);
        using (var r = accessor.OpenRead(save))
        {
            Assert.NotNull(r);
            using var ms = new MemoryStream();
            r!.CopyTo(ms);
            Assert.Equal(payload, ms.ToArray());
        }

        var readme = accessor.CombinePath(root, "readme.txt");
        using (var w = accessor.OpenWrite(readme))
        {
            var text = Encoding.UTF8.GetBytes("hello");
            w.Write(text);
        }
        Assert.Equal("hello", accessor.ReadAllText(readme));

        // 不存在的文件：存在性 false、字节 null
        var missing = accessor.CombinePath(root, "missing.txt");
        Assert.False(accessor.FileExists(missing));
        Assert.Null(accessor.ReadAllBytes(missing));

        // 写路径自动建父目录（两种实现同语义）
        var auto = accessor.CombinePath(accessor.CombinePath(root, "auto"), "nested");
        var autoFile = accessor.CombinePath(auto, "x.bin");
        using (var w = accessor.OpenWrite(autoFile))
            w.WriteByte(7);
        Assert.True(accessor.DirectoryExists(auto));
        Assert.Equal([7], accessor.ReadAllBytes(autoFile)!);

        // 枚举：直接子项 / 通配 / 递归（结果名集合比较，不依赖 FS 枚举顺序）
        Assert.Equal(["auto", "sav"], Names(accessor.GetDirectories(root)));
        Assert.Equal(["readme.txt"], Names(accessor.GetFiles(root)));
        Assert.Equal(["global.sav"], Names(accessor.GetFiles(sav, "*.sav", SearchOption.TopDirectoryOnly)));
        Assert.Empty(accessor.GetFiles(root, "*.sav", SearchOption.TopDirectoryOnly));
        Assert.Equal(["global.sav"], Names(accessor.GetFiles(root, "*.sav", SearchOption.AllDirectories)));
        Assert.Equal(["auto", "nested", "sav"], Names(accessor.GetDirectories(root, "*", SearchOption.AllDirectories)));

        // 批量拼接 = 逐条拼接（顺序一一对应）
        var names = new[] { "a.txt", "b.txt", "游戏.txt" };
        var batched = accessor.CombinePaths(root, names);
        Assert.Equal(names.Select(n => accessor.CombinePath(root, n)), batched);

        // 删除幂等
        accessor.Delete(save);
        Assert.False(accessor.FileExists(save));
        accessor.Delete(save);

        Assert.True(accessor.HasWriteAccess());
    }

    [Theory]
    [MemberData(nameof(AccessorKinds))]
    public void Library_scan_recognizes_only_csv_and_erb_subdirectories(string kind)
    {
        using var fixture = Fixture.Create(kind);
        var accessor = fixture.Accessor;
        var root = fixture.Root;

        CreateGame(accessor, root, "zelda");
        CreateGame(accessor, root, "aaa-game");
        // 序数排序的判别样本：Ordinal 下 "BBB" 在 "aaa" 之前，文化排序相反。
        CreateGame(accessor, root, "BBB");

        accessor.CreateDirectory(accessor.CombinePath(accessor.CombinePath(root, "only-csv"), "csv"));
        accessor.CreateDirectory(accessor.CombinePath(accessor.CombinePath(root, "only-erb"), "erb"));
        accessor.CreateDirectory(accessor.CombinePath(root, "empty-dir"));
        // csv / erb 是文件而不是目录——不算游戏
        var fake = accessor.CombinePath(root, "csv-is-file");
        accessor.CreateDirectory(fake);
        using (var w = accessor.OpenWrite(accessor.CombinePath(fake, "csv")))
            w.WriteByte(0);
        accessor.CreateDirectory(accessor.CombinePath(fake, "erb"));

        var games = GameScanner.Scan(root, accessor);

        Assert.Equal(["BBB", "aaa-game", "zelda"], games.Select(g => g.Name).ToArray());
        Assert.All(games, g =>
        {
            var treeRoot = g.FullPath;
            Assert.True(accessor.DirectoryExists(accessor.CombinePath(treeRoot, "csv")));
            Assert.True(accessor.DirectoryExists(accessor.CombinePath(treeRoot, "erb")));
            Assert.Equal(g.Name, accessor.GetFileName(treeRoot));
        });

        // 根不存在 / null / 空 → 空列表（与今天一致，不抛）
        Assert.True(GameScanner.RootDirectoryExists(root, accessor));
        Assert.False(GameScanner.RootDirectoryExists(accessor.CombinePath(root, "missing"), accessor));
        Assert.Empty(GameScanner.Scan(null, accessor));
        Assert.Empty(GameScanner.Scan("", accessor));
        Assert.Empty(GameScanner.Scan(accessor.CombinePath(root, "missing"), accessor));
    }

    private static string[] Names(IEnumerable<string> fullPaths)
        => fullPaths.Select(path => path.Contains("/document/", StringComparison.Ordinal)
                ? Runtime.Utils.SafPath.GetLogicalFileName(path)
                : Path.GetFileName(path))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

    private static void CreateGame(IGameDirAccessor accessor, string root, string name)
    {
        var gameDir = accessor.CombinePath(root, name);
        accessor.CreateDirectory(accessor.CombinePath(gameDir, "csv"));
        accessor.CreateDirectory(accessor.CombinePath(gameDir, "erb"));
    }

    private sealed class Fixture : IDisposable
    {
        private readonly Action? _cleanup;

        private Fixture(IGameDirAccessor accessor, string root, Action? cleanup = null)
        {
            Accessor = accessor;
            Root = root;
            _cleanup = cleanup;
        }

        public IGameDirAccessor Accessor { get; }
        public string Root { get; }

        public static Fixture Create(string kind)
        {
            if (kind == "local")
            {
                var root = Path.Combine(Path.GetTempPath(), "emuera-fileaxis-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(root);
                return new Fixture(new FileSystemGameDirAccessor(), root,
                    () => Directory.Delete(root, recursive: true));
            }

            var content = new InMemoryContentDirAccessor();
            return new Fixture(content, content.Root);
        }

        public void Dispose() => _cleanup?.Invoke();
    }
}
