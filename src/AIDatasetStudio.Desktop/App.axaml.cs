using AIDatasetStudio.Application.Workspaces;
using AIDatasetStudio.Desktop.Localization;
using AIDatasetStudio.Desktop.Services;
using AIDatasetStudio.Desktop.ViewModels;
using AIDatasetStudio.Desktop.Views;
using AIDatasetStudio.Infrastructure;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Microsoft.Extensions.DependencyInjection;

namespace AIDatasetStudio.Desktop;

public sealed partial class App : Avalonia.Application
{
    private ServiceProvider? _services;

    public override void Initialize()
    {
        // Until language preferences are stored (settings database), follow the OS UI language.
        Localizer.Instance.Apply();
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            _services = ConfigureServices(desktop);
            desktop.MainWindow = new MainWindow
            {
                DataContext = _services.GetRequiredService<MainWindowViewModel>(),
            };
            desktop.Exit += (_, _) => Shutdown();
        }

        base.OnFrameworkInitializationCompleted();
    }

    private static ServiceProvider ConfigureServices(IClassicDesktopStyleApplicationLifetime desktop)
    {
        var services = new ServiceCollection();
        services.AddInfrastructure();
        services.AddSingleton(Localizer.Instance);
        services.AddSingleton<IFileDialogService>(provider =>
            new FileDialogService(() => desktop.MainWindow, provider.GetRequiredService<Localizer>()));
        services.AddTransient<MainWindowViewModel>();
        return services.BuildServiceProvider();
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
