using MinorShift.Emuera.Primitives;
using MinorShift.Emuera.Runtime.Config;
using MinorShift.Emuera.UI.Game.Image;
using System;
using System.Text;
using static MinorShift.Emuera.Runtime.Utils.EvilMask.Utils;

namespace MinorShift.Emuera.UI.Game;

sealed class ConsoleImagePart : AConsoleDisplayNode
{
	#region EM_私家版_HTMLパラメータ拡張
	//public ConsoleImagePart(string resName, string resNameb, int raw_height, int raw_width, int raw_ypos)
	public ConsoleImagePart(string resName, string? resNameb, string? resNamem, MixedNum? raw_height, MixedNum? raw_width, MixedNum? raw_ypos)
	{
		top = 0;
		bottom = Config.FontSize;
		Text = "";
		ResourceName = resName ?? "";
		ButtonResourceName = resNameb!;
		MappingGraphName = resNamem!;
		StringBuilder sb = new();
		sb.Append("<img src='").Append(ResourceName).Append('\'');
		if (ButtonResourceName != null)
			AddTagArg(sb, "srcb", ButtonResourceName);
		//{
		//	sb.Append("' srcb='");
		//	sb.Append(ButtonResourceName);
		//}
		if (!string.IsNullOrEmpty(MappingGraphName))
			AddTagArg(sb, "srcm", MappingGraphName);
		AddTagMixedNumArg(sb, "height", raw_height);
		AddTagMixedNumArg(sb, "width", raw_width);
		AddTagMixedNumArg(sb, "ypos", raw_ypos);
		//{
		//	sb.Append("' srcm='");
		//	sb.Append(MappingGraphName);
		//}
		//if(raw_height != 0)
		//	if (raw_height != null && raw_height.num != 0)
		//	{
		//	sb.Append("' height='");
		//	sb.Append(raw_height.num.ToString());
		//	if (raw_height.isPx) sb.Append("px");
		//}
		//if(raw_width != 0)
		//if (raw_width != null && raw_width.num != 0)
		//	{
		//	sb.Append("' width='");
		//	sb.Append(raw_width.num.ToString());
		//	if (raw_width.isPx) sb.Append("px");
		//}
		////if(raw_ypos != 0)
		//if(raw_ypos != null && raw_ypos.num != 0)
		//	{
		//	sb.Append("' ypos='");
		//	sb.Append(raw_ypos.num.ToString());
		//	if (raw_ypos.isPx) sb.Append("px");
		//}
		// sb.Append("'>");
		sb.Append('>');
		AltText = sb.ToString();
		cImage = AppContents.GetSprite(ResourceName);
		// issue 01：几何计算与 sprite 解耦。
		// 非无头（ImageProbeEnabled=false）保持原行为：sprite 缺失 → 文本回退，几何不产出。
		// 无头下几何无条件计算：来源尺寸 = sprite → 探针 → 无（缺省宽度回退 0，参数始终生效）；
		// Text 保留 AltText 供 CLI BuildString 显示调试标记（原 HEADLESS 行为）。
		if (cImage == null && !AppContents.ImageProbeEnabled)
		{
			Text = AltText;
			return;
		}
		int sourceWidth = 0;
		int sourceHeight = 0;
		if (cImage != null)
		{
			sourceWidth = cImage.DestBaseSize.Width;
			sourceHeight = cImage.DestBaseSize.Height;
		}
		else if (AppContents.TryGetImageSize(ResourceName, out sourceWidth, out sourceHeight))
		{
			// HEADLESS：sprite 表不可用，探针读取原图像素尺寸
		}
		if (cImage == null)
			Text = AltText;
		int height;
		//if (raw_height == 0)//HTMLで高さが指定されていない又は0が指定された場合、フォントサイズをそのまま高さ(px単位)として使用する。
		if (raw_height == null || raw_height.num == 0)//HTMLで高さが指定されていない又は0が指定された場合、フォントサイズをそのまま高さ(px単位)として使用する。
			height = Config.FontSize;
		// else//HTMLで高さが指定された場合、フォントサイズの100分率と解釈する。
		//	height = Config.Config.FontSize * raw_height / 100;
		else if (raw_height.isPx)//HTMLで高さがpx指定された場合、そのまま使う。
			height = raw_height.num;
		else // フォントサイズの100分率と解釈する。
			height = Config.FontSize * raw_height.num / 100;
		//幅が指定されていない又は0が指定された場合、元画像の縦横比を維持するように幅(px単位)を設定する。1未満は端数としてXsubpixelに記録。
		//負の値が指定される可能性があるが、最終的なWidthは正の値になるようにあとで調整する。
		//if (raw_width == 0)
		if (raw_width == null || raw_width.num == 0)
		{
			// 缺省宽度：有来源尺寸按纵横比推算；无来源（探针失败）回退 0（ticket 01 验收 2）
			if (sourceHeight > 0)
			{
				Width = sourceWidth * height / sourceHeight;
				XsubPixel = (float)sourceWidth * height / sourceHeight - Width;
			}
		}
		else if (raw_width.isPx)
		{
			Width = raw_width.num;
		}
		else
		{
			// Width = Config.Config.FontSize * raw_width / 100;
			// XsubPixel = ((float)Config.Config.FontSize * raw_width / 100f) - Width;
			Width = Config.FontSize * raw_width.num / 100;
			XsubPixel = (float)Config.FontSize * raw_width.num / 100f - Width;
		}
		//top = raw_ypos * Config.Config.FontSize / 100;
		top = raw_ypos != null ? raw_ypos.isPx ? raw_ypos.num : raw_ypos.num * Config.FontSize / 100 : 0;
		destRect = new EmuRectangle(0, top, Width, height);
		if (destRect.Width < 0)
		{
			destRect = destRect with { X = -destRect.Width };
			Width = -destRect.Width;
		}
		if (destRect.Height < 0)
		{
			destRect = destRect with { Y = destRect.Y - destRect.Height };
			height = -destRect.Height;
		}
		_height = height;
		bottom = top + height;
		// issue 07（协议 v9）：裁切矩形几何——HeadlessSprite 携带 SourceSize（整图）/CropOrigin（原点）。
		// 按显示尺寸缩放：img 负偏移（margin-left/top，≤0）+ img 元素渲染尺寸（整图×缩放系数）——
		// 前端只做 overflow:hidden 容器 + 负 margin，零布局数学（协议携带已解析 px 几何原则）。
		// 缩放系数 = 显示尺寸 / 裁切尺寸（WinForms SpriteF.GraphicsDraw 把裁切源区域缩放绘制到 destRect 的语义）。
		if (cImage is HeadlessSprite hs && hs.HasCrop)
		{
			var cropW = hs.DestBaseSize.Width;
			var cropH = hs.DestBaseSize.Height;
			if (cropW > 0 && cropH > 0 && Width > 0 && height > 0)
			{
				var scaleX = (double)Width / cropW;
				var scaleY = (double)height / cropH;
				HasCrop = true;
				CropX = -(int)Math.Round(hs.CropOrigin.X * scaleX);
				CropY = -(int)Math.Round(hs.CropOrigin.Y * scaleY);
				CropImgWidth = (int)Math.Round(hs.SourceSize.Width * scaleX);
				CropImgHeight = (int)Math.Round(hs.SourceSize.Height * scaleY);
			}
		}
		//if(top > 0)
		//	top = 0;
		//if(bottom < Config.Config.FontSize)
		//	bottom = Config.Config.FontSize;
		if (ButtonResourceName != null)
		{
			cImageB = AppContents.GetSprite(ButtonResourceName);
			//if (cImageB != null && !cImageB.IsCreated)
			//	cImageB = null;
		}
		if (MappingGraphName != null)
		{
			cImageM = AppContents.GetSprite(MappingGraphName);
		}
	}
	public readonly string MappingGraphName = null!;
	private readonly ASprite cImageM = null!;
	#endregion
	private readonly ASprite cImage = null!;
	private readonly ASprite cImageB = null!;
	private readonly int top;
	private readonly int bottom;
	private readonly EmuRectangle destRect;
	/// <summary>issue 01：修正后的 px 高度（负高输入时取正值）。探针/文本回退失败时为 0。</summary>
	private readonly int _height = 0;
	//#pragma warning disable CS0649 // フィールド 'ConsoleImagePart.ia' は割り当てられません。常に既定値 null を使用します。
	//		private readonly ImageAttributes ia;
	//#pragma warning restore CS0649 // フィールド 'ConsoleImagePart.ia' は割り当てられません。常に既定値 null を使用します。
	public readonly string ResourceName;
	public readonly string ButtonResourceName = null!;
	public override int Top { get { return top; } }
	public override int Bottom { get { return bottom; } }
	/// <summary>issue 01：解析后的 px 高度（修正后正值），协议序列化用。文本回退时为 0。</summary>
	public int Height { get { return _height; } }
	/// <summary>issue 01：ypos（可为负/超行高），协议序列化用。</summary>
	public int YPos { get { return top; } }

