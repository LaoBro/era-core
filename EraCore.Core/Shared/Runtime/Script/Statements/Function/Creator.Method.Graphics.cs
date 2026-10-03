using MinorShift.Emuera.GameData.Variable;
using MinorShift.Emuera.GameView;

using MinorShift.Emuera.Runtime.Config;
using MinorShift.Emuera.Runtime.Script.Data;
using MinorShift.Emuera.Runtime.Script.Parser;
using MinorShift.Emuera.Runtime.Script.Statements;
using MinorShift.Emuera.Runtime.Script.Statements.Expression;
using MinorShift.Emuera.Runtime.Script.Statements.Function;
using MinorShift.Emuera.Runtime.Script.Statements.Variable;
using MinorShift.Emuera.Runtime.Utils;
using MinorShift.Emuera.Runtime.Utils.EvilMask;
using MinorShift.Emuera.Primitives;
using MinorShift.Emuera.UI.Game;
using MinorShift.Emuera.UI.Game.Image;
using System;
using System.Collections.Generic;
using System.Linq;
using trerror = MinorShift.Emuera.Runtime.Utils.EvilMask.Lang.Error;

namespace MinorShift.Emuera.GameData.Function;

internal static partial class FunctionMethodCreator
{
	private static GraphicsImage ReadGraphics(string Name, ExpressionMediator exm, List<AExpression> arguments, int argNo)
	{
		long target = arguments[argNo].GetIntValue(exm);
		if (target < 0)//funcname + "関数:GraphicsIDに負の値(" + target.ToString() + ")が指定されました"
			throw new CodeEE(string.Format(trerror.GIdIsNegative.Text, Name, target));
		else if (target > int.MaxValue)//funcname + "関数:GraphicsIDの値(" + target.ToString() + ")が大きすぎます"
			throw new CodeEE(string.Format(trerror.GIdIsTooLarge.Text, Name, target));
		return AppContents.GetGraphics((int)target);
	}

	public static GraphicsImage ReadGraphics(int target)
	{
		if (target < 0)//funcname + "関数:GraphicsIDに負の値(" + target.ToString() + ")が指定されました"
			throw new CodeEE(string.Format(trerror.GIdIsNegative.Text, "HTML_PRINT", target));
		else if (target > int.MaxValue)//funcname + "関数:GraphicsIDの値(" + target.ToString() + ")が大きすぎます"
			throw new CodeEE(string.Format(trerror.GIdIsTooLarge.Text, "HTML_PRINT", target));
		return AppContents.GetGraphics(target);
	}

	private static void RequireGDIPlus(string name)
	{
		if (Config.TextDrawingMode == TextDrawingMode.WINAPI)
			throw new CodeEE(string.Format(trerror.GDIPlusOnly.Text, name));
	}

	private static GraphicsImage? TryGetCreatedGraphics(string name, ExpressionMediator exm, List<AExpression> arguments, int argNo)
	{
		RequireGDIPlus(name);
		var g = ReadGraphics(name, exm, arguments, argNo);
		return g.IsCreated ? g : null;
	}

	private static GraphicsImage? TryGetUncreatedGraphics(string name, ExpressionMediator exm, List<AExpression> arguments, int argNo)
	{
		RequireGDIPlus(name);
		var g = ReadGraphics(name, exm, arguments, argNo);
		return g.IsCreated ? null : g;
	}

	private static EmuColor ReadColor(string Name, ExpressionMediator exm, List<AExpression> arguments, int argNo)
	{
		long c64 = arguments[argNo].GetIntValue(exm);
		if (c64 < 0 || c64 > 0xFFFFFFFF)
			throw new CodeEE(string.Format(trerror.InvalidColorARGB.Text, Name, c64));
		return EmuColor.FromArgb((int)(c64 >> 24) & 0xFF, (int)(c64 >> 16) & 0xFF, (int)(c64 >> 8) & 0xFF, (int)c64 & 0xFF);
	}

