using Android.Runtime;
using Android.Util;
using Avalonia.Android;

namespace EraCore.Native.Android;

/// <summary>
/// Avalonia 12 起 AppBuilder 配置挂在 Application 子类。
/// [Register] 固定 Java 类名，与 AndroidManifest 的 android:name 保持一致；
/// 构造函数必须 public（mono 运行时经 JNI 反射创建）。
///
/// <para>平台层（设置 / 内置字体 / 游戏库目录）必须在这里初始化：Avalonia 在
/// Application 阶段就构造 <see cref="NativeApp"/> 与 <see cref="NativeShell"/>，
/// 早于 Activity 的 OnCreate。若等到 Activity 再读 settings.json，壳会拿到默认值
/// （深色 / 跟随系统），持久化的主题与屏幕方向就不生效。</para>
/// </summary>
[Register("com.eracore.nativeapp.NativeAndroidApplication")]
public class NativeAndroidApplication : AvaloniaAndroidApplication<NativeApp>
{
    private const string LogTag = "EraCoreNative";

    public NativeAndroidApplication(nint javaReference, JniHandleOwnership transfer)
        : base(javaReference, transfer)
    {
    }

    public override void OnCreate()
    {
        try
        {
            NativeRuntime.Initialize(this);
        }
        catch (System.Exception ex)
        {
            Log.Error(LogTag, "NativeRuntime.Initialize failed: " + ex);
        }

        base.OnCreate();
    }
}