	// ---------- issue 07（协议 v9）：裁切矩形几何（已缩放，协议序列化用） ----------

	/// <summary>是否携带裁切（图集 sprite 且显示尺寸 > 0）。</summary>
	public bool HasCrop { get; private set; }
	/// <summary>img 元素 margin-left（≤0，已按显示尺寸缩放）。</summary>
	public int CropX { get; private set; }
	/// <summary>img 元素 margin-top（≤0，已按显示尺寸缩放）。</summary>
	public int CropY { get; private set; }
	/// <summary>img 元素渲染宽度（整图×缩放系数）。</summary>
	public int CropImgWidth { get; private set; }
	/// <summary>img 元素渲染高度（整图×缩放系数）。</summary>
	public int CropImgHeight { get; private set; }

	public override bool CanDivide { get { return false; } }
	public override void SetWidth(StringMeasure sm, float subPixel)
	{
		if (Error)
		{
			Width = 0;
			return;
		}
		if (cImage != null)
			return;
		// issue 01：探针已定宽（Width 在构造函数期设置）——与 sprite 路径同语义
		if (_height > 0)
			return;
		Width = sm.GetDisplayLength(Text, Config.DefaultFont);
		XsubPixel = subPixel;
	}

	public override string ToString()
	{
		if (AltText == null)
			return "";
		return AltText;
	}
	#region EM_私家版_描画拡張
	public override StringBuilder BuildString(StringBuilder sb)
	{
		if (AltText != null) sb.Append(AltText);
		return sb;
	}
	#endregion
	#region EM_私家版_imgマースク
	public long GetMappingColor(int pointX, int pointY)
	{
		if (cImageM != null && cImageM.IsCreated)
		{
			EmuSize spriteSize;
			if (cImageM is SpriteF sf)
			{
				spriteSize = sf.DestBaseSize;
			}
			else if (cImageM is SpriteG sg)
			{
				spriteSize = sg.DestBaseSize;
			}
			else return 0;
			pointX = pointX * spriteSize.Width / destRect.Width;
			pointY = pointY * spriteSize.Height / destRect.Height;
			var c = cImageM.SpriteGetColor(pointX, pointY);
			return c.ToArgb() & 0xFFFFFF;
		}
		return 0;
	}
	#endregion
	public override void DrawTo(IImageContext graph, int pointY, bool isSelecting, bool isFocus, bool isBackLog, TextDrawingMode mode, bool isButton = false)
	{
		if (Error)
			return;
		ASprite img = cImage;
		if ((isSelecting || isFocus) && cImageB != null)
			img = cImageB;

		if (img != null && img.IsCreated)
		{
			// Headless mode: no actual rendering
			// In WinForms mode, this would call img.GraphicsDraw(graph, rect)
			// For now, we just skip rendering in headless mode
		}
		else
		{
			if (mode == TextDrawingMode.GRAPHICS)
				graph.DrawString(AltText, Config.DefaultFont, Config.ForeColor, new EmuPoint(PointX, pointY));
#if !HEADLESS
			// WinForms rendering path - no-op in headless mode
#endif
		}
	}
}
