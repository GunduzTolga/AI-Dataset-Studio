using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Numerics;
using AIDatasetStudio.Domain.Enums;
using AIDatasetStudio.Domain.Payloads;

namespace AIDatasetStudio.Application.Analysis;

/// <summary>
/// A question as a vector: its embedding, normalized and kept as signed bytes with one scale
/// (a quarter of the size of floats; cosines stay within about 0.01), and a 256-bit
/// random-hyperplane signature for finding candidates.
/// </summary>
public sealed record QuestionVector(sbyte[] Values, float Scale, byte[] Signature);

/// <summary>
/// Questions alike in meaning (SDD §8.2, M16): "Parolamı unuttum" and "Şifremi nasıl
/// sıfırlarım?" share few characters but mean the same. An embedding model turns each question
/// into a vector; two questions are alike when the cosine of their vectors reaches the threshold.
/// Pairs join the near-duplicate groups of <see cref="NearDuplicates"/>, so the rule, the filter,
/// the clean-up and the split treat them alike.
/// </summary>
/// <remarks>
/// Candidates come from random-hyperplane hashing (SimHash): every vector gets
/// <see cref="SignatureBits"/> sign bits, banded into <see cref="Bands"/> keys of
/// <see cref="BandBits"/> bits. Two vectors with cosine 0.9 share a band key in 99.7 % of cases.
/// Candidates whose bit agreement puts them clearly below the threshold are dropped, the rest are
/// checked with the exact cosine. Hyperplanes come from a fixed seed, so stored signatures stay valid.
/// </remarks>
public static class SemanticQuestions
{
    public const double DefaultThreshold = 0.90;
    public const double MinThreshold = 0.80;
    public const double MaxThreshold = 0.98;

    /// <summary>
    /// Questions sent per request.
    /// </summary>
    public const int BatchSize = 16;

    /// <summary>
    /// Longer questions are cut: a question's meaning is in its beginning, and small models have small windows.
    /// </summary>
    public const int MaxTextLength = 2_000;

    public const int SignatureBits = 256;
    public const int SignatureBytes = SignatureBits / 8;
    public const int Bands = 24;
    public const int BandBits = 10;

    // Candidates estimated this far below the threshold are not checked (about 3.5 standard deviations at 256 bits).
    private const double EstimateMargin = 0.12;

    // A bucket larger than this (thousands of near-identical questions) is compared along a window
    // instead of pair by pair; its records still connect through the chain.
    private const int FullBucket = 512;
    private const int Window = 64;

    private static readonly ConcurrentDictionary<int, float[]> Hyperplanes = new();

    /// <summary>
    /// The text whose meaning is compared, cut to <see cref="MaxTextLength"/>: instruction and
    /// input, or the user turns of a conversation. The system prompt is left out, as many records
    /// share a long one that would make them all alike; it counts only without user turns.
    /// <c>null</c> for preference pairs (as for <see cref="RecordAnalyzer.QuestionText"/>) and empty questions.
    /// </summary>
    public static string? Text(RecordPayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        var text = payload is ChatPayload chat
            ? string.Join('\n', chat.Messages.Where(m => m.Role == ChatRole.User && !string.IsNullOrWhiteSpace(m.Content)).Select(m => m.Content.Trim()))
            : null;
        if (string.IsNullOrEmpty(text))
        {
            text = RecordAnalyzer.QuestionText(payload)?.Trim();
        }

        return string.IsNullOrEmpty(text) ? null : text.Length <= MaxTextLength ? text : text[..MaxTextLength];
    }

