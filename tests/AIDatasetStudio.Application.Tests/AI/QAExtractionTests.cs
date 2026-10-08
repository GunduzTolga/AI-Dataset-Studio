using AIDatasetStudio.Application.AI;
using AIDatasetStudio.Application.Documents;
using AIDatasetStudio.Domain.Enums;
using AIDatasetStudio.Domain.Payloads;

namespace AIDatasetStudio.Application.Tests.AI;

/// <summary>
/// Passages of documents and the question-answer pairs a model writes from them (M13, SDD §17.5).
/// </summary>
public sealed class QAExtractionTests
{
    // Ten tokens a word, so a passage of 200 tokens holds 20 words.
    private static int Tokens(string text) => 10 * text.Split([' ', '\n'], StringSplitOptions.RemoveEmptyEntries).Length;

    private static DocumentBlock H(int level, string text, int position = 1) => new(DocumentBlockKind.Heading, text, position, level);

    private static DocumentBlock P(string text, int position = 1) => new(DocumentBlockKind.Paragraph, text, position);

    [Fact]
    public void Small_sections_share_a_passage_and_a_heading_starts_a_new_one_once_there_is_substance()
    {
        var chunks = DocumentChunker.Split(
        [
            H(1, "Sipariş", 1),
            P("Siparişler uygulamadan verilir.", 1),
            H(2, "Kargo", 2),
            P("Kargo 500 TL üzeri ücretsizdir.", 2),
            H(1, "İade", 3),
            P("Bir iki üç dört beş altı yedi sekiz dokuz on. On bir on iki on üç on dört. Son cümle burada.", 3),
            new DocumentBlock(DocumentBlockKind.ListItem, "- Fatura", 4),
            new DocumentBlock(DocumentBlockKind.ListItem, "- Kutu", 4, Level: 1),
        ], 200, Tokens);

        Assert.Equal(
        [
            (1, "Sipariş", "# Sipariş\n\nSiparişler uygulamadan verilir.\n\n## Kargo\n\nKargo 500 TL üzeri ücretsizdir.", 1, 2),
            (2, "İade", "# İade\n\nBir iki üç dört beş altı yedi sekiz dokuz on. On bir on iki on üç on dört.", 3, 3),
            (3, "İade", "Son cümle burada.\n\n- Fatura\n  - Kutu", 3, 4),
        ], chunks.Select(c => (c.Number, c.Section, c.Text, c.First, c.Last)));
        Assert.Equal(Tokens(chunks[2].Text), chunks[2].Tokens);
    }

    [Fact]
    public void Headings_at_the_end_of_a_passage_go_with_the_text_under_them()
    {
        var chunks = DocumentChunker.Split(
        [
            H(1, "Garanti"),
            P("İki yıl garantilidir."),
            H(2, "Kapsam"),
            P(string.Join(' ', Enumerable.Repeat("kelime", 18)) + "."),
            P(string.Join(' ', Enumerable.Repeat("uzun", 45))),
        ], 200, Tokens);

        Assert.Equal(["# Garanti\n\nİki yıl garantilidir.", "## Kapsam\n\n" + string.Join(' ', Enumerable.Repeat("kelime", 18)) + "."], chunks.Take(2).Select(c => c.Text));
        Assert.Equal(["Garanti", "Garanti › Kapsam", "Garanti › Kapsam", "Garanti › Kapsam", "Garanti › Kapsam"], chunks.Select(c => c.Section));

        // A sentence longer than a passage is cut at words.
        Assert.Equal([20, 20, 5], chunks.Skip(2).Select(c => c.Text.Split(' ').Length));
    }

    [Fact]
    public void The_prompt_asks_for_pairs_from_the_passage_with_the_dataset_instructions()
    {
        var chunk = new DocumentChunk(1, "Sipariş › Kargo", "Kargo 500 TL üzeri ücretsizdir.", 3, 3, 40);

        var messages = QAExtraction.Prompt("kılavuz.pdf", chunk, 4, "Resmî Türkçeyle yanıtla.");

        Assert.Equal([ChatRole.System, ChatRole.User], messages.Select(m => m.Role));
        Assert.Contains("Write at most 4 question-answer pairs", messages[0].Content, StringComparison.Ordinal);
        Assert.Contains("[{\"question\": \"...\", \"answer\": \"...\"}]", messages[0].Content, StringComparison.Ordinal);
        Assert.EndsWith("Instructions for this dataset:\nResmî Türkçeyle yanıtla.", messages[0].Content, StringComparison.Ordinal);
        Assert.Equal("Document: kılavuz.pdf\nSection: Sipariş › Kargo\n\nPassage:\nKargo 500 TL üzeri ücretsizdir.", messages[1].Content);

        var bare = QAExtraction.Prompt("notlar.txt", chunk with { Section = "" }, 0, null);
        Assert.Contains("Write at most 1 question-answer pairs", bare[0].Content, StringComparison.Ordinal);
        Assert.DoesNotContain("Instructions for this dataset", bare[0].Content, StringComparison.Ordinal);
        Assert.Equal("Document: notlar.txt\n\nPassage:\nKargo 500 TL üzeri ücretsizdir.", bare[1].Content);
        Assert.Contains("Write at most 10 question-answer pairs", QAExtraction.Prompt("a", chunk, 50, null)[0].Content, StringComparison.Ordinal);
    }

