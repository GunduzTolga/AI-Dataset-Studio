using System.Text;
using AIDatasetStudio.Application.Documents;
using AIDatasetStudio.Domain.Enums;
using AIDatasetStudio.Domain.Payloads;

namespace AIDatasetStudio.Application.AI;

/// <summary>
/// A question with the answer a passage gives.
/// </summary>
public sealed record QAPair(string Question, string Answer);

/// <summary>
/// Asks a model for the questions a passage of a document answers (SDD §17.5), and turns the
/// pairs into records. Instructions to the model are in English, which models follow best; the
/// pairs are asked in the passage's language.
/// </summary>
public static class QAExtraction
{
    public const int DefaultQuestions = 3;
    public const int MaxQuestions = 10;

    private const string Rules =
        "You turn a passage of a document into training data for an assistant: the questions people " +
        "would ask about it, each with the answer the passage gives.\n" +
        "- Write at most {0} question-answer pairs; fewer when the passage has less to ask about, and none " +
        "when it has nothing useful (a table of contents, an index, references, a title page).\n" +
        "- Each question must make sense on its own: name the subject instead of writing \"the passage\", " +
        "\"this document\" or \"the text\".\n" +
        "- Answer only with what the passage says and add no facts. Answers are complete, helpful sentences.\n" +
        "- Do not ask the same thing twice in other words.\n" +
        "- Write the questions and answers in the language of the passage.\n" +
        "Reply with only a JSON array, nothing before or after it: [{{\"question\": \"...\", \"answer\": \"...\"}}]";

    /// <summary>
    /// The dataset types question-answer pairs become records of.
    /// </summary>
    public static bool Supports(DatasetType type) => type is DatasetType.Instruction or DatasetType.Chat or DatasetType.Preference;

    /// <param name="fileName">The document's file name, which often says what it is about.</param>
    /// <param name="questions">The most pairs to write, 1–<see cref="MaxQuestions"/>.</param>
    /// <param name="guidance">The dataset's instructions for the model (tone, length, language…).</param>
    public static IReadOnlyList<AIChatMessage> Prompt(string fileName, DocumentChunk chunk, int questions, string? guidance)
    {
        ArgumentNullException.ThrowIfNull(chunk);
        var system = new StringBuilder(string.Format(System.Globalization.CultureInfo.InvariantCulture, Rules, Math.Clamp(questions, 1, MaxQuestions)));
        if (!string.IsNullOrWhiteSpace(guidance))
        {
            system.Append("\n\nInstructions for this dataset:\n").Append(guidance.Trim());
        }

        var user = new StringBuilder("Document: ").Append(fileName).Append('\n');
        if (chunk.Section.Length > 0)
        {
            user.Append("Section: ").Append(chunk.Section).Append('\n');
        }

        user.Append("\nPassage:\n").Append(chunk.Text);
        return [new(ChatRole.System, system.ToString()), new(ChatRole.User, user.ToString())];
    }

    /// <summary>
    /// A record of <paramref name="type"/> made of the pair: the question asks and the answer
    /// answers; a preference pair's rejected answer is left to write later.
    /// </summary>
    public static RecordPayload Payload(DatasetType type, QAPair pair) => type switch
    {
        DatasetType.Instruction => new InstructionPayload(pair.Question, string.Empty, pair.Answer),
        DatasetType.Chat => new ChatPayload(string.Empty, [new ChatMessage(ChatRole.User, pair.Question), new ChatMessage(ChatRole.Assistant, pair.Answer)]),
        DatasetType.Preference => new PreferencePayload(pair.Question, pair.Answer, string.Empty),
        _ => throw new ArgumentException($"Question-answer pairs cannot become {type} records.", nameof(type)),
    };
}
