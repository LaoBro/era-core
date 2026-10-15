#if HEADLESS
using System;

namespace MinorShift.Emuera.UI.Game.Image;

internal sealed class ConstImage : AbstractImage
{
	public ConstImage(string name) { Name = name; }
	public readonly string Name;
	public int ImageWidth;
	public int ImageHeight;
	public bool RealIsCreated;

	internal void CreateFrom(object bmp, string filepath, bool useGDI)
	{
		RealIsCreated = true;
	}

	public static void Load() { }

	public override int Width => ImageWidth;
	public override int Height => ImageHeight;
	public override bool IsCreated => RealIsCreated;
	public override void Dispose() { }
}
#else
using System;
using System.IO;

namespace MinorShift.Emuera.UI.Game.Image;

internal sealed class ConstImage : AbstractImage
{
	public ConstImage(string name)
	{ Name = name; RealIsCreated = false; }

	public readonly string Name;
	public Bitmap RealBitmap;
	public string Filepath;
	public int Width;
	public int Height;
	public bool RealIsCreated;

	internal void CreateFrom(Bitmap bmp, string filepath, bool useGDI)
	{
		if (RealBitmap != null || !string.IsNullOrEmpty(Filepath))
			throw new Exception();

		try
		{
			RealBitmap = bmp;
			Filepath = filepath;
			Width = RealBitmap.Width;
			Height = RealBitmap.Height;
			lock (AppContents.tempLoadedConstImages)
				AppContents.tempLoadedConstImages.Add(this);
			RealIsCreated = true;
			return;
		}
		catch (Exception e)
		{
			// 原先直写相对路径 "img_err.log"——落在进程的当前工作目录（可能只读、跨运行互相覆盖），
			// 既不属游戏树也不属私有域。F-4：诊断落点统一进 AppDataPaths 豁免域的唯一口子。
			try { AppDataIo.WriteAllText(AppDataIo.Resolve("img_err.log"), e.ToString()); }
			catch { /* 日志自身失败不再递归 */ }
		}
		return;
	}

	public void Load()
	{
		if (RealBitmap != null || !RealIsCreated)
			return;
		try
		{
			RealBitmap = ImgUtils.LoadImage(Filepath);
			if (RealBitmap == null)
				return;
			lock (AppContents.tempLoadedConstImages)
				AppContents.tempLoadedConstImages.Add(this);
		}
		catch
		{
			return;
		}
	}

	public override void Dispose()
	{
		if (RealBitmap == null || !RealIsCreated)
			return;
		if (g != null)
		{
			g.Dispose();
			g = null;
		}
		if (RealBitmap != null)
		{
			RealBitmap.Dispose();
			RealBitmap = null;
		}
	}

	~ConstImage() { Dispose(); }

	public override bool IsCreated => RealIsCreated;

	public override Bitmap Bitmap
	{
		set { RealBitmap = value; }
		get { Load(); return RealBitmap; }
	}
}
#endif
