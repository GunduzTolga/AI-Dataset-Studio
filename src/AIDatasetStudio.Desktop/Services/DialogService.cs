using AIDatasetStudio.Desktop.Localization;
using AIDatasetStudio.Desktop.ViewModels;
using AIDatasetStudio.Desktop.ViewModels.Dialogs;
using AIDatasetStudio.Desktop.Views.Dialogs;
using AIDatasetStudio.Domain.Enums;
using Avalonia.Controls;

namespace AIDatasetStudio.Desktop.Services;

internal sealed class DialogService(Func<Window?> owner, Localizer localizer) : IDialogService
{
    public async Task<string?> PromptTextAsync(string title, string message, string initialText)
    {
        var dialog = new InputDialogViewModel
        {
            Title = title,
            Message = message,
            ConfirmText = localizer["Dialog_OK"],
            CancelText = localizer["Dialog_Cancel"],
            ShowText = true,
            Text = initialText,
        };

        return await ShowAsync(dialog) ? dialog.Text.Trim() : null;
    }

    public async Task<NewDatasetRequest?> PromptNewDatasetAsync()
    {
        var options = Labels.CreatableDatasetTypes.Select(type => new DialogOption(type, Labels.For(localizer, type))).ToList();
        var dialog = new InputDialogViewModel
        {
            Title = localizer["Dialog_NewDatasetTitle"],
            Message = localizer["Dialog_NewDatasetMessage"],
            ConfirmText = localizer["Dialog_OK"],
            CancelText = localizer["Dialog_Cancel"],
            ShowText = true,
            Text = localizer["Default_DatasetName"],
            Options = options,
            SelectedOption = options[0],
        };

        return await ShowAsync(dialog) && dialog.SelectedOption is { Value: DatasetType type }
            ? new NewDatasetRequest(dialog.Text.Trim(), type)
            : null;
    }

    public async Task<T?> ChooseAsync<T>(string title, string message, IReadOnlyList<(T Value, string Label)> options, int selectedIndex)
        where T : struct
    {
        var dialogOptions = options.Select(option => new DialogOption(option.Value, option.Label)).ToList();
        var dialog = new InputDialogViewModel
        {
            Title = title,
            Message = message,
            ConfirmText = localizer["Dialog_OK"],
            CancelText = localizer["Dialog_Cancel"],
            Options = dialogOptions,
            SelectedOption = dialogOptions[selectedIndex],
        };

        return await ShowAsync(dialog) && dialog.SelectedOption is { Value: T value } ? value : null;
    }

    public Task<bool> ConfirmAsync(string title, string message, string confirmText) =>
        ShowAsync(new InputDialogViewModel
        {
            Title = title,
            Message = message,
            ConfirmText = confirmText,
            CancelText = localizer["Dialog_Cancel"],
        });

    private async Task<bool> ShowAsync(InputDialogViewModel viewModel)
    {
        var window = owner() ?? throw new InvalidOperationException("No window is available to host a dialog.");
        return await new InputDialog { DataContext = viewModel }.ShowDialog<bool>(window);
    }
}