    /// <summary>
    /// Normalizes, quantizes and signs an embedding.
    /// </summary>
    /// <exception cref="ArgumentException">The vector is empty or all zeros.</exception>
    public static QuestionVector Encode(ReadOnlySpan<float> embedding)
    {
        var norm = Math.Sqrt(Dot(embedding, embedding));
        if (embedding.Length == 0 || norm == 0 || !double.IsFinite(norm))
        {
            throw new ArgumentException("An embedding needs at least one value that is not zero.", nameof(embedding));
        }

        var unit = new float[embedding.Length];
        var largest = 0f;
        for (var i = 0; i < unit.Length; i++)
        {
            unit[i] = (float)(embedding[i] / norm);
            largest = Math.Max(largest, Math.Abs(unit[i]));
        }

        var scale = largest / 127f;
        var values = new sbyte[unit.Length];
        for (var i = 0; i < unit.Length; i++)
        {
            values[i] = (sbyte)Math.Round(unit[i] / scale, MidpointRounding.AwayFromZero);
        }

        return new QuestionVector(values, scale, Sign(unit));
    }

    /// <summary>
    /// The cosine of two encoded vectors; 0 when their sizes differ (different models).
    /// </summary>
    public static double Cosine(ReadOnlySpan<sbyte> a, float scaleA, ReadOnlySpan<sbyte> b, float scaleB)
    {
        if (a.Length != b.Length || a.Length == 0)
        {
            return 0;
        }

        long dot = 0;
        var i = 0;
        if (Vector.IsHardwareAccelerated && a.Length >= Vector<sbyte>.Count)
        {
            var sum = Vector<int>.Zero;
            for (; i <= a.Length - Vector<sbyte>.Count; i += Vector<sbyte>.Count)
            {
                Vector.Widen(new Vector<sbyte>(a[i..]), out var aLow, out var aHigh);
                Vector.Widen(new Vector<sbyte>(b[i..]), out var bLow, out var bHigh);
                sum += Products(aLow * bLow) + Products(aHigh * bHigh);
            }

            dot = Vector.Sum(sum);
        }

        for (; i < a.Length; i++)
        {
            dot += a[i] * b[i];
        }

        return dot * (double)scaleA * scaleB;

        // 127 × 127 fits a short; their sums need ints.
        static Vector<int> Products(Vector<short> products)
        {
            Vector.Widen(products, out var low, out var high);
            return low + high;
        }
    }

