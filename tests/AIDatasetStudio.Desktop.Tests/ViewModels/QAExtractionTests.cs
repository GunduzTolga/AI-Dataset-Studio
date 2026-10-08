using AIDatasetStudio.Application.AI;
using AIDatasetStudio.Application.Documents;
using AIDatasetStudio.Application.Records;
using AIDatasetStudio.Desktop.Localization;
using AIDatasetStudio.Desktop.Tests.Support;
using AIDatasetStudio.Desktop.ViewModels.Documents;
using AIDatasetStudio.Domain.Enums;
using AIDatasetStudio.Domain.Payloads;

namespace AIDatasetStudio.Desktop.Tests.ViewModels;

/// <summary>
/// File › Extract Q&amp;A from Document (M13, SDD §17.5).
/// </summary>
[Collection(LanguageCollection.Name)]
public sealed class QAExtractionTests
{
    private const string Guide = """
        # Kargo

        Kargo ücreti 500 TL ve üzeri siparişlerde ücretsizdir. Daha düşük tutarlı siparişlerde kargo ücreti 49,90 TL olarak uygulanır ve teslimat iki ya da üç iş günü sürer.

        Kargo takip numarası sipariş kargoya verildiğinde e-posta ile gönderilir ve Hesabım sayfasındaki Siparişlerim bölümünden de görülebilir.

        # İade

        Ürünler teslim tarihinden itibaren on dört gün içinde iade edilebilir. İade için Hesabım sayfasından talep oluşturulur ve ürün orijinal ambalajıyla gönderilir.

        Ücret iadesi ürün depoya ulaştıktan sonra üç iş günü içinde kartınıza yapılır; bankanıza bağlı olarak hesabınıza yansıması birkaç gün sürebilir.
        """;

    public QAExtractionTests() => Localizer.Instance.SetCulture(Localizer.English);

