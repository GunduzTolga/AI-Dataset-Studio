using AIDatasetStudio.Application.Analysis;
using AIDatasetStudio.Application.Documents;
using AIDatasetStudio.Desktop.Localization;
using AIDatasetStudio.Desktop.ViewModels.Documents;

namespace AIDatasetStudio.Desktop.Services;

/// <summary>
/// Starts Q&amp;A extraction from a document (SDD §17.5) with the services it needs.
/// </summary>
public sealed class DocumentQuestions(
    IDocumentReader documents,
    ITokenizerProvider tokenizers,
    AIAssistant ai,
    RecordOperations operations,
    IDialogService dialogs,
    Localizer localizer)
{
    /// <summary>
    /// The documents questions can be extracted from, with the leading dot.
    /// </summary>
    public IReadOnlyList<string> SupportedExtensions => documents.SupportedExtensions;

    /// <summary>
    /// Passages are measured with the tokenizer of current OpenAI models; for sizing a passage
    /// any common tokenizer is close enough.
    /// </summary>
    public QAExtractionViewModel Create(string filePath, QATarget target) =>
        new(documents, tokenizers.Get(Tokenizers.O200kBase), ai, operations, dialogs, localizer, filePath, target);
}
