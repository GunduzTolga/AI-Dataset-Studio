using AIDatasetStudio.Application.Analysis;
using AIDatasetStudio.Domain.Enums;
using AIDatasetStudio.Domain.Payloads;

namespace AIDatasetStudio.Application.Tests.Analysis;

public sealed class RecordAnalyzerTests
{
    private static readonly RecordAnalyzer Analyzer = new(new WordCounter(), new PiiScanner());

    [Fact]
    public void Clean_instruction_scores_100_with_tokens_split_by_side()
    {
        var result = Analyzer.Analyze(new InstructionPayload("Başkent neresi?", "Türkiye için", "Türkiye'nin başkenti Ankara'dır."));

        Assert.Empty(result.Issues);
        Assert.Equal(100, result.Score);
        Assert.Equal(new TokenCounts(Input: 4, Output: 3), result.Tokens);
        Assert.Equal(7, result.Tokens.Total);
        Assert.NotNull(result.QuestionKey);
    }

    [Fact]
    public void Empty_fields_are_errors()
    {
        var result = Analyzer.Analyze(new InstructionPayload("Soru?", "", "   "));

        Assert.Equal([new QualityIssue(QualityRule.EmptyContent, IssueSeverity.Error, "Output")], result.Issues);
        Assert.Equal(60, result.Score);
    }

    [Theory]
    [InlineData("Tamam.")]
    [InlineData("EVET")]
    [InlineData("Bilmiyorum...")]
    [InlineData("I don't know")]
    [InlineData("-")]
    public void Throwaway_answers_are_flagged(string answer)
    {
        Assert.Contains(Analyzer.Analyze(new InstructionPayload("Soru?", "", answer)).Issues, i => i.Rule == QualityRule.ShortAnswer);
    }

    [Fact]
    public void A_short_but_real_answer_is_fine()
    {
        Assert.Empty(Analyzer.Analyze(new InstructionPayload("Başkent neresi?", "", "Ankara.")).Issues);
    }

    [Fact]
    public void Answers_repeating_the_question_are_flagged()
    {
        var echo = Analyzer.Analyze(new InstructionPayload("Şifremi unuttum", "", "şifremi  UNUTTUM!"));

        Assert.Contains(echo.Issues, i => i.Rule == QualityRule.EchoedInput);
    }

    [Fact]
    public void Answers_much_longer_than_the_question_are_flagged()
    {
        var longAnswer = string.Join(' ', Enumerable.Repeat("kelime", 250));

        var result = Analyzer.Analyze(new InstructionPayload("Özetle", "", longAnswer));

        Assert.Contains(result.Issues, i => i.Rule == QualityRule.UnbalancedLength);
        Assert.DoesNotContain(Analyzer.Analyze(new InstructionPayload("Bunu tüm ayrıntılarıyla ve örneklerle detaylı anlat lütfen", "", longAnswer)).Issues, i => i.Rule == QualityRule.UnbalancedLength);
    }

    [Fact]
    public void Unclosed_code_blocks_and_broken_json_are_flagged_per_field()
    {
        var result = Analyzer.Analyze(new InstructionPayload(
            "Kod yaz",
            "{\"dil\": \"python\",}",
            "```python\nprint('merhaba')\n\nAyrıca:\n```json\n{\"a\": 1,, }\n```"));

        Assert.Contains(new QualityIssue(QualityRule.UnclosedCodeBlock, IssueSeverity.Warning, "Output"), result.Issues);
        Assert.Contains(new QualityIssue(QualityRule.InvalidJson, IssueSeverity.Warning, "Output"), result.Issues);
        Assert.DoesNotContain(result.Issues, i => i.Rule == QualityRule.InvalidJson && i.Detail == "Input"); // trailing commas are tolerated
    }

    [Fact]
    public void Closed_code_blocks_and_valid_json_pass()
    {
        var result = Analyzer.Analyze(new InstructionPayload("Kod", "", "```json\n{\"a\": [1, 2]}\n```\n\n```\nx\n```"));

        Assert.Empty(result.Issues);
    }

