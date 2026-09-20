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
