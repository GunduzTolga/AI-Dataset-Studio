using AIDatasetStudio.Desktop.Localization;

namespace AIDatasetStudio.Desktop.ViewModels;

/// <summary>
/// Where a passage is in its document, as people say it: "page 3", "pages 3–4", "paragraphs 12–20".
/// </summary>
internal static class SourceText
{
    /// <param name="unit">"page", "paragraph" or "line" (<see cref="Application.Documents.SourceDocument.PositionUnit"/>).</param>
    public static string Location(Localizer localizer, string unit, int first, int last)
    {
        var name = unit switch
        {
            "page" => "Page",
            "paragraph" => "Paragraph",
            _ => "Line",
        };
        return first >= last ? localizer.Format($"Location_{name}", first) : localizer.Format($"Location_{name}s", first, last);
    }
}
