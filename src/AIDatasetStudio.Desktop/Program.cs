using AIDatasetStudio.Desktop.Services;
using Avalonia;
using Velopack;

namespace AIDatasetStudio.Desktop;

internal static class Program
{
    // Don't use any Avalonia, third-party APIs or any SynchronizationContext-reliant code
    // before AppMain is called: things aren't initialized yet and stuff might break.
    [STAThread]
    public static void Main(string[] args)
    {
        // Installer hooks (first run, update, uninstall) are handled here and may exit early.
        VelopackApp.Build().Run();
        CrashLog.Install();
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    // Avalonia configuration, don't remove; also used by the visual designer.
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
