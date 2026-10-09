using AIDatasetStudio.Desktop.Localization;
using AIDatasetStudio.Domain.Entities;
using AIDatasetStudio.Domain.ValueObjects;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AIDatasetStudio.Desktop.ViewModels.Versions;

/// <summary>
/// A suggested next number: "Minor · 1.3.0".
/// </summary>
public sealed record VersionSuggestion(string Number, string Label);

/// <summary>
/// Asks for the number and an optional note of a new version. The number starts at the next
/// minor version (1.0.0 for the first) and is checked while typing.
/// </summary>
public sealed partial class NewVersionViewModel : ViewModelBase
{
    private readonly Localizer _localizer;
    private readonly HashSet<string> _taken;

    public NewVersionViewModel(Localizer localizer, string datasetName, IEnumerable<string> existingNumbers, long recordCount)
    {
        _localizer = localizer;
        _taken = existingNumbers.ToHashSet(StringComparer.Ordinal);
        DatasetName = datasetName;
        SummaryText = localizer.Format("NewVersion_Summary", recordCount);

        var latest = _taken.Select(n => VersionNumber.TryParse(n, out var parsed) ? parsed : (VersionNumber?)null)
            .OfType<VersionNumber>()
            .DefaultIfEmpty()
            .Max();
        Suggestions = _taken.Count == 0
            ? []
            :
            [
                new(latest.NextPatch().ToString(), localizer.Format("NewVersion_Patch", latest.NextPatch())),
                new(latest.NextMinor().ToString(), localizer.Format("NewVersion_Minor", latest.NextMinor())),
                new(latest.NextMajor().ToString(), localizer.Format("NewVersion_Major", latest.NextMajor())),
            ];
        Number = _taken.Count == 0 ? VersionNumber.First.ToString() : latest.NextMinor().ToString();
    }

    /// <summary>
    /// Raised with <c>true</c> when the version should be created, <c>false</c> when cancelled.
    /// </summary>
    public event EventHandler<bool>? CloseRequested;

    public string DatasetName { get; }

    public string SummaryText { get; }

    public IReadOnlyList<VersionSuggestion> Suggestions { get; }

    public bool HasSuggestions => Suggestions.Count > 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ErrorText), nameof(HasError))]
    [NotifyCanExecuteChangedFor(nameof(ConfirmCommand))]
    public partial string Number { get; set; }

    [ObservableProperty]
    public partial string Description { get; set; } = string.Empty;

    public int MaxDescriptionLength => DatasetVersion.MaxDescriptionLength;

    /// <summary>
    /// Why the number cannot be used; <c>null</c> when it can.
    /// </summary>
    public string? ErrorText =>
        !VersionNumber.TryParse(Number, out var parsed) ? _localizer["NewVersion_InvalidNumber"]
        : _taken.Contains(parsed.ToString()) ? _localizer.Format("NewVersion_TakenNumber", parsed)
        : null;

    public bool HasError => ErrorText is not null;

    [RelayCommand]
    private void UseSuggestion(string number) => Number = number;

    [RelayCommand(CanExecute = nameof(CanConfirm))]
    private void Confirm() => CloseRequested?.Invoke(this, true);

    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke(this, false);

    private bool CanConfirm() => !HasError;
}
