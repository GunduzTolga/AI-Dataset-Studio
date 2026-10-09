namespace AIDatasetStudio.Application.Analysis;

/// <summary>
/// Whether a dataset compares its questions by meaning (SDD §8.2, M16), and how far its vectors are.
/// </summary>
/// <param name="Model">The embedding model the questions are compared with; <c>null</c> when the comparison is off.</param>
/// <param name="Threshold">The cosine from which two questions count as alike.</param>
/// <param name="QuestionCount">The dataset's distinct analysed questions (archived records included).</param>
/// <param name="EmbeddedCount">Of them, those with a vector from <paramref name="Model"/>.</param>
public sealed record SemanticState(string? Model, double Threshold, long QuestionCount, long EmbeddedCount)
{
    public bool IsOn => Model is not null;

    /// <summary>
    /// Questions still to be turned into vectors; all of them while the comparison is off.
    /// </summary>
    public long PendingCount => QuestionCount - EmbeddedCount;
}

/// <summary>
/// A question without a vector: the key shared by the records asking it, and its text (<see cref="SemanticQuestions.Text"/>).
/// </summary>
public sealed record PendingQuestion(string QuestionKey, string Text);

/// <summary>
/// A question's vector as the embedding model gave it.
/// </summary>
public sealed record QuestionEmbedding(string QuestionKey, float[] Embedding);

/// <summary>
/// The vectors of a dataset's questions. They are made only when the user asks (the model is
/// on their AI connection) and kept in the workspace, one per distinct question; the
/// near-duplicate pass joins questions whose vectors are close (<see cref="SemanticQuestions"/>).
/// </summary>
public interface ISemanticIndex
{
    Task<SemanticState> GetStateAsync(Guid datasetId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Turns comparing by meaning on with the model and threshold, or off when <paramref name="model"/>
    /// is <c>null</c>. Vectors of another model, and of questions no record asks any more, are
    /// dropped; the near-duplicate groups are made again on the next pass
    /// (<see cref="IRecordAnalysisService.GroupNearDuplicatesAsync"/>).
    /// </summary>
    /// <param name="threshold">Kept within <see cref="SemanticQuestions.MinThreshold"/> and <see cref="SemanticQuestions.MaxThreshold"/>.</param>
    Task SetAsync(Guid datasetId, string? model, double threshold, CancellationToken cancellationToken = default);

    /// <summary>
    /// Questions without a vector from the dataset's model, in dataset order; none while the comparison is off.
    /// </summary>
    Task<IReadOnlyList<PendingQuestion>> FindPendingAsync(Guid datasetId, int max, CancellationToken cancellationToken = default);

    /// <summary>
    /// Keeps vectors made by <paramref name="model"/>, unless the dataset compares with another
    /// model by now (or not at all).
    /// </summary>
    /// <returns>The number of vectors kept.</returns>
    Task<int> SaveAsync(Guid datasetId, string model, IReadOnlyList<QuestionEmbedding> embeddings, CancellationToken cancellationToken = default);
}