	private static EmuPoint ReadPoint(string Name, ExpressionMediator exm, List<AExpression> arguments, int argNo)
	{
		long x64 = arguments[argNo].GetIntValue(exm);
		if (x64 < int.MinValue || x64 > int.MaxValue)
			throw new CodeEE(string.Format(trerror.ArgIsOutOfRange.Text, Name, argNo + 1, x64, int.MinValue, int.MaxValue));
		long y64 = arguments[argNo + 1].GetIntValue(exm);
		if (y64 < int.MinValue || y64 > int.MaxValue)
			throw new CodeEE(string.Format(trerror.ArgIsOutOfRange.Text, Name, argNo + 2, y64, int.MinValue, int.MaxValue));
		return new EmuPoint((int)x64, (int)y64);
	}

	private static EmuRectangle ReadRectangle(string Name, ExpressionMediator exm, List<AExpression> arguments, int argNo)
	{
		long x64 = arguments[argNo].GetIntValue(exm);
		if (x64 < int.MinValue || x64 > int.MaxValue)
			throw new CodeEE(string.Format(trerror.ArgIsOutOfRange.Text, Name, argNo + 1, x64, int.MinValue, int.MaxValue));
		long y64 = arguments[argNo + 1].GetIntValue(exm);
		if (y64 < int.MinValue || y64 > int.MaxValue)
			throw new CodeEE(string.Format(trerror.ArgIsOutOfRange.Text, Name, argNo + 2, y64, int.MinValue, int.MaxValue));

		long w64 = arguments[argNo + 2].GetIntValue(exm);
		if (w64 < int.MinValue || w64 > int.MaxValue || w64 == 0)
			throw new CodeEE(string.Format(trerror.ArgIsOutOfRangeExcept.Text, Name, argNo + 3, w64, int.MinValue, int.MaxValue, 0));
		long h64 = arguments[argNo + 3].GetIntValue(exm);
		if (h64 < int.MinValue || h64 > int.MaxValue || h64 == 0)
			throw new CodeEE(string.Format(trerror.ArgIsOutOfRangeExcept.Text, Name, argNo + 4, h64, int.MinValue, int.MaxValue, 0));
		return new EmuRectangle((int)x64, (int)y64, (int)w64, (int)h64);
	}

	// 读取逻辑提取到 ColorMatrixReader 深模块（实现票 22），Graphics 与 SETIMAGELAYER 族共用，
	// 数组维度判定 / 5×5 边界校验 / 错误消息单点维护。
	private static float[][] ReadColormatrix(string Name, ExpressionMediator exm, List<AExpression> arguments, int argNo)
		=> ColorMatrixReader.Read(Name, argNo + 1, arguments[argNo], exm);

	public sealed class GraphicsStateMethod : FunctionMethod
	{
		public GraphicsStateMethod()
		{
			ReturnType = typeof(long);
			argumentTypeArrayEx = [new ArgTypeList{ ArgTypes = { ArgType.Int | ArgType.DisallowVoid } }];
			CanRestructure = false;
		}
		public override long GetIntValue(ExpressionMediator exm, List<AExpression> arguments)
		{
			GraphicsImage? g = TryGetCreatedGraphics(Name, exm, arguments, 0);
			if (g == null)
				return 0;
			switch (Name)
			{
				case "GCREATED":
					return 1;
				case "GWIDTH":
					return g.Width;
				case "GHEIGHT":
					return g.Height;
				#region EE_GDRAWTEXTに付随する要素
				case "GGETFONTSIZE":
					return g.Fontsize;
				case "GGETFONTSTYLE":
					return g.Fontstyle;
				case "GGETPEN":
#if HEADLESS
				return 0;
#else
				return g.Pen.Color.ToArgb() & 0xffffffffL;
#endif
			case "GGETPENWIDTH":
#if HEADLESS
				return 0;
#else
				return (long)g.Pen.Width;
#endif
			case "GGETBRUSH":
#if HEADLESS
				return 0;
#else
				SolidBrush b = (SolidBrush)g.Brush;
				return b.Color.ToArgb() & 0xffffffffL;
#endif
					#endregion
			}
			throw new ExeEE("GraphicsState:" + Name + ":異常な分岐");
		}
	}

	public sealed class GraphicsStateStrMethod : FunctionMethod
	{
		public GraphicsStateStrMethod()
		{
			ReturnType = typeof(string);
			argumentTypeArrayEx = [new ArgTypeList{ ArgTypes = { ArgType.Int | ArgType.DisallowVoid } }];
			CanRestructure = false;
		}
		public override string GetStrValue(ExpressionMediator exm, List<AExpression> arguments)
		{
			GraphicsImage? g = TryGetCreatedGraphics(Name, exm, arguments, 0);
			if (g == null)
				return "";
			switch (Name)
			{
				case "GGETFONT":
					return g.Fontname;
			}
			throw new ExeEE("GraphicsState:" + Name + ":Abnormal branching");
		}
	}

