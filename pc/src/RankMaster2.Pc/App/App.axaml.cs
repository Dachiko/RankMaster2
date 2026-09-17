namespace RankMaster2.Pc.App;

using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;
using Avalonia.Themes.Simple;

/// <summary>A-startup-and-shell.md § 3.1. Simple theme, Dark variant, added in code (§ 1: "Simple
/// over Fluent... every control is templated explicitly to SPEC.md anyway"). No resource
/// dictionary work happens before <see cref="Initialize"/> returns.</summary>
public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
        Styles.Add(new SimpleTheme());
        RequestedThemeVariant = ThemeVariant.Dark;
        StartupClock.Mark("avalonia_built");
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var services = Composition.Build();
            CrashLog.SetDiagnostics(services.Video.DiagnosticsDump);
            desktop.MainWindow = new MainWindow(services);
        }

        base.OnFrameworkInitializationCompleted();
    }
}
