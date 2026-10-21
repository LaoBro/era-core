using MinorShift.Emuera.Files;
using System;
using System.IO;
using System.Text;
using Xunit;

namespace MinorShift.Emuera.Tests;

/// <summary>
/// issues/20：<see cref="FileSystemGameDirAccessor"/> 的**精确名读取**（<c>FileExists</c> /
/// <c>OpenRead</c> / <c>ReadAllText</c> / <c>ReadAllBytes</c> / <c>DirectoryExists</c>）必须在
/// 大小写敏感挂载上容错——引擎对固定名资源用硬编码大写（<c>GAMEBASE.CSV</c>/<c>ABL.CSV</c>…），
/// 磁盘真实名任意（<c>Abl.csv</c>/<c>GameBase.csv</c>）；eraTK 根目录 20 个 CSV 里只有
/// <c>CFLAG.CSV</c> 是全大写。缺失文件是 early-return，症状是「一堆未知标识符」而非
/// 「文件打不开」——静默，且换到 app 私有 /data、部分 SD/OTG、桌面 Linux 挂载即触发。
///
/// 平台说明：Windows/FUSE 挂载不区分大小写，OS 层精确命中 → 容错分支不触发，以下用例在
/// Windows 上**改前也绿**；真红只在大小写敏感的 WSL/Linux（<c>docs/LESSONS/testing-and-ci.md</c>
/// #12 关卡，本项改动必须过该关卡）。「同名仅大小写不同共存」在不敏感 FS 上无法构造
/// （第二次写覆盖第一次），共存用例在 Windows 退化为单文件语义、在 Linux 验证「精确命中优先」。
/// </summary>
public class FileSystemGameDirAccessorExactNameTests
{
	[Fact]
	public void FileExists_and_DirectoryExists_resolve_case_insensitively_on_exact_miss()
	{
		using var fx = new Fixture();
		var accessor = new FileSystemGameDirAccessor();

		Assert.True(accessor.FileExists(Path.Combine(fx.CsvDir, "ABL.CSV")));       // 磁盘 Abl.csv
		Assert.True(accessor.FileExists(Path.Combine(fx.CsvDir, "GAMEBASE.CSV")));  // 磁盘 GameBase.csv
		Assert.True(accessor.DirectoryExists(Path.Combine(fx.Root, "RESOURCES")));  // 磁盘 resources

		Assert.False(accessor.FileExists(Path.Combine(fx.CsvDir, "NOPE.CSV")));     // 不存在不得误报
		Assert.False(accessor.DirectoryExists(Path.Combine(fx.Root, "NOPE")));
	}

	[Fact]
	public void OpenRead_ReadAllBytes_ReadAllText_resolve_case_insensitively()
	{
		using var fx = new Fixture();
		var accessor = new FileSystemGameDirAccessor();
		var abl = Path.Combine(fx.CsvDir, "ABL.CSV");

		using (var stream = accessor.OpenRead(abl))
		{
			Assert.NotNull(stream);
			using var ms = new MemoryStream();
			stream!.CopyTo(ms);
			Assert.Equal("abl-content", Encoding.UTF8.GetString(ms.ToArray()));
		}
		Assert.Equal("abl-content", Encoding.UTF8.GetString(accessor.ReadAllBytes(abl)!));
		Assert.Equal("abl-content", accessor.ReadAllText(abl));

		Assert.Null(accessor.OpenRead(Path.Combine(fx.CsvDir, "NOPE.CSV")));
		Assert.Null(accessor.ReadAllBytes(Path.Combine(fx.CsvDir, "NOPE.CSV")));
	}

	[Fact]
	public void Exact_hit_wins_when_case_variants_coexist()
	{
		using var fx = new Fixture();
		var dir = Path.Combine(fx.Root, "coexist");
		Directory.CreateDirectory(dir);
		File.WriteAllText(Path.Combine(dir, "Abl.csv"), "lower");
		// 大小写不敏感 FS 上这行覆盖上一行 → 单文件 "UPPER"；敏感 FS 上两文件并存
		File.WriteAllText(Path.Combine(dir, "ABL.CSV"), "UPPER");
		var accessor = new FileSystemGameDirAccessor();

		// 精确命中优先：ABL.CSV 读到的是精确同名文件的内容，不是 Abl.csv 的
		Assert.Equal("UPPER", accessor.ReadAllText(Path.Combine(dir, "ABL.CSV")));
		Assert.True(accessor.FileExists(Path.Combine(dir, "ABL.CSV")));
	}

	[Fact]
	public void Runtime_writes_are_visible_to_case_mismatched_reads()
	{
		using var fx = new Fixture();
		var accessor = new FileSystemGameDirAccessor();
		// 先触发 csv 目录名映射构建（eraTK 形态：全部小写扩展名 → 精确全 miss）
		Assert.True(accessor.FileExists(Path.Combine(fx.CsvDir, "ABL.CSV")));

		// 运行期经 accessor 写入新文件（写失效父目录映射）后，大小写失配的读取必须可见
		using (accessor.OpenWrite(Path.Combine(fx.CsvDir, "savedvar.csv")))
			{ }
		Assert.True(accessor.FileExists(Path.Combine(fx.CsvDir, "SAVEDVAR.CSV")));
	}

	private sealed class Fixture : IDisposable
	{
		public string Root { get; }
		public string CsvDir { get; }

		public Fixture()
		{
			Root = Path.Combine(Path.GetTempPath(), "emuera-fs-exactname-" + Guid.NewGuid().ToString("N"));
			CsvDir = Path.Combine(Root, "csv");
			Directory.CreateDirectory(CsvDir);
			Directory.CreateDirectory(Path.Combine(Root, "erb"));
			Directory.CreateDirectory(Path.Combine(Root, "resources"));
			File.WriteAllText(Path.Combine(CsvDir, "Abl.csv"), "abl-content");
			File.WriteAllText(Path.Combine(CsvDir, "GameBase.csv"), "gamebase-content");
		}

		public void Dispose()
		{
			try
			{
				if (Directory.Exists(Root))
					Directory.Delete(Root, recursive: true);
			}
			catch { /* best-effort */ }
		}
	}
}
