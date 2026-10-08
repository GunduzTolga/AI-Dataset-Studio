using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using AIDatasetStudio.Application.Analysis;
using AIDatasetStudio.Domain.Enums;
using AIDatasetStudio.Domain.Payloads;

namespace AIDatasetStudio.Application.Export;

/// <summary>
/// Hash-based train / validation / test assignment (SDD §10.1, ADR-018). Each record is placed on
/// its own, from a hash of the seed and its group key, so adding or removing records never moves
/// the others, and the result is the same on every platform and .NET version.
/// </summary>
public static class ExportSplit
{
    private const int Buckets = 10_000;

    /// <summary>
    /// The part a record goes to: <c>SHA-256("seed:key")</c> mod 10,000 against the cumulative shares.
    /// </summary>
    public static SplitPart Assign(SplitOptions split, string groupKey)
    {
        ArgumentNullException.ThrowIfNull(split);
        ArgumentNullException.ThrowIfNull(groupKey);
        Span<byte> hash = stackalloc byte[SHA256.HashSizeInBytes];
        SHA256.HashData(Encoding.UTF8.GetBytes(string.Create(CultureInfo.InvariantCulture, $"{split.Seed}:{groupKey}")), hash);
        var bucket = (int)(BinaryPrimitives.ReadUInt64BigEndian(hash) % Buckets);

        var train = split.TrainPercent * (Buckets / 100);
        var validation = train + (split.ValidationPercent * (Buckets / 100));
        return bucket < train ? SplitPart.Train : bucket < validation ? SplitPart.Validation : SplitPart.Test;
    }

    /// <summary>
    /// What keeps related records together: records that ask the same question (the prompt side,
    /// normalized as for "same question, different answer") share a key, so a question never ends up
    /// in both training and test data. Records without a question use their id.
    /// </summary>
    public static string GroupKey(RecordPayload payload, Guid recordId)
    {
        var question = payload switch
        {
            InstructionPayload p => p.Instruction + "\n" + p.Input,
            ChatPayload p => string.Join('\n', new[] { p.SystemPrompt ?? string.Empty }.Concat(p.Messages.Where(m => m.Role != ChatRole.Assistant).Select(m => m.Content))),
            PreferencePayload p => p.Prompt,
            _ => string.Empty,
        };

        var normalized = RecordAnalyzer.Normalize(question);
        return normalized.Length > 0 ? "q:" + normalized : "id:" + recordId.ToString("D");
    }
}
