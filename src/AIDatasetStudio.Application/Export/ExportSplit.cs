using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using AIDatasetStudio.Application.Analysis;
using AIDatasetStudio.Domain.Entities;
using AIDatasetStudio.Domain.Enums;
using AIDatasetStudio.Domain.Payloads;

namespace AIDatasetStudio.Application.Export;

/// <summary>
/// Records that share a group key, for <see cref="ExportSplit.AssignStratified"/>.
/// </summary>
/// <param name="Stratum">The tag the group is split within; "" for records without one.</param>
public readonly record struct StratifiedGroup(string GroupKey, string Stratum, int RecordCount);

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
        var bucket = (int)(Hash(split, groupKey) % Buckets);

        var train = split.TrainPercent * (Buckets / 100);
        var validation = train + (split.ValidationPercent * (Buckets / 100));
        return bucket < train ? SplitPart.Train : bucket < validation ? SplitPart.Validation : SplitPart.Test;
    }

    /// <summary>
    /// Exact shares within each stratum (SDD §10.1, stratified sampling). In every stratum the
    /// groups are ordered by the same hash as <see cref="Assign"/> and cut where the training, then
    /// the validation share of its records is reached; a group is never split. Adding records can
    /// move a few groups at the cut, which hash-only assignment never does.
    /// </summary>
    /// <param name="split">Shares and seed.</param>
    /// <param name="groups">Each group of records with its stratum and size, in list order.</param>
    /// <returns>The part of every group key.</returns>
    public static IReadOnlyDictionary<string, SplitPart> AssignStratified(SplitOptions split, IReadOnlyList<StratifiedGroup> groups)
    {
        ArgumentNullException.ThrowIfNull(split);
        ArgumentNullException.ThrowIfNull(groups);
        var parts = new Dictionary<string, SplitPart>(StringComparer.Ordinal);
        foreach (var stratum in groups.GroupBy(g => g.Stratum, StringComparer.Ordinal))
        {
            var ordered = stratum
                .Select(g => (Group: g, Hash: Hash(split, g.GroupKey)))
                .OrderBy(x => x.Hash).ThenBy(x => x.Group.GroupKey, StringComparer.Ordinal)
                .ToList();
            var total = ordered.Sum(x => x.Group.RecordCount);
            var trainTarget = Share(total, split.TrainPercent);
            var validationTarget = split.TestPercent == 0 ? total - trainTarget : Share(total, split.ValidationPercent);
            var rest = split.TestPercent > 0 ? SplitPart.Test : split.ValidationPercent > 0 ? SplitPart.Validation : SplitPart.Train;
            long train = 0, validation = 0;
            foreach (var (group, _) in ordered)
            {
                SplitPart part;
                if (train < trainTarget)
                {
                    part = SplitPart.Train;
                    train += group.RecordCount;
                }
                else if (validation < validationTarget)
                {
                    part = SplitPart.Validation;
                    validation += group.RecordCount;
                }
                else
                {
                    part = rest;
                }

                parts[group.GroupKey] = part;
            }
        }

        return parts;
    }

    /// <summary>
    /// The stratum of a record: for <see cref="SplitOptions.AnyTag"/> its first tag by name, for a
    /// group its first tag of that group ("kategori:hukuk"); "" when it has none.
    /// </summary>
    public static string StratumOf(string? stratifyBy, IEnumerable<string> tagNames)
    {
        ArgumentNullException.ThrowIfNull(tagNames);
        if (stratifyBy is null)
        {
            return string.Empty;
        }

        var group = Tag.FoldName(stratifyBy.Trim());
        return tagNames
            .Where(name => group.Length == 0 || (Tag.GroupOf(name) is { } g && Tag.FoldName(g) == group))
            .Order(StringComparer.Ordinal)
            .FirstOrDefault() ?? string.Empty;
    }

    private static ulong Hash(SplitOptions split, string groupKey)
    {
        ArgumentNullException.ThrowIfNull(groupKey);
        Span<byte> hash = stackalloc byte[SHA256.HashSizeInBytes];
        SHA256.HashData(Encoding.UTF8.GetBytes(string.Create(CultureInfo.InvariantCulture, $"{split.Seed}:{groupKey}")), hash);
        return BinaryPrimitives.ReadUInt64BigEndian(hash);
    }

    private static long Share(long total, int percent) => (long)Math.Round(total * percent / 100.0, MidpointRounding.AwayFromZero);

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
