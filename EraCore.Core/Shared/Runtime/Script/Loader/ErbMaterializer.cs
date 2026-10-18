using MinorShift.Emuera.GameProc;
using MinorShift.Emuera.Runtime.Script.Parser;
using MinorShift.Emuera.Runtime.Script.Statements;
using MinorShift.Emuera.Runtime.Utils;
using MinorShift.Emuera.Sub;
using System;

namespace MinorShift.Emuera.Runtime.Script.Loader;

// ADR-0047（E 组 · C-E2a）：物化的两条输入流从 ErbLoader 外移。放在本文件而不是单独文件，
// 是因为它们是 ErbMaterializer 的输入抽象，下一步引擎搬入时同处一处。
/// <summary>b2-irdiskcache：「行文本流」抽象（design §5.3）——源回读与 blob 文本两条实现共用同一物化循环，物化产物等价性收敛为「输入流 == 源文件启用行」一件事。</summary>
internal interface IFunctionLineSource
{
	/// <summary>取下一启用行（blob 源不回源、不 rename、不重 [ 预处理——已在产生端过滤）。行流在物化循环内驱动 ppstate。</summary>
	CharStream? ReadEnabledLine(bool disabled);
	/// <summary>最近一次返回行的行号（对齐 EraStreamReader.LineNo 语义）。</summary>
	int LineNo { get; }
	string Filename { get; }
	/// <summary>
	/// 该流是否只含单个函数（blob 源 = 单函数块）。true 时「@名」在本流内唯一，定位无需行号消歧
	/// （IR 索引行的行号与 blob 首行行号可能不一致，见实现票 31）。源文件流 = false，需按行号区分同名多定义。
	/// </summary>
	bool SingleFunction { get; }
}

/// <summary>源回读后端：包一层 <see cref="EraStreamReader"/>（Preload 缓存 / DirAccessor / EncodingHandler，ADR-0023 决策二回读通道）。</summary>
internal sealed class SourceLineSource : IFunctionLineSource
{
	private readonly EraStreamReader _reader;
	public SourceLineSource(EraStreamReader reader) => _reader = reader;
	public string Filename => _reader.Filename;
	public int LineNo => _reader.LineNo;
	public bool SingleFunction => false;
	public CharStream? ReadEnabledLine(bool disabled) => _reader.ReadEnabledLine(disabled);
}

/// <summary>IR blob 后端：回放函数块内已过滤的启用行（行号 + 文本），不做 rename / SkipWhiteSpace 之外的加工。</summary>
internal sealed class BlobLineSource : IFunctionLineSource
{
	private readonly IrLine[] _lines;
	private readonly string _filename;
	private int _index;
	private int _lineNo;
	public BlobLineSource(IrLine[] lines, string filename) { _lines = lines; _filename = filename; }
	public string Filename => _filename;
	public int LineNo => _lineNo;
	public bool SingleFunction => true;
	public CharStream? ReadEnabledLine(bool disabled)
	{
		if (_index >= _lines.Length)
			return null;
		var l = _lines[_index++];
		_lineNo = l.LineNo;
		var st = new CharStream(l.Text);
		LexicalAnalyzer.SkipWhiteSpace(st); // 对齐 ReadEnabledLine 产出的指向（源侧在 ReadEnabledLine 内已 Skip）
		return st;
	}
}