	public sealed class GraphicsGetColorMethod : FunctionMethod
	{
		public GraphicsGetColorMethod()
		{
			ReturnType = typeof(long);
			argumentTypeArrayEx = [new ArgTypeList{ ArgTypes = { ArgType.Int | ArgType.DisallowVoid, ArgType.Int | ArgType.DisallowVoid, ArgType.Int | ArgType.DisallowVoid } }];
			CanRestructure = false;
		}
		public override long GetIntValue(ExpressionMediator exm, List<AExpression> arguments)
		{
			GraphicsImage? g = TryGetCreatedGraphics(Name, exm, arguments, 0);
			//失敗したら負の値を返す。他と戻り値違うけど仕方ないね
			if (g == null)
				return -1;
			EmuPoint p = ReadPoint(Name, exm, arguments, 1);
			if (p.X < 0 || p.X >= g.Width || p.Y < 0 || p.Y >= g.Height)
				return -1;
			EmuColor c = GraphicsImage.GGetColor(p.X, p.Y);
			//Color.ToArgb()はInt32の負の値をとることがあり、Int64にうまく変換できない?（と思ったが気のせいだった
			return c.ToArgb() & 0xFFFFFFFFL;
		}
	}

	public sealed class GraphicsSetColorMethod : FunctionMethod
	{
		public GraphicsSetColorMethod()
		{
			ReturnType = typeof(long);
			argumentTypeArrayEx = [new ArgTypeList{ ArgTypes = { ArgType.Int | ArgType.DisallowVoid, ArgType.Int | ArgType.DisallowVoid, ArgType.Int | ArgType.DisallowVoid, ArgType.Int | ArgType.DisallowVoid } }];
			CanRestructure = false;
		}
		public override long GetIntValue(ExpressionMediator exm, List<AExpression> arguments)
		{
			GraphicsImage? g = TryGetCreatedGraphics(Name, exm, arguments, 0);
			if (g == null)
				return 0;
			EmuColor c = ReadColor(Name, exm, arguments, 1);
			EmuPoint p = ReadPoint(Name, exm, arguments, 2);
			if (p.X < 0 || p.X >= g.Width || p.Y < 0 || p.Y >= g.Height)
				return 0;
			GraphicsImage.GSetColor(c, p.X, p.Y);
			return 1;
		}
	}

	public sealed class GraphicsSetBrushMethod : FunctionMethod
	{
		public GraphicsSetBrushMethod()
		{
			ReturnType = typeof(long);
			argumentTypeArrayEx = [new ArgTypeList{ ArgTypes = { ArgType.Int | ArgType.DisallowVoid, ArgType.Int | ArgType.DisallowVoid } }];
			CanRestructure = false;
		}
		public override long GetIntValue(ExpressionMediator exm, List<AExpression> arguments)
		{
			GraphicsImage? g = TryGetCreatedGraphics(Name, exm, arguments, 0);
			if (g == null)
				return 0;
			EmuColor c = ReadColor(Name, exm, arguments, 1);
#if HEADLESS
			GraphicsImage.GSetBrush(null!);
#else
			g.GSetBrush(new SolidBrush(c));
#endif
			return 1;
		}
	}

