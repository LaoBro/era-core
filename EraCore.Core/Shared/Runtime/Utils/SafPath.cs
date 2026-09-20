using System;
using System.Collections.Generic;
using System.IO;

namespace MinorShift.Emuera.Runtime.Utils;

/// <summary>
/// SAF content:// URI 与普通路径统一的逻辑文件名 / 相对路径工具。
/// <para>
/// SAF 的路径分隔符活在 documentId 的 <c>%2F</c> 里，不在 URI path 的 <c>/</c> 上。
/// 对完整 content URI 调用 <see cref="Path.GetFileName"/> / <see cref="Path.GetFileNameWithoutExtension"/>
/// 会得到整段 URL-encoded documentId，而不是游戏逻辑短名（如 <c>DVAR</c>）。
/// </para>
/// </summary>
internal static class SafPath
{
	public static bool IsContentUri(string? path) =>
		path != null && path.StartsWith("content://", StringComparison.Ordinal);

	/// <summary>
	/// 从 content URI 提取 Unescape 后的 documentId（<c>.../document/</c> 之后整段；
	/// tree URI 无 <c>/document/</c> 段时取 <c>/tree/</c> 之后整段——<c>DocumentsContract.GetTreeDocumentId</c>
	/// 的纯 C# 等价）。非 content 或无法解析时返回 null。
	/// </summary>
	public static string? TryGetDocumentId(string path)
	{
		if (!IsContentUri(path)) return null;
		var posDoc = path.LastIndexOf("/document/", StringComparison.Ordinal);
		string encoded;
		if (posDoc >= 0)
		{
			encoded = path[(posDoc + "/document/".Length)..];
		}
		else
		{
			// tree URI（.../tree/...，无 /document/）：取 /tree/ 之后整段。
			// document URI 因 posDoc 优先不会落此分支。
			var posTree = path.LastIndexOf("/tree/", StringComparison.Ordinal);
			if (posTree < 0) return null;
			encoded = path[(posTree + "/tree/".Length)..];
		}
		var q = encoded.IndexOfAny(['?', '#']);
		if (q >= 0) encoded = encoded[..q];
		if (encoded.Length == 0) return null;
		return Uri.UnescapeDataString(encoded);
	}

	/// <summary>
	/// 游戏逻辑文件名（短名）。content URI → documentId 最后一段；普通路径 → <see cref="Path.GetFileName"/>。
	/// </summary>
	public static string GetLogicalFileName(string path)
	{
		if (IsContentUri(path))
		{
			var docId = TryGetDocumentId(path);
			if (docId != null)
			{
				var last = docId.LastIndexOf('/');
				return last >= 0 ? docId[(last + 1)..] : docId;
			}
			// 无 /document/ 段时回退：URI 最后一段 Unescape（仍可能偏长，但优于 Path.GetFileName）
			var slash = path.LastIndexOf('/');
			var raw = slash >= 0 ? path[(slash + 1)..] : path;
			var q = raw.IndexOfAny(['?', '#']);
			if (q >= 0) raw = raw[..q];
			return Uri.UnescapeDataString(raw);
		}
		return Path.GetFileName(path);
	}

	/// <summary>逻辑文件名去扩展名（如 <c>DVAR.erd</c> → <c>DVAR</c>）。</summary>
	public static string GetLogicalFileNameWithoutExtension(string path)
	{
		return Path.GetFileNameWithoutExtension(GetLogicalFileName(path));
	}

	/// <summary>
	/// issues/15：content URI 的**可读路径**——形如 <c>内部存储 / games / eratoho</c>，供游戏选择页展示。
	/// <para>
	/// SAF 的 <c>content://</c> URI 是 provider 内部句柄，直接铺给用户没有意义；但把它折叠成一个常量
	/// （曾经的「Android 存储目录」）会让用户换任何目录都看到同一句话——目录名信息全丢。而 documentId
	/// 本来就带人类可读的相对路径（<c>primary:games/eratoho</c>），本方法把它还原出来。
	/// </para>
	/// <para>
	/// 卷名映射：<c>primary</c> → <c>内部存储</c>；其余卷（SD / OTG 的卷 UUID）保留原标识——
	/// 卷描述要经 Android StorageManager 取，纯 Core 层拿不到，不在此猜中文名。
	/// 非 content URI 原样返回（真实文件路径本身就是可读的）。
	/// 解析不出 documentId（未知 provider）时返回 null——调用方回退到折叠文案。
	/// </para>
	/// </summary>
	public static string? TryGetDisplayPath(string? path)
	{
		if (string.IsNullOrEmpty(path)) return null;
		if (!IsContentUri(path)) return path;

		var docId = TryGetDocumentId(path);
		if (string.IsNullOrEmpty(docId)) return null;

		// documentId 形态：<volumeId>:<相对路径>。无冒号的自定义 provider 只有相对路径。
		var colon = docId.IndexOf(':');
		var volume = colon > 0 ? docId[..colon] : string.Empty;
		var relative = colon >= 0 ? docId[(colon + 1)..] : docId;

		var parts = new List<string>();
		if (volume.Length > 0)
			parts.Add(volume.Equals("primary", StringComparison.OrdinalIgnoreCase) ? "内部存储" : volume);
		foreach (var segment in relative.Split('/', StringSplitOptions.RemoveEmptyEntries))
			parts.Add(segment);

		return parts.Count > 0 ? string.Join(" / ", parts) : null;
	}

