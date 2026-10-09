using AIDatasetStudio.Application.Analysis;
using AIDatasetStudio.Domain.Enums;
using AIDatasetStudio.Domain.Payloads;

namespace AIDatasetStudio.Application.Tests.Analysis;

/// <summary>
/// Questions alike in meaning (M16): stored vectors, their cosines and the candidate search.
/// </summary>
public sealed class SemanticQuestionsTests
{
    [Fact]
    public void Stored_vectors_keep_the_cosine_within_a_hundredth()
    {
        var random = new Random(16);
        for (var i = 0; i < 50; i++)
        {
            var a = Gaussian(random, 768);
            var b = Mix(a, Gaussian(random, 768), random.NextDouble());
            var (va, vb) = (SemanticQuestions.Encode(a), SemanticQuestions.Encode(b));

            Assert.Equal(TrueCosine(a, b), SemanticQuestions.Cosine(va.Values, va.Scale, vb.Values, vb.Scale), 0.01);
        }

        var raw = Gaussian(random, 33);
        var v = SemanticQuestions.Encode(raw);
        var negated = SemanticQuestions.Encode([.. raw.Select(x => -x)]);
        Assert.Equal(1, SemanticQuestions.Cosine(v.Values, v.Scale, v.Values, v.Scale), 0.01);
        Assert.Equal(-1, SemanticQuestions.Cosine(v.Values, v.Scale, negated.Values, negated.Scale), 0.01);
        Assert.Equal(33, v.Values.Length);
        Assert.Equal(SemanticQuestions.SignatureBytes, v.Signature.Length);
        Assert.Equal(0, SemanticQuestions.Cosine(v.Values, v.Scale, new sbyte[34], 1));

        // Scaling a vector does not change it.
        Assert.Equal(SemanticQuestions.Encode([1, 2, 3]).Values, SemanticQuestions.Encode([10, 20, 30]).Values);
        Assert.Equal(SemanticQuestions.Encode([1, 2, 3]).Signature, SemanticQuestions.Encode([10, 20, 30]).Signature);
        Assert.Throws<ArgumentException>(() => SemanticQuestions.Encode([0, 0, 0]));
        Assert.Throws<ArgumentException>(() => SemanticQuestions.Encode([]));
        Assert.Throws<ArgumentException>(() => SemanticQuestions.Encode([float.NaN, 1]));
    }

    [Fact]
    public void Signatures_estimate_the_cosine_and_do_not_change_between_runs()
    {
        var random = new Random(7);
        foreach (var cosine in new[] { 0.95, 0.9, 0.7, 0.3, 0.0 })
        {
            var a = Gaussian(random, 384);
            var b = Mix(a, Gaussian(random, 384), cosine);
            var actual = TrueCosine(a, b);

            Assert.Equal(actual, SemanticQuestions.Estimate(SemanticQuestions.Encode(a).Signature, SemanticQuestions.Encode(b).Signature), 0.2);
        }

        // Stored signatures stay valid only while the hyperplanes are the same.
        float[] fixedVector = [1, -2, 3, -4, 5, -6, 7, -8];
        Assert.Equal("1C038C794C9D", Convert.ToHexString(SemanticQuestions.Encode(fixedVector).Signature)[..12]);
    }

    [Fact]
    public void Candidates_find_close_questions_once_and_skip_far_ones()
    {
        var random = new Random(42);
        var vectors = new List<float[]>();
        for (var i = 0; i < 400; i++)
        {
            vectors.Add(Gaussian(random, 64));
        }

        // Forty questions get a close neighbor at the end of the list.
        var close = new List<(int A, int B)>();
        for (var i = 0; i < 40; i++)
        {
            close.Add((i * 10, vectors.Count));
            vectors.Add(Mix(vectors[i * 10], Gaussian(random, 64), 0.95));
        }

        var signatures = vectors.Select(v => SemanticQuestions.Encode(v).Signature).ToList<byte[]?>();
        var keys = new string?[vectors.Count];
        var pairs = SemanticQuestions.Candidates(signatures, keys, 0.9).ToList();

        Assert.All(close, pair => Assert.Contains(pair, pairs));
        Assert.Equal(pairs.Count, pairs.Distinct().Count());
        Assert.All(pairs, pair => Assert.True(pair.A < pair.B));
        // Random 64-dimensional vectors are far apart: hardly any of their 80,000 pairs is checked.
        Assert.True(pairs.Count < close.Count + 50, $"{pairs.Count} candidates");
    }

