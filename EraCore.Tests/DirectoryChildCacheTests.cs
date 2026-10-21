using MinorShift.Emuera.Files;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Xunit;

namespace MinorShift.Emuera.Tests;

/// <summary>
/// O1：<see cref="DirectoryChildCache"/> 目录级子项缓存契约
/// （命中 / miss / 覆盖 / 失效 / 清空 / 大小写 / 隔离 / 并发 / 快照）。
/// </summary>
public class DirectoryChildCacheTests
{
    private static ChildEntry File(string name) => new($"primary:emuera/ERB/{name}", name, "text/plain");
    private static ChildEntry Dir(string name) => new($"primary:emuera/ERB/{name}", name, "vnd.android.document/directory");

    [Fact]
    public void Get_miss_returns_null_for_unknown_directory()
    {
        var cache = new DirectoryChildCache();
        Assert.Null(cache.Get("primary:emuera/ERB"));
    }

    [Fact]
    public void Put_then_Get_returns_same_children()
    {
        var cache = new DirectoryChildCache();
        var children = new List<ChildEntry> { File("a.ERB"), Dir("sub"), File("b.ERB") };

        cache.Put("primary:emuera/ERB", children);
        var got = cache.Get("primary:emuera/ERB");

        Assert.NotNull(got);
        Assert.Equal(children, got); // 顺序与原列表一致
    }

    [Fact]
    public void Put_replaces_previous_children_for_same_directory()
    {
        var cache = new DirectoryChildCache();
        cache.Put("primary:emuera/ERB", new List<ChildEntry> { File("old.ERB") });
        var newer = new List<ChildEntry> { File("new.ERB") };

        cache.Put("primary:emuera/ERB", newer);

        Assert.Equal(newer, cache.Get("primary:emuera/ERB"));
    }

    [Fact]
    public void Invalidate_removes_directory_entry()
    {
        var cache = new DirectoryChildCache();
        cache.Put("primary:emuera/ERB", new List<ChildEntry> { File("a.ERB") });
        cache.Put("primary:emuera/CSV", new List<ChildEntry> { File("b.csv") });

        cache.Invalidate("primary:emuera/ERB");

        Assert.Null(cache.Get("primary:emuera/ERB"));
        Assert.NotNull(cache.Get("primary:emuera/CSV")); // 其他目录不受影响
    }

    [Fact]
    public void Clear_removes_all_entries()
    {
        var cache = new DirectoryChildCache();
        cache.Put("primary:emuera/ERB", new List<ChildEntry> { File("a.ERB") });
        cache.Put("primary:emuera/CSV", new List<ChildEntry> { File("b.csv") });

        cache.Clear();

        Assert.Null(cache.Get("primary:emuera/ERB"));
        Assert.Null(cache.Get("primary:emuera/CSV"));
    }

    [Fact]
    public void Directory_keys_are_case_insensitive()
    {
        var cache = new DirectoryChildCache();
        var children = new List<ChildEntry> { File("a.ERB") };

        cache.Put("primary:emuera/ERB", children);

        Assert.NotNull(cache.Get("PRIMARY:EMUERA/erb"));
        Assert.Equal(children, cache.Get("Primary:emuera/Erb"));
    }

    [Fact]
    public void Different_directories_are_isolated()
    {
        var cache = new DirectoryChildCache();
        cache.Put("primary:emuera", new List<ChildEntry> { Dir("ERB"), Dir("CSV") });
        cache.Put("primary:emuera/ERB", new List<ChildEntry> { File("a.ERB") });

        var root = cache.Get("primary:emuera");
        var erb = cache.Get("primary:emuera/ERB");

        Assert.Equal(2, root!.Count);
        Assert.Single(erb!);
        // 父目录子项（ERB 目录）与子目录子项（ERB 内文件）互不串扰
        Assert.All(root, c => Assert.NotEqual("a.ERB", c.Name));
    }

    [Fact]
    public void Concurrent_put_get_invalidate_is_safe()
    {
        var cache = new DirectoryChildCache();
        var keys = Enumerable.Range(0, 8).Select(i => $"primary:emuera/dir{i}").ToList();

        Parallel.For(0, 1000, i =>
        {
            var key = keys[i % keys.Count];
            cache.Put(key, new List<ChildEntry> { File($"f{i}.ERB") });
            _ = cache.Get(key);
            if (i % 3 == 0) cache.Invalidate(key);
            if (i % 97 == 0) cache.Clear();
        });

        // 无异常抛出即为通过；终态确定性：最后再写一次并读取
        cache.Put(keys[0], new List<ChildEntry> { File("final.ERB") });
        Assert.Equal("final.ERB", cache.Get(keys[0])![0].Name);
    }

    [Fact]
    public void Get_returns_snapshot_not_live_list()
    {
        var cache = new DirectoryChildCache();
        var first = new List<ChildEntry> { File("a.ERB") };
        cache.Put("primary:emuera/ERB", first);

        var snapshot = cache.Get("primary:emuera/ERB");
        first.Add(File("b.ERB")); // 调用方改原列表：不应影响已缓存的快照

        Assert.Single(snapshot!);
        // 再次 Put 覆盖后，旧引用仍为旧内容
        var old = cache.Get("primary:emuera/ERB");
        cache.Put("primary:emuera/ERB", new List<ChildEntry> { File("c.ERB") });
        Assert.Equal("a.ERB", old![0].Name);
    }
}
