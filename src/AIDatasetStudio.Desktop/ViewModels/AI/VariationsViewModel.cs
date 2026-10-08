using System.Collections.ObjectModel;
using AIDatasetStudio.Application.AI;
using AIDatasetStudio.Application.Records;
using AIDatasetStudio.Desktop.Localization;
using AIDatasetStudio.Desktop.Services;
using AIDatasetStudio.Desktop.ViewModels.Documents;
using AIDatasetStudio.Domain.Enums;
using AIDatasetStudio.Domain.ValueObjects;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AIDatasetStudio.Desktop.ViewModels.AI;

/// <summary>
/// Generate variations (SDD §17.6): the default model asks the question of each chosen record in
/// other words (and, if asked, tells the answer in other words too), the user reviews the variants
/// (edit, leave out, write a record's again) and adds the kept ones as draft records of the
/// record's family. Adding is one step in the undo history.
/// </summary>
public sealed partial class VariationsViewModel : ViewModelBase, IDisposable
{
    private readonly AIAssistant _ai;
    private readonly RecordOperations _operations;
    private readonly IDialogService _dialogs;
    private readonly Localizer _localizer;
    private CancellationTokenSource? _run;
    private bool _closing;

    /// <param name="records">The records to vary; those without a question are left out.</param>
    public VariationsViewModel(
        AIAssistant ai,
        RecordOperations operations,
        IDialogService dialogs,
        Localizer localizer,
        QATarget target,
        IReadOnlyList<RecordDetails> records)
    {
        _ai = ai;
        _operations = operations;
        _dialogs = dialogs;
        _localizer = localizer;
        Target = target;
        foreach (var record in records)
        {
            if (Variations.SourceOf(record.Payload) is { } source)
            {
                Records.Add(new VariationRecordViewModel(record, source, localizer, OnVariantsChanged));
            }
            else
            {
                SkippedCount++;
            }
        }

        SelectedRecord = Records.FirstOrDefault();
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

    public QATarget Target { get; }

    /// <summary>
    /// "Into “Destek SSS” (Instruction)".
    /// </summary>
    public string TargetText => _localizer.Format("Import_Target", Target.Name, Labels.For(_localizer, Target.Type));

    /// <summary>
    /// "Record #12" or "3 records".
    /// </summary>
    public string RecordsText => Records.Count == 1
        ? _localizer.Format("Variations_OneRecord", Records[0].Record.Ordinal)
        : _localizer.Format("Variations_Records", Records.Count);

    public bool IsSupported => Variations.Supports(Target.Type);

    public ObservableCollection<VariationRecordViewModel> Records { get; } = [];

    public bool HasRecords => Records.Count > 0;

    /// <summary>
    /// With one record the list is left out.
    /// </summary>
    public bool HasManyRecords => Records.Count > 1;

    /// <summary>
    /// Chosen records without a question yet.
    /// </summary>
    public int SkippedCount { get; }

    public bool HasSkipped => SkippedCount > 0 && HasRecords;

    public string SkippedText => _localizer.Format("Variations_Skipped", SkippedCount);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    [NotifyCanExecuteChangedFor(nameof(RewriteCommand))]
    public partial VariationRecordViewModel? SelectedRecord { get; set; }

    public bool HasSelection => SelectedRecord is not null;

    /// <summary>
    /// Whether every record is varied: <c>null</c> when only some are.
    /// </summary>
    public bool? AllIncluded
    {
        get => Records.All(r => r.IsIncluded) ? true : Records.Any(r => r.IsIncluded) ? null : false;
        set
        {
            var include = value != false;
            foreach (var record in Records)
            {
                record.IsIncluded = include;
            }
        }
    }

    /// <summary>
    /// How many variants the model writes of each record.
    /// </summary>
    [ObservableProperty]
    public partial decimal? Count { get; set; } = Variations.DefaultCount;

    /// <summary>
    /// Whether the model tells the answers in other words too.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ModeIndex))]
    public partial bool RewordsAnswers { get; set; }

    /// <summary>
    /// What is reworded, for the list: 0 the question, 1 the question and the answer.
    /// </summary>
    public int ModeIndex
    {
        get => RewordsAnswers ? 1 : 0;
        set => RewordsAnswers = value == 1;
    }

    /// <summary>
    /// Whether some questions read like quick chat messages, with typos.
    /// </summary>
    [ObservableProperty]
    public partial bool IsCasual { get; set; }

    public VariationOptions Options => new(
        (int)Math.Clamp(Count ?? Variations.DefaultCount, 1, Variations.MaxCount),
        RewordsAnswers ? VariationMode.QuestionsAndAnswers : VariationMode.Questions,
        IsCasual);

    [ObservableProperty]
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

    /// <summary>
    /// Why the variants could not be added (the workspace is gone, the disk is full…).
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAddError))]
    public partial string? AddError { get; private set; }

    public bool HasAddError => AddError is not null;

    public bool HasConnection => _ai.Connection is not null;

    /// <summary>
    /// "qwen2.5:7b · Ollama", or that there is no connection.
    /// </summary>
    public string ConnectionText => _ai.Connection is { } connection ? AIText.Describe(connection) : _localizer["Overview_NoAIConnection"];

    /// <summary>
    /// The variants that will be added.
    /// </summary>
    public int KeptCount => Records.Sum(r => r.KeptVariants.Count());

    public int VariantCount => Records.Sum(r => r.Variants.Count);

    public string AddText => _localizer.Format("QA_Add", KeptCount);

    /// <summary>
    /// "7 of 9 variations will be added".
    /// </summary>
    public string SummaryText => VariantCount == 0 ? _localizer["Variations_NoneYet"] : _localizer.Format("Variations_Summary", KeptCount, VariantCount);

    /// <summary>
    /// The records added; <c>null</c> until the variants are added.
    /// </summary>
    public IReadOnlyList<Guid>? AddedIds { get; private set; }

    /// <summary>
    /// The current writing; awaited by tests.
    /// </summary>
    public Task PendingWork { get; private set; } = Task.CompletedTask;

    private bool CanStart() => !IsRunning && IsSupported && Records.Any(r => r.IsIncluded && r.State is WritingState.Waiting or WritingState.Unreadable);

    /// <summary>
    /// Has the model write variants of every included record not written yet.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanStart))]
    private Task StartAsync() =>
        PendingWork = RunAsync([.. Records.Where(r => r.IsIncluded && r.State is WritingState.Waiting or WritingState.Unreadable)]);

    [RelayCommand(CanExecute = nameof(IsRunning))]
    private void Stop() => _run?.Cancel();

    private bool CanRewrite(VariationRecordViewModel? record) => !IsRunning && IsSupported && (record ?? SelectedRecord) is not null;

    /// <summary>
    /// Writes the variants of one record again (its current ones are replaced).
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanRewrite))]
    private Task RewriteAsync(VariationRecordViewModel? record) =>
        (record ?? SelectedRecord) is { } target ? PendingWork = RunAsync([target]) : Task.CompletedTask;

    private bool CanAdd() => !IsRunning && KeptCount > 0;

    /// <summary>
    /// Adds the kept variants as draft records and closes.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanAdd))]
    private async Task AddAsync()
    {
        var records = Records.SelectMany(record => record.KeptVariants.Select(variant => ToRecord(record, variant))).ToList();
        AddError = null;
        try
        {
            AddedIds = await _operations.AddAsync(Target.DatasetId, records, _localizer["Variations_History"]);
            _closing = true;
            CloseRequested?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex) when (UiErrors.TryDescribe(ex) is { } error)
        {
            AddError = _localizer.Format(error.Key, error.Args);
        }
    }

    /// <summary>
    /// Closes the window; variants not added are dropped once the user agrees.
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
    /// Whether the window may close now: nothing is lost, or the user agreed to drop the variants.
    /// Stops the model first.
    /// </summary>
    public async Task<bool> CanCloseAsync()
    {
        if (_closing)
        {
            return true;
        }

        if (KeptCount > 0 && !await _dialogs.ConfirmAsync(
                _localizer["Variations_DiscardTitle"], _localizer.Format("Variations_DiscardMessage", KeptCount), _localizer["QA_Discard"], isDestructive: true))
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

    // The variant becomes a record of the family of the record it varies: same input, system
    // prompt, earlier turns and rejected answer, and the passage the record came from.
    private static NewRecord ToRecord(VariationRecordViewModel record, VariantViewModel variant)
    {
        var variation = variant.ToPair();
        var metadata = VariantOf.For(record.Record);
        var family = VariantOf.FromMetadata(metadata)!;
        return new NewRecord(
            Variations.Payload(record.Record.Payload, variation, variant.HasOwnAnswer ? VariationMode.QuestionsAndAnswers : VariationMode.Questions),
            new RecordLineage(RecordSourceKind.Synthetic, "#" + family.Ordinal.ToString(System.Globalization.CultureInfo.InvariantCulture), GeneratedByModel: record.Model),
            metadata);
    }

    private async Task RunAsync(IReadOnlyList<VariationRecordViewModel> records)
    {
        if (records.Count == 0)
        {
            return;
        }

        if (_ai.Connection is not { } connection)
        {
            AIError = _localizer["Editor_NoAIConnection"];
            return;
        }

        AIError = null;
        var options = Options;
        using var cancellation = new CancellationTokenSource();
        _run = cancellation;
        IsRunning = true;
        var written = 0;
        VariationRecordViewModel? previous = null;
        try
        {
            foreach (var record in records)
            {
                // The record being written is shown, unless the user went to read another one.
                if (previous is null || SelectedRecord == previous)
                {
                    SelectedRecord = record;
                }

                previous = record;

                ProgressValue = (double)written / records.Count;
                ProgressText = _localizer.Format("Variations_Progress", written + 1, records.Count, connection.Model);
                await WriteAsync(record, connection, options, cancellation.Token);
                written++;
            }

            ProgressText = _localizer.Format("Variations_Finished", VariantCount);
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
            OnVariantsChanged();
        }
    }

    private async Task WriteAsync(VariationRecordViewModel record, AIConnection connection, VariationOptions options, CancellationToken cancellationToken)
    {
        record.Variants.Clear();
        record.RawAnswer = null;
        record.Model = connection.Model;
        record.State = WritingState.Writing;
        var reader = new QAPairReader(questionsOnly: true);
        void Add(QAPair variation)
        {
            // A model may write more than asked, or answers it was not asked for.
            if (record.Variants.Count < options.Count)
            {
                record.Add(options.Mode == VariationMode.QuestionsAndAnswers ? variation : variation with { Answer = string.Empty });
            }
        }

        try
        {
            var messages = Variations.Prompt(record.Source, options, Target.Guidance);
            await foreach (var piece in _ai.WriteAsync(connection, messages, cancellationToken))
            {
                foreach (var variation in reader.Push(piece))
                {
                    Add(variation);
                }
            }

            foreach (var variation in reader.Complete())
            {
                Add(variation);
            }

            record.RawAnswer = reader.Text.Trim();
            record.State = record.Variants.Count > 0 ? WritingState.Done
                : reader.Pairs.Count > 0 || IsEmptyList(reader.Text) ? WritingState.Empty
                : WritingState.Unreadable;
        }
        catch
        {
            // Stopped or failed: what was written is kept.
            record.State = record.Variants.Count > 0 ? WritingState.Done : WritingState.Waiting;
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
        OnPropertyChanged(nameof(RecordsText));
        OnPropertyChanged(nameof(SkippedText));
        OnPropertyChanged(nameof(ConnectionText));
        OnVariantsChanged();
        foreach (var record in Records)
        {
            record.Refresh();
        }
    }

    // "[]", possibly in a code fence: the model found nothing to ask.
    private static bool IsEmptyList(string answer) => string.Concat(answer.Where(c => !char.IsWhiteSpace(c))).Contains("[]", StringComparison.Ordinal);

    private void OnVariantsChanged()
    {
        OnPropertyChanged(nameof(KeptCount));
        OnPropertyChanged(nameof(VariantCount));
        OnPropertyChanged(nameof(AddText));
        OnPropertyChanged(nameof(SummaryText));
        OnPropertyChanged(nameof(AllIncluded));
        AddCommand.NotifyCanExecuteChanged();
        StartCommand.NotifyCanExecuteChanged();
    }
}