    [Fact]
    public void Of_records_with_the_same_question_only_the_first_takes_part()
    {
        float[] question = [1, 2, 3, 4];
        float[] alike = [1, 2, 3, 4.2f];
        var signatures = new[] { question, question, alike, null }.Select(v => v is null ? null : SemanticQuestions.Encode(v).Signature).ToList();

        var pairs = SemanticQuestions.Candidates(signatures, ["k1", "k1", "k2", "k3"], 0.9).ToList();

        Assert.Equal([(0, 2)], pairs);
    }

    [Fact]
    public void A_crowded_bucket_is_compared_along_a_window_and_still_groups_everything()
    {
        // Two thousand questions with nearly one meaning crowd the buckets: large ones are compared
        // along a window, so fewer than all two million pairs are produced, and the chain still
        // links them all.
        var random = new Random(3);
        var center = Gaussian(random, 32);
        var vectors = Enumerable.Range(0, 2_000).Select(_ => Mix(center, Gaussian(random, 32), 0.995)).ToList();
        var encoded = vectors.Select(v => SemanticQuestions.Encode(v)).ToList();
        var keys = new string?[vectors.Count];

        var pairs = SemanticQuestions.Candidates([.. encoded.Select(e => e.Signature)], keys, 0.9).ToList();
        var roots = NearDuplicates.Group(vectors.Count, keys, pairs, (a, b) =>
            SemanticQuestions.Cosine(encoded[a].Values, encoded[a].Scale, encoded[b].Values, encoded[b].Scale) >= 0.9);

        Assert.InRange(pairs.Count, vectors.Count - 1, vectors.Count * (vectors.Count - 1) / 2 * 3 / 4);
        Assert.Equal(pairs.Count, pairs.Distinct().Count());
        Assert.All(roots, root => Assert.Equal(0, root));
    }

    [Fact]
    public void The_compared_text_is_the_question_without_the_system_prompt_if_there_is_one()
    {
        Assert.Equal("Özetle\nUzun metin", SemanticQuestions.Text(new InstructionPayload("Özetle", "Uzun metin", "Kısa")));
        Assert.Equal("Özetle", SemanticQuestions.Text(new InstructionPayload(" Özetle ", " ", "Kısa")));
        Assert.Equal(
            "Merhaba\nKargo ne zaman gelir?",
            SemanticQuestions.Text(new ChatPayload(
                "Sen bir destek asistanısın.",
                [new(ChatRole.User, "Merhaba"), new(ChatRole.Assistant, "Buyrun"), new(ChatRole.User, " Kargo ne zaman gelir? ")])));
        Assert.Equal("Sen bir destek asistanısın.", SemanticQuestions.Text(new ChatPayload("Sen bir destek asistanısın.", [new(ChatRole.Assistant, "Merhaba")])));
        Assert.Null(SemanticQuestions.Text(new PreferencePayload("Soru", "İyi", "Kötü")));
        Assert.Null(SemanticQuestions.Text(new InstructionPayload("  ", "", "Yanıt")));
        Assert.Equal(SemanticQuestions.MaxTextLength, SemanticQuestions.Text(new InstructionPayload(new string('a', 5_000), "", "b"))!.Length);
    }

    private static float[] Gaussian(Random random, int dimensions) =>
        [.. Enumerable.Range(0, dimensions).Select(_ => (float)(Math.Sqrt(-2 * Math.Log(1 - random.NextDouble())) * Math.Cos(2 * Math.PI * random.NextDouble())))];

    // A vector whose cosine with a is about the given one: a's direction plus noise across it.
    private static float[] Mix(float[] a, float[] noise, double cosine)
    {
        var (ua, un) = (Unit(a), Unit(noise));
        var along = ua.Zip(un, (x, y) => (double)x * y).Sum();
        var across = Unit([.. un.Select((y, i) => (float)(y - along * ua[i]))]);
        var sine = Math.Sqrt(1 - cosine * cosine);
        return [.. ua.Select((x, i) => (float)(cosine * x + sine * across[i]))];
    }

    private static float[] Unit(float[] v)
    {
        var norm = Math.Sqrt(v.Sum(x => (double)x * x));
        return [.. v.Select(x => (float)(x / norm))];
    }

    private static double TrueCosine(float[] a, float[] b) => Unit(a).Zip(Unit(b), (x, y) => (double)x * y).Sum();
}
