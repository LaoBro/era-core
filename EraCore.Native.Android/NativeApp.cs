using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;

namespace EraCore.Native.Android;

public sealed class NativeApp : Avalonia.Application
{
    public NativeApp()
    {
        // TextBox（M2 输入栏）/ TextBlock 需要主题模板；暗色与终端视觉一致。
        Styles.Add(new FluentTheme());
        RequestedThemeVariant = ThemeVariant.Dark;
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is ISingleViewApplicationLifetime singleView)
            singleView.MainView = new NativeRoot();

        base.OnFrameworkInitializationCompleted();
    }
}
