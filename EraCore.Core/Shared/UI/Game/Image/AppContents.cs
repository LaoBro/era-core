#if HEADLESS
using MinorShift.Emuera;
using MinorShift.Emuera.Primitives;
using MinorShift.Emuera.Runtime.Config;
using MinorShift.Emuera.Runtime.Utils;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace MinorShift.Emuera.UI.Game.Image;

static class AppContents
{
	static ConcurrentDictionary<int, GraphicsImage> gList = [];

	/// <summary>GetSprite 替身缓存（按游戏根隔离——切目录时重建）。</summary>
	static readonly object _stubSync = new();
	static string? _stubRoot;
	static ConcurrentDictionary<string, ASprite> _stubs = new(StringComparer.OrdinalIgnoreCase);

	public static GraphicsImage GetGraphics(int i)
	{
		if (gList.TryGetValue(i, out GraphicsImage? value))
			return value;
		GraphicsImage g = new(i);
		gList[i] = g;
		return g;
	}

	/// <summary>
	/// 无头下 sprite 表不可用（LoadContents stub），但 <see cref="ImageNameTable"/> 提供
	/// 「sprite 名 → 文件 + 裁切矩形」映射——命中时返回轻量替身（IsCreated=true、DestBaseSize=
	/// 裁切尺寸、SourceSize=整图尺寸、CropOrigin=裁切原点），让 <c>SPRITECREATED</c>/
	/// <c>SPRITEWIDTH</c>/<c>SPRITEHEIGHT</c> 及 <see cref="ConsoleImagePart"/> 几何分支正常工作
	/// （真实游戏立绘选择逻辑依赖；issue 07 起裁切矩形进入几何——Face_1 → 180×180）。
	/// 未命中（表无此名 / 文件读不到）返回 null——调用方按"未创建"处理。
	/// </summary>
	public static ASprite GetSprite(string name)
	{
		var paths = GamePaths.Current;
		if (string.IsNullOrEmpty(name) || paths?.DirAccessor == null)
			return null!;
		lock (_stubSync)
		{
			// 缓存按游戏根隔离：/load-game 切目录后同名 sprite 可能是新文件
			if (_stubRoot != paths.ExeDir)
			{
				_stubRoot = paths.ExeDir;
				_stubs = new ConcurrentDictionary<string, ASprite>(StringComparer.OrdinalIgnoreCase);
			}
			if (_stubs.TryGetValue(name, out var cached))
				return cached;
		}
		// 1. 直接相对路径（test_game 的 img/test.png 形态）——无裁切，Dest=Source=整图
		if (ProbeFile(paths, name, out var fw, out var fh) && fw > 0 && fh > 0)
		{
			var full = new EmuSize(fw, fh);
			var stub = new HeadlessSprite(name, full);
			_stubs[name] = stub;
			return stub;
		}
		// 2. sprite 名表（era 的 Face_1 → resources/1_Face.png）：Dest=裁切尺寸；越界（OoR）宽容回退整图
		if (ImageNameTable.TryResolveEntry(paths.DirAccessor, paths.ExeDir, name, out var entry)
			&& entry != null
			&& ProbeFile(paths, entry.RelativePath, out var sfw, out var sfh)
			&& sfw > 0 && sfh > 0)
		{
			var source = new EmuSize(sfw, sfh);
			ASprite stub;
			if (entry.Crop is SpriteCrop c && c.X + c.Width <= sfw && c.Y + c.Height <= sfh)
				stub = new HeadlessSprite(name, new EmuSize(c.Width, c.Height), source, new EmuPoint(c.X, c.Y));
			else
				stub = new HeadlessSprite(name, source); // 无裁切 / 裁切越界→整图（WinForms 告警跳过，无头宽容）
			_stubs[name] = stub;
			return stub;
		}
		return null!;
	}
	public static void SpriteDispose(string name) { }
	public static long SpriteDisposeAll(bool delCsvImage) => 0;
	public static void CreateSpriteG(string imgName, GraphicsImage parent, EmuRectangle rect) { }
	internal static void CreateSpriteAnime(string imgName, int w, int h) { }
	public static Exception LoadContents(bool reload) => null!;
	public static void UnloadContents()
	{
		foreach (var graph in gList.Values) graph.GDispose();
		gList.Clear();
		// mem-leak-fix：_stubs 替身缓存退出游戏后仍驻留静态——随 UnloadContents 一并清（锁内，与 GetSprite 互斥）。
		lock (_stubSync) { _stubRoot = null; _stubs.Clear(); }
	}
	public static void UnloadGraphicList()
	{
		foreach (var graph in gList.Values) graph.GDispose();
		gList.Clear();
		lock (_stubSync) { _stubRoot = null; _stubs.Clear(); }
	}
	public static void UnloadTempLoadedConstImageNames() { }
	public static void UnloadTempLoadedGraphicsImageNames() { }

