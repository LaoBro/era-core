using System.IO;
using System.Linq;
using System.Threading.Tasks;
using MinorShift.Emuera.GameData.Variable;
using MinorShift.Emuera.Runtime.Script;
using MinorShift.Emuera.Runtime.Script.Data;
using MinorShift.Emuera.Runtime.Script.Loader;
using MinorShift.Emuera.Runtime.Script.Statements;
using MinorShift.Emuera.Runtime.Script.Statements.Variable;
using MinorShift.Emuera.Runtime.Utils;
using Xunit;

namespace MinorShift.Emuera.Tests;

/// <summary>
/// 票 46（Skia 18.0.0）：`#DIMF` 支持一至三维浮点数组。
/// 覆盖声明/读写、默认值回填、文本与二进制存档、GLOBAL、函数内 STATIC/DYNAMIC，
/// 以及 CHARADATA 仍拒绝、`#REFF` 仍为标量。
/// </summary>
[Collection("LoaderTests")]
public sealed class P0FloatMultiDimVariableTests
{
	private static VariableToken Token(LoaderTestHarness h, string name)
	{
		VariableToken? token = h.IdDic.GetVariableToken(name, null, false);
		Assert.NotNull(token);
		return token!;
	}

	private static string ScreenText(LoaderTestHarness h)
		=> string.Join("\n", h.Console.DisplayLineList.Select(line => line?.ToString() ?? ""));

	private static byte[] ToBytes(MemoryStream ms) => ms.ToArray();

	private static UserDefinedVariableToken Private(
		LoaderTestHarness h, string name, int[] lengths, bool isStatic, double[]? defaults = null)
	{
		return h.VEvaluator.VariableData.CreatePrivateVariable(new UserDefinedVariableData
		{
			Name = name,
			Dimension = lengths.Length,
			Lengths = lengths,
			Static = isStatic,
			Private = true,
			TypeIsFloat = true,
			DefaultFloat = defaults!,
		});
	}

	[Fact]
	public async Task Dimf_2d_and_3d_declare_and_read_write()
	{
		using var h = new LoaderTestHarness(dialectProfileId: DialectRegistry.SnakeId);
		h.WriteErh("multi.ERH", "#DIMF M2, 3, 2\n#DIMF M3, 2, 3, 4\n");
		await h.InitializeAsync();

		VariableToken m2 = Token(h, "M2");
		Assert.Equal(2, m2.Dimension);
		m2.SetValue(1.5, [1L, 1L]);
		Assert.Equal(1.5, m2.GetFloatValue(null!, [1L, 1L]), 10);
		Assert.Equal(3, ((double[,])m2.GetArray()).GetLength(0));
		Assert.Equal(2, ((double[,])m2.GetArray()).GetLength(1));

		VariableToken m3 = Token(h, "M3");
		Assert.Equal(3, m3.Dimension);
		m3.SetValue(-2.25, [1L, 2L, 3L]);
		Assert.Equal(-2.25, m3.GetFloatValue(null!, [1L, 2L, 3L]), 10);
		Assert.Equal(2, ((double[,,])m3.GetArray()).GetLength(0));
		Assert.Equal(3, ((double[,,])m3.GetArray()).GetLength(1));
		Assert.Equal(4, ((double[,,])m3.GetArray()).GetLength(2));

		// 整数赋值拓宽为浮点（与 1D 口径一致）。
		m2.SetValue(7L, [0L, 1L]);
		Assert.Equal(7.0, m2.GetFloatValue(null!, [0L, 1L]), 10);
	}

	[Fact]
	public async Task Dimf_2d_3d_defaults_fill_row_major()
	{
		using var h = new LoaderTestHarness(dialectProfileId: DialectRegistry.SnakeId);
		await h.InitializeAsync();

		// 默认值来自程序化数据（常数表等路径），按行优先展开。
		UserDefinedVariableToken d2 = h.VEvaluator.VariableData.CreateUserDefVariable(
			new UserDefinedVariableData
			{
				Name = "D2",
				Dimension = 2,
				Lengths = [2, 2],
				TypeIsFloat = true,
				DefaultFloat = [1.5, 2.5],
			}, new DimLineWC(null!, false, true, false, null));
		Assert.Equal(1.5, d2.GetFloatValue(null!, [0L, 0L]), 10);
		Assert.Equal(2.5, d2.GetFloatValue(null!, [0L, 1L]), 10);
		Assert.Equal(0.0, d2.GetFloatValue(null!, [1L, 0L]), 10);

		d2.SetValue(9.0, [0L, 0L]);
		d2.SetDefault();
		Assert.Equal(1.5, d2.GetFloatValue(null!, [0L, 0L]), 10);

		UserDefinedVariableToken d3 = h.VEvaluator.VariableData.CreateUserDefVariable(
			new UserDefinedVariableData
			{
				Name = "D3",
				Dimension = 3,
				Lengths = [2, 1, 2],
				TypeIsFloat = true,
				DefaultFloat = [0.25, 0.5],
			}, new DimLineWC(null!, false, true, false, null));
		Assert.Equal(0.25, d3.GetFloatValue(null!, [0L, 0L, 0L]), 10);
		Assert.Equal(0.5, d3.GetFloatValue(null!, [0L, 0L, 1L]), 10);
	}

