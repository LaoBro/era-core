using Android.Runtime;
using Avalonia.Android;

namespace EraCore.Native.Android;

/// <summary>
/// Avalonia 12 起 AppBuilder 配置挂在 Application 子类。
/// [Register] 固定 Java 类名，与 AndroidManifest 的 android:name 保持一致；
/// 构造函数必须 public（mono 运行时经 JNI 反射创建）。
/// </summary>
[Register("com.eracore.nativeapp.NativeAndroidApplication")]
public class NativeAndroidApplication : AvaloniaAndroidApplication<NativeApp>
{
    public NativeAndroidApplication(nint javaReference, JniHandleOwnership transfer)
        : base(javaReference, transfer)
    {
    }
}
