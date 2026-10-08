using AIDatasetStudio.Desktop.Localization;
using AIDatasetStudio.Desktop.Services;
using AIDatasetStudio.Desktop.ViewModels;
using Avalonia.Controls;
using Microsoft.Extensions.DependencyInjection;

namespace AIDatasetStudio.Desktop;

public static class AppServices
{
    /// <summary>
    /// Registers the desktop UI: localization, dialogs and view models.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="mainWindow">Returns the window that hosts dialogs (resolved when a dialog opens).</param>
    public static IServiceCollection AddDesktop(this IServiceCollection services, Func<Window?> mainWindow)
    {
        services.AddSingleton(Localizer.Instance);
        services.AddSingleton<IFileDialogService>(provider => new FileDialogService(mainWindow, provider.GetRequiredService<Localizer>()));
        services.AddSingleton<IDialogService>(provider => new DialogService(mainWindow, provider.GetRequiredService<Localizer>()));
        services.AddSingleton<StatusBarViewModel>();
        services.AddSingleton<ExplorerViewModel>();
        services.AddSingleton<RecordListViewModel>();
        services.AddSingleton<RecordDetailsViewModel>();
        services.AddSingleton<MainWindowViewModel>();
        return services;
    }
}