	[Fact]
	public async Task Dimf_savedata_2d_3d_text_and_binary_roundtrip()
	{
		using var h = new LoaderTestHarness(dialectProfileId: DialectRegistry.SnakeId);
		h.WriteErh("multi.ERH", "#DIMF SAVEDATA S2, 2, 2\n#DIMF SAVEDATA S3, 2, 2, 2\n");
		await h.InitializeAsync();

		VariableToken s2 = Token(h, "S2");
		VariableToken s3 = Token(h, "S3");
		s2.SetValue(0.5, [0L, 1L]);
		s2.SetValue(-1.25, [1L, 0L]);
		s3.SetValue(3.5, [0L, 1L, 1L]);
		s3.SetValue(4.75, [1L, 0L, 1L]);

		// 文本
		var textMs = new MemoryStream();
		var textWriter = new EraDataWriter(textMs);
		h.VEvaluator.SaveToStream(textWriter, "multi");
		textWriter.Close();
		byte[] textBytes = ToBytes(textMs);

		s2.SetValue(99.0, [0L, 1L]);
		s3.SetValue(99.0, [0L, 1L, 1L]);
		var textReader = new EraDataReader(new MemoryStream(textBytes));
		h.VEvaluator.LoadFromStream(textReader);
		textReader.Close();
		Assert.Equal(0.5, s2.GetFloatValue(null!, [0L, 1L]), 10);
		Assert.Equal(-1.25, s2.GetFloatValue(null!, [1L, 0L]), 10);
		Assert.Equal(3.5, s3.GetFloatValue(null!, [0L, 1L, 1L]), 10);
		Assert.Equal(4.75, s3.GetFloatValue(null!, [1L, 0L, 1L]), 10);

		// 二进制
		var binMs = new MemoryStream();
		var binWriter = new EraBinaryDataWriter(binMs);
		h.VEvaluator.SaveToStreamBinary(binWriter, "multi");
		binWriter.Close();
		byte[] binBytes = ToBytes(binMs);

		s2.SetValue(99.0, [0L, 1L]);
		s3.SetValue(99.0, [0L, 1L, 1L]);
		var binReader = EraBinaryDataReader.CreateReader(new MemoryStream(binBytes));
		Assert.NotNull(binReader);
		h.VEvaluator.LoadFromStreamBinary(binReader);
		binReader.Close();
		Assert.Equal(0.5, s2.GetFloatValue(null!, [0L, 1L]), 10);
		Assert.Equal(-1.25, s2.GetFloatValue(null!, [1L, 0L]), 10);
		Assert.Equal(3.5, s3.GetFloatValue(null!, [0L, 1L, 1L]), 10);
		Assert.Equal(4.75, s3.GetFloatValue(null!, [1L, 0L, 1L]), 10);
	}

	[Fact]
	public async Task Dimf_global_savedata_2d_3d_text_and_binary_roundtrip()
	{
		using var h = new LoaderTestHarness(dialectProfileId: DialectRegistry.SnakeId);
		h.WriteErh("multi.ERH", "#DIMF GLOBAL SAVEDATA G2, 2, 2\n#DIMF GLOBAL SAVEDATA G3, 2, 1, 2\n");
		await h.InitializeAsync();

		VariableData varData = h.VEvaluator.VariableData;
		VariableToken g2 = Token(h, "G2");
		VariableToken g3 = Token(h, "G3");
		g2.SetValue(1.25, [1L, 1L]);
		g3.SetValue(6.5, [1L, 0L, 1L]);

		// 文本
		var textMs = new MemoryStream();
		var textWriter = new EraDataWriter(textMs);
		textWriter.EmuStart();
		varData.SaveGlobalToStream1808(textWriter);
		textWriter.Close();
		byte[] textBytes = ToBytes(textMs);

		varData.SetDefaultGlobalValue();
		Assert.Equal(0.0, g2.GetFloatValue(null!, [1L, 1L]), 10);

		var textReader = new EraDataReader(new MemoryStream(textBytes));
		Assert.True(textReader.SeekEmuStart());
		varData.LoadGlobalFromStream1808(textReader);
		textReader.Close();
		Assert.Equal(1.25, g2.GetFloatValue(null!, [1L, 1L]), 10);
		Assert.Equal(6.5, g3.GetFloatValue(null!, [1L, 0L, 1L]), 10);

		// 二进制（Global 走全局段）
		var binMs = new MemoryStream();
		var binWriter = new EraBinaryDataWriter(binMs);
		binWriter.WriteHeader();
		binWriter.WriteFileType(EraSaveFileType.Global);
		binWriter.WriteInt64(0);
		binWriter.WriteInt64(0);
		binWriter.WriteString("");
		varData.SaveGlobalToStreamBinary(binWriter);
		binWriter.WriteEOF();
		binWriter.Close();
		byte[] binBytes = ToBytes(binMs);

		g2.SetValue(99.0, [1L, 1L]);
		g3.SetValue(99.0, [1L, 0L, 1L]);
		var binReader = EraBinaryDataReader.CreateReader(new MemoryStream(binBytes));
		Assert.NotNull(binReader);
		Assert.Equal(EraSaveFileType.Global, binReader.ReadFileType());
		binReader.ReadInt64();
		binReader.ReadInt64();
		binReader.ReadString();
		varData.LoadFromStreamBinary(binReader);
		binReader.Close();
		Assert.Equal(1.25, g2.GetFloatValue(null!, [1L, 1L]), 10);
		Assert.Equal(6.5, g3.GetFloatValue(null!, [1L, 0L, 1L]), 10);
	}

