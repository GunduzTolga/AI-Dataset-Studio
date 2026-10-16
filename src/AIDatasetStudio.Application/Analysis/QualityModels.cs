namespace AIDatasetStudio.Application.Analysis;

public enum IssueSeverity
{
    Info,
    Warning,
    Error,
}

/// <summary>
/// The checks of the health engine (SDD §8.2). <see cref="QualityRules.RecordRules"/> look at one
/// record; the others compare records or depend on dataset settings and are evaluated when
/// queried, so they never go stale.
/// </summary>
public enum QualityRule
{
    /// <summary>A required field or turn is empty. Detail: the field ("Instruction", "Assistant"…).</summary>
    EmptyContent,

    /// <summary>A preference pair whose chosen and rejected answers are the same.</summary>
    IdenticalAnswers,

    /// <summary>A throwaway answer such as "tamam", "evet" or "bilmiyorum".</summary>
    ShortAnswer,

    /// <summary>An answer more than 50 times longer (in tokens) than its question.</summary>
    UnbalancedLength,

    /// <summary>The answer repeats the question.</summary>
    EchoedInput,

    /// <summary>A ``` code block is not closed. Detail: the field.</summary>
    UnclosedCodeBlock,

    /// <summary>Text that is meant to be JSON does not parse. Detail: the field.</summary>
    InvalidJson,

    /// <summary>Personal or secret data was found. Detail: the kinds, comma-separated.</summary>
    PersonalData,

    /// <summary>Another record in the dataset has exactly the same content.</summary>
    Duplicate,

    /// <summary>Another record asks the same question but answers differently.</summary>
    ConflictingAnswer,

    /// <summary>The record has more tokens than the dataset's context length.</summary>
    OverContextLimit,

    /// <summary>
    /// Another record asks nearly the same question (<see cref="NearDuplicates"/>). Detail: the number
    /// of the first such record.
    /// </summary>
    NearDuplicate,

    /// <summary>A tool is not a JSON function definition with a name (SDD §4.4). Detail: its number.</summary>
    InvalidTool,

    /// <summary>A call names a function that is not among the tools. Detail: the name.</summary>
    UnknownTool,

    /// <summary>A call's arguments are not a JSON object. Detail: the function's name.</summary>
    InvalidArguments,

    /// <summary>A call leaves out a parameter its function requires. Detail: "function.parameter".</summary>
    MissingArgument,

    /// <summary>
    /// The turns of a tool calling conversation are out of order, or a call before an answer has
    /// no result: the training formats cannot write it.
    /// </summary>
    ToolTurnOrder,
}

/// <param name="Detail">Rule-specific detail (a field name or a list of kinds), not localized.</param>
public sealed record QualityIssue(QualityRule Rule, IssueSeverity Severity, string? Detail = null);

/// <summary>
/// Severities and the score formula (SDD §8.1): 100 minus 40 per error and 10 per warning, at least 0.
/// </summary>
public static class QualityRules
{
    /// <summary>
    /// Bumped whenever a rule changes, so stored results are recomputed.
    /// </summary>
    public const int Version = 2;

    public const int ErrorPenalty = 40;
    public const int WarningPenalty = 10;

    /// <summary>
    /// Rules evaluated on a single record and stored with its analysis.
    /// </summary>
    public static IReadOnlyList<QualityRule> RecordRules { get; } =
    [
        QualityRule.EmptyContent, QualityRule.IdenticalAnswers, QualityRule.ShortAnswer, QualityRule.UnbalancedLength,
        QualityRule.EchoedInput, QualityRule.UnclosedCodeBlock, QualityRule.InvalidJson, QualityRule.PersonalData,
        QualityRule.InvalidTool, QualityRule.UnknownTool, QualityRule.InvalidArguments, QualityRule.MissingArgument, QualityRule.ToolTurnOrder,
    ];

    public static IssueSeverity SeverityOf(QualityRule rule) => rule switch
    {
        QualityRule.EmptyContent or QualityRule.IdenticalAnswers or QualityRule.OverContextLimit => IssueSeverity.Error,
        QualityRule.InvalidTool or QualityRule.UnknownTool or QualityRule.InvalidArguments or QualityRule.ToolTurnOrder => IssueSeverity.Error,
        _ => IssueSeverity.Warning,
    };

    public static int Penalty(IssueSeverity severity) => severity switch
    {
        IssueSeverity.Error => ErrorPenalty,
        IssueSeverity.Warning => WarningPenalty,
        _ => 0,
    };

    public static int Score(IEnumerable<QualityIssue> issues) => Math.Max(0, 100 - issues.Sum(issue => Penalty(issue.Severity)));
}

/// <summary>
/// Content tokens of a record (without chat-template overhead, SDD §7.3): the prompt side and the
/// answer side. For preference pairs the answer side is the longer of chosen and rejected, since
/// that is what has to fit next to the prompt.
/// </summary>
public readonly record struct TokenCounts(int Input, int Output)
{
    public int Total => Input + Output;
}

/// <summary>
/// The stored result of analysing one record.
/// </summary>
/// <param name="QuestionKey">Fingerprint of the normalized question, for "same question, different answer"; <c>null</c> when the rule does not apply.</param>
/// <param name="Signature">MinHash signature of the normalized question, for near-duplicate questions (<see cref="NearDuplicates"/>); <c>null</c> when the rule does not apply.</param>
public sealed record RecordAnalysisResult(TokenCounts Tokens, IReadOnlyList<QualityIssue> Issues, string? QuestionKey, byte[]? Signature = null)
{
    public int Score => QualityRules.Score(Issues);
}

/// <summary>
/// Counts tokens with one tokenizer.
/// </summary>
public interface ITokenCounter
{
    int CountTokens(string text);
}

/// <summary>
/// The tokenizers available offline (SDD §7.2).
/// </summary>
public interface ITokenizerProvider
{
    /// <summary>
    /// Tokenizer ids, e.g. "o200k_base".
    /// </summary>
    IReadOnlyList<string> Available { get; }

    /// <exception cref="ArgumentException">The tokenizer is not available.</exception>
    ITokenCounter Get(string tokenizerId);
}

public static class Tokenizers
{
    public const string O200kBase = "o200k_base";
    public const string Cl100kBase = "cl100k_base";

    /// <summary>
    /// Context lengths offered for the warning thresholds (SDD §7.3).
    /// </summary>
    public static IReadOnlyList<int> ContextLengths { get; } = [2_048, 4_096, 8_192, 16_384, 32_768, 131_072];

    /// <summary>
    /// Upper bounds of the token histogram buckets; the last bucket is open-ended.
    /// </summary>
    public static IReadOnlyList<int> HistogramBounds { get; } = [128, 256, 512, 1_024, 2_048, 4_096, 8_192];
}
