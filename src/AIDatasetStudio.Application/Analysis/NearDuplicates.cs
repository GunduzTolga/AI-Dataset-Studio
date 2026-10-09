using System.Buffers.Binary;

namespace AIDatasetStudio.Application.Analysis;

/// <summary>
/// Near-duplicate questions (SDD §8.2, rule 2): two records whose normalized questions share at
/// least <see cref="Threshold"/> of their character 5-grams (Jaccard similarity), such as
/// "Kargo ücreti ne kadar?" and "Kargo ücreti ne kadardır?". This is a textual check;
/// paraphrases with other words ("Parolamı unuttum" / "Şifremi nasıl sıfırlarım?") need
/// embeddings and are left for later.
/// </summary>
/// <remarks>
/// Every record gets a MinHash signature of <see cref="SignatureLength"/> 16-bit values when it
/// is analysed. Locality-sensitive hashing (<see cref="Bands"/> bands of <see cref="Rows"/>
/// values) turns them into candidate pairs in near-linear time; a candidate is kept only when the
/// exact Jaccard similarity of the two texts reaches the threshold, so the threshold is exact and
/// the result does not depend on hash luck. Records are then grouped transitively. Records with
/// exactly the same question join one group without a comparison (LSH sees only the first of
/// them); whether such twins count as near-duplicates of each other is for the caller to decide,
/// since "same question, different answer" and exact copies have their own rules.
/// </remarks>
public static class NearDuplicates
{
    /// <summary>
    /// Share of common 5-grams from which two questions are near-duplicates.
    /// </summary>
    public const double Threshold = 0.85;

    public const int ShingleLength = 5;
    public const int Bands = 16;
    public const int Rows = 4;
    public const int SignatureLength = Bands * Rows;

    /// <summary>
    /// Size of a stored signature in bytes.
    /// </summary>
    public const int SignatureBytes = SignatureLength * sizeof(ushort);

    // Candidates whose signatures agree on less than this share are not compared exactly. With 64
    // values a true 0.85 falls below it with a probability of about 1 in 2,000.
    private const double EstimateFloor = 0.7;

    // Within an LSH bucket each record is compared with this many records before it, so a bucket
    // of thousands of similar records costs linear time; the groups still connect through the chain.
    private const int Window = 4;

    private static readonly ulong[] Seeds = CreateSeeds();

    /// <summary>
    /// The MinHash signature of a normalized question (see <see cref="RecordAnalyzer.Normalize"/>),
    /// little-endian; <c>null</c> for an empty question.
    /// </summary>
    public static byte[]? Signature(string normalizedQuestion)
    {
        ArgumentNullException.ThrowIfNull(normalizedQuestion);
        var shingles = Shingles(normalizedQuestion);
        if (shingles.Count == 0)
        {
            return null;
        }

        var signature = new byte[SignatureBytes];
        for (var i = 0; i < SignatureLength; i++)
        {
            var min = ulong.MaxValue;
            foreach (var shingle in shingles)
            {
                min = Math.Min(min, Mix(shingle ^ Seeds[i]));
            }

            // The top bits of the smallest value; 16 bits are plenty to compare and halve the storage.
            BinaryPrimitives.WriteUInt16LittleEndian(signature.AsSpan(i * sizeof(ushort)), (ushort)(min >> 48));
        }

        return signature;
    }

    /// <summary>
    /// The character 5-grams of a normalized text, hashed; a shorter text is one shingle.
    /// </summary>
    public static HashSet<ulong> Shingles(string normalized)
    {
        ArgumentNullException.ThrowIfNull(normalized);
        var shingles = new HashSet<ulong>();
        if (normalized.Length == 0)
        {
            return shingles;
        }

        if (normalized.Length <= ShingleLength)
        {
            shingles.Add(Hash(normalized));
            return shingles;
        }

        for (var i = 0; i + ShingleLength <= normalized.Length; i++)
        {
            shingles.Add(Hash(normalized.AsSpan(i, ShingleLength)));
        }

        return shingles;
    }

    /// <summary>
    /// Shared shingles divided by all shingles of the two sets; 0 when both are empty.
    /// </summary>
    public static double Jaccard(IReadOnlySet<ulong> a, IReadOnlySet<ulong> b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        if (a.Count == 0 && b.Count == 0)
        {
            return 0;
        }

        var (small, large) = a.Count <= b.Count ? (a, b) : (b, a);
        var shared = small.Count(large.Contains);
        return (double)shared / (a.Count + b.Count - shared);
    }

    /// <summary>
    /// The exact similarity of two texts as this rule measures it (normalized, then <see cref="Jaccard"/>).
    /// </summary>
    public static double Similarity(string a, string b) =>
        Jaccard(Shingles(RecordAnalyzer.Normalize(a)), Shingles(RecordAnalyzer.Normalize(b)));

