namespace AIDatasetStudio.Application.AI;

/// <summary>
/// Where an evaluation's questions come from (M22).
/// </summary>
public enum EvaluationSource
{
    /// <summary>The records the last export put in the test file: questions a trained model has not seen.</summary>
    TestPart,

    /// <summary>The records the last export put in the validation file.</summary>
    ValidationPart,

    /// <summary>Every record, the training ones included.</summary>
    AllRecords,
}

/// <summary>
/// A finished evaluation of a model on a dataset, kept to compare models (M22).
/// </summary>
/// <param name="Model">The model that answered.</param>
/// <param name="Judge">The model that graded the answers.</param>
/// <param name="Seed">The seed the questions were drawn with.</param>
public sealed record EvaluationRun(Guid Id, string Model, string Judge, EvaluationSource Source, int Seed, EvaluationSummary Summary, DateTimeOffset RanAt);

/// <summary>
/// A dataset's evaluations (SDD §17.10): their summaries, kept in the workspace; the answers
/// themselves go to the report file.
/// </summary>
public interface IEvaluationRunService
{
    /// <summary>
    /// Keeps a finished evaluation with the dataset; nothing is kept if the dataset was deleted meanwhile.
    /// </summary>
    Task<EvaluationRun> AddAsync(
        Guid datasetId, string model, string judge, EvaluationSource source, int seed, EvaluationSummary summary, CancellationToken cancellationToken = default);

    /// <summary>
    /// The dataset's evaluations, the latest first.
    /// </summary>
    Task<IReadOnlyList<EvaluationRun>> GetRunsAsync(Guid datasetId, CancellationToken cancellationToken = default);

    Task DeleteAsync(Guid runId, CancellationToken cancellationToken = default);
}
