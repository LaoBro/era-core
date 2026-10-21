using MinorShift.Emuera.Files;
using System;
using System.IO;
using System.Text;
using MinorShift.Emuera;
using Xunit;

namespace MinorShift.Emuera.Tests;

/// <summary>
/// ADR-0048（14 票批 A）：<see cref="AppDataIo"/> 补齐的目录级原语。
/// <para>
/// 这些原语存在的唯一理由是让 <c>ErbIrCache</c> / <c>SrcContentCache</c> 的裸
/// <c>File.*</c> / <c>Directory.*</c> 归零（不变量 F-4「应用私有域是唯一豁免域」的收口）。
/// 语义刻意选成<b>幂等</b>：删除不存在的目标视为成功、枚举不存在的目录返回空——
/// 这样调用点的「先判存在再操作」可以整块删掉，而不是原样搬进接缝。
/// </para>
/// </summary>
[Collection("AppDataIsolated")]
public class AppDataIoTests : IDisposable
{
	private readonly string previousRoot;
	private readonly string root;

	public AppDataIoTests()
	{
		previousRoot = AppDataPaths.Directory;
		root = Path.Combine(Path.GetTempPath(), "eracore-appdataio-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(root);
		AppDataPaths.Configure(root);
	}

	public void Dispose()
	{
		AppDataPaths.Configure(previousRoot);
		try
		{
			if (Directory.Exists(root))
				Directory.Delete(root, recursive: true);
		}
		catch
		{
			// 临时目录清理失败不影响断言结果
		}
	}

	[Fact]
	public void EnumerateDirectories_returns_child_directories()
	{
		Directory.CreateDirectory(Path.Combine(root, "IRCache", "keyA"));
		Directory.CreateDirectory(Path.Combine(root, "IRCache", "keyB"));

		var dirs = AppDataIo.EnumerateDirectories(AppDataIo.Resolve("IRCache"));

		Assert.Equal(2, dirs.Length);
		Assert.All(dirs, d => Assert.Equal("IRCache", Path.GetFileName(Path.GetDirectoryName(d))));
	}

	[Fact]
	public void EnumerateDirectories_returns_empty_for_missing_root()
	{
		Assert.Empty(AppDataIo.EnumerateDirectories(AppDataIo.Resolve("nope")));
		Assert.False(AppDataIo.DirectoryExists(AppDataIo.Resolve("nope")));
	}

	[Fact]
	public void GetDirectoryLastWriteUtc_returns_minvalue_for_missing_root()
	{
		var missing = AppDataIo.Resolve("nope");
		Assert.Equal(DateTime.MinValue, AppDataIo.GetDirectoryLastWriteUtc(missing));

		var existing = AppDataIo.Resolve("IRCache");
		Directory.CreateDirectory(Path.Combine(root, "IRCache"));
		var stamp = AppDataIo.GetDirectoryLastWriteUtc(existing);
		Assert.NotEqual(DateTime.MinValue, stamp);
		Assert.Equal(DateTimeKind.Utc, stamp.Kind);
	}

	[Fact]
	public void DeleteDirectoryTree_is_recursive_and_idempotent()
	{
		var dir = Path.Combine(root, "IRCache", "keyA");
		Directory.CreateDirectory(dir);
		File.WriteAllText(Path.Combine(dir, "era_ir.dat"), "x");
		Directory.CreateDirectory(Path.Combine(dir, "nested"));

		AppDataIo.DeleteDirectoryTree(AppDataIo.Resolve("IRCache/keyA"));
		Assert.False(Directory.Exists(dir));

		// 第二次（目标已不存在）不抛：调用点的「判存在再删」可以省掉
		AppDataIo.DeleteDirectoryTree(AppDataIo.Resolve("IRCache/keyA"));
	}

	[Fact]
	public void OpenWrite_truncates_and_DeleteFile_is_idempotent()
	{
		var path = AppDataIo.Resolve("IRCache/dump.bin");
		Directory.CreateDirectory(Path.Combine(root, "IRCache"));

		using (var w = AppDataIo.OpenWrite(path))
			w.Write(Encoding.UTF8.GetBytes("0123456789"));

		using (var w = AppDataIo.OpenWrite(path))
			w.Write(Encoding.UTF8.GetBytes("ab"));

		Assert.Equal(2, new FileInfo(Path.Combine(root, "IRCache", "dump.bin")).Length);

		AppDataIo.DeleteFile(path);
		Assert.False(AppDataIo.FileExists(path));

		// 再删一次不抛（File.Delete 对不存在目标是 no-op）
		AppDataIo.DeleteFile(path);
	}
}