	/// <summary>
	/// issue 01：无头下启用文件头尺寸探针。ConsoleImagePart 用它区分两条路径——
	/// 非无头（false）保持 sprite 缺失时的原文本回退；无头（true）几何无条件计算，
	/// 探针只服务于缺省宽度的纵横比推算。
	/// </summary>
	public static bool ImageProbeEnabled => true;

	/// <summary>
	/// issue 01：无头下 sprite 表不可用（<see cref="GetSprite"/> 恒 null 的旧前提已由 11a8334 消除——
	/// 替身存在时走 GetSprite 路径），探针仍用于缺省宽度的纵横比推算。
	/// name 解释为游戏根目录下的相对路径（与 Web 资源通道 /assets 一致）。
	/// sprite 名回退（issue）：era 的 <c>&lt;img src='Face_1'&gt;</c> 引用 sprite 名，
	/// 映射在 resources/*.csv（<see cref="ImageNameTable"/>）——直接路径失败后查表。
	/// issue 07：sprite 名尺寸按裁切矩形返回（Face_1 → 180×180），无裁切/越界回退整图探针尺寸。
	/// </summary>
	public static bool TryGetImageSize(string name, out int width, out int height)
	{
		width = 0;
		height = 0;
		var paths = GamePaths.Current;
		if (string.IsNullOrEmpty(name) || paths?.DirAccessor == null)
			return false;
		// 1. 直接相对路径（test_game 的 img/test.png 形态）
		if (ProbeFile(paths, name, out width, out height))
			return true;
		// 2. sprite 名表（era 的 Face_1 → resources/1_Face.png）
		if (ImageNameTable.TryResolveEntry(paths.DirAccessor, paths.ExeDir, name, out var entry)
			&& entry != null
			&& ProbeFile(paths, entry.RelativePath, out var fileW, out var fileH))
		{
			// 裁切越界（OoR）宽容回退整图尺寸——与 GetSprite 替身同策略
			if (entry.Crop is SpriteCrop c && c.X + c.Width <= fileW && c.Y + c.Height <= fileH)
			{
				width = c.Width;
				height = c.Height;
			}
			else
			{
				width = fileW;
				height = fileH;
			}
			return true;
		}
		return false;
	}

	private static bool ProbeFile(GamePaths paths, string relativePath, out int width, out int height)
	{
		var fullPath = paths.DirAccessor!.CombinePath(paths.ExeDir, relativePath);
		return ImageSizeProbe.TryGetPixelSize(paths.DirAccessor, fullPath, out width, out height);
	}
}

/// <summary>
/// 无头轻量 sprite 替身——无位图，仅携带「存在 + 尺寸 + 裁切几何」。
/// 供 <c>SPRITECREATED</c>/<c>SPRITEWIDTH</c>/<c>SPRITEHEIGHT</c> 及
/// <see cref="ConsoleImagePart"/> 的 <c>cImage != null</c> 几何分支使用。
/// issue 07：DestBaseSize 为裁切尺寸（无裁切 = 整图）；SourceSize 为整图尺寸；
/// CropOrigin 为裁切原点 (x,y)——<see cref="ConsoleImagePart"/> 据此产出协议 v9 的
/// 已缩放裁切几何（img 负偏移 + 元素尺寸）。
/// </summary>
internal sealed class HeadlessSprite : ASprite
{
	public HeadlessSprite(string name, EmuSize size) : base(name, size)
	{
		SourceSize = size;
		CropOrigin = new EmuPoint(0, 0);
	}

