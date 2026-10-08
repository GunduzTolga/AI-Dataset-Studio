namespace AIDatasetStudio.Infrastructure.Persistence.Entities;

/// <summary>
/// A model's last judgment of a record (SDD §17.7). Current while <see cref="ContentHash"/>
/// matches the record; otherwise it is shown as out of date and left out of filters and counts.
/// </summary>
internal sealed class RecordJudgmentRow
{
    public Guid RecordId { get; set; }

    /// <summary>
    /// Copied from the record so dataset queries stay within one index range.
    /// </summary>
    public Guid DatasetId { get; set; }

    public string ContentHash { get; set; } = string.Empty;

    /// <summary>
    /// 1–5.
    /// </summary>
    public int Score { get; set; }

    public string Reason { get; set; } = string.Empty;

    /// <summary>
    /// <see cref="Application.AI.JudgeFlags"/>.
    /// </summary>
    public int Flags { get; set; }

    public string Model { get; set; } = string.Empty;

    public DateTimeOffset JudgedAt { get; set; }
}

/// <summary>
/// What a model said about the answers of two records that ask the same thing. A pair is stored
/// once, with the smaller id first, and is current while both contents are as compared.
/// </summary>
internal sealed class RecordConflictRow
{
    public Guid RecordId { get; set; }

    public Guid OtherId { get; set; }

    public Guid DatasetId { get; set; }

    public string RecordHash { get; set; } = string.Empty;

    public string OtherHash { get; set; } = string.Empty;

    public bool Contradicts { get; set; }

    public string Reason { get; set; } = string.Empty;

    public string Model { get; set; } = string.Empty;

    public DateTimeOffset CheckedAt { get; set; }
}