	public sealed class GraphicsSetFontMethod : FunctionMethod
	{
		public GraphicsSetFontMethod()
		{
			ReturnType = typeof(long);
			argumentTypeArrayEx = [
					new ArgTypeList{ ArgTypes = { ArgType.Int, ArgType.String, ArgType.Int, ArgType.Int }, OmitStart = 2 }
				];
			CanRestructure = false;
		}
		public override long GetIntValue(ExpressionMediator exm, List<AExpression> arguments)
		{
			GraphicsImage? g = TryGetCreatedGraphics(Name, exm, arguments, 0);
			if (g == null)
				return 0;
#if HEADLESS
			// Headless 模式下不支持字体设置
			return 0;
#else
			string fontname = arguments[1].GetStrValue(exm);
			long fontsize = arguments[2].GetIntValue(exm);
			FontStyle fs = FontStyle.Regular;
			if (arguments.Count > 3)
			{
				long style = arguments[3].GetIntValue(exm);

				if ((style & 1) != 0)
					fs |= FontStyle.Bold;
				if ((style & 2) != 0)
					fs |= FontStyle.Italic;
				if ((style & 4) != 0)
					fs |= FontStyle.Strikeout;
				if ((style & 8) != 0)
					fs |= FontStyle.Underline;
			}

			Font styledFont;
			try
			{
				#region EE_フォントファイル対応
				foreach (FontFamily ff in GlobalStatic.Pfc.Families)
				{
					if (ff.Name == fontname)
					{
						styledFont = new Font(ff, fontsize, fs, GraphicsUnit.Pixel);
						goto foundfont;
					}
				}
				styledFont = new Font(fontname, fontsize, fs, GraphicsUnit.Pixel);
			}
			catch (Exception e)
			{
				if (e is CodeEE)
					throw;
				EmueraLog.Warn(Name, e.Message);
				return 0;
			}
		foundfont:
			#endregion
			g.GSetFont(styledFont, fs);
			return 1;
#endif
		}
	}

	public sealed class GraphicsSetPenMethod : FunctionMethod
	{
		public GraphicsSetPenMethod()
		{
			ReturnType = typeof(long);
			// 私家版のバグだと思う
			argumentTypeArrayEx = [new ArgTypeList{ ArgTypes = { ArgType.Int | ArgType.DisallowVoid, ArgType.Int | ArgType.DisallowVoid, ArgType.Int | ArgType.DisallowVoid } }];
			CanRestructure = false;
		}
		public override long GetIntValue(ExpressionMediator exm, List<AExpression> arguments)
		{
			GraphicsImage? g = TryGetCreatedGraphics(Name, exm, arguments, 0);
			if (g == null)
				return 0;
			EmuColor c = ReadColor(Name, exm, arguments, 1);
			long width = arguments[2].GetIntValue(exm);
#if HEADLESS
			GraphicsImage.GSetPen(null!);
#else
			g.GSetPen(new Pen(c, width));
#endif
			return 1;
		}
	}

	public sealed class GraphicsSetDashStyleMethod : FunctionMethod
	{
		public GraphicsSetDashStyleMethod()
		{
			ReturnType = typeof(long);
			argumentTypeArrayEx = [new ArgTypeList{ ArgTypes = { ArgType.Int | ArgType.DisallowVoid, ArgType.Int | ArgType.DisallowVoid, ArgType.Int | ArgType.DisallowVoid } }];
			CanRestructure = false;
		}
		public override long GetIntValue(ExpressionMediator exm, List<AExpression> arguments)
		{
			GraphicsImage? g = TryGetCreatedGraphics(Name, exm, arguments, 0);
			if (g == null)
				return 0;

			GraphicsImage.GDashStyle(arguments[1].GetIntValue(exm), arguments[2].GetIntValue(exm));
			return 1;
		}
	}

	public sealed class GraphicsDrawStringMethod : FunctionMethod
	{
		public GraphicsDrawStringMethod()
		{
			ReturnType = typeof(long);
			argumentTypeArrayEx = [
					new ArgTypeList{ ArgTypes = { ArgType.Int, ArgType.String, ArgType.Int, ArgType.Int }, OmitStart = 2 }
				];
			CanRestructure = false;
		}


		public override long GetIntValue(ExpressionMediator exm, List<AExpression> arguments)
		{
			GraphicsImage? g = TryGetCreatedGraphics(Name, exm, arguments, 0);
			if (g == null)
				return 0;
			string text = arguments[1].GetStrValue(exm);
			if (arguments.Count == 2)
			{
				GraphicsImage.GDrawString(text, 0, 0);
			}
			else if (arguments.Count == 4)
			{
				EmuPoint p = ReadPoint(Name, exm, arguments, 2);
				GraphicsImage.GDrawString(text, p.X, p.Y);
			}
#if HEADLESS
			// Headless 模式下无法测量文本尺寸，返回 0
			long[] resultArray0 = exm.VEvaluator.RESULT_ARRAY;
			resultArray0[1] = 0;
			resultArray0[2] = 0;
			return 1;
#else
			//生成する画像のサイズを取得
			var bitmap = new Bitmap(16, 16);
			var graphics = Graphics.FromImage(bitmap);
			Font font = g.Fnt;
			if (font == null)
				font = new Font(Config.FontName, 100, GlobalStatic.Console.StringStyle.FontStyle, GraphicsUnit.Pixel);
			var size = graphics.MeasureString(text, font, int.MaxValue, StringFormat.GenericTypographic);

			long[] resultArray = exm.VEvaluator.RESULT_ARRAY;
			resultArray[1] = (long)size.Width;
			resultArray[2] = (long)size.Height;
			return 1;
#endif
		}
	}