	public HeadlessSprite(string name, EmuSize size, EmuSize sourceSize, EmuPoint cropOrigin) : base(name, size)
	{
		SourceSize = sourceSize;
		CropOrigin = cropOrigin;
	}

	/// <summary>整图尺寸（图集全图）。无裁切时 = DestBaseSize。</summary>
	public EmuSize SourceSize { get; }

	/// <summary>裁切原点（图集内 x,y）。无裁切时 (0,0)。</summary>
	public EmuPoint CropOrigin { get; }

	/// <summary>是否携带裁切：裁切尺寸 ≠ 整图尺寸（裁切 = 全图时无需容器裁剪）。</summary>
	public bool HasCrop => SourceSize != DestBaseSize;

	public override bool IsCreated => true;
	public override void Dispose() { }
}
#else
using MinorShift.Emuera.Runtime.Config;
using MinorShift.Emuera.Runtime.Utils;
using MinorShift.Emuera.Runtime.Utils.EvilMask;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using trerror = MinorShift.Emuera.Runtime.Utils.EvilMask.Lang.Error;

namespace MinorShift.Emuera.UI.Game.Image;

static class AppContents
{
	static ConcurrentDictionary<string, AbstractImage> resourceDic = new(Config.StrComper);
	static ConcurrentDictionary<string, ASprite> imageDictionary = new(Config.StrComper);
	static ConcurrentDictionary<int, GraphicsImage> gList = [];
	static ConcurrentDictionary<string, ASprite> resourceImageDictionary = new(Config.StrComper);

	public static HashSet<ConstImage> tempLoadedConstImages = [];
	public static HashSet<GraphicsImage> tempLoadedGraphicsImages = [];

	static public GraphicsImage GetGraphics(int i)
	{
		if (gList.TryGetValue(i, out GraphicsImage value))
			return value;
		GraphicsImage g = new(i);
		gList[i] = g;
		return g;
	}

	static public ASprite GetSprite(string name)
	{
		if (name == null) return null!;
		name = name.ToUpper();
		if (!imageDictionary.TryGetValue(name, out ASprite value)) return null!;
		return value;
	}

	/// <summary>
	/// issue 01：非无头分支保持原行为——sprite 缺失时走文本回退，
	/// 不引入探针（避免改变 WinForms 下缺图时的调试信息显示）。
	/// </summary>
	public static bool ImageProbeEnabled => false;

	/// <summary>
	/// issue 01：非无头分支保持原行为——sprite 缺失时走文本回退，
	/// 不引入探针（避免改变 WinForms 下缺图时的调试信息显示）。
	/// </summary>
	static public bool TryGetImageSize(string name, out int width, out int height)
	{
		width = 0;
		height = 0;
		return false;
	}

	static public void SpriteDispose(string name)
	{
		if (name == null) return;
		name = name.ToUpper();
		if (!imageDictionary.TryGetValue(name, out ASprite value)) return;
		value.Dispose();
		imageDictionary.TryRemove(name, out _);
	}

	static public long SpriteDisposeAll(bool delCsvImage)
	{
		int sprites = imageDictionary.Count;
		int csprites = resourceImageDictionary.Count;
		if (delCsvImage)
		{
			imageDictionary.Clear();
			resourceImageDictionary.Clear();
			return sprites;
		}
		else
		{
			imageDictionary = new ConcurrentDictionary<string, ASprite>(resourceImageDictionary);
			return sprites - csprites;
		}
	}

	static public void CreateSpriteG(string imgName, GraphicsImage parent, Rectangle rect)
	{
		if (string.IsNullOrEmpty(imgName)) throw new ArgumentOutOfRangeException();
		imgName = imgName.ToUpper();
		SpriteG newCImg = new(imgName, parent, rect);
		imageDictionary[imgName] = newCImg;
	}