	/// <summary>
	/// fullPath 是否位于 rootDir 之下（含与 root 同 document 的文件）。
	/// content URI 用 Unescape 后的 documentId 前缀比较，避免 <c>%2F</c> 导致 raw 字符串前缀匹配失效。
	/// </summary>
	public static bool IsUnderRoot(string rootDir, string fullPath)
	{
		if (IsContentUri(rootDir) && IsContentUri(fullPath))
		{
			var rootId = TryGetDocumentId(rootDir);
			var fileId = TryGetDocumentId(fullPath);
			if (rootId == null || fileId == null) return false;
			rootId = rootId.TrimEnd('/');
			return fileId.Equals(rootId, StringComparison.OrdinalIgnoreCase)
				|| fileId.StartsWith(rootId + "/", StringComparison.OrdinalIgnoreCase);
		}

		try
		{
			var root = Path.GetFullPath(rootDir);
			var full = Path.GetFullPath(fullPath);
			var rootTrim = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
			if (full.Equals(rootTrim, StringComparison.OrdinalIgnoreCase))
				return true;
			if (!root.EndsWith(Path.DirectorySeparatorChar) && !root.EndsWith(Path.AltDirectorySeparatorChar))
				root += Path.DirectorySeparatorChar;
			return full.StartsWith(root, StringComparison.OrdinalIgnoreCase);
		}
		catch
		{
			return false;
		}
	}

	/// <summary>
	/// 相对 rootDir 的逻辑相对路径。分隔符统一为 <c>\</c>，对齐 <c>Config.getFiles</c> / Windows 行为。
	/// 若不在 root 下，回退为逻辑短文件名。
	/// </summary>
	public static string GetRelativePathFromRoot(string rootDir, string fullPath)
	{
		if (IsContentUri(rootDir) && IsContentUri(fullPath))
		{
			var rootId = TryGetDocumentId(rootDir);
			var fileId = TryGetDocumentId(fullPath);
			if (rootId != null && fileId != null)
			{
				rootId = rootId.TrimEnd('/');
				if (fileId.Equals(rootId, StringComparison.OrdinalIgnoreCase))
					return GetLogicalFileName(fullPath);
				if (fileId.StartsWith(rootId + "/", StringComparison.OrdinalIgnoreCase))
					return fileId[(rootId.Length + 1)..].Replace('/', '\\');
			}
			return GetLogicalFileName(fullPath);
		}

		try
		{
			var root = Path.GetFullPath(rootDir);
			var full = Path.GetFullPath(fullPath);
			if (!root.EndsWith(Path.DirectorySeparatorChar) && !root.EndsWith(Path.AltDirectorySeparatorChar))
				root += Path.DirectorySeparatorChar;
			if (full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
				return full[root.Length..].Replace(Path.AltDirectorySeparatorChar, '\\');
			return Path.GetFileName(fullPath);
		}
		catch
		{
			return Path.GetFileName(fullPath);
		}
	}

	/// <summary>
	/// issues/15 §0.3：sidecar 存的相对名 → 本平台物理相对名。
	/// <para>
	/// <see cref="GetRelativePathFromRoot"/> 的存储契约是分隔符统一为 <c>\</c>（对齐 Config.getFiles /
	/// Windows）。重建物理路径时必须再归一回<b>本平台</b>分隔符：Android 上 <c>\</c> 不是分隔符，
	/// 原样拼接会拼出 <c>erb/ABL\ABL.ERB</c> 这种打不开的死路径；且物理路径进内容指纹，
	/// 分隔符不一致会让命中轮 fp 与建库轮分叉 → 永远 miss。
	/// </para>
	/// <para>Windows：两次替换后全为 <c>\</c>；Android：全为 <c>/</c>。与枚举直接产出的路径形态一致。</para>
	/// </summary>
	public static string RelToPhysical(string relToRoot)
	{
		var sep = Path.DirectorySeparatorChar;
		return relToRoot.Replace('\\', sep).Replace('/', sep);
	}
}