    [Fact]
    public void Personal_data_is_reported_by_kind()
    {
        var result = Analyzer.Analyze(new InstructionPayload("Bana yazın", "", "ali@firma.com veya 0532 123 45 67"));

        Assert.Contains(new QualityIssue(QualityRule.PersonalData, IssueSeverity.Warning, "Email,Phone"), result.Issues);
    }

    [Fact]
    public void Chats_need_user_and_assistant_turns()
    {
        var noAnswer = Analyzer.Analyze(new ChatPayload(null, [new ChatMessage(ChatRole.User, "Merhaba")]));
        var emptyTurn = Analyzer.Analyze(new ChatPayload(null, [
            new ChatMessage(ChatRole.User, "Merhaba"),
            new ChatMessage(ChatRole.Assistant, "Selam"),
            new ChatMessage(ChatRole.User, " "),
        ]));

        Assert.Equal([new QualityIssue(QualityRule.EmptyContent, IssueSeverity.Error, "Assistant")], noAnswer.Issues);
        Assert.Equal([new QualityIssue(QualityRule.EmptyContent, IssueSeverity.Error, "User")], emptyTurn.Issues);
    }

    [Fact]
    public void Chat_tokens_put_system_and_user_turns_on_the_input_side()
    {
        var result = Analyzer.Analyze(new ChatPayload("Kısa yanıt ver", [
            new ChatMessage(ChatRole.User, "Siparişim nerede"),
            new ChatMessage(ChatRole.Assistant, "Yolda"),
            new ChatMessage(ChatRole.User, "Teşekkürler"),
            new ChatMessage(ChatRole.Assistant, "Rica ederim, iyi günler"),
        ]));

        Assert.Equal(new TokenCounts(Input: 6, Output: 5), result.Tokens);
    }

    [Fact]
    public void Preference_pairs_use_the_longer_answer_and_need_different_answers()
    {
        var same = Analyzer.Analyze(new PreferencePayload("Başkent?", "Ankara.", "ankara"));
        var pair = Analyzer.Analyze(new PreferencePayload("Başkent?", "Türkiye'nin başkenti Ankara'dır.", "İstanbul."));

        Assert.Contains(same.Issues, i => i.Rule == QualityRule.IdenticalAnswers);
        Assert.Equal(new TokenCounts(Input: 1, Output: 3), pair.Tokens);
        Assert.Null(pair.QuestionKey); // several pairs per prompt are normal
    }

    [Fact]
    public void Question_keys_ignore_case_spacing_and_punctuation()
    {
        var a = Analyzer.Analyze(new InstructionPayload("Şifremi unuttum?", "", "x")).QuestionKey;
        var b = Analyzer.Analyze(new InstructionPayload("şifremi   UNUTTUM", "", "y")).QuestionKey;
        var c = Analyzer.Analyze(new InstructionPayload("Şifremi değiştirdim", "", "x")).QuestionKey;

        Assert.Equal(a, b);
        Assert.NotEqual(a, c);
    }

    [Theory]
    [InlineData(new[] { "EmptyContent" }, 60)]
    [InlineData(new[] { "EmptyContent", "EmptyContent", "EmptyContent" }, 0)]
    [InlineData(new[] { "ShortAnswer", "PersonalData" }, 80)]
    public void Score_subtracts_penalties(string[] rules, int expected)
    {
        var issues = rules.Select(r => Enum.Parse<QualityRule>(r)).Select(r => new QualityIssue(r, QualityRules.SeverityOf(r)));

        Assert.Equal(expected, QualityRules.Score(issues));
    }

    /// <summary>
    /// One token per whitespace-separated word, so expected counts are easy to read.
    /// </summary>
    private sealed class WordCounter : ITokenCounter
    {
        public int CountTokens(string text) => text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
    }
}
