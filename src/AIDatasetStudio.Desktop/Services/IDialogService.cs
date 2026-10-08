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
}
