using AIDatasetStudio.Application.Import;
using AIDatasetStudio.Application.Records;
using AIDatasetStudio.Domain.Enums;
using AIDatasetStudio.Domain.Payloads;
using AIDatasetStudio.Infrastructure.Import;
using AIDatasetStudio.Infrastructure.Tests.Support;

namespace AIDatasetStudio.Infrastructure.Tests.Import;

/// <summary>
/// Conversation logs — WhatsApp exports and transcripts — imported as chat records (M18, SDD §17.2).
/// </summary>
public sealed class TranscriptImportTests
{
    private const string AndroidTurkish = """
        08.10.2026 14:30 - Mesajlar ve aramalar uçtan uca şifrelidir. Bu sohbetin dışında kalan hiç kimse onları okuyamaz.
        08.10.2026 14:32 - Ayşe Yılmaz: Merhaba, siparişim nerede?
        08.10.2026 14:33 - Ayşe Yılmaz: Sipariş no 12345
        08.10.2026 14:35 - Mağaza Destek: Merhaba Ayşe Hanım,
        kargonuz yarın elinizde olur.
        08.10.2026 14:36 - Ayşe Yılmaz: <Medya dahil edilmedi>
        08.10.2026 14:36 - Ayşe Yılmaz: Teşekkürler
        08.10.2026 22:10 - Ayşe Yılmaz: İade nasıl yapılır?
        08.10.2026 22:15 - Mağaza Destek: Hesabım sayfasından talep oluşturabilirsiniz. <Bu mesaj düzenlendi>
        08.10.2026 22:16 - Mağaza Destek: Bu mesaj silindi
        """;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void A_whatsapp_chat_is_split_where_it_pauses_and_its_placeholders_are_left_out()
    {
        var log = TranscriptReader.Read(AndroidTurkish.Split('\n'));

        Assert.Equal(["Ayşe Yılmaz", "Mağaza Destek"], log.Speakers);
        Assert.Equal(2, log.Openers["Ayşe Yılmaz"]);
        Assert.Equal(2, log.Conversations.Count);
        Assert.Equal(
        [
            ("Ayşe Yılmaz", "Merhaba, siparişim nerede?", 2L),
            ("Ayşe Yılmaz", "Sipariş no 12345", 3L),
            ("Mağaza Destek", "Merhaba Ayşe Hanım,\nkargonuz yarın elinizde olur.", 4L),
            ("Ayşe Yılmaz", "Teşekkürler", 7L),
        ],
            log.Conversations[0].Messages.Select(m => (m.Speaker, m.Text, m.Line)));
        Assert.Equal(
            [("Ayşe Yılmaz", "İade nasıl yapılır?"), ("Mağaza Destek", "Hesabım sayfasından talep oluşturabilirsiniz.")],
            log.Conversations[1].Messages.Select(m => (m.Speaker, m.Text)));
        Assert.Equal(8, log.Conversations[1].Line);
    }

    [Fact]
    public void Ios_exports_with_english_dates_are_read_month_first()
    {
        var log = TranscriptReader.Read(
        [
            "[10/8/26, 2:32:05 PM] John: Hi, where is my order?",
            "[10/8/26, 2:35:10 PM] Store Support: It ships tomorrow.",
            "‎[10/8/26, 2:36:00 PM] John: ‎image omitted",
            "[10/9/26, 9:15:00 AM] John: Can I return it?",
            "[10/9/26, 9:20:00 AM] Store Support: Yes, within 14 days.",
            "[10/9/26, 11:40:00 AM] John: Great, thanks!",
        ]);

        Assert.Equal(2, log.Conversations.Count);
        Assert.Equal(["Hi, where is my order?", "It ships tomorrow."], log.Conversations[0].Messages.Select(m => m.Text));
        Assert.Equal(3, log.Conversations[1].Messages.Count);
        Assert.Equal(
            new Dictionary<TargetField, string> { [TargetField.User] = "John", [TargetField.Assistant] = "Store Support" },
            ImportLayouts.SuggestSpeakers(log.Speakers, log.Openers));
    }

