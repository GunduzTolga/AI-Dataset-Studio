using AIDatasetStudio.Application.Records;
using AIDatasetStudio.Desktop.Localization;
using AIDatasetStudio.Domain.Enums;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AIDatasetStudio.Desktop.ViewModels;

public sealed partial class RecordRowViewModel : ViewModelBase
{
    private readonly Localizer _localizer;

    public RecordRowViewModel(RecordSummary summary, Localizer localizer)
    {
        _localizer = localizer;
        Id = summary.Id;
        Ordinal = summary.Ordinal;
        Preview = summary.Preview;
        Status = summary.Status;
    }

    public Guid Id { get; }

    public long Ordinal { get; }

    public string OrdinalText => $"#{Ordinal}";

    [ObservableProperty]
    public partial string Preview { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusLabel))]
    public partial RecordStatus Status { get; set; }

    public string StatusLabel => Labels.For(_localizer, Status);

    public RecordCursor Cursor => new(Ordinal, Id);

    public void RefreshLabels() => OnPropertyChanged(nameof(StatusLabel));
}
