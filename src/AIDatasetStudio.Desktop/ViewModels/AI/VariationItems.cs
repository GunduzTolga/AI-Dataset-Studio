using System.Collections.ObjectModel;
using System.Globalization;
using AIDatasetStudio.Application.AI;
using AIDatasetStudio.Application.Records;
using AIDatasetStudio.Desktop.Localization;
using AIDatasetStudio.Desktop.ViewModels.Documents;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AIDatasetStudio.Desktop.ViewModels.AI;

/// <summary>
/// A record in the list: whether to vary it, and the variants the model wrote of it.
/// </summary>
public sealed partial class VariationRecordViewModel : ViewModelBase
{
    private const int TitleLength = 90;

    private readonly Localizer _localizer;
    private readonly Action _changed;

    public VariationRecordViewModel(RecordDetails record, VariationSource source, Localizer localizer, Action changed)
    {
        Record = record;
        Source = source;
        _localizer = localizer;
        _changed = changed;
        Variants.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(StateText));
            OnPropertyChanged(nameof(HasVariants));
            OnPropertyChanged(nameof(KeepsAnswer));
            _changed();
        };
    }

    public RecordDetails Record { get; }

    /// <summary>
    /// The question the variants ask in other words, and its answer.
    /// </summary>
    public VariationSource Source { get; }

    public ObservableCollection<VariantViewModel> Variants { get; } = [];

    /// <summary>
    /// Whether the model writes variants of the record.
    /// </summary>
    [ObservableProperty]
    public partial bool IsIncluded { get; set; } = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StateText), nameof(IsWriting), nameof(HasProblem))]
    public partial WritingState State { get; set; }

    /// <summary>
    /// The model that wrote the variants, noted in their records' lineage.
    /// </summary>
    public string? Model { get; set; }

    /// <summary>
    /// What the model answered, shown when it could not be read.
    /// </summary>
    [ObservableProperty]
    public partial string? RawAnswer { get; set; }

    public bool HasVariants => Variants.Count > 0;

    public bool IsWriting => State == WritingState.Writing;

    public bool HasProblem => State == WritingState.Unreadable;

    /// <summary>
    /// "#12".
    /// </summary>
    public string Number => "#" + Record.Ordinal.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// The question on one line, cut short.
    /// </summary>
    public string Title
    {
        get
        {
            var line = string.Join(' ', Source.Question.Split((char[])['\n', '\r', '\t'], StringSplitOptions.RemoveEmptyEntries));
            return line.Length <= TitleLength ? line : line[..TitleLength].TrimEnd() + "…";
        }
    }

    public bool HasContext => Source.Context is not null;

    public bool HasAnswer => Source.Answer.Length > 0;

    /// <summary>
    /// The variants keep the record's answer: none tells it in other words.
    /// </summary>
    public bool KeepsAnswer => HasAnswer && HasVariants && !Variants.Any(v => v.HasOwnAnswer);

    public string StateText => State switch
    {
        WritingState.Writing => _localizer["QA_StateWriting"],
        WritingState.Done => _localizer.Format("Variations_StateCount", Variants.Count),
        WritingState.Empty => _localizer["Variations_StateEmpty"],
        WritingState.Unreadable => _localizer["QA_StateUnreadable"],
        _ => string.Empty,
    };

    /// <summary>
    /// The variants that will be added: kept, and asking something in other words.
    /// </summary>
    public IEnumerable<VariantViewModel> KeptVariants => Variants.Where(v => v.IsKept && v.IsComplete);

    /// <summary>
    /// Adds what the model wrote, unless it asks exactly what the record or an earlier variant asks.
    /// </summary>
    public bool Add(QAPair variation)
    {
        if (Variations.AreSame(Source.Question, variation.Question) || Variants.Any(v => Variations.AreSame(v.Question, variation.Question)))
        {
            return false;
        }

        Variants.Add(new VariantViewModel(variation, Source.Question, _changed, v => Variants.Remove(v)));
        return true;
    }

    public void Refresh() => OnPropertyChanged(nameof(StateText));

    partial void OnIsIncludedChanged(bool value) => _changed();
}

/// <summary>
/// A question asked in other words (and, when the model was asked to, its answer told in other
/// words); both can be edited and the variant left out. One that changes little is left out at first.
/// </summary>
public sealed partial class VariantViewModel : ViewModelBase
{
    private readonly string _original;
    private readonly Action _changed;
    private readonly Action<VariantViewModel> _remove;

    /// <param name="original">The question the variant asks in other words.</param>
    public VariantViewModel(QAPair variation, string original, Action changed, Action<VariantViewModel> remove)
    {
        _original = original;
        _changed = changed;
        _remove = remove;
        HasOwnAnswer = variation.Answer.Length > 0;
        Question = variation.Question;
        Answer = variation.Answer;
        IsKept = !IsTooSimilar;
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsComplete), nameof(IsSame), nameof(IsTooSimilar))]
    public partial string Question { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsComplete))]
    public partial string Answer { get; set; }

    [ObservableProperty]
    public partial bool IsKept { get; set; }

    /// <summary>
    /// Whether the model told the answer in other words too; otherwise the record's answer is kept.
    /// </summary>
    public bool HasOwnAnswer { get; }

    /// <summary>
    /// The variant asks exactly what the record asks; it would add nothing.
    /// </summary>
    public bool IsSame => Variations.AreSame(_original, Question);

    /// <summary>
    /// The variant changes little (<see cref="Variations.TooSimilar"/>).
    /// </summary>
    public bool IsTooSimilar => !IsSame && Variations.Similarity(_original, Question) >= Variations.TooSimilar;

    public bool IsComplete => !string.IsNullOrWhiteSpace(Question) && !IsSame && (!HasOwnAnswer || !string.IsNullOrWhiteSpace(Answer));

    public QAPair ToPair() => new(Question.Trim(), HasOwnAnswer ? Answer.Trim() : string.Empty);

    [RelayCommand]
    private void Remove() => _remove(this);

    partial void OnIsKeptChanged(bool value) => _changed();

    partial void OnQuestionChanged(string value) => _changed();

    partial void OnAnswerChanged(string value) => _changed();

    /// <summary>
    /// For tests and logs.
    /// </summary>
    public override string ToString() => HasOwnAnswer ? $"{Question} → {Answer}" : Question;
}
