using AIDatasetStudio.Infrastructure.Search;

namespace AIDatasetStudio.Infrastructure.Tests.Search;

public sealed class SearchTextTests
{
    [Theory]
    [InlineData("İzin", "izin")]
    [InlineData("ILIK", "ilik")]
    [InlineData("ılık", "ilik")]
    [InlineData("Şifre ÇÖĞÜ", "şifre çöğü")]
    public void Normalize_folds_every_i_and_lowercases(string input, string expected)
    {
        Assert.Equal(expected, SearchText.Normalize(input));
    }

    [Fact]
    public void Match_expression_quotes_every_word_as_a_prefix()
    {
        Assert.Equal("\"yillik\"* \"izin\"*", SearchText.ToMatchExpression("  Yıllık   İZİN "));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("- * ( )")]
    public void Queries_without_words_mean_no_search(string? query)
    {
        Assert.Null(SearchText.ToMatchExpression(query));
    }

    [Fact]
    public void Quotes_in_queries_are_escaped()
    {
        Assert.Equal("\"say\"\"hi\"*", SearchText.ToMatchExpression("say\"hi"));
    }
}
