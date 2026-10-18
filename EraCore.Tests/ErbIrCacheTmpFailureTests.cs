using System;
using System.Collections.Generic;
using System.IO;
using MinorShift.Emuera.Runtime.Script.Loader;
using Xunit;

namespace MinorShift.Emuera.Tests;

/// <summary>
/// ADR-0048（14 票批 B）行为锁：<b>sidecar 写入失败不得破坏既有缓存</b>。
/// <para>
/// 写路径是「tmp + 原子替换」；把 IO 换成接缝（<c>SafCompat.OpenWrite</c>）后，唯一可能变的行为是
/// 「tmp 阶段失败时留下什么」。锁法：在 tmp 路径上放一个同名<b>目录</b>让写入必失败，
/// 然后断言旧 sidecar 仍可读、内容未被替换。
/// </para>
/// </summary>
[Collection("AppDataIsolated")]
public class ErbIrCacheTmpFailureTests : IDisposable
{
	private readonly string dir;

	public ErbIrCacheTmpFailureTests()
	{
		dir = Path.Combine(Path.GetTempPath(), "eracore-ircache-tmp-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(dir);
	}

	public void Dispose()
	{
		try
		{
			if (Directory.Exists(dir))
				Directory.Delete(dir, recursive: true);
		}
		catch
		{
			// 清理失败不影响断言
		}
	}

	[Fact]
	public void Write_failure_keeps_previous_sidecar_intact()
	{
		var irPath = Path.Combine(dir, "era_ir.dat");
		var first = new List<(string Logical, string RelToRoot)> { ("A.ERB", "a.erb") };
		var tree = new List<string> { "a.erb" };

		Assert.True(ErbIrCache.TryWriteFileList(irPath, first, tree));
		Assert.True(ErbIrCache.TryReadFileList(irPath, out var pending1, out var tree1));
		Assert.Single(pending1);
		Assert.Equal("A.ERB", pending1[0].Logical);
		Assert.Single(tree1);
		

		// 阻断 tmp：在真实 sidecar 的 tmp 路径上放同名目录 ⇒ SafCompat.OpenWrite 必失败
		var tmpBlocker = ErbIrCache.GetFileListPath(irPath) + ".tmp";
		Directory.CreateDirectory(tmpBlocker);

		var second = new List<(string Logical, string RelToRoot)> { ("B.ERB", "b.erb"), ("C.ERB", "c.erb") };
		Assert.False(ErbIrCache.TryWriteFileList(irPath, second, tree));

		// 旧缓存不被替换：仍是第一次那份
		Assert.True(ErbIrCache.TryReadFileList(irPath, out var pending2, out var tree2));
		Assert.Single(pending2);
		Assert.Equal("A.ERB", pending2[0].Logical);
		Assert.Single(tree2);
	}
}