    [Fact]
    public void A_transcript_is_split_at_separators_and_headings_and_messages_go_on_over_lines()
    {
        var log = TranscriptReader.Read("""
            # Konuşma 1
            **Müşteri:** Kargo ücreti ne kadar?
            **Temsilci:** 500 TL üzeri ücretsiz.

            Altında 49,90 TL.
            ---
            Müşteri: İade süresi?
            Temsilci: 14 gün.
            Müşteri: Teşekkürler.
            Temsilci: Rica ederim.
            """.Split('\n'));

        Assert.Equal(["Müşteri", "Temsilci"], log.Speakers);
        Assert.Equal(
            [("Müşteri", "Kargo ücreti ne kadar?"), ("Temsilci", "500 TL üzeri ücretsiz.\n\nAltında 49,90 TL.")],
            log.Conversations[0].Messages.Select(m => (m.Speaker, m.Text)));
        Assert.Equal(4, log.Conversations[1].Messages.Count);

        // Without separators, blank lines part the conversations.
        var plain = TranscriptReader.Read("Müşteri: A?\nTemsilci: B.\n\nMüşteri: C?\nTemsilci: D.".Split('\n'));
        Assert.Equal([2, 2], plain.Conversations.Select(c => c.Messages.Count));
    }

    [Fact]
    public void Prose_and_tables_are_not_taken_for_conversations()
    {
        Assert.True(TranscriptReader.Looks(AndroidTurkish.Split('\n')));
        Assert.True(TranscriptReader.Looks(["Müşteri: A?", "Temsilci: B.", "Müşteri: C?", "Temsilci: D."]));
        Assert.False(TranscriptReader.Looks(["Not: Kargo 2 iş günü sürer.", "Bu metin bir kılavuzdur.", "Örnek: Kargo takibi.", "Sonuç olarak iade kolaydır.", "Uyarı: Saklayın."]));
        Assert.False(TranscriptReader.Looks(["soru,cevap", "Kargo?,Ücretsiz", "İade?,14 gün", "Fatura?,Hesabım"]));
        Assert.False(TranscriptReader.Looks(["Ali: a", "Veli: b"]));
    }

    [Fact]
    public async Task A_whatsapp_chat_is_imported_as_chat_records_with_the_customer_and_the_agent()
    {
        await using var ws = await TestWorkspace.CreateAsync(Ct);
        var datasetId = await ws.CreateDatasetAsync(DatasetType.Chat, Ct);
        var path = ws.WriteFile("WhatsApp Sohbeti - Ayşe.txt", AndroidTurkish);

        var analysis = await ws.Import.AnalyzeAsync(path, DatasetType.Chat, cancellationToken: Ct);

        Assert.Equal((ImportFormat.Transcript, SourceLayout.Transcript, 2L), (analysis.Format, analysis.Layout, analysis.RowCount));
        Assert.Equal([ImportFormat.Transcript, ImportFormat.PlainText], analysis.ReadableAs);
        Assert.Equal(["Ayşe Yılmaz", "Mağaza Destek"], analysis.Columns);
        Assert.Equal(new Dictionary<TargetField, string> { [TargetField.User] = "Ayşe Yılmaz", [TargetField.Assistant] = "Mağaza Destek" }, analysis.SuggestedMapping);

        var result = await ws.Import.ImportAsync(PlanFrom(analysis, DatasetType.Chat), datasetId, cancellationToken: Ct);

        Assert.Equal(2, result.Imported);
        var page = await ws.Queries.GetPageAsync(datasetId, RecordFilter.None, after: null, pageSize: 10, Ct);
        var first = (await ws.Queries.GetAsync(page.Items[0].Id, Ct))!;
        Assert.Equal(
            new ChatPayload(null,
            [
                new ChatMessage(ChatRole.User, "Merhaba, siparişim nerede?\nSipariş no 12345"),
                new ChatMessage(ChatRole.Assistant, "Merhaba Ayşe Hanım,\nkargonuz yarın elinizde olur."),
            ]),
            first.Payload);
        Assert.Equal("WhatsApp Sohbeti - Ayşe.txt#line=2", first.Lineage.SourceReference);

        // Read as plain text instead, when the user chooses so.
        var text = await ws.Import.AnalyzeAsync(path, DatasetType.Chat, new ImportOverrides(Format: ImportFormat.PlainText), Ct);
        Assert.Equal(ImportFormat.PlainText, text.Format);
    }

