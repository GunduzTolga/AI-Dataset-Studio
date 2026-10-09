using AIDatasetStudio.Application.Analysis;
using AIDatasetStudio.Desktop.Localization;

namespace AIDatasetStudio.Desktop.ViewModels;

/// <summary>
/// A quality issue of the shown record, ready to display.
/// </summary>
public sealed record IssueItem(QualityRule Rule, IssueSeverity Severity, string Text)
{
    public bool IsError => Severity == IssueSeverity.Error;

    public bool IsWarning => Severity == IssueSeverity.Warning;
}

/// <summary>
/// Localized names and descriptions for quality rules and kinds of personal data.
/// </summary>
internal static class IssueText
{
    /// <summary>
    /// Short name of a rule, e.g. "Duplicates".
    /// </summary>
    public static string RuleName(Localizer localizer, QualityRule rule) => localizer[$"Rule_{rule}"];

    /// <summary>
    /// What is wrong with a record, e.g. "Empty field: Output".
    /// </summary>
    public static string Describe(Localizer localizer, QualityIssue issue) => issue.Rule switch
    {
        QualityRule.EmptyContent or QualityRule.UnclosedCodeBlock or QualityRule.InvalidJson when issue.Detail is { } field =>
            localizer.Format($"Issue_{issue.Rule}", localizer[$"Field_{field}"]),
        QualityRule.NearDuplicate when issue.Detail is { } ordinal =>
            localizer.Format("Issue_NearDuplicate", long.Parse(ordinal, System.Globalization.CultureInfo.InvariantCulture)),
        QualityRule.PersonalData when issue.Detail is { } kinds =>
            localizer.Format("Issue_PersonalData", PiiKinds(localizer, kinds.Split(',').Select(Enum.Parse<PiiKind>))),
        _ => localizer[$"Issue_{issue.Rule}"],
    };

    /// <summary>
    /// "e-mail, phone" for a set of kinds.
    /// </summary>
    public static string PiiKinds(Localizer localizer, IEnumerable<PiiKind> kinds) =>
        string.Join(", ", kinds.Order().Select(kind => localizer[$"Pii_{kind}"]));

    /// <summary>
    /// "2 e-mail addresses · 1 phone number" style summary of masked values.
    /// </summary>
    public static string PiiCounts(Localizer localizer, IReadOnlyDictionary<PiiKind, int> counts) =>
        string.Join(" · ", counts.OrderBy(p => p.Key).Select(p => localizer.Format("Pii_Count", p.Value, localizer[$"Pii_{p.Key}"])));
}
