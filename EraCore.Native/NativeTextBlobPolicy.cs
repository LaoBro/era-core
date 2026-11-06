namespace EraCore.Native;

/// <summary>
/// 「这段文本能不能走 <c>SKTextBlob</c>」的判定：渲染线程与后台预热必须用<b>同一份</b>规则，
/// 否则预热会为一条最终走 <c>DrawText</c> 回退的 run 创建 blob（白造一个原生对象）。
///
/// <para>成对 UTF-16 代理（BMP 外字符）在 blob 与直接 <c>DrawText</c> 的 glyph 映射上不保证逐像素一致，
/// 这类 run 一律回退到直接绘制，因此也不预热。</para>
/// </summary>
public static class NativeTextBlobPolicy
{
    /// <summary>文本中是否含有成对的 UTF-16 代理（BMP 外字符）。</summary>
    public static bool HasSurrogatePair(string text)
    {
        for (var i = 0; i < text.Length - 1; i++)
        {
            if (char.IsHighSurrogate(text[i]) && char.IsLowSurrogate(text[i + 1]))
                return true;
        }

        return false;
    }

    /// <summary>该 run 是否允许走 blob 路径（与 <c>NativeSkiaRenderer.DrawRun</c> 的回退口径一致）。</summary>
    public static bool CanBlob(string text) => !HasSurrogatePair(text);
}
