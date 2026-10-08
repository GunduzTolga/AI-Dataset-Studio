namespace AIDatasetStudio.Domain.Enums;

/// <summary>
/// The schema family a dataset (and every record inside it) follows. See SDD §4.
/// </summary>
/// <remarks>
/// Values are persisted as integers; never renumber existing members.
/// </remarks>
public enum DatasetType
{
    Instruction = 1,
    Chat = 2,
    Preference = 3,
    ToolCalling = 4,
    Evaluation = 5,
    Classification = 6,
    RagTest = 7,
}