	[Fact]
	public async Task Dimf_private_static_and_dynamic_2d_3d_scope()
	{
		using var h = new LoaderTestHarness(dialectProfileId: DialectRegistry.SnakeId);
		await h.InitializeAsync();

		// STATIC：首次访问即按默认值初始化；SetDefault 复位。
		UserDefinedVariableToken p2s = Private(h, "P2S", [2, 2], isStatic: true, [1.5, 2.5]);
		Assert.Equal(1.5, p2s.GetFloatValue(null!, [0L, 0L]), 10);
		Assert.Equal(2.5, p2s.GetFloatValue(null!, [0L, 1L]), 10);
		Assert.Equal(0.0, p2s.GetFloatValue(null!, [1L, 0L]), 10);
		p2s.SetValue(9.0, [1L, 1L]);
		p2s.SetDefault();
		Assert.Equal(0.0, p2s.GetFloatValue(null!, [1L, 1L]), 10);

		// DYNAMIC：每次 ScopeIn 重新分配并按默认值回填。
		UserDefinedVariableToken p2d = Private(h, "P2D", [2, 3], isStatic: false, [0.5, 1.5]);
		p2d.ScopeIn();
		Assert.Equal(0.5, p2d.GetFloatValue(null!, [0L, 0L]), 10);
		Assert.Equal(1.5, p2d.GetFloatValue(null!, [0L, 1L]), 10);
		p2d.SetValue(7.0, [1L, 2L]);
		Assert.Equal(7.0, p2d.GetFloatValue(null!, [1L, 2L]), 10);
		p2d.ScopeOut();
		p2d.ScopeIn();
		Assert.Equal(0.0, p2d.GetFloatValue(null!, [1L, 2L]), 10);
		Assert.Equal(0.5, p2d.GetFloatValue(null!, [0L, 0L]), 10);
		p2d.ScopeOut();

		// 3D DYNAMIC
		UserDefinedVariableToken p3d = Private(h, "P3D", [2, 2, 2], isStatic: false, [0.25]);
		p3d.ScopeIn();
		Assert.Equal(0.25, p3d.GetFloatValue(null!, [0L, 0L, 0L]), 10);
		p3d.SetValue(3.0, [1L, 1L, 1L]);
		p3d.ScopeOut();
		p3d.ScopeIn();
		Assert.Equal(0.0, p3d.GetFloatValue(null!, [1L, 1L, 1L]), 10);
		Assert.Equal(0.25, p3d.GetFloatValue(null!, [0L, 0L, 0L]), 10);
		p3d.ScopeOut();
	}

	[Fact]
	public async Task Dimf_charadata_still_rejected_and_reff_stays_scalar()
	{
		using (var h = new LoaderTestHarness(dialectProfileId: DialectRegistry.SnakeId))
		{
			await h.InitializeAsync();
			h.WriteErh("bad.ERH", "#DIMF CHARADATA FCHARA, 2, 2\n");
			await h.PreloadAsync();
			var loader = new ErhLoader(h.Console, h.IdDic, h.VEvaluator, h.Env);
			Assert.False(loader.LoadHeaderFiles(h.ErbDir, displayReport: false));
			Assert.Contains("CHARADATA", ScreenText(h));
		}

		// #REFF 仍为标量引用（Dimension = 0）。
		using (var h = new LoaderTestHarness(dialectProfileId: DialectRegistry.SnakeId))
		{
			await h.InitializeAsync();
			h.WriteErh("ref.ERH", "#REFF FR\n");
			await h.PreloadAsync();
			var loader = new ErhLoader(h.Console, h.IdDic, h.VEvaluator, h.Env);
			Assert.True(loader.LoadHeaderFiles(h.ErbDir, displayReport: false));
			VariableToken fr = Token(h, "FR");
			Assert.Equal(0, fr.Dimension);
		}
	}
}
