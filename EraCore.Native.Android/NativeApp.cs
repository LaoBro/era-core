using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;

namespace EraCore.Native.Android;

public sealed class NativeApp : Avalonia.Application
{
    public NativeApp()
    {
        // TextBox（输入栏）/ TextBlock / Slider 需要主题模板；默认暗色与终端视觉一致。
        Styles.Add(new FluentTheme());
        RequestedThemeVariant = ThemeVariant.Dark;
    }

    internal static NativeShell? Shell { get; private set; }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is ISingleViewApplicationLifetime singleView)
        {
            Shell = new NativeShell();
            singleView.MainView = Shell;
        }

        base.OnFrameworkInitializationCompleted();
    }

    internal static void ApplyTheme(AppTheme theme)
    {
        if (Current is null)
            return;
        Current.RequestedThemeVariant = theme == AppTheme.Light ? ThemeVariant.Light : ThemeVariant.Dark;
    }
}