    [Fact]
    public Task The_model_writes_questions_from_each_passage_and_the_kept_ones_become_records_with_their_source() => UiTest.RunAsync(async () =>
    {
        await using var host = new DesktopTestHost();
        var dataset = await host.CreateDatasetAsync(DatasetType.Instruction, []);
        dataset.AIGuidance = "Kısa yanıtla.";
        host.AddAIConnection();
        host.Files.DocumentPath = await WriteAsync(host, "sss.md", Guide);
        host.AI.Answers.Enqueue(["```json\n[{\"question\": \"Kargo ücreti", " ne kadar?\", \"answer\": \"500 TL üzeri ücretsiz.\"},", "\n {\"question\": \"Kargo kaç günde gelir?\", \"answer\": \"2-3 iş günü.\"}]\n```"]);
        host.AI.Answers.Enqueue(["[]"]);
        host.Dialogs.QAExtractionScript = async extraction =>
        {
            await extraction.InitializeAsync();
            Assert.Equal(DocumentFormat.Markdown, extraction.Document!.Format);
            Assert.Single(extraction.Chunks);
            Assert.Equal("· 1 passages", extraction.DocumentText);

            // Shorter passages: a heading starts a new one.
            extraction.ChunkTokens = 200;
            Assert.Equal(["Kargo", "İade"], extraction.Chunks.Select(c => c.Title));
            Assert.StartsWith("#1 · lines 1–5 · ", extraction.Chunks[0].Detail, StringComparison.Ordinal);
            Assert.Equal("Choose the passages and press Extract questions: the model writes the questions each one answers.", extraction.SummaryText);
            Assert.False(extraction.AddCommand.CanExecute(null));

            await extraction.StartCommand.ExecuteAsync(null);

            var (kargo, iade) = (extraction.Chunks[0], extraction.Chunks[1]);
            Assert.Equal((WritingState.Done, "2 questions"), (kargo.State, kargo.StateText));
            Assert.Equal((WritingState.Empty, "No questions"), (iade.State, iade.StateText));
            Assert.Equal("2 questions written; review them, then add them.", extraction.ProgressText);
            Assert.False(extraction.CanChangeChunks);
            extraction.ChunkTokens = 800;
            Assert.Equal(2, extraction.Chunks.Count);

            kargo.Pairs[0].Answer = "500 TL ve üzeri siparişlerde ücretsizdir.";
            kargo.Pairs[1].IsKept = false;
            Assert.Equal(("Add 1 records", "1 of 2 questions will be added as draft records"), (extraction.AddText, extraction.SummaryText));
            await extraction.AddCommand.ExecuteAsync(null);
        };

        await host.Main.ExtractQuestionsCommand.ExecuteAsync(null);

        Assert.Equal([".pdf", ".docx", ".docm", ".md", ".markdown", ".txt"], host.Files.DocumentExtensions);
        var requests = host.AI.Requests.Select(r => r.Request).ToList();
        Assert.Equal(2, requests.Count);
        Assert.EndsWith("Instructions for this dataset:\nKısa yanıtla.", requests[0].Messages[0].Content, StringComparison.Ordinal);
        Assert.StartsWith("Document: sss.md\nSection: Kargo\n\nPassage:\n# Kargo\n\nKargo ücreti 500 TL", requests[0].Messages[1].Content, StringComparison.Ordinal);
        Assert.Contains("Write at most 3 question-answer pairs", requests[1].Messages[0].Content, StringComparison.Ordinal);

        Assert.Equal("Added 1 questions and answers to “Dataset” as drafts to review", host.Main.Status.Message);
        var row = Assert.Single(host.Main.Records.Items);
        Assert.Equal(1, dataset.RecordCount);
        var record = await host.Get<IRecordQueryService>().GetAsync(row.Id);
        Assert.Equal(new InstructionPayload("Kargo ücreti ne kadar?", "", "500 TL ve üzeri siparişlerde ücretsizdir."), record!.Payload);
        Assert.Equal((RecordSourceKind.DocumentChunk, "sss.md#line=1", "qwen2.5:7b"), (record.Lineage.SourceKind, record.Lineage.SourceReference, record.Lineage.GeneratedByModel));
        Assert.Equal(RecordStatus.Draft, record.Status);

        // The editor shows where the answer comes from.
        host.Main.Records.SelectedItem = host.Main.Records.Items[0];
        await host.WaitForDetailsAsync(row.Id);
        var details = host.Main.Details;
        Assert.True(details.HasSource);
        Assert.StartsWith("# Kargo\n\nKargo ücreti 500 TL", details.Source!.Passage, StringComparison.Ordinal);
        Assert.Contains(details.Info, item => item.Label == "Section" && item.Value == "Kargo · lines 1–5");
        Assert.Contains(details.Info, item => item.Label == "Source" && item.Value == "From a document (sss.md#line=1)");

        // One step takes the addition back.
        Assert.Equal("_Undo Add #1 (sss.md)", host.Main.UndoHeader);
        await host.Main.UndoCommand.ExecuteAsync(null);
        Assert.Empty(host.Main.Records.Items);
        await host.Main.RedoCommand.ExecuteAsync(null);
        Assert.Single(host.Main.Records.Items);
    });

