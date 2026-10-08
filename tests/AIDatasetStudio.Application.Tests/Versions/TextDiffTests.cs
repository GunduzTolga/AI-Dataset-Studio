using System.Text;
using AIDatasetStudio.Application.Versions;

namespace AIDatasetStudio.Application.Tests.Versions;

public sealed class TextDiffTests
{
    [Fact]
    public void Edit_scripts_rebuild_both_sequences_with_the_fewest_edits()
    {
        // Random small sequences over a tiny alphabet: many repeats, the hard case for diffs.
        var random = new Random(1234);
        for (var run = 0; run < 500; run++)
        {
            var a = RandomList(random);
            var b = RandomList(random);

            var steps = SequenceDiff.Compute(a, b);

            Assert.Equal(a, steps.Where(s => s.Kind != DiffKind.Inserted).Select(s => a[s.OldIndex]));
            Assert.Equal(b, steps.Where(s => s.Kind != DiffKind.Deleted).Select(s => b[s.NewIndex]));
            Assert.All(steps.Where(s => s.Kind == DiffKind.Equal), s => Assert.Equal(a[s.OldIndex], b[s.NewIndex]));
            Assert.Equal(a.Count + b.Count - (2 * LongestCommonSubsequence(a, b)), steps.Count(s => s.Kind != DiffKind.Equal));
        }
    }

    [Fact]
    public void Past_the_edit_limit_the_middle_is_replaced_as_a_whole()
    {
        // "xyxy…" against "yxyx…": the shortest script shifts by one (2 edits).
        var a = Enumerable.Range(0, 50).Select(i => i % 2 == 0 ? "x" : "y").Prepend("start").Append("end").ToList();
        var b = Enumerable.Range(0, 50).Select(i => i % 2 == 0 ? "y" : "x").Prepend("start").Append("end").ToList();

        Assert.Equal(2, SequenceDiff.Compute(a, b).Count(s => s.Kind != DiffKind.Equal));

        var steps = SequenceDiff.Compute(a, b, maxEdits: 1);

        Assert.Equal(DiffKind.Equal, steps[0].Kind);
        Assert.Equal(DiffKind.Equal, steps[^1].Kind);
        Assert.Equal(50, steps.Count(s => s.Kind == DiffKind.Deleted));
        Assert.Equal(50, steps.Count(s => s.Kind == DiffKind.Inserted));
        Assert.Equal(b, steps.Where(s => s.Kind != DiffKind.Deleted).Select(s => b[s.NewIndex]));
    }

    [Fact]
    public void Words_are_compared_not_characters()
    {
        var diff = TextDiff.Compute("Şifremi nasıl sıfırlarım?", "Parolamı nasıl sıfırlarım?");

        Assert.Equal(
            [new(DiffKind.Deleted, "Şifremi"), new(DiffKind.Inserted, "Parolamı"), new(DiffKind.Equal, " nasıl sıfırlarım?")],
            diff.Segments);
    }

    [Fact]
    public void A_rewritten_phrase_is_one_change()
    {
        var diff = TextDiff.Compute("the quick brown fox", "a slow red fox");

        Assert.Equal(
            [new(DiffKind.Deleted, "the quick brown"), new(DiffKind.Inserted, "a slow red"), new(DiffKind.Equal, " fox")],
            diff.Segments);
    }

    [Fact]
    public void Line_breaks_keep_changes_apart()
    {
        var diff = TextDiff.Compute("one\ntwo", "uno\ndos");

        Assert.Equal(
            [new(DiffKind.Deleted, "one"), new(DiffKind.Inserted, "uno"), new(DiffKind.Equal, "\n"), new(DiffKind.Deleted, "two"), new(DiffKind.Inserted, "dos")],
            diff.Segments);
    }

    [Theory]
    [InlineData("", "")]
    [InlineData("", "yeni metin")]
    [InlineData("eski metin", "")]
    [InlineData("Aynı metin.", "Aynı metin.")]
    [InlineData("Merhaba 👋 dünya", "Merhaba 🌍 dünya")]
    [InlineData("a\r\nb  c\t d", "a\nb c d\r\n")]
    [InlineData("İstanbul'da ışık", "istanbul'da IŞIK")]
    public void Each_side_rebuilds_its_text(string oldText, string newText)
    {
        var diff = TextDiff.Compute(oldText, newText);

        Assert.Equal(oldText, string.Concat(diff.OldSide.Select(s => s.Text)));
        Assert.Equal(newText, string.Concat(diff.NewSide.Select(s => s.Text)));
        Assert.Equal(oldText != newText, diff.HasChanges);
        Assert.DoesNotContain(diff.Segments, s => s.Text.Length == 0);
    }

    [Fact]
    public void Random_edits_rebuild_both_texts()
    {
        var random = new Random(42);
        string[] words = ["veri", "seti", "model", " ", " ", "\n", ".", ",", "şifre", "İ", "ı", "😀"];
        for (var run = 0; run < 300; run++)
        {
            var oldText = RandomText(random, words);
            var newText = RandomText(random, words);

            var diff = TextDiff.Compute(oldText, newText);

            Assert.Equal(oldText, string.Concat(diff.OldSide.Select(s => s.Text)));
            Assert.Equal(newText, string.Concat(diff.NewSide.Select(s => s.Text)));
            for (var i = 1; i < diff.Segments.Count; i++)
            {
                Assert.False(diff.Segments[i].Kind == diff.Segments[i - 1].Kind, "Neighbouring segments of one kind are merged.");
            }
        }
    }

    [Fact]
    public void Surrogate_pairs_are_never_split()
    {
        Assert.Equal(["a", "👋", "b"], TextDiff.Tokenize("a👋b"));
        Assert.Equal(["Merhaba", " ", "dünya", "\r\n", "!"], TextDiff.Tokenize("Merhaba dünya\r\n!"));
    }

    [Fact]
    public void Long_unrelated_texts_finish_quickly()
    {
        var oldText = string.Join(' ', Enumerable.Range(0, 20_000).Select(i => "kelime" + (i % 997)));
        var newText = string.Join(' ', Enumerable.Range(0, 20_000).Select(i => "sözcük" + (i % 991)));

        var diff = TextDiff.Compute(oldText, newText);

        Assert.Equal(oldText, string.Concat(diff.OldSide.Select(s => s.Text)));
        Assert.Equal(newText, string.Concat(diff.NewSide.Select(s => s.Text)));
    }

    private static List<char> RandomList(Random random) =>
        [.. Enumerable.Range(0, random.Next(0, 12)).Select(_ => (char)('a' + random.Next(0, 3)))];

    private static string RandomText(Random random, string[] words)
    {
        var builder = new StringBuilder();
        for (var i = random.Next(0, 15); i > 0; i--)
        {
            builder.Append(words[random.Next(words.Length)]);
        }

        return builder.ToString();
    }

    private static int LongestCommonSubsequence(List<char> a, List<char> b)
    {
        var table = new int[a.Count + 1, b.Count + 1];
        for (var i = 1; i <= a.Count; i++)
        {
            for (var j = 1; j <= b.Count; j++)
            {
                table[i, j] = a[i - 1] == b[j - 1] ? table[i - 1, j - 1] + 1 : Math.Max(table[i - 1, j], table[i, j - 1]);
            }
        }

        return table[a.Count, b.Count];
    }
}
