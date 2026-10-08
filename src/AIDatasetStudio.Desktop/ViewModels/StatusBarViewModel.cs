using AIDatasetStudio.Desktop.Localization;

namespace AIDatasetStudio.Desktop.ViewModels;

/// <summary>
/// The status bar message, shared by all panes. Kept as a resource key plus arguments so it is
/// re-rendered when the UI language changes.
/// </summary>
public sealed class StatusBarViewModel : ViewModelBase
{
    private readonly Localizer _localizer;
    private string _key = "Status_Ready";
    private object?[] _args = [];

    public StatusBarViewModel(Localizer localizer)
    {
        _localizer = localizer;
        _localizer.CultureChanged += (_, _) => OnPropertyChanged(nameof(Message));
    }

    public string Message => _localizer.Format(_key, _args);

    public void Set(string key, params object?[] args)
    {
        _key = key;
        _args = args;
        OnPropertyChanged(nameof(Message));
    }

    /// <summary>
    /// Shows an expected failure (missing item, invalid input, file problem).
    /// </summary>
    /// <returns><c>false</c> if the exception is not one the UI expects; callers then rethrow it.</returns>
    public bool TryReport(Exception exception)
    {
        if (UiErrors.TryDescribe(exception) is not { } error)
        {
            return false;
        }

        Set(error.Key, error.Args);
        return true;
    }
}
