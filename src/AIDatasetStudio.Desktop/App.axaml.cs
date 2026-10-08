using System.Globalization;
using AIDatasetStudio.Application.Settings;
using AIDatasetStudio.Application.Workspaces;
using AIDatasetStudio.Desktop.Localization;
using AIDatasetStudio.Desktop.ViewModels;
using AIDatasetStudio.Desktop.Views;
using AIDatasetStudio.Infrastructure;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Microsoft.Extensions.DependencyInjection;

namespace AIDatasetStudio.Desktop;

public sealed partial class App : Avalonia.Application
{
    private ServiceProvider? _services;

    public override void Initialize()
    {
        _services = new ServiceCollection()
            .AddInfrastructure()
            .AddDesktop(() => (ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow)
            .BuildServiceProvider();

        ApplySavedLanguage(_services.GetRequiredService<IUserSettings>());
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop && _services is not null)
        {
            desktop.MainWindow = new MainWindow
            {
                DataContext = _services.GetRequiredService<MainWindowViewModel>(),
            };
            desktop.Exit += (_, _) => Shutdown();
        }

        base.OnFrameworkInitializationCompleted();
    }

    // The saved choice wins; otherwise the OS UI language decides (Localizer's default).
    private static void ApplySavedLanguage(IUserSettings settings)
    {
        if (settings.UiLanguage is { } language)
        {
            try
            {
                Localizer.Instance.SetCulture(CultureInfo.GetCultureInfo(language));
            }
            catch (CultureNotFoundException)
            {
                // Ignore an unknown stored value and fall back to the OS language.
            }
        }

        Localizer.Instance.Apply();
    }

    private void Shutdown()
    {
        if (_services is null)
        {
            return;
        }

        // Checkpoint the WAL and release the workspace file. Run off the UI thread so the
        // awaited work never needs the (now blocked) UI synchronization context.
        var services = _services;
        _services = null;
        Task.Run(async () =>
        {
            await services.GetRequiredService<IWorkspaceService>().CloseAsync();
            await services.DisposeAsync();
        }).GetAwaiter().GetResult();
    }
}
