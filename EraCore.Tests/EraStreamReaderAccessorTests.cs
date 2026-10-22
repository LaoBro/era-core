using MinorShift.Emuera.Files;
using System;
using System.Text;
using MinorShift.Emuera.Runtime.Utils;
using MinorShift.Emuera.Sub;
using Xunit;

namespace MinorShift.Emuera.Tests;

/// <summary>
/// ADR-0037 行为锁：<see cref="EraStreamReader"/> 的读取只经 <see cref="IGameDirAccessor"/>。
/// <para>
/// 原先存在「accessor 读空后再直连文件系统 <c>File.ReadAllBytes</c>」的最后兜底，本 ADR 予以删除——
/// 这是 Core 里最后一条<b>可编译且可达</b>的直连读缝，而本类又是 CSV（GAMEBASE / 固定名 / CHARA /
/// VarExt / .als）与 ERH 读取的<b>唯一咽喉</b>。故把两条路径各钉一条断言：
/// accessor 有字节 ⇒ 读出来；accessor 拿不到 ⇒ <c>Open</c> 返回 false（失败显性化），不再回落本地。
/// </para>
/// </summary>
[Collection("LoaderTests")]
public class EraStreamReaderAccessorTests
{
	private const string Root =
		"content://com.android.externalstorage.documents/tree/primary%3Aemuera/document/primary%3Aemuera%2FEraStreamReader";

	[Fact]
	public void Open_reads_lines_through_the_accessor()
	{
		var accessor = new InMemoryContentDirAccessor(Root);
		using var paths = new RootScope(Root, accessor);
		var path = accessor.CombinePath(Root, "ABL.CSV");
		accessor.Seed(path, Encoding.UTF8.GetBytes("体力,こんにちは\n気力,さようなら\n"));

		using var reader = new EraStreamReader(false);

		Assert.True(reader.OpenOnCache(path, SafPath.GetLogicalFileName(path)));
		Assert.Equal("体力,こんにちは", reader.ReadLine());
		Assert.Equal("気力,さようなら", reader.ReadLine());
	}

	[Fact]
	public void Open_returns_false_when_the_accessor_cannot_read_the_file()
	{
		var accessor = new InMemoryContentDirAccessor(Root);
		using var paths = new RootScope(Root, accessor);
		// content URI 形态：本机磁盘上绝无同名实文件——即便兜底还在也救不回来。
		// 这里锁的是「失败显性化」：返回 false 而非抛出，也不再尝试本地 IO。
		var missing = accessor.CombinePath(Root, "NOSUCH.CSV");

		using var reader = new EraStreamReader(false);

		Assert.False(reader.OpenOnCache(missing, SafPath.GetLogicalFileName(missing)));
	}

	/// <summary>
	/// 区分性最强的那一条：<b>磁盘上有、accessor 没有</b>。
	/// 旧的最后兜底在这一步会绕过 accessor 直接读磁盘并返回 true；删掉兜底后必须是 false。
	/// </summary>
	[Fact]
	public void Open_does_not_fall_back_to_the_local_file_system()
	{
		var tempRoot = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "emuera-sr-" + Guid.NewGuid().ToString("N"));
		System.IO.Directory.CreateDirectory(tempRoot);
		try
		{
			var localFile = System.IO.Path.Combine(tempRoot, "ONDISK.CSV");
			System.IO.File.WriteAllBytes(localFile, Encoding.UTF8.GetBytes("disk,行\n"));

			// accessor 是纯内存桩：既不注册该文件，也不触碰磁盘 ⇒ ReadAllBytes 返回 null。
			var accessor = new InMemoryContentDirAccessor(tempRoot);
			using var paths = new RootScope(tempRoot, accessor);
			using var reader = new EraStreamReader(false);
			Assert.False(reader.OpenOnCache(localFile, SafPath.GetLogicalFileName(localFile)));
		}
		finally { try { System.IO.Directory.Delete(tempRoot, recursive: true); } catch { /* 清理失败不掩盖断言 */ } }
	}
}
