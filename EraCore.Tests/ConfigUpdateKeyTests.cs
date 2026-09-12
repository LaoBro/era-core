using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using MinorShift.Emuera.Runtime.Config;
using MinorShift.Emuera.Runtime.Utils;
using Xunit;

namespace MinorShift.Emuera.Tests;

/// <summary>
/// issues/19：<see cref="ConfigData"/> 的 <c>getUpdateKey</c> 本地（真实路径）分支必须与 content-URI 分支
/// 同源——经 <see cref="IGameDirAccessor"/> 枚举，而不是直连 <c>Directory.GetFiles(path, pattern, option)</c>。
///
/// 背景：.NET 在 Unix/Android 由托管代码做 glob，<c>MatchCasing.PlatformDefault</c> 解析为大小写敏感，
/// <c>*.CSV</c> 只命中全大写扩展名（eraTK 根目录 20 个 CSV 里仅 <c>CFLAG.CSV</c>），漏掉 19 个固定 CSV 与
/// <c>Chara/</c> 下 165 个 <c>.csv</c>——legacy 与 SAF 于是算出不同的 update key（进而影响 ONCE 参数缩减
/// 与 IR 指纹）。修复见提交同批改动。
///
/// 平台说明：
/// - <c>Legacy_update_key_enumerates_through_the_game_accessor</c> 是**结构守卫**，改前在 Windows/Linux 都红
///   （本地分支没碰 accessor）。
/// - <c>Legacy_update_key_covers_lowercase_extension_files</c> 是**行为守卫**，真红只在大小写敏感的
///   WSL/Linux（Windows 的 OS 与托管 matcher 都不敏感，改前也绿），见 <c>docs/LESSONS/testing-and-ci.md</c> #12。
/// </summary>
[Collection("LoaderTests")]
public sealed class ConfigUpdateKeyTests
{
	private static readonly MethodInfo GetUpdateKeyMethod =
		typeof(ConfigData).GetMethod("getUpdateKey", BindingFlags.Instance | BindingFlags.NonPublic)
		?? throw new MissingMethodException(typeof(ConfigData).FullName, "getUpdateKey");

	private static long GetUpdateKey(ConfigData config) => (long)GetUpdateKeyMethod.Invoke(config, null)!;

	/// <summary>
	/// 结构守卫：legacy 分支的文件枚举必须落到当前 <see cref="IGameDirAccessor"/> 上
	///（<see cref="IGameDirAccessor"/> 契约：「所有文件 IO 必须通过此接口，禁止直接调用 File.* / Directory.*」）。
	/// 改前本地分支直连 <c>Directory.GetFiles</c>，此处断言在任何平台都红。
	/// </summary>
	[Fact]
	public void Legacy_update_key_enumerates_through_the_game_accessor()
	{
		using var tmp = new TempDir();
		tmp.Write("erb/Main.erb", "@MAIN\n");
		tmp.Write("csv/Abl.csv", "x\n");
		var accessor = new RecordingAccessor();
		_ = GamePaths.Resolve(tmp.Root, accessor);
		using var globals = GlobalStatic.OpenScope(new ConfigData());

		_ = GetUpdateKey(ConfigData.Current!);

		Assert.Contains(accessor.FileSearches,
			s => s.Path == GamePaths.Current.ErbDir && s.Pattern == "*.ERB");
		Assert.Contains(accessor.FileSearches,
			s => s.Path == GamePaths.Current.CsvDir && s.Pattern == "*.CSV");
	}

