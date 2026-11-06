using System;

namespace EraCore.Native;

/// <summary>
/// 文本 blob 的缓存键：覆盖所有影响 glyph 序列与位置的输入（文本内容、字体身份、变体、字号）。
///
/// <para>颜色、下划线、删除线只在绘制时由 paint / 额外画线决定，不参与 shaping，因此不进键：
/// 同一个 blob 可以被不同颜色的 run 复用。</para>
///
/// <para><see cref="Typeface"/> 是<b>字体身份对象</b>（Android 侧为 <c>SKTypeface</c>），
/// <c>NativeFontManager.Resolve</c> 保证同一字体返回同一实例，故按引用相等即可区分字体。
/// 放在 <c>EraCore.Native</c> 而不是 Android 项目，是为了让放大器与缓存在 Windows 单测里共用同一套键语义。</para>
/// </summary>
public readonly record struct NativeTextBlobKey(
    string Text,
    object Typeface,
    NativeFontVariant Variant,
    float FontSize);
