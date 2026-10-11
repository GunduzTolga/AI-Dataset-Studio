using AIDatasetStudio.Application.Export;
using AIDatasetStudio.Application.Import;
using AIDatasetStudio.Desktop.Localization;
using AIDatasetStudio.Desktop.ViewModels;
using AIDatasetStudio.Desktop.ViewModels.AI;
using AIDatasetStudio.Desktop.ViewModels.Dialogs;
using AIDatasetStudio.Desktop.ViewModels.Documents;
using AIDatasetStudio.Desktop.ViewModels.Export;
using AIDatasetStudio.Desktop.ViewModels.Import;
using AIDatasetStudio.Desktop.ViewModels.Tags;
using AIDatasetStudio.Desktop.ViewModels.Versions;
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

    public async Task<string?> PromptLinesAsync(string title, string message, string initialText)
    {
        var dialog = new InputDialogViewModel
        {
            Title = title,
            Message = message,
            ConfirmText = localizer["Dialog_OK"],
            CancelText = localizer["Dialog_Cancel"],
            ShowText = true,
            IsMultiline = true,
            AllowEmpty = true,
            MaxLength = 20_000,
            Text = initialText,
        };

        return await ShowAsync(dialog) ? dialog.Text : null;
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

    public Task<bool> ConfirmAsync(string title, string message, string confirmText, bool isDestructive = false) =>
        ShowAsync(new InputDialogViewModel
        {
            Title = title,
            Message = message,
            ConfirmText = confirmText,
            CancelText = localizer["Dialog_Cancel"],
            IsDestructive = isDestructive,
        });

    public async Task<ImportResult?> ShowImportWizardAsync(ImportWizardViewModel wizard)
    {
        await new ImportWizard { DataContext = wizard }.ShowDialog(Owner);
        return wizard.Result;
    }

    public async Task<ExportResult?> ShowExportWizardAsync(ExportWizardViewModel wizard)
    {
        await new ExportWizard { DataContext = wizard }.ShowDialog(Owner);
        return wizard.Result;
    }

    public Task ShowAboutAsync(AboutViewModel about) => new AboutDialog { DataContext = about }.ShowDialog(Owner);

    public Task<bool> PromptNewVersionAsync(NewVersionViewModel prompt) => new NewVersionDialog { DataContext = prompt }.ShowDialog<bool>(Owner);

    public Task ShowVersionsAsync(VersionsViewModel versions) => new VersionsWindow { DataContext = versions }.ShowDialog(Owner);

    public Task ShowTagManagerAsync(TagManagerViewModel tags) => new TagManagerWindow { DataContext = tags }.ShowDialog(Owner);

    public Task ShowAIConnectionsAsync(AIConnectionsViewModel connections) => new AIConnectionsWindow { DataContext = connections }.ShowDialog(Owner);

    public Task ShowQAExtractionAsync(QAExtractionViewModel extraction) => new QAExtractionWindow { DataContext = extraction }.ShowDialog(Owner);

    public Task ShowVariationsAsync(VariationsViewModel variations) => new VariationsWindow { DataContext = variations }.ShowDialog(Owner);

    public Task ShowJudgeAsync(JudgeViewModel judge) => new JudgeWindow { DataContext = judge }.ShowDialog(Owner);

    public Task ShowDatasetMergeAsync(ViewModels.Catalog.DatasetMergeViewModel merge) => new DatasetMergeWindow { DataContext = merge }.ShowDialog(Owner);

    public Task ShowHubImportAsync(ViewModels.Import.HubImportViewModel hub) => new HubImportWindow { DataContext = hub }.ShowDialog(Owner);

    public Task ShowModelEvaluationAsync(ModelEvaluationViewModel evaluation) => new ModelEvaluationWindow { DataContext = evaluation }.ShowDialog(Owner);

    public Task ShowTrainingAsync(ViewModels.Training.TrainingViewModel training) => new TrainingWindow { DataContext = training }.ShowDialog(Owner);

    private Task<bool> ShowAsync(InputDialogViewModel viewModel) => new InputDialog { DataContext = viewModel }.ShowDialog<bool>(Owner);

    private Window Owner => owner() ?? throw new InvalidOperationException("No window is available to host a dialog.");
}
