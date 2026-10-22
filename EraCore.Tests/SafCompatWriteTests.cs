using MinorShift.Emuera.Files;
using System;
using System.IO;
using System.Text;
using MinorShift.Emuera.Runtime.Utils;
using Xunit;

namespace MinorShift.Emuera.Tests;

/// <summary>
/// 方案 B / B2：SafCompat 写路径经 FileSystemGameDirAccessor 的本地契约。
/// </summary>
[Collection("LoaderTests")]
public class SafCompatWriteTests
{
    [Fact]
    public void CombinePath_local_joins_file_under_dir()
    {
        using var tmp = new TempDir();
        using var rootScope = new RootScope(tmp.Root, new FileSystemGameDirAccessor());

        var sav = SafCompat.ResolveSubPath(tmp.Root, "sav");
        var global = SafCompat.CombinePath(sav, "global.sav");

        Assert.Equal(Path.Combine(tmp.Root, "sav"), sav.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        Assert.Equal(Path.Combine(tmp.Root, "sav", "global.sav"), global);
    }

    [Fact]
    public void OpenWrite_CreateDirectory_roundtrip_via_SafCompat()
    {
        using var tmp = new TempDir();
        using var rootScope = new RootScope(tmp.Root, new FileSystemGameDirAccessor());

        var sav = SafCompat.ResolveSubPath(tmp.Root, "sav");
        SafCompat.CreateDirectory(sav);
        Assert.True(SafCompat.DirectoryExists(sav));

        var path = SafCompat.CombinePath(sav, "global.sav");
        var payload = Encoding.UTF8.GetBytes("global-data");
        using (var ws = SafCompat.OpenWrite(path))
            ws.Write(payload, 0, payload.Length);

        Assert.True(SafCompat.FileExists(path));
        using (var rs = SafCompat.OpenRead(path))
        {
            Assert.NotNull(rs);
            using var ms = new MemoryStream();
            rs!.CopyTo(ms);
            Assert.Equal(payload, ms.ToArray());
        }

        SafCompat.Delete(path);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void WriteAllText_roundtrip()
    {
        using var tmp = new TempDir();
        using var rootScope = new RootScope(tmp.Root, new FileSystemGameDirAccessor());
        var path = Path.Combine(tmp.Root, "note.txt");
        SafCompat.WriteAllText(path, "hello", Encoding.UTF8);
        Assert.Equal("hello", SafCompat.ReadAllText(path, Encoding.UTF8));
    }

    private sealed class TempDir : IDisposable
    {
        public string Root { get; }
        private bool _disposed;

        public TempDir()
        {
            Root = Path.Combine(Path.GetTempPath(), "emuera-safcompat-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
            Directory.CreateDirectory(Path.Combine(Root, "csv"));
            Directory.CreateDirectory(Path.Combine(Root, "erb"));
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try
            {
                if (Directory.Exists(Root))
                    Directory.Delete(Root, recursive: true);
            }
            catch { /* best-effort */ }
        }
    }
}
