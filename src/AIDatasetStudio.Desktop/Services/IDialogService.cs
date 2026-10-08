using AIDatasetStudio.Application.Export;
using AIDatasetStudio.Application.Import;
using AIDatasetStudio.Desktop.ViewModels.AI;
using AIDatasetStudio.Desktop.ViewModels.Dialogs;
using AIDatasetStudio.Desktop.ViewModels.Documents;
using AIDatasetStudio.Desktop.ViewModels.Export;
using AIDatasetStudio.Desktop.ViewModels.Import;
using AIDatasetStudio.Desktop.ViewModels.Tags;
using AIDatasetStudio.Desktop.ViewModels.Versions;
using AIDatasetStudio.Domain.Enums;

namespace AIDatasetStudio.Desktop.Services;

public sealed record NewDatasetRequest(string Name, DatasetType Type);

/// <summary>
/// Modal questions to the user, abstracted so view models can be tested without windows.
/// </summary>
public interface IDialogService
{
    /// <returns>The trimmed text, or <c>null</c> if cancelled.</returns>
    Task<string?> PromptTextAsync(string title, string message, string initialText);

    /// <summary>
    /// Asks for several lines of text, which may be empty.
    /// </summary>
    /// <returns>The text, or <c>null</c> if cancelled.</returns>
    Task<string?> PromptLinesAsync(string title, string message, string initialText);

    /// <returns>The name and type, or <c>null</c> if cancelled.</returns>
    Task<NewDatasetRequest?> PromptNewDatasetAsync();

    /// <returns>The chosen value, or <c>null</c> if cancelled.</returns>
    Task<T?> ChooseAsync<T>(string title, string message, IReadOnlyList<(T Value, string Label)> options, int selectedIndex)
        where T : struct;

    /// <param name="title">Dialog title.</param>
    /// <param name="message">The question.</param>
    /// <param name="confirmText">Label of the confirming button.</param>
    /// <param name="isDestructive">Styles the confirming button as dangerous (deletes).</param>
    Task<bool> ConfirmAsync(string title, string message, string confirmText, bool isDestructive = false);

    /// <summary>
    /// Shows the import wizard until the user closes it.
    /// </summary>
    /// <returns>The finished import, or <c>null</c> if nothing was imported.</returns>
    Task<ImportResult?> ShowImportWizardAsync(ImportWizardViewModel wizard);

    /// <summary>
    /// Shows the export wizard until the user closes it.
    /// </summary>
    /// <returns>The finished export, or <c>null</c> if nothing was written.</returns>
    Task<ExportResult?> ShowExportWizardAsync(ExportWizardViewModel wizard);

    /// <summary>
    /// Shows Help › About until it is closed.
    /// </summary>
    Task ShowAboutAsync(AboutViewModel about);

    /// <summary>
    /// Asks for the number and note of a new version.
    /// </summary>
    /// <returns><c>true</c> to create the version as entered in <paramref name="prompt"/>.</returns>
    Task<bool> PromptNewVersionAsync(NewVersionViewModel prompt);

    /// <summary>
    /// Shows the versions of a dataset until the window is closed.
    /// </summary>
    Task ShowVersionsAsync(VersionsViewModel versions);

    /// <summary>
    /// Shows Tools › Tags until the window is closed.
    /// </summary>
    Task ShowTagManagerAsync(TagManagerViewModel tags);

    /// <summary>
    /// Shows Tools › AI Connections until the window is closed.
    /// </summary>
    Task ShowAIConnectionsAsync(AIConnectionsViewModel connections);

    /// <summary>
    /// Shows Q&amp;A extraction from a document until the window is closed.
    /// </summary>
    Task ShowQAExtractionAsync(QAExtractionViewModel extraction);
}
