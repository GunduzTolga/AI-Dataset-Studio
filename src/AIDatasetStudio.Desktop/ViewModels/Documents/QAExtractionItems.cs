using System.Collections.ObjectModel;
using System.Globalization;
using AIDatasetStudio.Application.AI;
using AIDatasetStudio.Application.Documents;
using AIDatasetStudio.Desktop.Localization;
using AIDatasetStudio.Domain.Enums;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AIDatasetStudio.Desktop.ViewModels.Documents;

/// <summary>
/// The dataset that receives the pairs, with its instructions for the model.
/// </summary>
public sealed record QATarget(Guid DatasetId, string Name, DatasetType Type, string? Guidance);

public enum ChunkState
{
    /// <summary>Not written yet, or stopped before any pair.</summary>
    Waiting,
    Writing,

    /// <summary>The model wrote pairs.</summary>
    Done,

    /// <summary>The model found nothing to ask (an empty list).</summary>
    Empty,

    /// <summary>The model's answer had no pairs that could be read.</summary>
    Unreadable,
}

/// <summary>
/// A passage of the document in the list: whether to use it, and the pairs the model wrote from it.
/// </summary>
public sealed partial class QAChunkViewModel : ViewModelBase
{
    private const int TitleWords = 8;

    private readonly Localizer _localizer;
    private readonly Action _changed;

    private readonly string? _commonHeading;

    /// <param name="commonHeading">The heading above every passage (the document's title), left out of <see cref="Title"/>.</param>
    public QAChunkViewModel(DocumentChunk chunk, SourceDocument document, Localizer localizer, Action changed, string? commonHeading = null)
    {
        Chunk = chunk;
        Document = document;
        _commonHeading = commonHeading;
        _localizer = localizer;
        _changed = changed;
        Pairs.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(StateText));
            OnPropertyChanged(nameof(HasPairs));
            _changed();
        };
    }

    public DocumentChunk Chunk { get; }

    public SourceDocument Document { get; }

    public ObservableCollection<QAPairViewModel> Pairs { get; } = [];

    /// <summary>
    /// Whether the passage is sent to the model.
    /// </summary>
    [ObservableProperty]
    public partial bool IsIncluded { get; set; } = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StateText), nameof(IsWriting), nameof(HasProblem))]
    public partial ChunkState State { get; set; }

    /// <summary>
    /// The model that wrote the pairs, noted in their records' lineage.
    /// </summary>
    public string? Model { get; set; }

    /// <summary>
    /// What the model answered, shown when it could not be read.
    /// </summary>
    [ObservableProperty]
    public partial string? RawAnswer { get; set; }

    public bool HasPairs => Pairs.Count > 0;

    public bool IsWriting => State == ChunkState.Writing;

    public bool HasProblem => State == ChunkState.Unreadable;

    /// <summary>
    /// The headings above the passage without the one above all passages, or its first words when it has none.
    /// </summary>
    public string Title => Chunk.Section.Length == 0 ? FirstWords(Chunk.Text)
        : _commonHeading is { } common && Chunk.Section.StartsWith(common + DocumentChunker.SectionSeparator, StringComparison.Ordinal)
            ? Chunk.Section[(common.Length + DocumentChunker.SectionSeparator.Length)..]
            : Chunk.Section;

    /// <summary>
    /// All the headings above the passage, for the passage's own header.
    /// </summary>
    public string FullTitle => Chunk.Section.Length > 0 ? Chunk.Section : Title;

    /// <summary>
    /// "#3 · page 4 · 640 tokens".
    /// </summary>
    public string Detail => _localizer.Format(
        "QA_ChunkDetail",
        Chunk.Number,
        SourceText.Location(_localizer, Document.PositionUnit, Chunk.First, Chunk.Last),
        Chunk.Tokens);

    public string StateText => State switch
    {
        ChunkState.Writing => _localizer["QA_StateWriting"],
        ChunkState.Done => _localizer.Format("QA_StatePairs", Pairs.Count),
        ChunkState.Empty => _localizer["QA_StateEmpty"],
        ChunkState.Unreadable => _localizer["QA_StateUnreadable"],
        _ => string.Empty,
    };

    /// <summary>
    /// The pairs that will be added: kept and with both a question and an answer.
    /// </summary>
    public IEnumerable<QAPairViewModel> KeptPairs => Pairs.Where(p => p.IsKept && p.IsComplete);

    public void Add(QAPair pair) => Pairs.Add(new QAPairViewModel(pair, _changed, p => Pairs.Remove(p)));

    public void Refresh()
    {
        OnPropertyChanged(nameof(Detail));
        OnPropertyChanged(nameof(StateText));
    }

    partial void OnIsIncludedChanged(bool value) => _changed();

    private static string FirstWords(string text)
    {
        var words = text.Replace('\n', ' ').TrimStart('#', ' ', '-').Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return words.Length <= TitleWords ? string.Join(' ', words) : string.Join(' ', words.Take(TitleWords)) + "…";
    }
}

/// <summary>
/// A question and its answer as the model wrote them; both can be edited, and the pair left out.
/// </summary>
public sealed partial class QAPairViewModel(QAPair pair, Action changed, Action<QAPairViewModel> remove) : ViewModelBase
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsComplete))]
    public partial string Question { get; set; } = pair.Question;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsComplete))]
    public partial string Answer { get; set; } = pair.Answer;

    [ObservableProperty]
    public partial bool IsKept { get; set; } = true;

    public bool IsComplete => !string.IsNullOrWhiteSpace(Question) && !string.IsNullOrWhiteSpace(Answer);

    public QAPair ToPair() => new(Question.Trim(), Answer.Trim());

    [RelayCommand]
    private void Remove() => remove(this);

    partial void OnIsKeptChanged(bool value) => changed();

    partial void OnQuestionChanged(string value) => changed();

    partial void OnAnswerChanged(string value) => changed();

    /// <summary>
    /// For tests and logs.
    /// </summary>
    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"{Question} → {Answer}");
}
