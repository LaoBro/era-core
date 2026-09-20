using MinorShift.Emuera.Runtime.Utils;
using Xunit;

namespace MinorShift.Emuera.Tests;

/// <summary>
/// SAF content URI 逻辑文件名 / 相对路径：document 段内分隔符是 %2F，不能对整段 URI 用 Path.GetFileName*。
/// </summary>
public class SafPathTests
{
	// 模拟 Android DocumentsContract 文档 URI（tree + document，documentId 内路径被 %2F 编码）
	const string ErbRoot =
		"content://com.android.externalstorage.documents/tree/primary%3Aemuera/document/primary%3Aemuera%2FTK1.29.3%2FERB";
	const string DvarErd =
		"content://com.android.externalstorage.documents/tree/primary%3Aemuera/document/primary%3Aemuera%2FTK1.29.3%2FERB%2FDVAR.erd";
	const string NestedErb =
		"content://com.android.externalstorage.documents/tree/primary%3Aemuera/document/primary%3Aemuera%2FTK1.29.3%2FERB%2FSYSTEM%2FEVENT_DAILY%2Fxxx.ERB";
	const string OutsideCsv =
		"content://com.android.externalstorage.documents/tree/primary%3Aemuera/document/primary%3Aemuera%2FTK1.29.3%2FCSV%2FGAMEBASE.CSV";

	// issues/15：SAF 树的**可读路径**样本——documentId 里带人类可读相对路径（<volumeId>:<rel>）
	const string TreeRootGames =
		"content://com.android.externalstorage.documents/tree/primary%3Agames%2Feratoho";
	const string TreeRootVolumeOnly =
		"content://com.android.externalstorage.documents/tree/primary%3A";
	const string TreeRootRemovable =
		"content://com.android.externalstorage.documents/tree/1AEF-3B2C%3Agames";

	[Fact]
	public void GetLogicalFileName_SAF_returns_short_name_not_documentId()
	{
		Assert.Equal("DVAR.erd", SafPath.GetLogicalFileName(DvarErd));
		Assert.Equal("xxx.ERB", SafPath.GetLogicalFileName(NestedErb));
	}

	[Fact]
	public void GetLogicalFileNameWithoutExtension_SAF_yields_DVAR_key()
	{
		Assert.Equal("DVAR", SafPath.GetLogicalFileNameWithoutExtension(DvarErd));
		Assert.Equal("DVAR", SafPath.GetLogicalFileNameWithoutExtension(DvarErd).ToUpperInvariant());
	}

	[Fact]
	public void GetLogicalFileName_SAF_ignores_uri_query_and_fragment()
	{
		var uriWithMetadata = DvarErd + "?provider=documents#preview";

		Assert.Equal("DVAR.erd", SafPath.GetLogicalFileName(uriWithMetadata));
		Assert.Equal("DVAR", SafPath.GetLogicalFileNameWithoutExtension(uriWithMetadata));
	}

	[Fact]
	public void GetRelativePathFromRoot_SAF_uses_backslash_like_Config_getFiles()
	{
		Assert.Equal("DVAR.erd", SafPath.GetRelativePathFromRoot(ErbRoot, DvarErd));
		Assert.Equal(@"SYSTEM\EVENT_DAILY\xxx.ERB", SafPath.GetRelativePathFromRoot(ErbRoot, NestedErb));
	}

	[Fact]
	public void IsUnderRoot_filters_by_documentId_prefix()
	{
		Assert.True(SafPath.IsUnderRoot(ErbRoot, DvarErd));
		Assert.True(SafPath.IsUnderRoot(ErbRoot, NestedErb));
		Assert.False(SafPath.IsUnderRoot(ErbRoot, OutsideCsv));
	}

	/// <summary>
	/// issues/15：把 SAF 树解成用户看得懂的目录名——折叠成常量会让换任何目录都显示同一句话。
	/// </summary>
	[Fact]
	public void TryGetDisplayPath_SAF_tree_yields_readable_path_not_provider_uri()
	{
		Assert.Equal("内部存储 / games / eratoho", SafPath.TryGetDisplayPath(TreeRootGames));
	}