	internal static void CreateSpriteAnime(string imgName, int w, int h)
	{
		if (string.IsNullOrEmpty(imgName)) throw new ArgumentOutOfRangeException();
		imgName = imgName.ToUpper();
		SpriteAnime newCImg = new(imgName, new Size(w, h));
		imageDictionary[imgName] = newCImg;
	}

	static public Exception LoadContents(bool reload)
	{
		if (!Directory.Exists(Program.ContentDir)) return null!;
		try
		{
			var csvFiles = Directory.EnumerateFiles(Program.ContentDir, "*.csv", SearchOption.AllDirectories);
			foreach (var filepath in csvFiles)
			{
				if (reload)
				{
					foreach (string key in resourceImageDictionary.Keys)
						imageDictionary.TryRemove(key, out _);
					resourceImageDictionary.Clear();
					foreach (var img in resourceDic.Values) img.Dispose();
					resourceDic.Clear();
				}
			}
			csvFiles.AsParallel()
				.Where(path => Path.GetExtension(path).Equals(".csv", StringComparison.OrdinalIgnoreCase))
				.ForAll(path =>
				{
					SpriteAnime currentAnime = null;
					string directory = Path.GetDirectoryName(path) + "\\";
					string filename = Path.GetFileName(path);
					string[] lines = File.ReadAllLines(path, EncodingHandler.DetectEncoding(path));
					int lineNo = 0;
					foreach (var line in lines)
					{
						lineNo++;
						if (line.Length == 0) continue;
						string str = line.Trim();
						if (str.Length == 0 || str.StartsWith(';')) continue;
						string[] tokens = str.Split(',');
						ScriptPosition? sp = new(filename, lineNo);
						if (CreateFromCsv(tokens, directory, currentAnime, sp) is ASprite item)
						{
							currentAnime = item as SpriteAnime;
							if (reload && resourceImageDictionary.ContainsKey(item.Name))
								resourceImageDictionary.Remove(item.Name, out _);
							if (!resourceImageDictionary.TryAdd(item.Name, item))
							{
								ParserMediator.Warn(string.Format(trerror.SpriteNameAlreadyUsed.Text, item.Name), sp, 0);
								item.Dispose();
							}
						}
					}
				});
		}
		catch (Exception e) { return e; }
		imageDictionary = new ConcurrentDictionary<string, ASprite>(resourceImageDictionary);
		return null!;
	}

	static public void UnloadContents()
	{
		foreach (var img in resourceDic.Values) img.Dispose();
		resourceDic.Clear();
		imageDictionary.Clear();
		resourceImageDictionary.Clear();
		foreach (var graph in gList.Values) graph.GDispose();
		gList.Clear();
	}

	static public void UnloadGraphicList()
	{
		foreach (var graph in gList.Values) graph.GDispose();
		gList.Clear();
	}

	static public void UnloadTempLoadedConstImageNames()
	{
		lock (tempLoadedConstImages)
		{
			foreach (ConstImage img in tempLoadedConstImages) img.Dispose();
			tempLoadedConstImages.Clear();
		}
	}

	static public void UnloadTempLoadedGraphicsImageNames()
	{
		lock (tempLoadedGraphicsImages)
		{
			foreach (GraphicsImage img in tempLoadedGraphicsImages)
				if (img.useImgList) img.UnLoad();
			tempLoadedGraphicsImages.Clear();
		}
	}

