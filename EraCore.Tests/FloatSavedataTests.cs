using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using MinorShift.Emuera.GameData.Variable;
using MinorShift.Emuera.Runtime.Script.Statements;
using MinorShift.Emuera.Runtime.Script.Statements.Variable;
using MinorShift.Emuera.Runtime.Utils;
using Xunit;

namespace MinorShift.Emuera.Tests;

/// <summary>
/// 票 27b：`#DIMF SAVEDATA` / `#DIMF GLOBAL SAVEDATA` 的文本与二进制持久化往返。
/// 覆盖：声明接受、9 类 save 索引（float=2）、旧存档（无浮点段）兼容。
/// </summary>
[Collection("LoaderTests")]
public sealed class FloatSavedataTests
{
	private const string Erh =
		"#DIMF SAVEDATA FVAL, 3\n" +
		"#DIMF GLOBAL SAVEDATA GVAL, 2\n";

	private static VariableToken Token(LoaderTestHarness h, string name)
	{
		VariableToken? token = h.IdDic.GetVariableToken(name, null, false);
		Assert.NotNull(token);
		return token!;
	}

	private static byte[] ToBytes(MemoryStream ms)
	{
		// StreamWriter/BinaryWriter Dispose 会关闭底层 MemoryStream，但 ToArray 仍可读。
		return ms.ToArray();
	}

	[Fact]
	public async Task Text_savedata_float_roundtrip()
	{
		using var h = new LoaderTestHarness(dialectProfileId: DialectRegistry.SnakeId);
		h.WriteErh("float.ERH", Erh);
		await h.InitializeAsync();

		VariableToken token = Token(h, "FVAL");
		token.SetValue(1.25, [0L]);
		token.SetValue(2.5, [1L]);
		token.SetValue(-0.75, [2L]);

		var ms = new MemoryStream();
		var writer = new EraDataWriter(ms);
		h.VEvaluator.SaveToStream(writer, "float");
		writer.Close();
		byte[] bytes = ToBytes(ms);

		// 改动后再加载，验证确实从存档恢复。
		token.SetValue(99.0, [0L]);
		token.SetValue(99.0, [1L]);
		token.SetValue(99.0, [2L]);

		var reader = new EraDataReader(new MemoryStream(bytes));
		h.VEvaluator.LoadFromStream(reader);
		reader.Close();

		Assert.Equal(1.25, token.GetFloatValue(null!, [0L]), 10);
		Assert.Equal(2.5, token.GetFloatValue(null!, [1L]), 10);
		Assert.Equal(-0.75, token.GetFloatValue(null!, [2L]), 10);
	}

	[Fact]
	public async Task Binary_savedata_float_roundtrip()
	{
		using var h = new LoaderTestHarness(dialectProfileId: DialectRegistry.SnakeId);
		h.WriteErh("float.ERH", Erh);
		await h.InitializeAsync();

		VariableToken token = Token(h, "FVAL");
		token.SetValue(3.125, [0L]);
		token.SetValue(-0.5, [1L]);

		var ms = new MemoryStream();
		var writer = new EraBinaryDataWriter(ms);
		h.VEvaluator.SaveToStreamBinary(writer, "float");
		writer.Close();
		byte[] bytes = ToBytes(ms);

		token.SetValue(99.0, [0L]);
		token.SetValue(99.0, [1L]);

		var reader = EraBinaryDataReader.CreateReader(new MemoryStream(bytes));
		Assert.NotNull(reader);
		h.VEvaluator.LoadFromStreamBinary(reader);
		reader.Close();

		Assert.Equal(3.125, token.GetFloatValue(null!, [0L]), 10);
		Assert.Equal(-0.5, token.GetFloatValue(null!, [1L]), 10);
	}

	[Fact]
	public async Task Global_savedata_float_roundtrip_text_and_binary()
	{
		using var h = new LoaderTestHarness(dialectProfileId: DialectRegistry.SnakeId);
		h.WriteErh("float.ERH", Erh);
		await h.InitializeAsync();

		VariableData varData = h.VEvaluator.VariableData;
		VariableToken token = Token(h, "GVAL");
		token.SetValue(0.125, [0L]);
		token.SetValue(8.5, [1L]);

		// 文本
		var textMs = new MemoryStream();
		var textWriter = new EraDataWriter(textMs);
		textWriter.EmuStart();
		varData.SaveGlobalToStream1808(textWriter);
		textWriter.Close();
		byte[] textBytes = ToBytes(textMs);

		varData.SetDefaultGlobalValue();
		Assert.Equal(0.0, token.GetFloatValue(null!, [0L]), 10);

		var textReader = new EraDataReader(new MemoryStream(textBytes));
		Assert.True(textReader.SeekEmuStart());
		varData.LoadGlobalFromStream1808(textReader);
		textReader.Close();
		Assert.Equal(0.125, token.GetFloatValue(null!, [0L]), 10);
		Assert.Equal(8.5, token.GetFloatValue(null!, [1L]), 10);

		// 二进制
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

		varData.SetDefaultGlobalValue();
		Assert.Equal(0.0, token.GetFloatValue(null!, [0L]), 10);

		var binReader = EraBinaryDataReader.CreateReader(new MemoryStream(binBytes));
		Assert.NotNull(binReader);
		Assert.Equal(EraSaveFileType.Global, binReader.ReadFileType());
		binReader.ReadInt64();
		binReader.ReadInt64();
		binReader.ReadString();
		varData.LoadFromStreamBinary(binReader);
		binReader.Close();
		Assert.Equal(0.125, token.GetFloatValue(null!, [0L]), 10);
		Assert.Equal(8.5, token.GetFloatValue(null!, [1L]), 10);
	}

	[Fact]
	public void Float_marker_probe_keeps_old_streams_readable()
	{
		// 旧存档：只有 EMU_1808_START，没有浮点标记。
		var oldMs = new MemoryStream();
		var oldWriter = new EraDataWriter(oldMs);
		oldWriter.EmuStart();
		oldWriter.Close();
		var oldReader = new EraDataReader(new MemoryStream(ToBytes(oldMs)));
		Assert.True(oldReader.SeekEmuStart());
		Assert.False(oldReader.TryReadFloatStart());
		oldReader.Close();

		// 新存档：标记 + 1D/2D/3D 三段。
		double[] values = [0.5, -1.75, 2.0];
		var newMs = new MemoryStream();
		var newWriter = new EraDataWriter(newMs);
		newWriter.EmuFloatStart();
		newWriter.WriteExtended("FVAL", values);
		newWriter.EmuSeparete();
		newWriter.EmuSeparete();
		newWriter.EmuSeparete();
		newWriter.Close();

		var newReader = new EraDataReader(new MemoryStream(ToBytes(newMs)));
		Assert.True(newReader.TryReadFloatStart());
		System.Collections.Generic.Dictionary<string, System.Collections.Generic.List<double>> dic = newReader.ReadDoubleArrayExtended();
		Assert.Equal(values, dic["FVAL"]);
		Assert.Empty(newReader.ReadDoubleArray2DExtended());
		Assert.Empty(newReader.ReadDoubleArray3DExtended());
		newReader.Close();
	}
}
