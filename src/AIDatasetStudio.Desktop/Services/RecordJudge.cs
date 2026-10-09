using System.Text;
using AIDatasetStudio.Application.AI;
using AIDatasetStudio.Application.Documents;
using AIDatasetStudio.Application.Judgments;
using AIDatasetStudio.Application.Records;
using AIDatasetStudio.Desktop.Localization;
using AIDatasetStudio.Desktop.ViewModels.AI;
using AIDatasetStudio.Desktop.ViewModels.Documents;

namespace AIDatasetStudio.Desktop.Services;

/// <summary>
/// What judging a record gave.
/// </summary>
/// <param name="Verdict">The model's verdict, or <c>null</c> when its answer had no score.</param>
/// <param name="RawAnswer">What the model answered, shown when it could not be read.</param>
/// <param name="Details">The record's judgment and contradictions as now kept.</param>
public sealed record JudgeOutcome(JudgeVerdict? Verdict, string RawAnswer, JudgmentDetails Details);

/// <summary>
/// Has the default model judge a record and compare its answer with records that ask the same
/// thing (SDD §17.7). Results are kept as they come: stopping keeps what was judged.
/// </summary>
public sealed class RecordJudge(AIAssistant ai, IJudgmentService judgments, IRecordQueryService queries, Localizer localizer, TimeProvider time)
{
    /// <summary>
    /// How many other records a record is compared with at most, the first ones in the dataset first.
    /// </summary>
    public const int MaxComparisons = 3;

    public IJudgmentService Judgments => judgments;

    /// <summary>
    /// The language reasons are asked in: the app's ("Turkish").
    /// </summary>
    public string Language => (localizer.Culture.IsNeutralCulture ? localizer.Culture : localizer.Culture.Parent).EnglishName;

    /// <param name="guidance">The dataset's instructions for the model.</param>
    /// <param name="compare">Whether to compare the record with records that ask the same thing.</param>
    /// <param name="comparing">Told the number of each record the answer is compared with.</param>
    /// <exception cref="AIException">The server cannot be used.</exception>
    public async Task<JudgeOutcome> JudgeAsync(
        RecordDetails record,
        string? guidance,
        bool compare,
        AIConnection connection,
        Action<long>? comparing = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(connection);
        var answer = await AskAsync(connection, Judging.Prompt(record.Payload, PassageSource.FromMetadata(record.MetadataJson), guidance, Language), cancellationToken);
        var verdict = Judging.Read(answer);
        if (verdict is not null)
        {
            // The model has answered: the judgment is kept even if the user stops now.
            await judgments.SaveAsync(
                new RecordJudgment(record.Id, record.ContentHash, verdict.Score, verdict.Reason, verdict.Flags, connection.Model, time.GetUtcNow()),
                CancellationToken.None);
        }

        if (compare && Variations.SourceOf(record.Payload) is { } source)
        {
            foreach (var sibling in await judgments.FindSiblingsAsync(record.Id, MaxComparisons, cancellationToken))
            {
                comparing?.Invoke(sibling.Ordinal);
                var other = Variations.SourceOf(sibling.Payload)!;
                if (Judging.ReadConflict(await AskAsync(connection, Judging.ConflictPrompt(source, other, Language), cancellationToken)) is { } conflict)
                {
                    await judgments.SaveConflictAsync(
                        new RecordConflict(record.Id, sibling.Id, record.ContentHash, sibling.ContentHash, conflict.Contradicts, conflict.Reason, connection.Model, time.GetUtcNow()),
                        CancellationToken.None);
                }
            }
        }

        return new JudgeOutcome(verdict, answer.Trim(), await judgments.GetAsync(record.Id, CancellationToken.None));
    }

    /// <summary>
    /// The judge window for these records, in dataset order.
    /// </summary>
    public async Task<JudgeViewModel> CreateAsync(QATarget target, IReadOnlyCollection<Guid> recordIds, CancellationToken cancellationToken = default) =>
        new(this, queries, ai, localizer, target, await queries.GetSummariesAsync(recordIds, cancellationToken));

    private async Task<string> AskAsync(AIConnection connection, IReadOnlyList<AIChatMessage> messages, CancellationToken cancellationToken)
    {
        var answer = new StringBuilder();
        await foreach (var piece in ai.WriteAsync(connection, messages, cancellationToken, Judging.Temperature))
        {
            answer.Append(piece);
        }

        return answer.ToString();
    }
}
