using System.Collections.ObjectModel;
using AIDatasetStudio.Application.AI;
using AIDatasetStudio.Application.Analysis;
using AIDatasetStudio.Application.Documents;
using AIDatasetStudio.Application.Import;
using AIDatasetStudio.Application.Records;
using AIDatasetStudio.Desktop.Localization;
using AIDatasetStudio.Desktop.Services;
using AIDatasetStudio.Desktop.ViewModels.AI;
using AIDatasetStudio.Desktop.ViewModels.Import;
using AIDatasetStudio.Domain.Enums;
using AIDatasetStudio.Domain.ValueObjects;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AIDatasetStudio.Desktop.ViewModels.Documents;

/// <summary>
/// Extract Q&amp;A from a document (SDD §17.5): the document is split into passages, the default
/// model writes the questions each passage answers, the user reviews them (edit, leave out, write
/// a passage again) and adds the kept pairs to the dataset as draft records that remember their
/// passage. Adding is one step in the undo history.
/// </summary>
public sealed partial class QAExtractionViewModel : ViewModelBase, IDisposable
{
    private readonly IDocumentReader _documents;
    private readonly ITokenCounter _tokens;
    private readonly AIAssistant _ai;
    private readonly RecordOperations _operations;
    private readonly IDialogService _dialogs;
    private readonly Localizer _localizer;
    private CancellationTokenSource? _run;
    private bool _closing;

    public QAExtractionViewModel(
        IDocumentReader documents,
        ITokenCounter tokens,
        AIAssistant ai,
        RecordOperations operations,
        IDialogService dialogs,
        Localizer localizer,
        string filePath,
        QATarget target)
    {
        _documents = documents;
        _tokens = tokens;
        _ai = ai;
        _operations = operations;
        _dialogs = dialogs;
        _localizer = localizer;
        FilePath = filePath;
        Target = target;
        _ai.ConnectionChanged += OnConnectionChanged;
        _localizer.CultureChanged += OnCultureChanged;
    }

    /// <summary>
    /// Raised when the window should close.
    /// </summary>
    public event EventHandler? CloseRequested;

    /// <summary>
    /// Raised when the user wants to set up an AI connection.
    /// </summary>
    public event EventHandler? AIConnectionsRequested;

    public string FilePath { get; }

    public string FileName => Path.GetFileName(FilePath);

    public QATarget Target { get; }

    /// <summary>
    /// "Into “Destek SSS” (Instruction)".
    /// </summary>
    public string TargetText => _localizer.Format("Import_Target", Target.Name, Labels.For(_localizer, Target.Type));

    public bool IsSupported => QAExtraction.Supports(Target.Type);

    /// <summary>
    /// Preference pairs get their chosen answer; the rejected one is written later.
    /// </summary>
    public bool IsPreference => Target.Type == DatasetType.Preference;

    /// <summary>
    /// The document; <c>null</c> until it is read, or when it cannot be.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DocumentText), nameof(HasDocument), nameof(IsEmptyDocument))]
    public partial SourceDocument? Document { get; private set; }

    public bool HasDocument => Document is not null;

    public bool IsEmptyDocument => Document is not null && Chunks.Count == 0;

    [ObservableProperty]
    public partial bool IsReading { get; private set; }

    /// <summary>
    /// Why the document cannot be read.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? ErrorMessage { get; private set; }

    public bool HasError => ErrorMessage is not null;