    /// <summary>
    /// The cosine two signatures suggest, from the share of equal bits.
    /// </summary>
    public static double Estimate(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
    {
        var differing = 0;
        for (var i = 0; i < SignatureBytes; i += sizeof(ulong))
        {
            differing += BitOperations.PopCount(BinaryPrimitives.ReadUInt64LittleEndian(a[i..]) ^ BinaryPrimitives.ReadUInt64LittleEndian(b[i..]));
        }

        return Math.Cos(Math.PI * differing / SignatureBits);
    }

    /// <summary>
    /// Pairs worth an exact comparison: questions that share a band key and whose signatures do
    /// not put them clearly below <paramref name="threshold"/>. Of records with the same question
    /// key only the first takes part, as in <see cref="NearDuplicates.Candidates"/>. Pairs are
    /// produced as they are found and each only once, so memory stays linear in the records.
    /// </summary>
    /// <param name="signatures">One per record in list order; <c>null</c> for records without a vector.</param>
    /// <returns>Index pairs, the smaller index first.</returns>
    public static IEnumerable<(int A, int B)> Candidates(IReadOnlyList<byte[]?> signatures, IReadOnlyList<string?> questionKeys, double threshold)
    {
        ArgumentNullException.ThrowIfNull(signatures);
        ArgumentNullException.ThrowIfNull(questionKeys);
        return Pairs(signatures, questionKeys, threshold - EstimateMargin);
    }

    private static IEnumerable<(int A, int B)> Pairs(IReadOnlyList<byte[]?> signatures, IReadOnlyList<string?> questionKeys, double floor)
    {
        var firsts = new HashSet<string>(StringComparer.Ordinal);
        var members = Enumerable.Range(0, signatures.Count)
            .Where(i => signatures[i] is { Length: SignatureBytes } && (questionKeys[i] is not { } key || firsts.Add(key)))
            .ToArray();

        // Every member's bucket and place in it, per band: a pair met in an earlier band is not produced again.
        const int keyCount = 1 << BandBits;
        var keys = new ushort[Bands][];
        var places = new int[Bands][];
        var sizes = new int[Bands][];
        for (var band = 0; band < Bands; band++)
        {
            (keys[band], places[band], sizes[band]) = (new ushort[members.Length], new int[members.Length], new int[keyCount]);
            for (var m = 0; m < members.Length; m++)
            {
                var key = BandKey(signatures[members[m]]!, band);
                keys[band][m] = (ushort)key;
                places[band][m] = sizes[band][key]++;
            }
        }

        var starts = new int[keyCount + 1];
        var order = new int[members.Length];
        for (var band = 0; band < Bands; band++)
        {
            // The members by bucket, each bucket in list order.
            for (var key = 0; key < keyCount; key++)
            {
                starts[key + 1] = starts[key] + sizes[band][key];
            }

            for (var m = 0; m < members.Length; m++)
            {
                order[starts[keys[band][m]] + places[band][m]] = m;
            }

            for (var key = 0; key < keyCount; key++)
            {
                var (start, size) = (starts[key], sizes[band][key]);
                for (var j = 1; j < size; j++)
                {
                    for (var i = size <= FullBucket ? 0 : Math.Max(0, j - Window); i < j; i++)
                    {
                        var (x, y) = (order[start + i], order[start + j]);
                        if (!MetBefore(x, y, band, keys, places, sizes)
                            && Estimate(signatures[members[x]]!, signatures[members[y]]!) >= floor)
                        {
                            yield return (members[x], members[y]);
                        }
                    }
                }
            }
        }
    }

    // Whether an earlier band already produced the pair: same bucket, compared pair by pair or within the window.
    private static bool MetBefore(int x, int y, int band, ushort[][] keys, int[][] places, int[][] sizes)
    {
        for (var earlier = 0; earlier < band; earlier++)
        {
            var key = keys[earlier][x];
            if (key == keys[earlier][y] && (sizes[earlier][key] <= FullBucket || places[earlier][y] - places[earlier][x] <= Window))
            {
                return true;
            }
        }

        return false;
    }

    private static int BandKey(byte[] signature, int band)
    {
        var key = 0;
        var start = band * BandBits;
        for (var bit = 0; bit < BandBits; bit++)
        {
            var index = start + bit;
            key = (key << 1) | ((signature[index >> 3] >> (index & 7)) & 1);
        }

        return key;
    }

    private static byte[] Sign(float[] unit)
    {
        var planes = Hyperplanes.GetOrAdd(unit.Length, CreateHyperplanes);
        var signature = new byte[SignatureBytes];
        for (var bit = 0; bit < SignatureBits; bit++)
        {
            if (Dot(planes.AsSpan(bit * unit.Length, unit.Length), unit) >= 0)
            {
                signature[bit >> 3] |= (byte)(1 << (bit & 7));
            }
        }

        return signature;
    }

    private static double Dot(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        double sum = 0;
        for (var i = 0; i < a.Length; i++)
        {
            sum += a[i] * (double)b[i];
        }

        return sum;
    }

    // Gaussian hyperplanes from a fixed seed (SplitMix64 and Box–Muller), so signatures kept in a
    // workspace stay comparable. Should another math library round a last digit differently, only
    // a vector lying on a plane could get another bit, which at most costs a candidate.
    private static float[] CreateHyperplanes(int dimensions)
    {
        var planes = new float[SignatureBits * dimensions];
        var state = 0x5EED_A15E_7A15_0016UL ^ (ulong)dimensions;
        for (var i = 0; i < planes.Length; i += 2)
        {
            var u1 = (Next(ref state) >> 11) * (1.0 / (1UL << 53));
            var u2 = (Next(ref state) >> 11) * (1.0 / (1UL << 53));
            var radius = Math.Sqrt(-2 * Math.Log(1 - u1));
            planes[i] = (float)(radius * Math.Cos(2 * Math.PI * u2));
            if (i + 1 < planes.Length)
            {
                planes[i + 1] = (float)(radius * Math.Sin(2 * Math.PI * u2));
            }
        }

        return planes;

        static ulong Next(ref ulong state)
        {
            var z = state += 0x9E3779B97F4A7C15UL;
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
            return z ^ (z >> 31);
        }
    }
}
