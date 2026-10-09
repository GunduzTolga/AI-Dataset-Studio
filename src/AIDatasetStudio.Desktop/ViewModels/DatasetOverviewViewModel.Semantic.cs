using AIDatasetStudio.Application.AI;
using AIDatasetStudio.Application.Analysis;
using AIDatasetStudio.Desktop.ViewModels.AI;
using AIDatasetStudio.Domain.Enums;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AIDatasetStudio.Desktop.ViewModels;

// The SIMILAR IN MEANING card (SDD §8.2, M16): questions are compared by their words; with an
// embedding model on the AI connection they are compared by meaning too. Vectors are made only
// when the user runs it here, kept in the workspace, and the near-duplicate groups follow.
public sealed partial class DatasetOverviewViewModel
{
    /// <summary>
    /// Questions sent for vectors per page read from the workspace.
    /// </summary>
    private const int PendingPage = 256;

    private CancellationTokenSource? _embedding;
    private bool _applyingSemantic;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSemanticOn), nameof(SemanticStatusText), nameof(SemanticModelText), nameof(HasSemanticPending))]
    [NotifyPropertyChangedFor(nameof(SemanticRunText), nameof(CanRunSemantic), nameof(IsSemanticModelChanged), nameof(ShowSemanticRun), nameof(ShowSemanticOff))]
    [NotifyCanExecuteChangedFor(nameof(TurnOffSemanticCommand))]
    public partial SemanticState? Semantic { get; private set; }

    /// <summary>
    /// Whether the dataset's questions can be compared by meaning: preference pairs share prompts on purpose.
    /// </summary>
    public bool CanCompareMeaning => Dataset is { Type: not DatasetType.Preference };

    public bool IsSemanticOn => Semantic is { IsOn: true };

    /// <summary>
    /// The cosine from which two questions count as alike, as the user sets it.
    /// </summary>
    [ObservableProperty]
    public partial decimal? SemanticThreshold { get; set; } = (decimal)SemanticQuestions.DefaultThreshold;

    public decimal MinSemanticThreshold => (decimal)SemanticQuestions.MinThreshold;

    public decimal MaxSemanticThreshold => (decimal)SemanticQuestions.MaxThreshold;

    /// <summary>
    /// "Model: bge-m3".
    /// </summary>
    public string SemanticModelText => Semantic?.Model is { } model ? _localizer.Format("Overview_SemanticModel", model) : string.Empty;

    /// <summary>
    /// "38 of 40 questions compared by meaning", or how it works while it is off.
    /// </summary>
    public string SemanticStatusText => Semantic switch
    {
        { IsOn: true, PendingCount: 0 } state => _localizer.Format("Overview_SemanticAll", state.QuestionCount),
        { IsOn: true } state => _localizer.Format("Overview_SemanticSome", state.EmbeddedCount, state.QuestionCount),
        _ => _localizer["Overview_SemanticOff"],
    };

    public bool HasSemanticPending => Semantic is { IsOn: true, PendingCount: > 0 };

    /// <summary>
    /// The connection's embedding model differs from the one the vectors were made with.
    /// </summary>
    public bool IsSemanticModelChanged => Semantic is { Model: { } model } && _ai.Connection?.EmbeddingModel is { } current && current != model;

    /// <summary>
    /// "Compare by meaning", "Compare 2 new questions" or "Compare with nomic-embed-text instead".
    /// </summary>
    public string SemanticRunText => Semantic switch
    {
        _ when IsSemanticModelChanged => _localizer.Format("Overview_SemanticSwitch", _ai.Connection!.EmbeddingModel!),
        { IsOn: true } state => _localizer.Format("Overview_SemanticRest", state.PendingCount),
        _ => _localizer["Overview_SemanticRun"],
    };

    public bool CanRunSemantic => !IsSemanticOn || HasSemanticPending || IsSemanticModelChanged;

    public bool ShowSemanticRun => CanRunSemantic && !IsEmbedding;

    public bool ShowSemanticOff => IsSemanticOn && !IsEmbedding;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowSemanticRun), nameof(ShowSemanticOff))]
    [NotifyCanExecuteChangedFor(nameof(RunSemanticCommand), nameof(StopSemanticCommand), nameof(TurnOffSemanticCommand))]
    public partial bool IsEmbedding { get; private set; }

    [ObservableProperty]
    public partial string? EmbeddingText { get; private set; }

    [ObservableProperty]
    public partial double EmbeddingProgress { get; private set; }

    /// <summary>
    /// Why the questions could not be compared, with a way to the AI connections.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSemanticError))]
    public partial string? SemanticError { get; private set; }

    public bool HasSemanticError => SemanticError is not null;

    /// <summary>
    /// The current or last run; awaited by tests.
    /// </summary>
    public Task SemanticRun { get; private set; } = Task.CompletedTask;

    /// <summary>
    /// Turns comparing by meaning on with the connection's embedding model and gives every
    /// question without a vector one; the near-duplicate groups follow.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanStartSemantic))]
    private Task RunSemanticAsync()
    {
        if (Dataset is not { } dataset)
        {
            return Task.CompletedTask;
        }

        SemanticError = null;
        if (_ai.Connection is not { } connection)
        {
            SemanticError = _localizer["Overview_SemanticNoConnection"];
            return Task.CompletedTask;
        }

        if (connection.EmbeddingModel is not { } model)
        {
            SemanticError = AIText.Error(_localizer, new AIException(AIErrorKind.NoEmbeddingModel, "No embedding model."), connection);
            return Task.CompletedTask;
        }

        var cancellation = _embedding = new CancellationTokenSource();
        IsEmbedding = true;
        return SemanticRun = EmbedAsync(dataset, connection, model, cancellation);
    }

    private bool CanStartSemantic() => !IsEmbedding;

    [RelayCommand(CanExecute = nameof(IsEmbedding))]
    private void StopSemantic() => _embedding?.Cancel();

    /// <summary>
    /// Compares questions by their words only again; the vectors stay for turning it on later.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanTurnOffSemantic))]
    private async Task TurnOffSemanticAsync()
    {
        if (Dataset is not { } dataset)
        {
            return;
        }

        try
        {
            await _semantic.SetAsync(dataset.Id, null, (double)(SemanticThreshold ?? (decimal)SemanticQuestions.DefaultThreshold));
            await RegroupAsync(dataset);
            _status.Set("Status_SemanticOff", dataset.Name);
        }
        catch (Exception ex) when (_status.TryReport(ex))
        {
        }
    }

    private bool CanTurnOffSemantic() => IsSemanticOn && !IsEmbedding;

    [RelayCommand]
    private void DismissSemanticError() => SemanticError = null;

    partial void OnSemanticThresholdChanged(decimal? value)
    {
        if (_applyingSemantic || value is null || Dataset is not { } dataset || Semantic is not { IsOn: true } state)
        {
            return;
        }

        var threshold = Math.Clamp((double)value.Value, SemanticQuestions.MinThreshold, SemanticQuestions.MaxThreshold);
        if (Math.Abs(threshold - state.Threshold) < 1e-9)
        {
            return;
        }

        // One after the other, so the groups end up made with the last threshold.
        var previous = SemanticRun;
        SemanticRun = SaveThresholdAsync();

        async Task SaveThresholdAsync()
        {
            try
            {
                await previous;
                await _semantic.SetAsync(dataset.Id, state.Model, threshold);
                await RegroupAsync(dataset);
            }
            catch (Exception ex) when (_status.TryReport(ex))
            {
            }
        }
    }

    // Vectors for the questions without one, page by page; each batch is kept as it comes.
    private async Task EmbedAsync(DatasetNodeViewModel dataset, AIConnection connection, string model, CancellationTokenSource cancellation)
    {
        var token = cancellation.Token;
        var done = 0;
        try
        {
            await _semantic.SetAsync(dataset.Id, model, (double)(SemanticThreshold ?? (decimal)SemanticQuestions.DefaultThreshold), token);
            var state = await _semantic.GetStateAsync(dataset.Id, token);
            ApplySemantic(state);
            var total = Math.Max(1, state.PendingCount);
            var tried = new HashSet<string>(StringComparer.Ordinal);
            EmbeddingText = _localizer.Format("Overview_SemanticProgress", 0, total, model);
            while (true)
            {
                // A question whose vector was not kept (all zeros) is not asked for again.
                var pending = (await _semantic.FindPendingAsync(dataset.Id, PendingPage, token)).Where(q => tried.Add(q.QuestionKey)).ToList();
                if (pending.Count == 0)
                {
                    break;
                }

                foreach (var batch in pending.Chunk(SemanticQuestions.BatchSize))
                {
                    var vectors = await _ai.EmbedAsync(connection, [.. batch.Select(q => q.Text)], token);
                    var kept = await _semantic.SaveAsync(dataset.Id, model, [.. batch.Zip(vectors, (q, v) => new QuestionEmbedding(q.QuestionKey, v))], token);
                    state = state with { EmbeddedCount = state.EmbeddedCount + kept };
                    Semantic = state;
                    done += batch.Length;
                    EmbeddingProgress = Math.Min(1, (double)done / total);
                    EmbeddingText = _localizer.Format("Overview_SemanticProgress", Math.Min(done, total), total, model);
                }
            }

            _status.Set("Status_SemanticDone", done, model);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            _status.Set("Status_SemanticStopped", done);
        }
        catch (AIException ex)
        {
            SemanticError = _ai.Describe(ex, connection);
        }
        catch (Exception ex) when (_status.TryReport(ex))
        {
        }
        finally
        {
            _embedding = null;
            EmbeddingText = null;
            EmbeddingProgress = 0;
            IsEmbedding = false;
            cancellation.Dispose();
        }

        // What was kept counts, also after a stop or a failure.
        try
        {
            await RegroupAsync(dataset);
        }
        catch (Exception ex) when (_status.TryReport(ex))
        {
        }
    }

    // The groups are made again from the vectors; the list follows when they changed.
    private async Task RegroupAsync(DatasetNodeViewModel dataset)
    {
        var regrouped = await _analysis.GroupNearDuplicatesAsync(dataset.Id);
        await RefreshAsync();
        if (regrouped && Dataset?.Id == dataset.Id)
        {
            NearGroupsChanged?.Invoke(this, dataset.Id);
        }
    }

    /// <summary>
    /// Stops making vectors (another dataset is shown); what was made is kept.
    /// </summary>
    private async Task StopEmbeddingAsync()
    {
        _embedding?.Cancel();
        await SemanticRun;
    }

    private async Task LoadSemanticAsync(DatasetNodeViewModel dataset)
    {
        if (!CanCompareMeaning)
        {
            ApplySemantic(null);
            return;
        }

        var state = await _semantic.GetStateAsync(dataset.Id);
        if (Dataset?.Id == dataset.Id)
        {
            ApplySemantic(state);
        }
    }

    private void ApplySemantic(SemanticState? state)
    {
        Semantic = state;
        _applyingSemantic = true;
        SemanticThreshold = Math.Round((decimal)(state?.Threshold ?? SemanticQuestions.DefaultThreshold), 2);
        _applyingSemantic = false;
    }

    private void RefreshSemanticTexts()
    {
        OnPropertyChanged(nameof(SemanticStatusText));
        OnPropertyChanged(nameof(SemanticModelText));
        OnPropertyChanged(nameof(SemanticRunText));
        OnPropertyChanged(nameof(IsSemanticModelChanged));
        OnPropertyChanged(nameof(CanRunSemantic));
        OnPropertyChanged(nameof(ShowSemanticRun));
    }
}