	[Fact]
	public void TryGetDisplayPath_SAF_document_keeps_nested_segments()
	{
		Assert.Equal("内部存储 / emuera / TK1.29.3 / ERB", SafPath.TryGetDisplayPath(ErbRoot));
	}

	[Fact]
	public void TryGetDisplayPath_volume_root_is_storage_name_only()
	{
		// 用户在系统选择器里直接选了「内部存储」根
		Assert.Equal("内部存储", SafPath.TryGetDisplayPath(TreeRootVolumeOnly));
	}

	[Fact]
	public void TryGetDisplayPath_non_primary_volume_keeps_volume_id()
	{
		// SD / OTG 的卷描述要经 Android StorageManager 取，Core 层不猜中文名
		Assert.Equal("1AEF-3B2C / games", SafPath.TryGetDisplayPath(TreeRootRemovable));
	}

	[Fact]
	public void TryGetDisplayPath_filesystem_path_passes_through()
	{
		// 真实路径本身就是可读的——前端因此两条轨走同一条渲染路径
		Assert.Equal(@"D:\games\emuera", SafPath.TryGetDisplayPath(@"D:\games\emuera"));
	}

	[Fact]
	public void TryGetDisplayPath_returns_null_when_unresolvable()
	{
		// null 是「解析不出」的信号，前端据此回退 formatMainGameDirForDisplay 的折叠文案
		Assert.Null(SafPath.TryGetDisplayPath(null));
		Assert.Null(SafPath.TryGetDisplayPath(""));
		Assert.Null(SafPath.TryGetDisplayPath("content://unknown.provider/no-tree-or-document"));
	}

	// ── issues/15（方案 B）：content URI → 真实路径反推的三段纯逻辑 ──────────────
	// Android 层的 SafLocalPathResolver 只负责「拿卷根 + 探针」，判定规则全在这里，
	// 于是可以在桌面测试项目里锁住（反推本身要真机，但决策规则不必等真机）。

	[Fact]
	public void TrySplitDocumentId_separates_volume_and_relative_path()
	{
		Assert.True(SafPath.TrySplitDocumentId("primary:games/eratoho", out var volume, out var rel));
		Assert.Equal("primary", volume);
		Assert.Equal("games/eratoho", rel);

		Assert.True(SafPath.TrySplitDocumentId("1AEF-3B2C:games", out var removable, out var relRemovable));
		Assert.Equal("1AEF-3B2C", removable);
		Assert.Equal("games", relRemovable);
	}

	[Fact]
	public void TrySplitDocumentId_volume_root_yields_empty_relative_path()
	{
		// 用户直接选了卷根——相对路径为空是合法结果，不是失败
		Assert.True(SafPath.TrySplitDocumentId("primary:", out var volume, out var rel));
		Assert.Equal("primary", volume);
		Assert.Equal("", rel);
	}

	[Fact]
	public void TrySplitDocumentId_only_first_colon_separates()
	{
		Assert.True(SafPath.TrySplitDocumentId("primary:games/a:b", out var volume, out var rel));
		Assert.Equal("primary", volume);
		Assert.Equal("games/a:b", rel);
	}

	[Fact]
	public void TrySplitDocumentId_without_colon_keeps_relative_only()
	{
		// 无冒号的自定义 provider：可解析但卷标识为空，调用方据此放弃反推
		Assert.True(SafPath.TrySplitDocumentId("games/eratoho", out var volume, out var rel));
		Assert.Equal("", volume);
		Assert.Equal("games/eratoho", rel);
	}

	[Fact]
	public void TrySplitDocumentId_rejects_malformed()
	{
		Assert.False(SafPath.TrySplitDocumentId(null, out _, out _));
		Assert.False(SafPath.TrySplitDocumentId("", out _, out _));
		Assert.False(SafPath.TrySplitDocumentId(":x", out _, out _));
	}