    /// <summary>
    /// Why the pairs could not be added (the workspace is gone, the disk is full…).
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAddError))]
    public partial string? AddError { get; private set; }

    public bool HasAddError => AddError is not null;

    /// <summary>
    /// "12 pages · 24 passages".
    /// </summary>
    public string DocumentText => Document is { } document
        ? document.PageCount > 0
            ? _localizer.Format("QA_DocumentPages", document.PageCount, Chunks.Count)
            : _localizer.Format("QA_DocumentPassages", Chunks.Count)
        : string.Empty;

    /// <summary>
    /// The longest passage in tokens; the document is split again when it changes, as long as no pair was written.
    /// </summary>
    [ObservableProperty]
    public partial decimal? ChunkTokens { get; set; } = DocumentChunker.DefaultTokens;

    /// <summary>
    /// The most pairs the model writes from one passage.
    /// </summary>
    [ObservableProperty]
    public partial decimal? QuestionsPerChunk { get; set; } = QAExtraction.DefaultQuestions;

    public ObservableCollection<QAChunkViewModel> Chunks { get; } = [];

    public bool HasChunks => Chunks.Count > 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    [NotifyCanExecuteChangedFor(nameof(RewriteCommand))]
    public partial QAChunkViewModel? SelectedChunk { get; set; }

    public bool HasSelection => SelectedChunk is not null;

    /// <summary>
    /// Whether every passage is used: <c>null</c> when only some are.
    /// </summary>
    public bool? AllIncluded
    {
        get => Chunks.All(c => c.IsIncluded) ? true : Chunks.Any(c => c.IsIncluded) ? null : false;
        set
        {
            var include = value != false;
            foreach (var chunk in Chunks)
            {
                chunk.IsIncluded = include;
            }
        }
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanChangeChunks))]
    [NotifyCanExecuteChangedFor(nameof(StartCommand), nameof(StopCommand), nameof(RewriteCommand), nameof(AddCommand))]
    public partial bool IsRunning { get; private set; }

    [ObservableProperty]
    public partial string? ProgressText { get; private set; }

    [ObservableProperty]
    public partial double ProgressValue { get; private set; }

    /// <summary>
    /// Why the model could not write, with a way to the AI connections.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAIError))]
    public partial string? AIError { get; private set; }

    public bool HasAIError => AIError is not null;

    public bool HasConnection => _ai.Connection is not null;

    /// <summary>
    /// "qwen2.5:7b · Ollama", or that there is no connection.
    /// </summary>
    public string ConnectionText => _ai.Connection is { } connection ? AIText.Describe(connection) : _localizer["Overview_NoAIConnection"];

    /// <summary>
    /// The passages can be split differently until the model has written from them.
    /// </summary>
    public bool CanChangeChunks => !IsRunning && !Chunks.Any(c => c.Pairs.Count > 0);

    /// <summary>
    /// The pairs that will be added.
    /// </summary>
    public int KeptCount => Chunks.Sum(c => c.KeptPairs.Count());

    public int PairCount => Chunks.Sum(c => c.Pairs.Count);

    public string AddText => _localizer.Format("QA_Add", KeptCount);

    /// <summary>
    /// "12 of 15 pairs will be added".
    /// </summary>
    public string SummaryText => PairCount == 0 ? _localizer["QA_NoPairsYet"] : _localizer.Format("QA_Summary", KeptCount, PairCount);

    /// <summary>
    /// The records added; <c>null</c> until the pairs are added.
    /// </summary>
    public IReadOnlyList<Guid>? AddedIds { get; private set; }

    /// <summary>
    /// The current reading or writing; awaited by tests.
    /// </summary>
    public Task PendingWork { get; private set; } = Task.CompletedTask;

    /// <summary>
    /// Reads the document and splits it into passages.
    /// </summary>
    public Task InitializeAsync() => PendingWork = ReadAsync();

    private bool CanStart() => !IsRunning && IsSupported && Chunks.Any(c => c.IsIncluded && c.State is WritingState.Waiting or WritingState.Unreadable);

    /// <summary>
    /// Has the model write from every included passage not written yet.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanStart))]
    private Task StartAsync() =>
        PendingWork = RunAsync([.. Chunks.Where(c => c.IsIncluded && c.State is WritingState.Waiting or WritingState.Unreadable)]);

    [RelayCommand(CanExecute = nameof(IsRunning))]
    private void Stop() => _run?.Cancel();

    private bool CanRewrite(QAChunkViewModel? chunk) => !IsRunning && IsSupported && (chunk ?? SelectedChunk) is not null;

    /// <summary>
    /// Writes the pairs of one passage again (its current ones are replaced).
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanRewrite))]
    private Task RewriteAsync(QAChunkViewModel? chunk) =>
        (chunk ?? SelectedChunk) is { } target ? PendingWork = RunAsync([target]) : Task.CompletedTask;

    private bool CanAdd() => !IsRunning && KeptCount > 0;

    /// <summary>
    /// Adds the kept pairs as draft records and closes.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanAdd))]
    private async Task AddAsync()
    {
        if (Document is not { } document)
        {
            return;
        }

        var records = Chunks
            .SelectMany(chunk => chunk.KeptPairs.Select(pair => new NewRecord(
                QAExtraction.Payload(Target.Type, pair.ToPair()),
                new RecordLineage(RecordSourceKind.DocumentChunk, document.Reference(chunk.Chunk.First), GeneratedByModel: chunk.Model),
                PassageSource.From(document, chunk.Chunk).ToMetadata())))
            .ToList();
        AddError = null;
        try
        {
            AddedIds = await _operations.AddAsync(Target.DatasetId, records, document.FileName);
            _closing = true;
            CloseRequested?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex) when (UiErrors.TryDescribe(ex) is { } error)
        {
            AddError = _localizer.Format(error.Key, error.Args);
        }
    }

    /// <summary>
    /// Closes the window; pairs not added are dropped once the user agrees.
    /// </summary>
    [RelayCommand]
    private async Task CloseAsync()
    {
        if (!await CanCloseAsync())
        {
            return;
        }

        _closing = true;
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Whether the window may close now: nothing is lost, or the user agreed to drop the pairs.
    /// Stops the model first.
    /// </summary>
    public async Task<bool> CanCloseAsync()
    {
        if (_closing)
        {
            return true;
        }

        if (KeptCount > 0 && !await _dialogs.ConfirmAsync(
                _localizer["QA_DiscardTitle"], _localizer.Format("QA_DiscardMessage", KeptCount), _localizer["QA_Discard"], isDestructive: true))
        {
            return false;
        }

        _run?.Cancel();
        _closing = true;
        return true;
    }

    [RelayCommand]
    private void OpenAIConnections()
    {
        AIError = null;
        AIConnectionsRequested?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void DismissAIError() => AIError = null;

    partial void OnChunkTokensChanged(decimal? value)
    {
        if (value is not null && Document is not null && CanChangeChunks)
        {
            Split();
        }
    }

    private async Task ReadAsync()
    {
        IsReading = true;
        try
        {
            Document = await _documents.ReadAsync(FilePath);
            Split();
        }
        catch (ImportFileException ex)
        {
            ErrorMessage = ImportText.FileError(_localizer, ex);
        }
        catch (Exception ex) when (UiErrors.TryDescribe(ex) is { } error)
        {
            ErrorMessage = _localizer.Format(error.Key, error.Args);
        }
        finally
        {
            IsReading = false;
        }
    }

    private void Split()
    {
        var target = (int)Math.Clamp(ChunkTokens ?? DocumentChunker.DefaultTokens, DocumentChunker.MinTokens, DocumentChunker.MaxTokens);
        var chunks = DocumentChunker.Split(Document!.Blocks, target, _tokens.CountTokens);

        // A heading above every passage is the document's title; the list leaves it out.
        var first = chunks.Select(c => c.Section.Split(DocumentChunker.SectionSeparator)[0]).Distinct().ToList();
        var common = chunks.Count > 1 && first is [{ Length: > 0 } title] ? title : null;
        Chunks.Clear();
        foreach (var chunk in chunks)
        {
            Chunks.Add(new QAChunkViewModel(chunk, Document, _localizer, OnPairsChanged, common));
        }

        SelectedChunk = Chunks.FirstOrDefault();
        OnPropertyChanged(nameof(DocumentText));
        OnPropertyChanged(nameof(IsEmptyDocument));
        OnPropertyChanged(nameof(HasChunks));
        OnPairsChanged();
    }

    private async Task RunAsync(IReadOnlyList<QAChunkViewModel> chunks)
    {
        if (chunks.Count == 0 || Document is not { } document)
        {
            return;
        }

        if (_ai.Connection is not { } connection)
        {
            AIError = _localizer["Editor_NoAIConnection"];
            return;
        }

        AIError = null;
        var questions = (int)Math.Clamp(QuestionsPerChunk ?? QAExtraction.DefaultQuestions, 1, QAExtraction.MaxQuestions);
        using var cancellation = new CancellationTokenSource();
        _run = cancellation;
        IsRunning = true;
        var written = 0;
        QAChunkViewModel? previous = null;
        try
        {
            foreach (var chunk in chunks)
            {
                // The passage being written is shown, unless the user went to read another one.
                if (previous is null || SelectedChunk == previous)
                {
                    SelectedChunk = chunk;
                }

                previous = chunk;

                ProgressValue = (double)written / chunks.Count;
                ProgressText = _localizer.Format("QA_Progress", written + 1, chunks.Count, connection.Model);
                await WriteAsync(chunk, document, connection, questions, cancellation.Token);
                written++;
            }

            ProgressText = _localizer.Format("QA_Finished", PairCount);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            ProgressText = _localizer["QA_Stopped"];
        }
        catch (AIException ex)
        {
            AIError = _ai.Describe(ex, connection);
            ProgressText = null;
        }
        finally
        {
            ProgressValue = 0;
            _run = null;
            IsRunning = false;
            OnPairsChanged();
        }
    }

    private async Task WriteAsync(QAChunkViewModel chunk, SourceDocument document, AIConnection connection, int questions, CancellationToken cancellationToken)
    {
        chunk.Pairs.Clear();
        chunk.RawAnswer = null;
        chunk.Model = connection.Model;
        chunk.State = WritingState.Writing;
        var reader = new QAPairReader();
        try
        {
            var messages = QAExtraction.Prompt(document.FileName, chunk.Chunk, questions, Target.Guidance);
            await foreach (var piece in _ai.WriteAsync(connection, messages, cancellationToken))
            {
                foreach (var pair in reader.Push(piece))
                {
                    chunk.Add(pair);
                }
            }

            foreach (var pair in reader.Complete())
            {
                chunk.Add(pair);
            }

            chunk.RawAnswer = reader.Text.Trim();
            chunk.State = chunk.Pairs.Count > 0 ? WritingState.Done
                : IsEmptyList(reader.Text) ? WritingState.Empty
                : WritingState.Unreadable;
        }
        catch
        {
            // Stopped or failed: what was written is kept.
            chunk.State = chunk.Pairs.Count > 0 ? WritingState.Done : WritingState.Waiting;
            throw;
        }
    }

    /// <summary>
    /// Stops the model and lets go of the application's events once the window is closed.
    /// </summary>
    public void Dispose()
    {
        _run?.Cancel();
        _ai.ConnectionChanged -= OnConnectionChanged;
        _localizer.CultureChanged -= OnCultureChanged;
    }

    private void OnConnectionChanged(object? sender, EventArgs e)
    {
        OnPropertyChanged(nameof(ConnectionText));
        OnPropertyChanged(nameof(HasConnection));
    }

    private void OnCultureChanged(object? sender, EventArgs e)
    {
        OnPropertyChanged(nameof(TargetText));
        OnPropertyChanged(nameof(DocumentText));
        OnPropertyChanged(nameof(ConnectionText));
        OnPairsChanged();
        foreach (var chunk in Chunks)
        {
            chunk.Refresh();
        }
    }

    // "[]", possibly in a code fence: the model found nothing to ask.
    private static bool IsEmptyList(string answer) => string.Concat(answer.Where(c => !char.IsWhiteSpace(c))).Contains("[]", StringComparison.Ordinal);

    private void OnPairsChanged()
    {
        OnPropertyChanged(nameof(KeptCount));
        OnPropertyChanged(nameof(PairCount));
        OnPropertyChanged(nameof(AddText));
        OnPropertyChanged(nameof(SummaryText));
        OnPropertyChanged(nameof(CanChangeChunks));
        OnPropertyChanged(nameof(AllIncluded));
        AddCommand.NotifyCanExecuteChanged();
        StartCommand.NotifyCanExecuteChanged();
    }
}