	public sealed class GraphicsGetTextSizeMethod : FunctionMethod
	{
		public GraphicsGetTextSizeMethod()
		{
			ReturnType = typeof(long);
			argumentTypeArrayEx = [
					new ArgTypeList{ ArgTypes = { ArgType.String, ArgType.String, ArgType.Int, ArgType.Int }, OmitStart = 3 }
				];
			CanRestructure = false;
		}
		public override long GetIntValue(ExpressionMediator exm, List<AExpression> arguments)
		{
			RequireGDIPlus(Name);
			string text = arguments[0].GetStrValue(exm);
#if HEADLESS
			// Headless 模式下无法测量文本尺寸，返回 0
			long[] resultArrayH = exm.VEvaluator.RESULT_ARRAY;
			resultArrayH[1] = 0;
			return 0;
#else
			//生成する画像のサイズを取得
			string fontname = arguments[1].GetStrValue(exm);
			long fontsize = arguments[2].GetIntValue(exm);
			FontStyle fs = FontStyle.Regular;
			if (arguments.Count > 3)
			{
				long style = arguments[3].GetIntValue(exm);
				if ((style & 1) != 0)
					fs |= FontStyle.Bold;
				if ((style & 2) != 0)
					fs |= FontStyle.Italic;
				if ((style & 4) != 0)
					fs |= FontStyle.Strikeout;
				if ((style & 8) != 0)
					fs |= FontStyle.Underline;
			}
			Font fnt = new(fontname, fontsize, fs, GraphicsUnit.Pixel);
			var bitmap = new Bitmap(16, 16);
			var graphics = Graphics.FromImage(bitmap);
			var size = graphics.MeasureString(text, fnt, int.MaxValue, StringFormat.GenericTypographic);

			long[] resultArray = exm.VEvaluator.RESULT_ARRAY;
			resultArray[1] = (long)size.Height;
			return (long)size.Width;
#endif
		}
	}

	public sealed class GraphicsDrawGWithRotateMethod : FunctionMethod
	{
		public GraphicsDrawGWithRotateMethod()
		{
			ReturnType = typeof(long);
			argumentTypeArrayEx = [
					new ArgTypeList{ ArgTypes = { ArgType.Int, ArgType.Int, ArgType.Int, ArgType.Int, ArgType.Int }, OmitStart = 3 }
				];
			CanRestructure = false;
		}
		public override long GetIntValue(ExpressionMediator exm, List<AExpression> arguments)
		{
			GraphicsImage? dest = TryGetCreatedGraphics(Name, exm, arguments, 0);
			if (dest == null)
				return 0;
			GraphicsImage? src = TryGetCreatedGraphics(Name, exm, arguments, 1);
			if (src == null)
				return 0;
			long angle = arguments[2].GetIntValue(exm);

			//座標省略してたらx/2,y/2で渡す
			if (arguments.Count == 3)
			{
				GraphicsImage.GDrawGWithRotate(src, angle, src.Width / 2, src.Height / 2);
			}
			else
			{
				EmuPoint p = ReadPoint(Name, exm, arguments, 3);
			GraphicsImage.GDrawGWithRotate(src, angle, p.X, p.Y);
			}
			return 1;
		}
	}

	//brushの参照がうまくいかないので保留

