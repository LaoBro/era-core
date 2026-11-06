using System.Runtime.CompilerServices;

namespace EraCore.Native.Android;

/// <summary>
/// ATrace 分片开关（ATRACE_TAG_APP，类别 <c>view</c> 之外需 <c>atrace -a &lt;pkg&gt;</c> 才采集）。
///
/// <para>默认关闭：关闭时只有一次静态 bool 判断（可内联），Release 下开销可忽略。
/// 打开方式：<c>adb shell am start -n com.eracore.nativeapp/... --ez eracore.trace true</c>，
/// 由 <see cref="MainActivity.OnCreate"/> 读 Intent 后置位。</para>
/// </summary>
internal static class NativeTrace
{
    internal static volatile bool Enabled;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void Begin(string name)
    {
        if (Enabled)
            global::Android.OS.Trace.BeginSection(name);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void End()
    {
        if (Enabled)
            global::Android.OS.Trace.EndSection();
    }

    internal static Scope Section(string name) => new(name);

    /// <summary>可嵌套分片；未启用时 Dispose 无操作，异常路径也会正确闭合。</summary>
    internal readonly struct Scope : System.IDisposable
    {
        private readonly bool _active;

        internal Scope(string name)
        {
            _active = Enabled;
            if (_active)
                global::Android.OS.Trace.BeginSection(name);
        }

        public void Dispose()
        {
            if (_active)
                global::Android.OS.Trace.EndSection();
        }
    }
}
