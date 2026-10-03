using MinorShift.Emuera.Runtime.Script.Statements.Expression;
using MinorShift.Emuera.Runtime.Script.Statements.Variable;
using MinorShift.Emuera.Runtime.Utils;
using trerror = MinorShift.Emuera.Runtime.Utils.EvilMask.Lang.Error;

namespace MinorShift.Emuera.Runtime.Script.Statements;

/// <summary>
/// ColorMatrix 读取深模块：把 ERB 的整数二维/三维数组解释为 GDI+ 惯例的 5×5 颜色矩阵（元素 ÷256）。
///
/// 上游 Skia 的 <c>ColorMatrixHelper</c> 与此同源（5×5 二维/三维整数数组）。era-core 侧
/// Graphics 颜色矩阵函数（<c>GSETCOLORMATRIX</c> 族）与 SETIMAGELAYER 族共用本读取器——
/// 数组维度判定、5×5 边界校验与错误消息在此单一实现，避免多处漂移（实现票 22 / spec §5）。
/// </summary>
internal static class ColorMatrixReader
{
	/// <summary>
	/// 读取 <paramref name="expr"/> 指向的整数二维/三维数组为 5×5 矩阵。
	/// 非数组、越界或不足 5×5 时抛 <see cref="CodeEE"/>，消息含 <paramref name="funcName"/> 与
	/// <paramref name="argNo"/>（1 起算的实参序号）。
	/// </summary>
	public static float[][] Read(string funcName, int argNo, AExpression expr, ExpressionMediator exm)
	{
		if (expr is not VariableTerm varTerm)
			throw new CodeEE(string.Format(trerror.ArgIsNotNDArray.Text, funcName, argNo, "2または3"));

		// 数値型二次元以上配列変数のはず
		FixedVariableTerm p = varTerm.GetFixedVariableTerm(exm);
		if (!p.Identifier.IsArray2D && !p.Identifier.IsArray3D)
			throw new CodeEE(string.Format(trerror.ArgIsNotNDArray.Text, funcName, argNo, "2または3"));

		float[][] cm = new float[5][];
		if (p.Identifier.IsArray2D)
		{
			long[,] array;
			long e1, e2;
			if (p.Identifier.IsCharacterData)
			{
				array = (p.Identifier.GetArrayChara((int)p.Index1) as long[,])!;
				e1 = p.Index2;
				e2 = p.Index3;
			}
			else
			{
				array = (p.Identifier.GetArray() as long[,])!;
				e1 = p.Index1;
				e2 = p.Index2;
			}
			if (e1 < 0 || e2 < 0 || e1 + 5 > array.GetLength(0) || e2 + 5 > array.GetLength(1))
				throw new CodeEE(string.Format(trerror.InvalidColorMatrix.Text, funcName, e1, e2));
			for (int x = 0; x < 5; x++)
			{
				cm[x] = new float[5];
				for (int y = 0; y < 5; y++)
					cm[x][y] = array[e1 + x, e2 + y] / 256f;
			}
		}
		if (p.Identifier.IsArray3D)
		{
			long[,,] array;
			long e1, e2, e3;
			if (p.Identifier.IsCharacterData)
				throw new NotImplCodeEE();
			array = (p.Identifier.GetArray() as long[,,])!;
			e1 = p.Index1;
			e2 = p.Index2;
			e3 = p.Index3;
			if (e1 < 0 || e1 >= array.GetLength(0))
				throw new CodeEE(string.Format(trerror.InvalidColorMatrix.Text, funcName, e2, e3));
			if (e2 < 0 || e3 < 0 || e2 + 5 > array.GetLength(1) || e3 + 5 > array.GetLength(2))
				throw new CodeEE(string.Format(trerror.InvalidColorMatrix.Text, funcName, e2, e3));
			for (int x = 0; x < 5; x++)
			{
				cm[x] = new float[5];
				for (int y = 0; y < 5; y++)
					cm[x][y] = array[e1, e2 + x, e3 + y] / 256f;
			}
		}
		return cm;
	}
}