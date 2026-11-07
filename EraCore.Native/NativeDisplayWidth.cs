namespace EraCore.Native;

/// <summary>
/// 游戏文本的显示列宽（Emuera 网格口径）：半角 1 列、全角 2 列，列宽 = <c>FontSize / 2</c>。
///
/// <para>与 <c>EraCore.Core</c> 的 <c>TerminalDisplayWidth.IsWideChar</c> 必须逐条一致：
/// 引擎/协议用它计算行宽与对齐。原生前端若改用字体的实际 advance 来算行宽，比例字体
/// （如 Noto Sans SC 的 ASCII 只有 0.22–0.35em）会让同为 102 列的字符画每行得到不同的像素宽，
/// 逐行居中后左边缘参差；整行横线也会因宽度偏小而整段右移，贴不到左边。</para>
/// </summary>
public static class NativeDisplayWidth
{
    /// <summary>
    /// 判断字符在游戏设计中是否为全角（占 2 列）。
///
    /// <para>判定表与 Core 的 <c>TerminalDisplayWidth.IsWideChar</c> 完全一致：
/// Box Drawing / Geometric Shapes / Miscellaneous Symbols 为全角，
/// Block Elements（░▒▓█）为半角。</para>
    /// </summary>
    public static bool IsWideChar(char c)
    {
        // CJK 统一汉字、韩文、兼容汉字 — 全角
        if ((c >= 0x2E80 && c <= 0x9FFF)
            || (c >= 0xAC00 && c <= 0xD7AF)
            || (c >= 0xF900 && c <= 0xFAFF))
            return true;

        // 全角 ASCII 变体、全角符号 — 全角
        if ((c >= 0xFF01 && c <= 0xFF60)
            || (c >= 0xFFE0 && c <= 0xFFE6))
            return true;

        // CJK Compatibility Forms — 全角
        if (c >= 0xFE30 && c <= 0xFE4F)
            return true;

        // Box Drawing（━┃┏┗ 等）— 游戏设计为全角
        if (c >= 0x2500 && c <= 0x257F)
            return true;

        // Geometric Shapes（●■◆▲ 等）— 游戏设计为全角
        if (c >= 0x25A0 && c <= 0x25FF)
            return true;

        // Miscellaneous Symbols（★☆ 等）— 游戏设计为全角
        if (c >= 0x2600 && c <= 0x26FF)
            return true;

        // Block Elements（░▒▓█）— 游戏脚本按半角使用
        return false;
    }

    /// <summary>字符串的显示列数（半角 1、全角 2）。</summary>
    public static int Columns(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return 0;
        var columns = 0;
        foreach (var c in text)
            columns += IsWideChar(c) ? 2 : 1;
        return columns;
    }

    /// <summary>字符串按网格口径折算的像素宽度：<c>列数 × FontSize / 2</c>。</summary>
    public static double Pixels(string? text, double halfCellWidth)
        => Columns(text) * halfCellWidth;
}