    [Fact]
    public void Pairs_become_records_of_the_dataset_type()
    {
        var pair = new QAPair("Kargo ücreti ne kadar?", "500 TL üzeri ücretsiz.");

        Assert.Equal(new InstructionPayload(pair.Question, "", pair.Answer), QAExtraction.Payload(DatasetType.Instruction, pair));
        Assert.Equal(
            new ChatPayload("", [new ChatMessage(ChatRole.User, pair.Question), new ChatMessage(ChatRole.Assistant, pair.Answer)]),
            QAExtraction.Payload(DatasetType.Chat, pair));
        Assert.Equal(new PreferencePayload(pair.Question, pair.Answer, ""), QAExtraction.Payload(DatasetType.Preference, pair));
        Assert.True(QAExtraction.Supports(DatasetType.Chat));
        Assert.False(QAExtraction.Supports(DatasetType.Classification));
        Assert.Throws<ArgumentException>(() => QAExtraction.Payload(DatasetType.RagTest, pair));
    }

    [Fact]
    public void Pairs_are_read_while_the_answer_is_written()
    {
        var reader = new QAPairReader();
        const string answer = "Elbette! İşte sorular:\n```json\n[\n  {\"question\": \"Kargo \\\"ücreti\\\" {ne} kadar?\", \"answer\": \"500 TL üzeri ücretsiz.\"},\n  {\"soru\": \"İade süresi?\", \"cevap\": \"14 gün.\"},\n  {\"question\": \"Kargo \\\"ücreti\\\" {ne} kadar?\", \"answer\": \"Tekrar.\"},\n  {\"question\": \"Boş mu?\", \"answer\": \"\"},\n  {\"question\": \"Kaç taksit?\", \"answer\": 9}\n]\n```";

        var seen = new List<int>();
        foreach (var piece in answer.Chunk(7).Select(c => new string(c)))
        {
            reader.Push(piece);
            seen.Add(reader.Pairs.Count);
        }

        Assert.Empty(reader.Complete());
        Assert.Equal(
        [
            new QAPair("Kargo \"ücreti\" {ne} kadar?", "500 TL üzeri ücretsiz."),
            new QAPair("İade süresi?", "14 gün."),
            new QAPair("Kaç taksit?", "9"),
        ], reader.Pairs);

        // Each pair came as soon as its object closed, not at the end.
        Assert.Equal(1, seen[answer.IndexOf("},", StringComparison.Ordinal) / 7]);
    }

    [Fact]
    public void Pairs_wrapped_in_an_object_or_written_as_lines_are_read_too()
    {
        var wrapped = new QAPairReader();
        wrapped.Push("{\"pairs\": [{\"Question\": \"Fatura nerede?\", \"Answer\": \"Hesabım sayfasında.\"}]}");
        Assert.Equal([new QAPair("Fatura nerede?", "Hesabım sayfasında.")], wrapped.Pairs);

        var lines = new QAPairReader();
        lines.Push("1. **Soru:** Kargo ücreti ne kadar?\n**Cevap:** 500 TL üzeri ücretsizdir.\nDaha düşük tutarda 49,90 TL.\n\nQ2: İade süresi nedir?\nA: 14 gün.\nNot: sorular bitti.");

        Assert.Empty(lines.Pairs);
        Assert.Equal(
        [
            new QAPair("Kargo ücreti ne kadar?", "500 TL üzeri ücretsizdir.\nDaha düşük tutarda 49,90 TL."),
            new QAPair("İade süresi nedir?", "14 gün.\nNot: sorular bitti."),
        ], lines.Complete());
        Assert.Equal(2, lines.Pairs.Count);

        var nothing = new QAPairReader();
        nothing.Push("Bu bölümde soru çıkarılacak bilgi yok.");
        Assert.Empty(nothing.Complete());
    }

    [Fact]
    public void The_passage_is_kept_in_the_metadata()
    {
        var document = new SourceDocument("/belgeler/kılavuz.pdf", DocumentFormat.Pdf, [], 12);
        var chunk = new DocumentChunk(4, "Sipariş › Kargo", "Kargo \"ücreti\" 500 TL.", 3, 4, 20);

        var source = PassageSource.From(document, chunk);
        var metadata = source.ToMetadata();

        Assert.Equal(new PassageSource("kılavuz.pdf", "Sipariş › Kargo", "page", 3, 4, "Kargo \"ücreti\" 500 TL."), source);
        Assert.StartsWith("{\"source\":{\"file\":\"kılavuz.pdf\",\"section\":\"Sipariş › Kargo\",\"unit\":\"page\",\"first\":3,\"last\":4,", metadata, StringComparison.Ordinal);
        Assert.Equal(source, PassageSource.FromMetadata(metadata));
        Assert.Null(PassageSource.FromMetadata("{}"));
        Assert.Null(PassageSource.FromMetadata("{bozuk"));
        Assert.Null(PassageSource.FromMetadata(null));
        Assert.Equal("kılavuz.pdf#page=3", document.Reference(chunk.First));
    }
}
