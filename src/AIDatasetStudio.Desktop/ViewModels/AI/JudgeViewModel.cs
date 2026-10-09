using System.Collections.ObjectModel;
using System.Globalization;
using AIDatasetStudio.Application.AI;
using AIDatasetStudio.Application.Records;
using AIDatasetStudio.Desktop.Localization;
using AIDatasetStudio.Desktop.Services;
using AIDatasetStudio.Desktop.ViewModels.Documents;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AIDatasetStudio.Desktop.ViewModels.AI;

/// <summary>
/// Judge with AI (SDD §17.7): the default model rates the chosen records one by one from 1 to 5
/// with a reason, and compares each answer with records that ask the same thing. Judgments are
/// kept as they come, so there is nothing to add or lose when the window closes.
/// </summary>
public sealed partial class JudgeViewModel : ViewModelBase, IDisposable
{
    private readonly RecordJudge _judge;
    private readonly IRecordQueryService _queries;
    private readonly AIAssistant _ai;
    private readonly Localizer _localizer;
    private readonly HashSet<Guid> _judged = [];
    private CancellationTokenSource? _run;
    private TaskCompletionSource? _idle;

    public JudgeViewModel(
        RecordJudge judge,
        IRecordQueryService queries,
        AIAssistant ai,
        Localizer localizer,
        QATarget target,
        IReadOnlyList<RecordSummary> records)
    {
        _judge = judge;
        _queries = queries;
        _ai = ai;
        _localizer = localizer;
        Target = target;
        foreach (var record in records)
        {
            Records.Add(new JudgeRecordViewModel(record, localizer, OnRecordsChanged));
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
        ? _localizer.Format("Variations_OneRecord", Records[0].Summary.Ordinal)
        : _localizer.Format("Variations_Records", Records.Count);

    public bool IsSupported => Judging.Supports(Target.Type);

    public ObservableCollection<JudgeRecordViewModel> Records { get; } = [];

    public bool HasRecords => Records.Count > 0;

    public bool HasManyRecords => Records.Count > 1;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    [NotifyCanExecuteChangedFor(nameof(RewriteCommand))]
    public partial JudgeRecordViewModel? SelectedRecord { get; set; }

    public bool HasSelection => SelectedRecord is not null;

    /// <summary>
    /// Whether every record is judged: <c>null</c> when only some are.
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
    /// Whether each answer is also compared with records that ask the same thing.
    /// </summary>
    [ObservableProperty]
    public partial bool Compare { get; set; } = true;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCommand), nameof(StopCommand), nameof(RewriteCommand))]
    public partial bool IsRunning { get; private set; }

    [ObservableProperty]
    public partial string? ProgressText { get; private set; }

    [ObservableProperty]
    public partial double ProgressValue { get; private set; }

    /// <summary>
    /// Why the model could not judge, with a way to the AI connections.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAIError))]
    public partial string? AIError { get; private set; }

    public bool HasAIError => AIError is not null;

    public bool HasConnection => _ai.Connection is not null;

    public string ConnectionText => _ai.Connection is { } connection ? AIText.Describe(connection) : _localizer["Overview_NoAIConnection"];

    /// <summary>
    /// The records judged in this window, for the list to show their scores.
    /// </summary>
    public IReadOnlyCollection<Guid> JudgedIds => _judged;

    /// <summary>
    /// "8 of 12 judged · average 3.8 · 2 low".
    /// </summary>
    public string SummaryText
    {
        get
        {
            var scores = Records.Select(r => r.Score).OfType<int>().ToList();
            return scores.Count == 0
                ? _localizer["Judge_NoneYet"]
                : _localizer.Format(
                    "Judge_Summary",
                    scores.Count,
                    Records.Count,
                    scores.Average().ToString("0.0", CultureInfo.CurrentCulture),
                    scores.Count(s => s <= Judging.LowScore));
        }
    }

    /// <summary>
    /// The current judging, or reading the selected record; awaited by tests.
    /// </summary>
    public Task PendingWork { get; private set; } = Task.CompletedTask;

    private bool CanStart() => !IsRunning && IsSupported && Records.Any(r => r.IsIncluded && r.State is WritingState.Waiting or WritingState.Unreadable);

    /// <summary>
    /// Judges every included record not judged yet as it is now.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanStart))]
    private Task StartAsync() =>
        PendingWork = RunAsync([.. Records.Where(r => r.IsIncluded && r.State is WritingState.Waiting or WritingState.Unreadable)]);

    [RelayCommand(CanExecute = nameof(IsRunning))]
    private void Stop() => _run?.Cancel();

    private bool CanRewrite(JudgeRecordViewModel? record) => !IsRunning && IsSupported && (record ?? SelectedRecord) is not null;

    /// <summary>
    /// Judges one record again.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanRewrite))]
    private Task RewriteAsync(JudgeRecordViewModel? record) =>
        (record ?? SelectedRecord) is { } target ? PendingWork = RunAsync([target]) : Task.CompletedTask;

    /// <summary>
    /// Closes the window; a judging in progress stops, what was judged is kept.
    /// </summary>
    [RelayCommand]
    private void Close()
    {
        _run?.Cancel();
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void OpenAIConnections()
    {
        AIError = null;
        AIConnectionsRequested?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void DismissAIError() => AIError = null;

    partial void OnSelectedRecordChanged(JudgeRecordViewModel? value)
    {
        if (value is { Details: null } && !IsRunning)
        {
            PendingWork = LoadAsync(value);
        }
    }

    // The record and its judgment, read when it is first shown.
    private async Task LoadAsync(JudgeRecordViewModel record)
    {
        try
        {
            record.Details = await _queries.GetAsync(record.Id);
            record.Judgment = await _judge.Judgments.GetAsync(record.Id);
        }
        catch (Exception ex) when (UiErrors.TryDescribe(ex) is { } error)
        {
            AIError = _localizer.Format(error.Key, error.Args);
        }
    }

    private async Task RunAsync(IReadOnlyList<JudgeRecordViewModel> records)
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
        using var cancellation = new CancellationTokenSource();
        _run = cancellation;
        var idle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _idle = idle;
        IsRunning = true;
        var done = 0;
        JudgeRecordViewModel? previous = null;
        try
        {
            foreach (var record in records)
            {
                // The record being judged is shown, unless the user went to read another one.
                if (previous is null || SelectedRecord == previous)
                {
                    SelectedRecord = record;
                }

                previous = record;
                ProgressValue = (double)done / records.Count;
                ProgressText = _localizer.Format("Judge_Progress", done + 1, records.Count, connection.Model);
                await JudgeAsync(record, connection, cancellation.Token);
                done++;
            }

            ProgressText = _localizer.Format("Judge_Finished", records.Count(r => r.State == WritingState.Done));
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            ProgressText = _localizer["Judge_Stopped"];
        }
        catch (AIException ex)
        {
            AIError = _ai.Describe(ex, connection);
            ProgressText = null;
        }
        catch (Exception ex) when (UiErrors.TryDescribe(ex) is { } error)
        {
            AIError = _localizer.Format(error.Key, error.Args);
            ProgressText = null;
        }
        finally
        {
            ProgressValue = 0;
            _run = null;
            IsRunning = false;
            OnRecordsChanged();
            idle.TrySetResult();
        }
    }

    /// <summary>
    /// Ends when no judging is going on. Closing the window stops a run, which then unwinds; scores
    /// kept as it stopped are known only after that.
    /// </summary>
    public Task Idle => _idle?.Task ?? Task.CompletedTask;

    private async Task JudgeAsync(JudgeRecordViewModel record, AIConnection connection, CancellationToken cancellationToken)
    {
        var before = record.State;
        record.State = WritingState.Writing;
        record.RawAnswer = null;
        record.Activity = _localizer.Format("Judge_Judging", connection.Model);
        try
        {
            // Read as saved now: the record may have changed since the list was read.
            var details = record.Details = await _queries.GetAsync(record.Id, cancellationToken);
            if (details is null || !Judging.CanJudge(details.Payload))
            {
                record.State = WritingState.Empty;
                return;
            }

            var outcome = await _judge.JudgeAsync(
                details,
                Target.Guidance,
                Compare,
                connection,
                ordinal => record.Activity = _localizer.Format("Judge_Comparing", ordinal),
                cancellationToken);
            _judged.Add(record.Id);
            record.Judgment = outcome.Details;
            record.RawAnswer = outcome.Verdict is null ? outcome.RawAnswer : null;
            record.State = outcome.Verdict is null ? WritingState.Unreadable : WritingState.Done;
        }
        catch
        {
            // Stopped or failed: what was judged stays (the score may be kept, comparisons cut short).
            record.Judgment = await _judge.Judgments.GetAsync(record.Id, CancellationToken.None);
            if (record.Judgment.IsCurrent)
            {
                _judged.Add(record.Id);
            }

            record.State = record.Judgment.IsCurrent ? WritingState.Done : before;
            throw;
        }
        finally
        {
            record.Activity = null;
            OnRecordsChanged();
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
        OnPropertyChanged(nameof(ConnectionText));
        OnRecordsChanged();
        foreach (var record in Records)
        {
            record.Refresh();
        }
    }

    private void OnRecordsChanged()
    {
        OnPropertyChanged(nameof(SummaryText));
        OnPropertyChanged(nameof(AllIncluded));
        StartCommand.NotifyCanExecuteChanged();
    }
}
