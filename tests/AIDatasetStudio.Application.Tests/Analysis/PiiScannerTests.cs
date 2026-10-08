using AIDatasetStudio.Application.Analysis;
using AIDatasetStudio.Domain.Enums;
using AIDatasetStudio.Domain.Payloads;

namespace AIDatasetStudio.Application.Tests.Analysis;

public sealed class PiiScannerTests
{
    private static readonly PiiScanner Scanner = new();

    [Theory]
    [InlineData("Bana ali.veli@sirket.com.tr adresinden yazın.", "ali.veli@sirket.com.tr")]
    [InlineData("İletişim: destek@firma.istanbul", "destek@firma.istanbul")]
    [InlineData("Ara: +90 532 123 45 67 lütfen", "+90 532 123 45 67")]
    [InlineData("Cep: 0532 123 45 67", "0532 123 45 67")]
    [InlineData("Cep: 05321234567.", "05321234567")]
    [InlineData("Santral (0212) 555 12 34", "(0212) 555 12 34")]
    [InlineData("TC: 10000000146", "10000000146")]
    [InlineData("IBAN: TR33 0006 1005 1978 6457 8413 26", "TR33 0006 1005 1978 6457 8413 26")]
    [InlineData("Kart 4111 1111 1111 1111 ile ödendi", "4111 1111 1111 1111")]
    [InlineData("Sunucu 192.168.1.10 erişilemiyor", "192.168.1.10")]
    [InlineData("Wiki: https://wiki.sirket.local/sayfa?id=3", "https://wiki.sirket.local/sayfa?id=3")]
    [InlineData("key=AKIAIOSFODNN7EXAMPLE", "AKIAIOSFODNN7EXAMPLE")]
    [InlineData("export OPENAI_API_KEY=sk-proj-abcdefghijklmnopqrstuv123", "sk-proj-abcdefghijklmnopqrstuv123")]
    [InlineData("anthropic: sk-ant-api03-abcdefghijklmnopqrstuvwxyz", "sk-ant-api03-abcdefghijklmnopqrstuvwxyz")]
    [InlineData("Authorization: Bearer abcdefghijklmnop1234", "abcdefghijklmnop1234")]
    [InlineData("Server=db;User Id=sa;Password=Hunter2!;", "Hunter2!")]
    [InlineData("postgres://admin:s3cret@db:5432/app", "s3cret")]
    public void Finds_personal_and_secret_data(string text, string expected)
    {
        var match = Assert.Single(Scanner.Find(text));

        Assert.Equal(expected, match.Value);
        Assert.Equal(expected, text.Substring(match.Start, match.Length));
    }

    [Theory]
    [InlineData("Sipariş no: 12345, tarih 2024-01-15")]
    [InlineData("TC: 10000000147")] // wrong check digit
    [InlineData("Kart 4111 1111 1111 1112")] // fails Luhn
    [InlineData("IBAN: TR33 0006 1005 1978 6457 8413 27")] // fails mod-97
    [InlineData("DNS 8.8.8.8 ve 172.32.0.1 herkese açık")]
    [InlineData("Şifre: Güçlü bir şifre seçin")]
    [InlineData("Şifremi unuttum, ne yapmalıyım?")]
    [InlineData("Fiyat 1.250,00 TL; stok 3500 adet")]
    public void Ignores_look_alikes(string text)
    {
        Assert.Empty(Scanner.Find(text));
    }

    [Fact]
    public void Finds_private_key_blocks_and_jwts()
    {
        var text = "-----BEGIN RSA PRIVATE KEY-----\nMIIEow\n-----END RSA PRIVATE KEY-----\ntoken eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxMjM0NTY3ODkwIn0.dozjgNryP4J3jVmNHl0w5N_XgL0n3I9PlFUP0THsR8U";

        Assert.Equal([PiiKind.Secret, PiiKind.Secret], Scanner.Find(text).Select(m => m.Kind));
    }

    [Fact]
    public void Specific_rules_win_where_findings_overlap()
    {
        // The e-mail sits inside a URL with credentials; the IBAN contains card-like digit runs.
        var findings = Scanner.Find("https://ali:Parola123@wiki.sirket.local ve TR33 0006 1005 1978 6457 8413 26");

        Assert.Contains(findings, f => f.Kind == PiiKind.Secret && f.Value == "Parola123");
        Assert.Contains(findings, f => f.Kind == PiiKind.Iban);
        Assert.DoesNotContain(findings, f => f.Kind == PiiKind.CreditCard);
        Assert.True(findings.Zip(findings.Skip(1)).All(pair => pair.First.Start + pair.First.Length <= pair.Second.Start));
    }

    [Fact]
    public void Custom_terms_match_whole_words_ignoring_turkish_case()
    {
        var scanner = new PiiScanner(["Acme Lojistik", "Kartal", " "]);

        var findings = scanner.Find("ACME LOJİSTİK, acme lojistik; Kartal projesi, Kartalkaya değil.");

        Assert.Equal(["ACME LOJİSTİK", "acme lojistik", "Kartal"], findings.Select(f => f.Value));
        Assert.All(findings, f => Assert.Equal(PiiKind.CustomTerm, f.Kind));
        Assert.Equal(["Acme Lojistik", "Kartal"], scanner.CustomTerms);
    }

    [Fact]
    public void Masking_numbers_values_consistently_across_the_record()
    {
        var payload = new ChatPayload("Destek asistanısın.", [
            new ChatMessage(ChatRole.User, "Mailim ali@firma.com, telefonum 0532 123 45 67."),
            new ChatMessage(ChatRole.Assistant, "ali@firma.com adresine ve 05321234567 numarasına yazdık; yedek: veli@firma.com"),
        ]);

        var result = Scanner.Mask(payload);

        var masked = Assert.IsType<ChatPayload>(result.Payload);
        Assert.Equal("Mailim [EMAIL_1], telefonum [PHONE_1].", masked.Messages[0].Content);
        Assert.Equal("[EMAIL_1] adresine ve [PHONE_1] numarasına yazdık; yedek: [EMAIL_2]", masked.Messages[1].Content);
        Assert.Equal("Destek asistanısın.", masked.SystemPrompt);
        Assert.Equal(2, result.Counts[PiiKind.Email]);
        Assert.Equal(1, result.Counts[PiiKind.Phone]);
        Assert.True(result.HasFindings);
    }

    [Fact]
    public void Clean_payloads_are_returned_as_they_are()
    {
        var payload = new InstructionPayload("Başkent neresi?", "", "Ankara.");

        var result = Scanner.Mask(payload);

        Assert.Same(payload, result.Payload);
        Assert.False(result.HasFindings);
    }

    [Theory]
    [InlineData("10000000146", true)]
    [InlineData("12345678950", true)]
    [InlineData("12345678951", false)]
    [InlineData("02345678950", false)]
    public void National_id_checksum(string value, bool valid)
    {
        Assert.Equal(valid, PiiScanner.IsValidNationalId(value));
    }
}