    [Fact]
    public async Task Without_an_agent_everyone_else_answers_and_instruction_datasets_take_single_exchanges()
    {
        await using var ws = await TestWorkspace.CreateAsync(Ct);
        var path = ws.WriteFile("grup.txt", """
            Ali: Kargo ücreti ne kadar?
            Veli: 49,90 TL.
            Ayşe: 500 TL üzeri ücretsiz.

            Ali: İade süresi?
            Ayşe: 14 gün.
            Ali: Teşekkürler.
            Veli: Rica ederiz.
            """);

        var chat = await ws.Import.AnalyzeAsync(path, DatasetType.Chat, cancellationToken: Ct);
        Assert.Equal(new Dictionary<TargetField, string> { [TargetField.User] = "Ali" }, chat.SuggestedMapping);
        var preview = await ws.Import.PreviewAsync(PlanFrom(chat, DatasetType.Chat), cancellationToken: Ct);
        Assert.Equal(
            new ChatPayload(null, [new ChatMessage(ChatRole.User, "Kargo ücreti ne kadar?"), new ChatMessage(ChatRole.Assistant, "49,90 TL.\n500 TL üzeri ücretsiz.")]),
            preview.Rows[0].Payload);

        // One agent only: the other's messages are left out.
        var onlyAyse = PlanFrom(chat, DatasetType.Chat) with { Mapping = new Dictionary<TargetField, string> { [TargetField.User] = "Ali", [TargetField.Assistant] = "Ayşe" } };
        Assert.Equal(
            new ChatPayload(null, [new ChatMessage(ChatRole.User, "Kargo ücreti ne kadar?"), new ChatMessage(ChatRole.Assistant, "500 TL üzeri ücretsiz.")]),
            (await ws.Import.PreviewAsync(onlyAyse, cancellationToken: Ct)).Rows[0].Payload);

        var instruction = await ws.Import.AnalyzeAsync(path, DatasetType.Instruction, cancellationToken: Ct);
        Assert.True(instruction.SupportsTargetType);
        var rows = (await ws.Import.PreviewAsync(PlanFrom(instruction, DatasetType.Instruction), cancellationToken: Ct)).Rows;
        Assert.Equal(new InstructionPayload("Kargo ücreti ne kadar?", "", "49,90 TL.\n500 TL üzeri ücretsiz."), rows[0].Payload);
        Assert.Equal(ImportErrorCode.MultiTurnNotSupported, rows[1].Error!.Code);

        Assert.False((await ws.Import.AnalyzeAsync(path, DatasetType.Preference, cancellationToken: Ct)).SupportsTargetType);
    }

    [Fact]
    public async Task Logs_are_told_from_json_arrays_and_from_markdown_documents()
    {
        await using var ws = await TestWorkspace.CreateAsync(Ct);
        var ios = ws.WriteFile("_chat.txt", """
            [10/8/26, 2:32:05 PM] John: Hi, where is my order?
            [10/8/26, 2:35:10 PM] Store Support: It ships tomorrow.
            [10/8/26, 2:37:00 PM] John: Thanks!
            [10/8/26, 2:38:00 PM] Store Support: You're welcome.
            """);
        Assert.Equal(ImportFormat.Transcript, (await ws.Import.AnalyzeAsync(ios, DatasetType.Chat, cancellationToken: Ct)).Format);

        // A Markdown FAQ written as questions and answers: each section is one exchange.
        var faq = ws.WriteFile("sss.md", """
            ## Kargo
            **Soru:** Kargo ücreti ne kadar?
            **Cevap:** 500 TL üzeri ücretsiz.

            ## İade
            **Soru:** İade süresi?
            **Cevap:** 14 gün.
            """);
        var analysis = await ws.Import.AnalyzeAsync(faq, DatasetType.Instruction, cancellationToken: Ct);
        Assert.Equal([ImportFormat.Transcript, ImportFormat.Markdown, ImportFormat.PlainText], analysis.ReadableAs);
        Assert.Equal(new Dictionary<TargetField, string> { [TargetField.User] = "Soru", [TargetField.Assistant] = "Cevap" }, analysis.SuggestedMapping);
        Assert.Equal(
            [new InstructionPayload("Kargo ücreti ne kadar?", "", "500 TL üzeri ücretsiz."), new InstructionPayload("İade süresi?", "", "14 gün.")],
            (await ws.Import.PreviewAsync(PlanFrom(analysis, DatasetType.Instruction), cancellationToken: Ct)).Rows.Select(r => r.Payload));

        var guide = ws.WriteFile("kilavuz.md", "# Kargo\n\nNot: Kargo 2 iş günü sürer.\n\nSiparişler hafta içi çıkar.\n");
        Assert.Equal(ImportFormat.Markdown, (await ws.Import.AnalyzeAsync(guide, DatasetType.Instruction, cancellationToken: Ct)).Format);
    }

    private static ImportPlan PlanFrom(ImportAnalysis analysis, DatasetType type) =>
        new(analysis.FilePath, analysis.Format, analysis.Csv, analysis.Layout, analysis.SuggestedMapping, type);
}
