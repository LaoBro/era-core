using MinorShift.Emuera.Files;
using System;
using System.IO;
using System.Linq;
using System.Text;
using MinorShift.Emuera.Runtime.Utils;
using Xunit;

namespace MinorShift.Emuera.Tests;

/// <summary>
/// SAF 运行时契约：content URI 的文件操作必须委托给 IGameDirAccessor，不能退回 System.IO 路径语义。
/// </summary>
[Collection("LoaderTests")]
public class SafCompatContentUriTests
{
    private const string Root =
        "content://com.android.externalstorage.documents/tree/primary%3Aemuera/document/primary%3Aemuera%2FTK";

    [Fact]
    public void Content_uri_write_read_delete_roundtrip_uses_accessor()
    {
        var accessor = new InMemoryContentDirAccessor(Root);
        _ = GamePaths.Resolve(Root, accessor);

        var savDir = SafCompat.ResolveSubPath(Root, "sav");
        SafCompat.CreateDirectory(savDir);
        var savePath = SafCompat.CombinePath(savDir, "global.sav");
        Assert.Equal(savDir, accessor.GetParentPath(savePath));

        SafCompat.WriteAllText(savePath, "global-data", Encoding.UTF8);

        Assert.True(accessor.DirectoryExists(savDir));
        Assert.True(accessor.FileExists(savePath));
        Assert.Equal("global.sav", SafPath.GetLogicalFileName(savePath));
        Assert.Equal("global-data", SafCompat.ReadAllText(savePath, Encoding.UTF8));
        Assert.Equal(1, accessor.OpenWriteCalls);
        Assert.Equal(1, accessor.OpenReadCalls);

        SafCompat.Delete(savePath);

        Assert.False(accessor.FileExists(savePath));
        Assert.Equal(1, accessor.DeleteCalls);
    }

    [Fact]
    public void Content_uri_root_directory_exists_uses_accessor_semantics()
    {
        var accessor = new InMemoryContentDirAccessor(Root);

        Assert.True(GameScanner.RootDirectoryExists(Root, accessor));
        Assert.False(GameScanner.RootDirectoryExists(
            "content://com.android.externalstorage.documents/tree/primary%3Amissing/document/primary%3Amissing",
            accessor));
    }

    [Fact]
    public void Content_uri_getfiles_preserves_logical_filenames()
    {
        var accessor = new InMemoryContentDirAccessor(Root);
        _ = GamePaths.Resolve(Root, accessor);
        var datDir = SafCompat.ResolveSubPath(Root, "dat");
        SafCompat.CreateDirectory(datDir);
        accessor.Seed(SafCompat.CombinePath(datDir, "var_01.dat"), [0x01]);
        accessor.Seed(SafCompat.CombinePath(datDir, "var_custom.dat"), [0x02]);

        var files = SafCompat.GetFiles(datDir, "var_*.dat", SearchOption.TopDirectoryOnly);

        Assert.Equal(["var_01.dat", "var_custom.dat"], files
            .Select(SafPath.GetLogicalFileName)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray());
    }

    [Fact]
    public void Content_uri_write_contract_handles_overwrite_empty_nested_and_missing_delete()
    {
        var accessor = new InMemoryContentDirAccessor(Root);
        _ = GamePaths.Resolve(Root, accessor);

        var nestedDir = SafCompat.ResolveSubPath(SafCompat.ResolveSubPath(Root, "sav"), "nested");
        SafCompat.CreateDirectory(nestedDir);
        var path = SafCompat.CombinePath(nestedDir, "slot.sav");

        SafCompat.WriteAllText(path, "first", Encoding.UTF8);
        SafCompat.WriteAllText(path, "second", Encoding.UTF8);
        Assert.Equal("second", SafCompat.ReadAllText(path, Encoding.UTF8));

        SafCompat.WriteAllText(path, string.Empty, Encoding.UTF8);
        Assert.Equal(string.Empty, SafCompat.ReadAllText(path, Encoding.UTF8));
        SafCompat.Delete(SafCompat.CombinePath(nestedDir, "missing.sav"));
        Assert.True(accessor.FileExists(path));
    }
}
