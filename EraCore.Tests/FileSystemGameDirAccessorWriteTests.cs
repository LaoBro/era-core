using MinorShift.Emuera.Files;
using System;
using System.IO;
using System.Text;
using Xunit;

namespace MinorShift.Emuera.Tests;

/// <summary>
/// 方案 B / B0：<see cref="FileSystemGameDirAccessor"/> 写 API 契约
/// （CreateDirectory / OpenWrite / Delete / HasWriteAccess）。
/// </summary>
public class FileSystemGameDirAccessorWriteTests
{
    [Fact]
    public void HasWriteAccess_is_always_true()
    {
        var acc = new FileSystemGameDirAccessor();
        Assert.True(acc.HasWriteAccess());
    }

    [Fact]
    public void CreateDirectory_OpenWrite_readback_Delete_roundtrip()
    {
        using var tmp = new TempDir();
        var acc = new FileSystemGameDirAccessor();
        var savDir = Path.Combine(tmp.Root, "sav");
        var filePath = Path.Combine(savDir, "global.sav");

        acc.CreateDirectory(savDir);
        Assert.True(Directory.Exists(savDir));

        var payload = Encoding.UTF8.GetBytes("save-bytes");
        using (var ws = acc.OpenWrite(filePath))
            ws.Write(payload, 0, payload.Length);

        Assert.True(acc.FileExists(filePath));
        var read = acc.ReadAllBytes(filePath);
        Assert.NotNull(read);
        Assert.Equal(payload, read);

        // OpenWrite 对齐 FileMode.Create：截断重写
        var payload2 = Encoding.UTF8.GetBytes("x");
        using (var ws = acc.OpenWrite(filePath))
            ws.Write(payload2, 0, payload2.Length);
        Assert.Equal(payload2, acc.ReadAllBytes(filePath));

        acc.Delete(filePath);
        Assert.False(File.Exists(filePath));
        acc.Delete(filePath); // no-op
    }

    [Fact]
    public void OpenWrite_creates_missing_parent_directory()
    {
        using var tmp = new TempDir();
        var acc = new FileSystemGameDirAccessor();
        var nested = Path.Combine(tmp.Root, "a", "b", "c.dat");

        using (var ws = acc.OpenWrite(nested))
            ws.WriteByte(0x42);

        Assert.True(File.Exists(nested));
        Assert.Equal(0x42, File.ReadAllBytes(nested)[0]);
    }

    [Fact]
    public void CombinePath_and_GetFileName_match_System_IO()
    {
        using var tmp = new TempDir();
        var acc = new FileSystemGameDirAccessor();
        var combined = acc.CombinePath(tmp.Root, "save00.sav");
        Assert.Equal(Path.Combine(tmp.Root, "save00.sav"), combined);
        Assert.Equal("save00.sav", acc.GetFileName(combined));
    }

    private sealed class TempDir : IDisposable
    {
        public string Root { get; }
        private bool _disposed;

        public TempDir()
        {
            Root = Path.Combine(Path.GetTempPath(), "emuera-fs-write-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
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