	/// <summary>
	/// 行为守卫（真红在 WSL/Linux）：磁盘上是小写扩展名（<c>Main.erb</c> / <c>Abl.csv</c>，eraTK 的真实形态），
	/// 本地分支的 key 必须把它们计入，且随其 mtime 变化。
	/// 改前在大小写敏感 FS 上 <c>*.ERB</c>/<c>*.CSV</c> 一个都枚举不到 → key 恒 0，<c>first != 0</c> 断言即红。
	/// mtime 显式设成相隔一天，避免「同一系统时钟刻度」导致 XOR 抵消（Windows NTFS 尤其容易）而误判。
	/// </summary>
	[Fact]
	public void Legacy_update_key_covers_lowercase_extension_files()
	{
		using var tmp = new TempDir();
		var erb = tmp.Write("erb/Main.erb", "@MAIN\n");
		var csv = tmp.Write("csv/Abl.csv", "x\n");
		File.SetLastWriteTime(erb, new DateTime(2020, 1, 1, 0, 0, 0));
		File.SetLastWriteTime(csv, new DateTime(2020, 1, 2, 0, 0, 0));
		_ = GamePaths.Resolve(tmp.Root, new FileSystemGameDirAccessor());
		using var globals = GlobalStatic.OpenScope(new ConfigData());

		long first = GetUpdateKey(ConfigData.Current!);
		Assert.NotEqual(0L, first); // 改前（大小写敏感 FS）：小写扩展名全被 pattern 挡掉 → 0

		File.SetLastWriteTime(csv, new DateTime(2020, 1, 3, 0, 0, 0));
		Assert.NotEqual(first, GetUpdateKey(ConfigData.Current!));
	}

	/// <summary>转发到 <see cref="FileSystemGameDirAccessor"/> 并记录 pattern 枚举——用于验证调用确实走 accessor。</summary>
	private sealed class RecordingAccessor : IGameDirAccessor
	{
		private readonly FileSystemGameDirAccessor _inner = new();

		public List<(string Path, string Pattern)> FileSearches { get; } = new();

		public string[] GetFiles(string path, string searchPattern, SearchOption searchOption)
		{
			FileSearches.Add((path, searchPattern));
			return _inner.GetFiles(path, searchPattern, searchOption);
		}

		public Task<string?> PickDirectoryAsync(CancellationToken ct = default) => _inner.PickDirectoryAsync(ct);
		public bool DirectoryExists(string path) => _inner.DirectoryExists(path);
		public string[] GetDirectories(string path) => _inner.GetDirectories(path);
		public string[] GetDirectories(string path, string searchPattern, SearchOption searchOption)
			=> _inner.GetDirectories(path, searchPattern, searchOption);
		public bool FileExists(string path) => _inner.FileExists(path);
		public string[] GetFiles(string path) => _inner.GetFiles(path);
		public string ReadAllText(string path) => _inner.ReadAllText(path);
		public byte[]? ReadAllBytes(string path) => _inner.ReadAllBytes(path);
		public Stream? OpenRead(string path) => _inner.OpenRead(path);
		public void CreateDirectory(string path) => _inner.CreateDirectory(path);
		public Stream OpenWrite(string path) => _inner.OpenWrite(path);
		public void Delete(string path) => _inner.Delete(path);
		public bool HasWriteAccess() => _inner.HasWriteAccess();
		public string ResolveSubPath(string basePath, string subDir) => _inner.ResolveSubPath(basePath, subDir);
		public string CombinePath(string basePath, string filename) => _inner.CombinePath(basePath, filename);
		public string GetParentPath(string path) => _inner.GetParentPath(path);
		public string GetFileName(string path) => _inner.GetFileName(path);
	}

	private sealed class TempDir : IDisposable
	{
		public string Root { get; }

		public TempDir()
		{
			Root = Path.Combine(Path.GetTempPath(), "emuera-updatekey-" + Guid.NewGuid().ToString("N"));
			Directory.CreateDirectory(Root);
		}

		/// <summary>写一个相对名文件（父目录自动创建），返回其绝对路径。</summary>
		public string Write(string relativeName, string content)
		{
			var path = Path.Combine(Root, relativeName.Replace('/', Path.DirectorySeparatorChar));
			Directory.CreateDirectory(Path.GetDirectoryName(path)!);
			File.WriteAllText(path, content);
			return path;
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