    [Fact]
    public Task Writing_can_be_stopped_and_a_passage_written_again_after_errors() => UiTest.RunAsync(async () =>
    {
        await using var host = new DesktopTestHost();
        var dataset = await host.CreateDatasetAsync(DatasetType.Chat, []);
        host.AddAIConnection();
        var extraction = host.Get<Services.DocumentQuestions>().Create(await WriteAsync(host, "sss.md", Guide), new QATarget(dataset.Id, dataset.Name, dataset.Type, null));
        await extraction.InitializeAsync();
        extraction.ChunkTokens = 200;
        var (kargo, iade) = (extraction.Chunks[0], extraction.Chunks[1]);

        // Stopping keeps the pairs written so far.
        host.AI.Hold = new TaskCompletionSource();
        host.AI.Answers.Enqueue(["[{\"question\": \"Kargo ücretli mi?\", \"answer\": \"500 TL altında evet.\"},", "{\"question\": \"Yarım"]);
        var running = extraction.StartCommand.ExecuteAsync(null);
        await DesktopTestHost.WaitUntilAsync(() => kargo.Pairs.Count == 1 && extraction.IsRunning);
        Assert.Equal((WritingState.Writing, "Writing…"), (kargo.State, kargo.StateText));
        Assert.Equal("Writing passage 1 of 2 with qwen2.5:7b…", extraction.ProgressText);
        Assert.False(extraction.AddCommand.CanExecute(null));
        extraction.StopCommand.Execute(null);
        await running;

        Assert.Equal((WritingState.Done, WritingState.Waiting), (kargo.State, iade.State));
        Assert.Equal("Stopped; what was written is kept.", extraction.ProgressText);
        host.AI.Hold = null;

        // The server is down: nothing changes, and the user is told why.
        host.AI.Failure = new AIException(AIErrorKind.Unreachable, "down");
        await extraction.StartCommand.ExecuteAsync(null);
        Assert.Equal("Nothing answers at http://localhost:11434/v1. Is Ollama running?", extraction.AIError);
        Assert.Equal(WritingState.Waiting, iade.State);
        extraction.DismissAIErrorCommand.Execute(null);

        // An answer without pairs is shown, and the passage can be tried again.
        host.AI.Failure = null;
        host.AI.Answers.Enqueue(["Üzgünüm, bu konuda yardımcı olamam."]);
        await extraction.StartCommand.ExecuteAsync(null);
        Assert.Equal((WritingState.Unreadable, "Unreadable", "Üzgünüm, bu konuda yardımcı olamam."), (iade.State, iade.StateText, iade.RawAnswer));
        Assert.True(extraction.StartCommand.CanExecute(null));

        host.AI.Answers.Enqueue(["Soru: İade süresi nedir?\nCevap: 14 gün."]);
        await extraction.RewriteCommand.ExecuteAsync(iade);
        Assert.Equal([("İade süresi nedir?", "14 gün.")], iade.Pairs.Select(p => (p.Question, p.Answer)));
        Assert.False(extraction.StartCommand.CanExecute(null));

        // Writing again replaces the pairs; a removed pair is not added.
        host.AI.Answers.Enqueue(["[{\"q\": \"Kargo kaç gün sürer?\", \"a\": \"2-3 gün.\"}, {\"q\": \"Takip nasıl?\", \"a\": \"E-posta ile.\"}]"]);
        await extraction.RewriteCommand.ExecuteAsync(kargo);
        Assert.Equal(["Kargo kaç gün sürer?", "Takip nasıl?"], kargo.Pairs.Select(p => p.Question));
        kargo.Pairs[1].RemoveCommand.Execute(null);
        Assert.Equal(2, extraction.KeptCount);

        await extraction.AddCommand.ExecuteAsync(null);

        var records = await Task.WhenAll(extraction.AddedIds!.Select(id => host.Get<IRecordQueryService>().GetAsync(id)));
        Assert.Equal(
            [new ChatPayload("", [new ChatMessage(ChatRole.User, "Kargo kaç gün sürer?"), new ChatMessage(ChatRole.Assistant, "2-3 gün.")]),
             new ChatPayload("", [new ChatMessage(ChatRole.User, "İade süresi nedir?"), new ChatMessage(ChatRole.Assistant, "14 gün.")])],
            records.Select(r => r!.Payload));
        Assert.Equal(["sss.md#line=1", "sss.md#line=7"], records.Select(r => r!.Lineage.SourceReference));
        Assert.Equal("İade", PassageSource.FromMetadata(records[1]!.MetadataJson)!.Section);
    });

    [Fact]
    public Task Passages_can_be_left_out_and_pairs_not_added_are_kept_until_the_user_lets_them_go() => UiTest.RunAsync(async () =>
    {
        await using var host = new DesktopTestHost();
        var dataset = await host.CreateDatasetAsync(DatasetType.Preference, []);
        var extraction = host.Get<Services.DocumentQuestions>().Create(await WriteAsync(host, "sss.md", Guide), new QATarget(dataset.Id, dataset.Name, dataset.Type, null));
        await extraction.InitializeAsync();
        extraction.ChunkTokens = 200;
        var closed = false;
        extraction.CloseRequested += (_, _) => closed = true;

        // Without a connection the model cannot be asked.
        await extraction.StartCommand.ExecuteAsync(null);
        Assert.Equal("Set up an AI connection to write with a model: Ollama or LM Studio on this computer is enough.", extraction.AIError);
        extraction.OpenAIConnectionsCommand.Execute(null);
        Assert.False(extraction.HasAIError);

        host.AddAIConnection();
        extraction.AllIncluded = false;
        Assert.False(extraction.StartCommand.CanExecute(null));
        extraction.Chunks[1].IsIncluded = true;
        Assert.Null(extraction.AllIncluded);
        host.AI.Answers.Enqueue(["[{\"question\": \"İade süresi nedir?\", \"answer\": \"14 gün.\"}]"]);
        await extraction.StartCommand.ExecuteAsync(null);
        Assert.Equal(WritingState.Waiting, extraction.Chunks[0].State);
        Assert.Single(host.AI.Requests);

        host.Dialogs.ConfirmAnswer = false;
        await extraction.CloseCommand.ExecuteAsync(null);
        Assert.False(closed);
        Assert.Equal("1 questions and answers have not been added to the dataset. Discard them?", host.Dialogs.ConfirmMessages[^1]);

        host.Dialogs.ConfirmAnswer = true;
        await extraction.CloseCommand.ExecuteAsync(null);
        Assert.True(closed);
        Assert.Null(extraction.AddedIds);
        Assert.Equal(0, dataset.RecordCount);
    });

