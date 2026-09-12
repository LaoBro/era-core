using System;
using System.IO;
using System.Linq;
using MinorShift.Emuera.Runtime.Utils;
using Xunit;

namespace MinorShift.Emuera.Tests;

/// <summary>
/// issues/21：<see cref="SafCompat"/> 的**枚举**必须两种路径形态都经 <see cref="IGameDirAccessor"/>——
/// 本地分支直连 <c>Directory.GetFiles/EnumerateFiles(path, searchPattern, option)</c> 会绕过通配的
/// 大小写不敏感语义（.NET 在 Unix/Android 由托管代码做 glob），与 content 分支（<see cref="Wildcard"/>）分叉。
///
/// 受影响调用点：<c>ENUMFILES</c>（pattern 由游戏脚本给）、<c>VariableEvaluator.GetDatFiles</c>、
/// 补丁版本号 <c>*.txt</c>。
///
/// 判据取「SafCompat 结果 == accessor 结果」的等价性——两平台各有一个改前即红的 pattern：
/// - Windows：<c>*.</c>（Win32 语义返回无扩展名文件，<see cref="Wildcard"/> 拒绝）；
/// - WSL/Linux：<c>*.CSV</c> / <c>main.erb</c>（托管 glob 大小写敏感，accessor 不敏感）。
/// 即无需 spy accessor，本地 <c>dotnet test</c> 也有真红（口径同 <c>FileSystemGameDirAccessorPatternTests</c>）。
/// </summary>
[Collection("LoaderTests")]
public sealed class SafCompatLocalEnumerationTests
{
	[Theory]
	[InlineData("*.", SearchOption.TopDirectoryOnly)]
	[InlineData("*.CSV", SearchOption.TopDirectoryOnly)]
	[InlineData("main.erb", SearchOption.TopDirectoryOnly)]
	[InlineData("*.CSV", SearchOption.AllDirectories)]
	public void Local_enumeration_matches_the_accessor(string pattern, SearchOption option)
	{
		using var fx = new Fixture();
		var accessor = new FileSystemGameDirAccessor();
		_ = GamePaths.Resolve(fx.Root, accessor);

		Assert.Equal(
			accessor.GetFiles(fx.Root, pattern, option).OrderBy(p => p, StringComparer.Ordinal),
			SafCompat.GetFiles(fx.Root, pattern, option).OrderBy(p => p, StringComparer.Ordinal));
	}

	/// <summary>eraTK 的真实形态：磁盘小写/混合大小写 vs 引擎固定大写 pattern；递归枚举也要覆盖到子目录文件。</summary>
	[Fact]
	public void Local_enumeration_covers_lowercase_extension_files()
	{
		using var fx = new Fixture();
		_ = GamePaths.Resolve(fx.Root, new FileSystemGameDirAccessor());

		var csv = SafCompat.GetFiles(fx.Root, "*.CSV", SearchOption.AllDirectories)
			.Select(Path.GetFileName)
			.OrderBy(n => n, StringComparer.Ordinal)
			.ToArray();

		Assert.Equal(new[] { "Abl.csv", "VarExt1.csv" }, csv);
	}

	private sealed class Fixture : IDisposable
	{
		public string Root { get; }

		public Fixture()
		{
			Root = Path.Combine(Path.GetTempPath(), "emuera-safcompat-enum-" + Guid.NewGuid().ToString("N"));
			Directory.CreateDirectory(Root);
			File.WriteAllText(Path.Combine(Root, "Abl.csv"), "x\n");
			File.WriteAllText(Path.Combine(Root, "MAIN.ERB"), "@MAIN\n");
			File.WriteAllText(Path.Combine(Root, "README"), "no extension\n"); // "*." 哨兵
			Directory.CreateDirectory(Path.Combine(Root, "1#EV"));
			File.WriteAllText(Path.Combine(Root, "1#EV", "VarExt1.csv"), "x\n");
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