	public sealed class GraphicsDrawLineMethod : FunctionMethod
	{
		public GraphicsDrawLineMethod()
		{
			ReturnType = typeof(long);
			argumentTypeArrayEx = [new ArgTypeList{ ArgTypes = { ArgType.Int | ArgType.DisallowVoid, ArgType.Int | ArgType.DisallowVoid, ArgType.Int | ArgType.DisallowVoid, ArgType.Int | ArgType.DisallowVoid, ArgType.Int | ArgType.DisallowVoid } }];
			CanRestructure = false;
		}
		public override long GetIntValue(ExpressionMediator exm, List<AExpression> arguments)
		{
			GraphicsImage? g = TryGetCreatedGraphics(Name, exm, arguments, 0);
			if (g == null)
				return 0;
			EmuPoint fromP = ReadPoint(Name, exm, arguments, 1);
			EmuPoint forP = ReadPoint(Name, exm, arguments, 3);
			GraphicsImage.GDrawLine(fromP.X, fromP.Y, forP.X, forP.Y);
			return 1;
		}
	}

	public sealed class GraphicsCreateMethod : FunctionMethod
	{
		public GraphicsCreateMethod()
		{
			ReturnType = typeof(long);
			argumentTypeArrayEx = [new ArgTypeList{ ArgTypes = { ArgType.Int | ArgType.DisallowVoid, ArgType.Int | ArgType.DisallowVoid, ArgType.Int | ArgType.DisallowVoid } }];
			CanRestructure = false;
		}
		public override long GetIntValue(ExpressionMediator exm, List<AExpression> arguments)
		{
			GraphicsImage? g = TryGetUncreatedGraphics(Name, exm, arguments, 0);
			if (g == null)
				return 0;

			EmuPoint p = ReadPoint(Name, exm, arguments, 1);
			int width = p.X; int height = p.Y;
			if (width <= 0)//{0}関数:GraphicsのWidthに0以下の値({1})が指定されました
				throw new CodeEE(string.Format(trerror.GParamIsNegative.Text, Name, "Width", width));
			else if (width > AbstractImage.MAX_IMAGESIZE)//{0}関数:GraphicsのWidthに{2}以上の値({1})が指定されました
				throw new CodeEE(string.Format(trerror.GParamTooLarge.Text, Name, "Width", AbstractImage.MAX_IMAGESIZE, width));
			if (height <= 0)//{0}関数:GraphicsのHeightに0以下の値({1})が指定されました
				throw new CodeEE(string.Format(trerror.GParamIsNegative.Text, Name, "Height", height));
			else if (height > AbstractImage.MAX_IMAGESIZE)//{0}関数:GraphicsのHeightに{2}以上の値({1})が指定されました
				throw new CodeEE(string.Format(trerror.GParamTooLarge.Text, Name, "Height", AbstractImage.MAX_IMAGESIZE, height));

			g.GCreate(width, height, false);
			return 1;

		}
	}

	public sealed class GraphicsClearMethod : FunctionMethod
	{
		#region EM_私家版_GCLEAR拡張
		public GraphicsClearMethod()
		{
			ReturnType = typeof(long);
			argumentTypeArrayEx = [
					new ArgTypeList{ ArgTypes = { ArgType.Int, ArgType.Int } },
					new ArgTypeList{ ArgTypes = { ArgType.Int, ArgType.Int, ArgType.Int, ArgType.Int, ArgType.Int, ArgType.Int } }
				];
			CanRestructure = false;
		}
		public override long GetIntValue(ExpressionMediator exm, List<AExpression> arguments)
		{
			RequireGDIPlus(Name);
			GraphicsImage g = ReadGraphics(Name, exm, arguments, 0);
			EmuColor c = ReadColor(Name, exm, arguments, 1);
			if (!g.IsCreated)
				return 0;
			if (arguments.Count == 2)
				GraphicsImage.GClear(c);
			else
				GraphicsImage.GClear(c, (int)arguments[2].GetIntValue(exm), (int)arguments[3].GetIntValue(exm), (int)arguments[4].GetIntValue(exm), (int)arguments[5].GetIntValue(exm));
			return 1;
		}
		#endregion
	}