    /// <summary>
    /// The share of equal values of two signatures, an estimate of their Jaccard similarity.
    /// </summary>
    public static double Estimate(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
    {
        var equal = 0;
        for (var i = 0; i < SignatureBytes; i += sizeof(ushort))
        {
            if (a[i] == b[i] && a[i + 1] == b[i + 1])
            {
                equal++;
            }
        }

        return (double)equal / SignatureLength;
    }

    /// <summary>
    /// Pairs worth an exact comparison: records whose signatures fall into the same bucket of a
    /// band and agree enough overall. Of the records with the same question key only the first
    /// takes part; <see cref="Group(int, IReadOnlyList{string}, IEnumerable{ValueTuple{int, int}}, Func{int, int, bool})"/> joins the others to it.
    /// </summary>
    /// <param name="signatures">One per record in list order; <c>null</c> for records without a question.</param>
    /// <param name="questionKeys">The records' question keys (exactly the same normalized question), or <c>null</c>.</param>
    /// <returns>Index pairs, the smaller index first.</returns>
    public static IReadOnlyList<(int A, int B)> Candidates(IReadOnlyList<byte[]?> signatures, IReadOnlyList<string?> questionKeys)
    {
        ArgumentNullException.ThrowIfNull(signatures);
        ArgumentNullException.ThrowIfNull(questionKeys);
        // Not a packed long: its hash code is a ^ b, which collides for nearby indexes.
        var seen = new HashSet<(int, int)>();
        var pairs = new List<(int A, int B)>();
        var buckets = new Dictionary<ulong, List<int>>();
        var firsts = new HashSet<string>(StringComparer.Ordinal);
        var representatives = Enumerable.Range(0, signatures.Count)
            .Where(i => signatures[i] is not null && (questionKeys[i] is not { } key || firsts.Add(key)))
            .ToList();
        for (var band = 0; band < Bands; band++)
        {
            buckets.Clear();
            foreach (var i in representatives)
            {
                var signature = signatures[i]!;

                // Four 16-bit values make exactly one 64-bit bucket key.
                var key = BinaryPrimitives.ReadUInt64LittleEndian(signature.AsSpan(band * Rows * sizeof(ushort)));
                if (!buckets.TryGetValue(key, out var bucket))
                {
                    buckets[key] = bucket = [];
                }

                bucket.Add(i);
            }

            foreach (var bucket in buckets.Values)
            {
                for (var j = 1; j < bucket.Count; j++)
                {
                    for (var back = 1; back <= Window && back <= j; back++)
                    {
                        var (a, b) = (bucket[j - back], bucket[j]);
                        if (seen.Add((a, b)) && Estimate(signatures[a]!, signatures[b]!) >= EstimateFloor)
                        {
                            pairs.Add((a, b));
                        }
                    }
                }
            }
        }

        return pairs;
    }

    /// <summary>
    /// Joins records into groups: two records are in one group when they ask exactly the same
    /// question or a chain of similar pairs links them. Pairs already in one group are not checked again.
    /// </summary>
    /// <param name="count">The number of records.</param>
    /// <param name="questionKeys">The records' question keys, or <c>null</c>.</param>
    /// <param name="candidates">Pairs to check (see <see cref="Candidates"/>).</param>
    /// <param name="similar">Whether a pair is near-duplicate (the exact check).</param>
    /// <returns>For every record the smallest index of its group, i.e. its first record in list order.</returns>
    public static int[] Group(int count, IReadOnlyList<string?> questionKeys, IEnumerable<(int A, int B)> candidates, Func<int, int, bool> similar)
    {
        ArgumentNullException.ThrowIfNull(questionKeys);
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(similar);
        var parent = Enumerable.Range(0, count).ToArray();
        var firsts = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < count; i++)
        {
            if (questionKeys[i] is { } key && !firsts.TryAdd(key, i))
            {
                Union(firsts[key], i);
            }
        }

        foreach (var (a, b) in candidates)
        {
            if (Find(a) != Find(b) && similar(a, b))
            {
                Union(a, b);
            }
        }

        return [.. Enumerable.Range(0, count).Select(Find)];

        // The smaller index stays the root, so a group is named after its first record.
        void Union(int a, int b)
        {
            var (ra, rb) = (Find(a), Find(b));
            if (ra != rb)
            {
                parent[Math.Max(ra, rb)] = Math.Min(ra, rb);
            }
        }

        int Find(int i)
        {
            while (parent[i] != i)
            {
                parent[i] = parent[parent[i]];
                i = parent[i];
            }

            return i;
        }
    }

    /// <summary>
    /// Groups questions in memory, as the analysis does from stored signatures (for tests and small sets).
    /// </summary>
    /// <returns>For every question the index of the first question of its group.</returns>
    public static int[] Group(IReadOnlyList<string?> questions)
    {
        ArgumentNullException.ThrowIfNull(questions);
        var normalized = questions.Select(q => q is null ? null : RecordAnalyzer.Normalize(q)).ToList();
        var signatures = normalized.Select(n => n is null ? null : Signature(n)).ToList();
        var shingles = normalized.Select(n => n is null ? null : Shingles(n)).ToList();
        return Group(questions.Count, normalized, Candidates(signatures, normalized), (a, b) => Jaccard(shingles[a]!, shingles[b]!) >= Threshold);
    }

    // FNV-1a over the UTF-16 code units.
    private static ulong Hash(ReadOnlySpan<char> text)
    {
        var hash = 14695981039346656037UL;
        foreach (var c in text)
        {
            hash = (hash ^ c) * 1099511628211UL;
        }

        return hash;
    }

    // SplitMix64's finalizer: a fast, well-mixed permutation of 64-bit values.
    private static ulong Mix(ulong z)
    {
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }

    // Fixed seeds: signatures must be the same in every run and on every platform.
    private static ulong[] CreateSeeds()
    {
        var seeds = new ulong[SignatureLength];
        var state = 0x4144535F4D494E48UL; // "ADS_MINH"
        for (var i = 0; i < seeds.Length; i++)
        {
            state += 0x9E3779B97F4A7C15UL;
            seeds[i] = Mix(state);
        }

        return seeds;
    }
}
