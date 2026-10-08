using AIDatasetStudio.Desktop.Localization;
using AIDatasetStudio.Desktop.Services;
using AIDatasetStudio.Desktop.ViewModels;
using AIDatasetStudio.Desktop.ViewModels.Tags;
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
        // Dialogs and pickers belong to the window in front, so a wizard can open its own.
        Func<Window?> owner = () => ActiveWindow.Find(mainWindow);
        services.AddSingleton<IFileDialogService>(provider => new FileDialogService(owner, provider.GetRequiredService<Localizer>()));
        services.AddSingleton<IDialogService>(provider => new DialogService(owner, provider.GetRequiredService<Localizer>()));
        services.AddSingleton<IShellService>(_ => new ShellService(owner));
        services.AddSingleton<IUpdateService, VelopackUpdateService>();
        services.AddSingleton<UndoHistory>();
        services.AddSingleton<RecordOperations>();
        services.AddSingleton<TagCatalog>();
        services.AddSingleton<AIAssistant>();
        services.AddSingleton<StatusBarViewModel>();
        services.AddSingleton<ExplorerViewModel>();
        services.AddSingleton<RecordListViewModel>();
        services.AddSingleton<RecordDetailsViewModel>();
        services.AddSingleton<DatasetOverviewViewModel>();
        services.AddSingleton<MainWindowViewModel>();
        return services;
    }
}