    [Fact]
    public Task The_list_leaves_out_the_title_above_every_passage_and_follows_the_language() => UiTest.RunAsync(async () =>
    {
        await using var host = new DesktopTestHost();
        var dataset = await host.CreateDatasetAsync(DatasetType.Instruction, []);
        var guide = "# Müşteri Kılavuzu\n\n" + Guide.Replace("# ", "## ", StringComparison.Ordinal);
        using var extraction = host.Get<Services.DocumentQuestions>().Create(await WriteAsync(host, "kilavuz.md", guide), new QATarget(dataset.Id, dataset.Name, dataset.Type, null));
        await extraction.InitializeAsync();
        extraction.ChunkTokens = 200;

        Assert.Equal(["Kargo", "İade"], extraction.Chunks.Select(c => c.Title));
        Assert.Equal("Müşteri Kılavuzu › Kargo", extraction.Chunks[0].FullTitle);
        Assert.StartsWith("#1 · lines 1–7 · ", extraction.Chunks[0].Detail, StringComparison.Ordinal);

        Localizer.Instance.SetCulture(Localizer.Turkish);
        Assert.StartsWith("#1 · satır 1–7 · ", extraction.Chunks[0].Detail, StringComparison.Ordinal);
        Assert.Equal("Hedef: “Dataset” · Talimat", extraction.TargetText);
    });

    [Fact]
    public Task The_import_wizard_and_the_overview_lead_to_extraction() => UiTest.RunAsync(async () =>
    {
        await using var host = new DesktopTestHost();
        await host.CreateDatasetAsync(DatasetType.Instruction, []);
        var path = await WriteAsync(host, "notlar.txt", "Kargo ücreti 500 TL üzeri siparişlerde ücretsizdir.\n\nİade süresi 14 gündür.\n");
        host.Files.ImportPath = path;
        host.Dialogs.ImportWizardScript = async wizard =>
        {
            await wizard.InitializeAsync();
            Assert.True(wizard.CanExtractQuestions);
            wizard.ExtractQuestionsCommand.Execute(null);
        };

        await host.Main.ImportCommand.ExecuteAsync(null);

        var extraction = host.Dialogs.LastQAExtraction!;
        Assert.Equal(path, extraction.FilePath);
        Assert.Equal(DocumentFormat.PlainText, extraction.Document!.Format);
        Assert.Equal("Kargo ücreti 500 TL üzeri siparişlerde ücretsizdir.\n\nİade süresi 14 gündür.", Assert.Single(extraction.Chunks).Chunk.Text);
        Assert.Empty(host.Main.Records.Items);

        // From the overview's AI card: a document is chosen first.
        host.Files.DocumentPath = null;
        host.Main.Overview.ExtractQuestionsCommand.Execute(null);
        await DesktopTestHost.WaitUntilAsync(() => host.Files.DocumentExtensions is not null);
        Assert.Same(extraction, host.Dialogs.LastQAExtraction);

        // A file that cannot be read is explained in the window.
        host.Files.DocumentPath = await WriteAsync(host, "bozuk.pdf", "%PDF-1.7\nbozuk");
        await host.Main.ExtractQuestionsCommand.ExecuteAsync(null);
        Assert.Equal("“bozuk.pdf” cannot be read as a document; it may be damaged.", host.Dialogs.LastQAExtraction!.ErrorMessage);
        Assert.False(host.Dialogs.LastQAExtraction.HasChunks);
    });

    private static async Task<string> WriteAsync(DesktopTestHost host, string name, string text)
    {
        var path = Path.Combine(host.Directory, name);
        await File.WriteAllTextAsync(path, text);
        return path;
    }
}
