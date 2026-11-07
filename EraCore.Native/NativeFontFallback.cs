using System;
using System.Collections.Generic;

namespace EraCore.Native;

/// <summary>
/// 内置字体源身份。解析按码位逐个回退，同一码位总是落到同一字体源。
///
/// <para><see cref="Grid"/> 与 <see cref="Block"/> 的分工复刻 Web 的
/// <c>EmueraMonoJP → EmueraBlock</c> 回退链：ASCII 与全角符号由网格字体提供（advance 0.5em / 1em，
/// 与 ＭＳ ゴシック 同度量），IPAGothic 缺失的半角符号由 EmueraBlock 补齐。</para>
/// </summary>
public enum NativeFontSource
{
    /// <summary>EraGridMono：ASCII 与全角符号（Box Drawing / Geometric / Misc 的全角部分）。</summary>
    Grid,

    /// <summary>EmueraBlock：IPAGothic 缺失的符号（Block Elements 等半角图形）。</summary>
    Block,

    /// <summary>EraCoreCJK：中文 / 日文汉字等 CJK 字形。</summary>
    Cjk,

    /// <summary>系统字体（Android <c>SKFontManager.MatchCharacter</c>，仅当前三个源都缺字时使用）。</summary>
    System,
}

/// <summary>
/// 逐码位回退顺序（纯策略，不含任何平台字体 API）：<c>Grid → Block → Cjk → System</c>。
/// 平台侧只需注入「该字体源是否覆盖该码位」的判定（Android 为 <c>SKTypeface.GetGlyph != 0</c>），
/// 因此顺序可以在 Windows 单测里用真实字体的覆盖表逐码位对拍。
/// </summary>
public static class NativeFontFallback
{
    private static readonly NativeFontSource[] Chain =
    {
        NativeFontSource.Grid,
        NativeFontSource.Block,
        NativeFontSource.Cjk,
    };

    /// <summary>内置字体源的尝试顺序（不含 <see cref="NativeFontSource.System"/>）。</summary>
    public static IReadOnlyList<NativeFontSource> Order => Chain;

    /// <summary>
    /// 从 <see cref="Order"/> 里选第一个覆盖该码位的源；全部不覆盖时返回 <see cref="NativeFontSource.System"/>。
    /// </summary>
    public static NativeFontSource Select(char codePoint, Func<NativeFontSource, char, bool> covers)
    {
        ArgumentNullException.ThrowIfNull(covers);

        foreach (var source in Chain)
        {
            if (covers(source, codePoint))
                return source;
        }

        return NativeFontSource.System;
    }
}