	public sealed class GraphicsFillRectangleMethod : FunctionMethod
	{
		public GraphicsFillRectangleMethod()
		{
			ReturnType = typeof(long);
			argumentTypeArrayEx = [new ArgTypeList{ ArgTypes = { ArgType.Int | ArgType.DisallowVoid, ArgType.Int | ArgType.DisallowVoid, ArgType.Int | ArgType.DisallowVoid, ArgType.Int | ArgType.DisallowVoid, ArgType.Int | ArgType.DisallowVoid } }];
			CanRestructure = false;
		}
		public override long GetIntValue(ExpressionMediator exm, List<AExpression> arguments)
		{
			GraphicsImage? g = TryGetCreatedGraphics(Name, exm, arguments, 0);
			if (g == null)
				return 0;
			EmuRectangle rect = ReadRectangle(Name, exm, arguments, 1);
			GraphicsImage.GFillRectangle(rect);
			return 1;
		}
	}

	public sealed class GraphicsDrawGMethod : FunctionMethod
	{
		public GraphicsDrawGMethod()
		{
			ReturnType = typeof(long);
			argumentTypeArrayEx = [
					new ArgTypeList{ ArgTypes = { ArgType.Int, ArgType.Int,
							ArgType.Int, ArgType.Int, ArgType.Int, ArgType.Int,
							ArgType.Int, ArgType.Int, ArgType.Int, ArgType.Int, ArgType.RefInt2D | ArgType.AllowConstRef }, OmitStart = 10 },
					new ArgTypeList{ ArgTypes = { ArgType.Int, ArgType.Int,
							ArgType.Int, ArgType.Int, ArgType.Int, ArgType.Int,
							ArgType.Int, ArgType.Int, ArgType.Int, ArgType.Int, ArgType.RefInt3D | ArgType.AllowConstRef }, OmitStart = 10 },
				];
			CanRestructure = false;
			HasUniqueRestructure = true;
		}

		public override long GetIntValue(ExpressionMediator exm, List<AExpression> arguments)
		{
			GraphicsImage? dest = TryGetCreatedGraphics(Name, exm, arguments, 0);
			if (dest == null)
				return 0;
			GraphicsImage? src = TryGetCreatedGraphics(Name, exm, arguments, 1);
			if (src == null)
				return 0;
			EmuRectangle destRect = ReadRectangle(Name, exm, arguments, 2);
			EmuRectangle srcRect = ReadRectangle(Name, exm, arguments, 6);
			if (arguments.Count == 10 || arguments[10] == null)
			{
				GraphicsImage.GDrawG(src, destRect, srcRect);
				return 1;
			}
			float[][] cm = ReadColormatrix(Name, exm, arguments, 10);
			GraphicsImage.GDrawG(src, destRect, srcRect, cm);
			return 1;
		}

		public override bool UniqueRestructure(ExpressionMediator exm, List<AExpression> arguments)
		{
			for (int i = 0; i < arguments.Count; i++)
			{
				if (arguments[i] == null)
					continue;
				//11番目の引数はColorMatrixの配列を指しているので定数にしてはいけない
				if (i == 10)
					arguments[i].Restructure(exm);
				else
					arguments[i] = arguments[i].Restructure(exm);
			}
			return false;
		}
	}

	public sealed class GraphicsDrawGWithMaskMethod : FunctionMethod
	{
		public GraphicsDrawGWithMaskMethod()
		{
			ReturnType = typeof(long);
			argumentTypeArrayEx = [new ArgTypeList{ ArgTypes = { ArgType.Int | ArgType.DisallowVoid, ArgType.Int | ArgType.DisallowVoid, ArgType.Int | ArgType.DisallowVoid, ArgType.Int | ArgType.DisallowVoid, ArgType.Int | ArgType.DisallowVoid } }];
			CanRestructure = false;
		}


		public override long GetIntValue(ExpressionMediator exm, List<AExpression> arguments)
		{
			GraphicsImage? dest = TryGetCreatedGraphics(Name, exm, arguments, 0);
			if (dest == null)
				return 0;
			GraphicsImage? src = TryGetCreatedGraphics(Name, exm, arguments, 1);
			if (src == null)
				return 0;
			GraphicsImage? mask = TryGetCreatedGraphics(Name, exm, arguments, 2);
			if (mask == null)
				return 0;
			if (src.Width != mask.Width || src.Height != mask.Height)
				return 0;
			EmuPoint destPoint = ReadPoint(Name, exm, arguments, 3);
			if (destPoint.X + src.Width > dest.Width || destPoint.Y + src.Height > dest.Height)
				return 0;
			GraphicsImage.GDrawGWithMask(src, mask, destPoint);
			return 1;
		}


	}
}