	static private AContentItem CreateFromCsv(string[] tokens, string dir, SpriteAnime currentAnime, ScriptPosition? sp)
	{
		if (tokens.Length < 2) return null!;
		string name = tokens[0].Trim().ToUpper();
		string arg2 = tokens[1];
		if (name.Length == 0 || arg2.Length == 0) return null!;
		if (arg2.Equals("ANIME", StringComparison.OrdinalIgnoreCase))
		{
			if (tokens.Length < 4) { ParserMediator.Warn(trerror.NotDeclaredAnimationSpriteSize.Text, sp, 1); return null!; }
			int[] sizeValue = new int[2]; bool sccs = true;
			for (int i = 0; i < 2; i++) sccs &= int.TryParse(tokens[i + 2], out sizeValue[i]);
			if (!sccs || sizeValue[0] <= 0 || sizeValue[1] <= 0 || sizeValue[0] > AbstractImage.MAX_IMAGESIZE || sizeValue[1] > AbstractImage.MAX_IMAGESIZE)
			{ ParserMediator.Warn(trerror.InvalidAnimationSpriteSize.Text, sp, 1); return null!; }
			SpriteAnime anime = new(name, new Size(sizeValue[0], sizeValue[1]));
			return anime;
		}

		if (arg2.IndexOf('.', StringComparison.Ordinal) < 0)
		{ ParserMediator.Warn(string.Format(trerror.MissingSecondArgumentExtension.Text, arg2), sp, 1); return null!; }
		string parentName = dir + arg2;

		if (!resourceDic.TryGetValue(parentName, out AbstractImage value))
		{
			string filepath = parentName;
			Bitmap bmp;
			var webpbmp = Utils.LoadImage(filepath);
			if (webpbmp == null)
			{ ParserMediator.Warn(string.Format(trerror.FailedLoadFile.Text, arg2), sp, 1); return null!; }
			bmp = webpbmp;
			if (bmp.Width > AbstractImage.MAX_IMAGESIZE || bmp.Height > AbstractImage.MAX_IMAGESIZE)
				ParserMediator.Warn(string.Format(trerror.TooLargeImageFile.Text, AbstractImage.MAX_IMAGESIZE.ToString(), arg2), sp, 1);
			ConstImage img = new(parentName);
			img.CreateFrom(bmp, filepath, Config.TextDrawingMode == TextDrawingMode.WINAPI);
			if (!img.IsCreated)
			{ ParserMediator.Warn(string.Format(trerror.FailedCreateResource.Text, arg2), sp, 1); return null!; }
			value = img;
			resourceDic.TryAdd(parentName, value);
			img.Dispose();
		}
		if (value is not ConstImage parentImage || !parentImage.IsCreated)
		{ ParserMediator.Warn(string.Format(trerror.SpriteCreateFromFailedResource.Text, arg2), sp, 1); return null!; }
		Rectangle rect = new(0, 0, parentImage.Width, parentImage.Height);
		Size size = rect.Size;
		Point pos = new();
		int delay = 1000;
		if (tokens.Length >= 6)
		{
			int[] outValue = new int[4]; bool sccs = true;
			for (int i = 0; i < 4; i++) sccs &= int.TryParse(tokens[i + 2], out outValue[i]);
			if (sccs)
			{
				rect = new Rectangle(outValue[0], outValue[1], outValue[2], outValue[3]);
				size = rect.Size;
				if (rect.Width <= 0 || rect.Height <= 0) { ParserMediator.Warn(string.Format(trerror.SpriteSizeIsNegatibe.Text, name), sp, 1); return null!; }
				if (!rect.IntersectsWith(new Rectangle(0, 0, parentImage.Width, parentImage.Height)))
				{ ParserMediator.Warn(string.Format(trerror.OoRParentImage.Text, name), sp, 1); return null!; }
			}
			if (tokens.Length >= 8) { sccs = true; for (int i = 0; i < 2; i++) sccs &= int.TryParse(tokens[i + 6], out outValue[i]); if (sccs) pos = new Point(outValue[0], outValue[1]); }
			if (tokens.Length >= 9) { sccs = int.TryParse(tokens[8], out delay); if (sccs && delay <= 0) { ParserMediator.Warn(string.Format(trerror.FrameTimeIsNegative.Text, name), sp, 1); return null!; } }
			if (tokens.Length >= 11) { sccs = true; for (int i = 0; i < 2; i++) sccs &= int.TryParse(tokens[i + 9], out outValue[i]); if (sccs) size = new Size(outValue[0], outValue[1]); }
		}
		if (currentAnime != null && currentAnime.Name == name)
		{
			if (!currentAnime.AddFrame(parentImage, rect, pos, delay))
			{ ParserMediator.Warn(string.Format(trerror.FailedAddSpriteFrame.Text, arg2), sp, 1); return null!; }
			return null!;
		}
		ASprite image = new SpriteF(name, parentImage, rect, pos, size);
		return image;
	}
}
#endif
