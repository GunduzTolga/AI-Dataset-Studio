using System.Diagnostics;
using AIDatasetStudio.Application.Analysis;

namespace AIDatasetStudio.Application.Tests.Analysis;

[Collection(TimingCollection.Name)]
public sealed class NearDuplicatesTests
{
    [Fact]
    public void Similarity_is_the_share_of_common_five_grams()
    {
        // "kargo ucreti ne kadar" has 17 five-grams, all of them in "kargo ucreti ne kadardir" (20).
        Assert.Equal(17.0 / 20, NearDuplicates.Similarity("Kargo ücreti ne kadar?", "Kargo ücreti ne kadardır?"), 10);
        Assert.Equal(1.0, NearDuplicates.Similarity("ŞİFREMİ UNUTTUM!", "şifremi   unuttum"));
        Assert.True(NearDuplicates.Similarity("Siparişim ne zaman gelir?", "Kargo ücreti ne kadar?") < 0.1);
        Assert.Equal(1.0, NearDuplicates.Similarity("Selam", "selam!"));
        Assert.Equal(0, NearDuplicates.Similarity("", "?"));
    }

    [Fact]
    public void Near_questions_are_grouped_under_the_first_one()
    {
        int[] groups = NearDuplicates.Group(
        [
            "Kargo ücreti ne kadar?",
            "Mesai saatleri nedir?",
            "Kargo ücreti ne kadardır?",
            "kargo ücreti ne kadar", // the same question as the first: joins through the third
            null,
            "Mağaza adresi nerede?",
            "Kargo ücreti ne kadardır acaba?", // 0.74 to the first, 0.83 to the third: not near enough
        ]);

        Assert.Equal([0, 1, 0, 0, 4, 5, 6], groups);
    }

    [Fact]
    public void The_same_question_joins_one_group_even_in_a_crowded_bucket()
    {
        // Ten copies of a question and a near one at the end: LSH compares a record with only the
        // few before it in a bucket, so the copies must be joined by their key, not by comparison.
        var questions = Enumerable.Repeat<string?>("Şifremi unuttum, ne yapmalıyım?", 10).Append("Şifremi unuttum, ne yapmalıyım ki?").ToList();

        Assert.All(NearDuplicates.Group(questions), group => Assert.Equal(0, group));
        Assert.True(NearDuplicates.Similarity(questions[0]!, questions[^1]!) >= NearDuplicates.Threshold);
    }

    [Fact]
    public void Signatures_are_stable()
    {
        // Stored signatures are compared with new ones: a change here needs a new rules version.
        var signature = NearDuplicates.Signature("kargo ucreti ne kadar")!;

        Assert.Equal(NearDuplicates.SignatureBytes, signature.Length);
        Assert.Equal("740FED09", Convert.ToHexString(signature)[..8]);
        Assert.Null(NearDuplicates.Signature(string.Empty));
        Assert.Equal(1.0, NearDuplicates.Estimate(signature, NearDuplicates.Signature("kargo ucreti ne kadar")));
    }

    [Fact]
    public void Candidates_include_every_pair_at_the_threshold_and_few_others()
    {
        var random = new Random(7);
        string[] words = ["sipariş", "kargo", "iade", "fatura", "ödeme", "hesap", "şifre", "adres", "mağaza", "ürün", "teslimat", "kampanya", "indirim", "üyelik", "destek"];
        var questions = new List<string>();
        for (var i = 0; i < 400; i++)
        {
            var text = string.Join(' ', Enumerable.Range(0, random.Next(6, 14)).Select(_ => words[random.Next(words.Length)]));
            questions.Add(text);
            questions.Add(random.Next(3) switch
            {
                0 => text + " mi",
                1 => text.Replace(' ', ',') + "?",
                _ => words[random.Next(words.Length)] + " " + text,
            });
        }

        var normalized = questions.Select(RecordAnalyzer.Normalize).ToList();
        var signatures = normalized.Select(NearDuplicates.Signature).ToList();
        var candidates = NearDuplicates.Candidates(signatures, normalized).ToHashSet();
        var shingles = normalized.Select(NearDuplicates.Shingles).ToList();

        var near = 0;
        var checkedPairs = 0;
        for (var a = 0; a < questions.Count; a++)
        {
            for (var b = a + 1; b < questions.Count; b++)
            {
                if (normalized[a] == normalized[b])
                {
                    continue;
                }

                checkedPairs++;
                if (NearDuplicates.Jaccard(shingles[a], shingles[b]) >= NearDuplicates.Threshold)
                {
                    near++;
                    Assert.Contains((a, b), candidates);
                }
            }
        }

        Assert.True(near >= 200, $"only {near} near pairs");
        Assert.True(candidates.Count < near * 3, $"{candidates.Count} candidates for {near} near pairs of {checkedPairs}");
    }

    [Fact]
    public void Templated_questions_take_linear_time()
    {
        // Twenty thousand questions that differ only in a number: LSH puts them in huge buckets.
        var questions = Enumerable.Range(0, 20_000).Select(i => (string?)$"{i}. soruda verilen metni Türkçeden İngilizceye çevirir misiniz?").ToList();

        var watch = Stopwatch.StartNew();
        var groups = NearDuplicates.Group(questions);
        watch.Stop();

        // Comparing every pair would take minutes; the bound leaves room for a busy build machine
        // (test assemblies run side by side on two cores).
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(15), $"took {watch.Elapsed}");
        // A number at the start changes only the few 5-grams that cover it, so most are near.
        Assert.All(Enumerable.Range(1, 9), i => Assert.Equal(0, groups[i]));
    }

    [Fact]
    public void Analysis_keeps_a_signature_of_the_question_only()
    {
        var analyzer = new RecordAnalyzer(new CharCounter(), new PiiScanner());

        var a = analyzer.Analyze(new Domain.Payloads.InstructionPayload("Kargo ücreti ne kadar?", "", "500 TL üzeri ücretsiz."));
        var b = analyzer.Analyze(new Domain.Payloads.InstructionPayload("kargo ücreti ne kadar", "", "Başka bir yanıt."));
        var pair = analyzer.Analyze(new Domain.Payloads.PreferencePayload("Kargo ücreti ne kadar?", "a", "b"));

        Assert.Equal(a.Signature, b.Signature);
        Assert.Null(pair.Signature);
    }

    private sealed class CharCounter : ITokenCounter
    {
        public int CountTokens(string text) => text.Length;
    }
}