	[Fact]
	public void IsDirectAccessBlocked_covers_android_data_and_obb_only()
	{
		// Android 11+ 即使有 MANAGE_EXTERNAL_STORAGE 也禁止直接路径读这两个区
		Assert.True(SafPath.IsDirectAccessBlocked("Android/data"));
		Assert.True(SafPath.IsDirectAccessBlocked("Android/data/com.foo/files"));
		Assert.True(SafPath.IsDirectAccessBlocked("android/DATA/com.foo"));
		Assert.True(SafPath.IsDirectAccessBlocked("Android/obb/com.foo"));

		// 只匹配顶层前缀——同名嵌套目录不在禁区
		Assert.False(SafPath.IsDirectAccessBlocked("games/Android/data"));
		Assert.False(SafPath.IsDirectAccessBlocked("Android/media/com.foo"));
		Assert.False(SafPath.IsDirectAccessBlocked("Android"));
		Assert.False(SafPath.IsDirectAccessBlocked(""));
	}

	[Fact]
	public void TryCombineVolumeRoot_joins_root_and_relative()
	{
		Assert.True(SafPath.TryCombineVolumeRoot("/storage/emulated/0", "games/eratoho", out var path));
		Assert.Equal("/storage/emulated/0/games/eratoho", path);

		Assert.True(SafPath.TryCombineVolumeRoot("/storage/1AEF-3B2C/", "games", out var removable));
		Assert.Equal("/storage/1AEF-3B2C/games", removable);
	}

	[Fact]
	public void TryCombineVolumeRoot_empty_relative_returns_root()
	{
		Assert.True(SafPath.TryCombineVolumeRoot("/storage/emulated/0/", "", out var path));
		Assert.Equal("/storage/emulated/0", path);
	}

	[Fact]
	public void TryCombineVolumeRoot_rejects_traversal_and_bad_input()
	{
		// 宁可退回 SAF 慢一点，也不产出一个可疑路径去指错目录
		Assert.False(SafPath.TryCombineVolumeRoot(null, "games", out _));
		Assert.False(SafPath.TryCombineVolumeRoot("", "games", out _));
		Assert.False(SafPath.TryCombineVolumeRoot("/storage/emulated/0", "../etc", out _));
		Assert.False(SafPath.TryCombineVolumeRoot("/storage/emulated/0", "games/../..", out _));
		Assert.False(SafPath.TryCombineVolumeRoot("/storage/emulated/0", "games//x", out _));
	}

	[Fact]
	public void Filesystem_paths_still_use_Path_semantics()
	{
		var file = @"D:\games\TK\ERB\SYSTEM\a.ERB";
		// 不强制存在磁盘上的路径：GetLogicalFileName 不解析真实 FS
		Assert.Equal("a.ERB", SafPath.GetLogicalFileName(file));
		Assert.Equal("a", SafPath.GetLogicalFileNameWithoutExtension(file));
	}

	[Fact]
	public void RelToPhysical_normalizes_storage_separator_to_platform()
	{
		// sidecar/IR 存储契约是 '\\'；重建物理路径必须归一到本平台分隔符
		// （Android '/'、Windows '\\'），否则会拼出 `erb/1#EV\2#NEST` 死路径（issues/15 §0.3）。
		var sep = System.IO.Path.DirectorySeparatorChar;
		Assert.Equal($"1#EV{sep}2#NEST", SafPath.RelToPhysical(@"1#EV\2#NEST"));
		Assert.Equal($"1#EV{sep}2#NEST", SafPath.RelToPhysical("1#EV/2#NEST"));
		Assert.Equal($"SYSTEM{sep}EVENT{sep}a.ERB", SafPath.RelToPhysical(@"SYSTEM\EVENT/a.ERB"));
	}

	[Fact]
	public void Path_GetFileName_on_SAF_URI_is_wrong_baseline()
	{
		// 对照：证明旧写法会得到整段 documentId，而不是短名
		var broken = System.IO.Path.GetFileNameWithoutExtension(DvarErd);
		Assert.NotEqual("DVAR", broken);
		Assert.Contains("emuera", broken, System.StringComparison.OrdinalIgnoreCase);
	}
}
