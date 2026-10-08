using AIDatasetStudio.Application.Records;
using AIDatasetStudio.Desktop.Localization;
using AIDatasetStudio.Desktop.ViewModels.AI;
using AIDatasetStudio.Desktop.ViewModels.Documents;

namespace AIDatasetStudio.Desktop.Services;

/// <summary>
/// Starts generating variations of records (SDD §17.6) with the services it needs.
/// </summary>
public sealed class RecordVariations(
    IRecordQueryService queries,
    AIAssistant ai,
    RecordOperations operations,
    IDialogService dialogs,
    Localizer localizer)
{
    /// <summary>
    /// Reads the records as they are saved now and prepares their variations.
    /// </summary>
    public async Task<VariationsViewModel> CreateAsync(QATarget target, IReadOnlyCollection<Guid> recordIds, CancellationToken cancellationToken = default)
    {
        var records = new List<RecordDetails>(recordIds.Count);
        foreach (var id in recordIds)
        {
            if (await queries.GetAsync(id, cancellationToken) is { } record)
            {
                records.Add(record);
            }
        }

        return Create(target, records);
    }

    public VariationsViewModel Create(QATarget target, IReadOnlyList<RecordDetails> records) =>
        new(ai, operations, dialogs, localizer, target, records);
}
