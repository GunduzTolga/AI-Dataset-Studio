using AIDatasetStudio.Application.AI;
using AIDatasetStudio.Application.Records;
using AIDatasetStudio.Desktop.Localization;
using AIDatasetStudio.Desktop.Services;
using AIDatasetStudio.Desktop.Tests.Support;
using AIDatasetStudio.Desktop.ViewModels.AI;
using AIDatasetStudio.Desktop.ViewModels.Documents;
using AIDatasetStudio.Domain.Enums;
using AIDatasetStudio.Domain.Payloads;

namespace AIDatasetStudio.Desktop.Tests.ViewModels;

/// <summary>
/// Record › Generate Variations (M14, SDD §17.6).
/// </summary>
[Collection(LanguageCollection.Name)]
public sealed class VariationsTests
{
    public VariationsTests() => Localizer.Instance.SetCulture(Localizer.English);

    [Fact]
    public Task Variations_of_the_shown_record_are_reviewed_and_added_to_its_family() => UiTest.RunAsync(async () =>
    {
        await using var host = new DesktopTestHost();
        var dataset = await host.CreateDatasetAsync(DatasetType.Instruction,
        [
            new InstructionPayload("Kargo ücreti ne kadar?", "", "500 TL üzeri siparişlerde ücretsiz."),
            new InstructionPayload("İade süresi nedir?", "", "14 gün."),
        ]);
        dataset.AIGuidance = "Resmî yaz.";
        host.AddAIConnection();
        var source = host.Main.Records.Items[0];
        host.Main.Records.SelectedItem = source;
        await host.WaitForDetailsAsync(source.Id);
        Assert.True(host.Main.Details.CanVary);
        Assert.True(host.Main.GenerateVariationsCommand.CanExecute(null));

        // The same question again is dropped; one that changes little is left out at first.
        host.AI.Answers.Enqueue(["[{\"question\": \"Kargo kaç para?\"}, {\"question\": \"kargo ücreti ne kadar\"},", " {\"question\": \"Kargo ücreti ne kadardır?\", \"answer\": \"Sorulmadı.\"}, {\"question\": \"Gönderim ücreti nedir?\"}, {\"question\": \"Fazladan\"}]"]);
        host.Dialogs.VariationsScript = async variations =>
        {
            Assert.Equal(("Record #1", false), (variations.RecordsText, variations.HasManyRecords));
            var record = Assert.Single(variations.Records);
            Assert.Same(record, variations.SelectedRecord);
            Assert.Equal(("Kargo ücreti ne kadar?", "#1", true, false), (record.Title, record.Number, record.HasAnswer, record.HasContext));
            Assert.Equal("Choose the records and press Generate variations: the model asks each question in other words.", variations.SummaryText);

            await variations.StartCommand.ExecuteAsync(null);

            Assert.Equal((WritingState.Done, "3 variations"), (record.State, record.StateText));
            Assert.Equal(["Kargo kaç para?", "Kargo ücreti ne kadardır?", "Gönderim ücreti nedir?"], record.Variants.Select(v => v.Question));
            Assert.Equal([false, true, false], record.Variants.Select(v => v.IsTooSimilar));
            Assert.Equal([true, false, true], record.Variants.Select(v => v.IsKept));
            Assert.All(record.Variants, v => Assert.False(v.HasOwnAnswer));
            Assert.True(record.KeepsAnswer);
            Assert.Equal("3 variations written; review them, then add them.", variations.ProgressText);

            record.Variants[2].Question = "Gönderim için ne kadar ödemem gerekir?";
            Assert.Equal(("Add 2 records", "2 of 3 variations will be added as draft records"), (variations.AddText, variations.SummaryText));

            // A variant edited back into the question itself would add nothing.
            record.Variants[0].Question = "Kargo ücreti ne kadar?";
            Assert.True(record.Variants[0].IsSame);
            Assert.Equal(1, variations.KeptCount);
            record.Variants[0].Question = "Kargo kaç para?";
            await variations.AddCommand.ExecuteAsync(null);
        };

        await host.Main.GenerateVariationsCommand.ExecuteAsync(null);

        var (connection, request) = Assert.Single(host.AI.Requests);
        Assert.Contains("Write 3 different ways", request.Messages[0].Content, StringComparison.Ordinal);
        Assert.EndsWith("Instructions for this dataset:\nResmî yaz.", request.Messages[0].Content, StringComparison.Ordinal);
        Assert.Equal("Question:\nKargo ücreti ne kadar?\n\nAnswer:\n500 TL üzeri siparişlerde ücretsiz.", request.Messages[1].Content);

        Assert.Equal("Added 2 variations to “Dataset” as drafts to review", host.Main.Status.Message);
        Assert.Equal(4, dataset.RecordCount);
        var added = host.Dialogs.LastVariations!.AddedIds!;
        Assert.Equal(added, host.Main.Records.SelectedRows.Select(r => r.Id));
        var records = await Task.WhenAll(added.Select(id => host.Get<IRecordQueryService>().GetAsync(id)));
        Assert.Equal(
            [new InstructionPayload("Kargo kaç para?", "", "500 TL üzeri siparişlerde ücretsiz."),
             new InstructionPayload("Gönderim için ne kadar ödemem gerekir?", "", "500 TL üzeri siparişlerde ücretsiz.")],
            records.Select(r => r!.Payload));
        Assert.All(records, r =>
        {
            Assert.Equal((RecordSourceKind.Synthetic, "#1", connection.Model, RecordStatus.Draft), (r!.Lineage.SourceKind, r.Lineage.SourceReference, r.Lineage.GeneratedByModel, r.Status));
            Assert.Equal(new VariantOf(source.Id, 1), VariantOf.FromMetadata(r.MetadataJson));
        });

        // The editor names the record a variation varies.
        host.Main.Records.SelectedItem = host.Main.Records.Items.Single(r => r.Id == added[0]);
        await host.WaitForDetailsAsync(added[0]);
        Assert.Contains(host.Main.Details.Info, item => item.Label == "Source" && item.Value == "Synthetic (variation of #1)");

        // One step takes the addition back.
        Assert.Equal("_Undo Add 2 records (variations)", host.Main.UndoHeader);
        await host.Main.UndoCommand.ExecuteAsync(null);
        Assert.Equal(2, host.Main.Records.Items.Count);
    });

    [Fact]
    public Task Selected_chats_get_their_answers_reworded_and_a_variation_of_a_variation_joins_the_first_family() => UiTest.RunAsync(async () =>
    {
        await using var host = new DesktopTestHost();
        var chat = new ChatPayload("Sen bir mağaza asistanısın.",
        [
            new ChatMessage(ChatRole.User, "Merhaba"),
            new ChatMessage(ChatRole.Assistant, "Merhaba, nasıl yardımcı olabilirim?"),
            new ChatMessage(ChatRole.User, "Kargo ücreti ne kadar?"),
            new ChatMessage(ChatRole.Assistant, "500 TL üzeri siparişlerde ücretsiz."),
        ]);
        var dataset = await host.CreateDatasetAsync(DatasetType.Chat, [chat, new ChatPayload(null, [new ChatMessage(ChatRole.Assistant, "Hoş geldiniz!")])]);
        host.AddAIConnection();
        Assert.True(host.Main.Records.Select([.. host.Main.Records.Items.Select(r => r.Id)]));
        await DesktopTestHost.WaitUntilAsync(() => host.Main.Details.IsBatch);

        host.AI.Answers.Enqueue(["[{\"question\": \"Kargo kaç para?\", \"answer\": \"500 TL'yi geçen siparişlerde kargo bedava.\"}, {\"question\": \"kargo ne kadr\"}]"]);
        host.Dialogs.VariationsScript = async variations =>
        {
            Assert.Equal((1, true, "1 of the selected records have no question yet and are left out."), (variations.Records.Count, variations.HasSkipped, variations.SkippedText));
            Assert.Equal("Record #1", variations.RecordsText);
            variations.Count = 2;
            variations.ModeIndex = 1;
            variations.IsCasual = true;
            Assert.Equal(new VariationOptions(2, VariationMode.QuestionsAndAnswers, Casual: true), variations.Options);
            await variations.StartCommand.ExecuteAsync(null);

            var record = variations.Records[0];
            Assert.Equal([true, false], record.Variants.Select(v => v.HasOwnAnswer));
            Assert.False(record.KeepsAnswer);
            await variations.AddCommand.ExecuteAsync(null);
        };

        // From the batch panel.
        host.Main.Details.GenerateVariationsCommand.Execute(null);
        await DesktopTestHost.WaitUntilAsync(() => host.Main.Status.Message == "Added 2 variations to “Dataset” as drafts to review");

        var prompt = Assert.Single(host.AI.Requests).Request.Messages[0].Content;
        Assert.Contains("Write 2 different ways", prompt, StringComparison.Ordinal);
        Assert.Contains("common typos", prompt, StringComparison.Ordinal);
        Assert.Contains("write the answer again in other words", prompt, StringComparison.Ordinal);

        var added = host.Dialogs.LastVariations!.AddedIds!;
        var queries = host.Get<IRecordQueryService>();
        var first = (ChatPayload)(await queries.GetAsync(added[0]))!.Payload;
        Assert.Equal(chat.SystemPrompt, first.SystemPrompt);
        Assert.Equal(
            ["Merhaba", "Merhaba, nasıl yardımcı olabilirim?", "Kargo kaç para?", "500 TL'yi geçen siparişlerde kargo bedava."],
            first.Messages.Select(m => m.Content));
        Assert.Equal("500 TL üzeri siparişlerde ücretsiz.", ((ChatPayload)(await queries.GetAsync(added[1]))!.Payload).Messages[3].Content);

        // A variation of a variation belongs to the first record's family.
        var root = host.Main.Records.Items[0].Id;
        var variation = await queries.GetAsync(added[0]);
        using var again = host.Get<RecordVariations>().Create(new QATarget(dataset.Id, dataset.Name, dataset.Type, null), [variation!]);
        host.AI.Answers.Enqueue(["- Gönderim ücreti nedir?"]);
        await again.StartCommand.ExecuteAsync(null);
        await again.AddCommand.ExecuteAsync(null);
        var grandchild = (await queries.GetAsync(again.AddedIds![0]))!;
        Assert.Equal(new VariantOf(root, 1), VariantOf.FromMetadata(grandchild.MetadataJson));
        Assert.Equal("#1", grandchild.Lineage.SourceReference);
        Assert.Contains("Question:\nKargo kaç para?", host.AI.Requests.Last().Request.Messages[1].Content, StringComparison.Ordinal);
    });

    [Fact]
    public Task Writing_can_be_stopped_written_again_after_errors_and_variants_are_kept_until_the_user_lets_them_go() => UiTest.RunAsync(async () =>
    {
        await using var host = new DesktopTestHost();
        var dataset = await host.CreateDatasetAsync(DatasetType.Preference,
        [
            new PreferencePayload("Kargo ücreti ne kadar?", "500 TL üzeri ücretsiz.", "Bilmiyorum."),
            new PreferencePayload("İade süresi nedir?", "14 gün.", "Sanırım bir hafta."),
        ]);
        var records = await Task.WhenAll(host.Main.Records.Items.Select(r => host.Get<IRecordQueryService>().GetAsync(r.Id)));
        using var variations = host.Get<RecordVariations>().Create(new QATarget(dataset.Id, dataset.Name, dataset.Type, null), [.. records.Select(r => r!)]);
        var (kargo, iade) = (variations.Records[0], variations.Records[1]);
        var closed = false;
        variations.CloseRequested += (_, _) => closed = true;
        Assert.Equal(("2 records", true), (variations.RecordsText, variations.HasManyRecords));

        // Without a connection the model cannot be asked.
        await variations.StartCommand.ExecuteAsync(null);
        Assert.Equal("Set up an AI connection to write with a model: Ollama or LM Studio on this computer is enough.", variations.AIError);
        host.AddAIConnection();
        variations.DismissAIErrorCommand.Execute(null);

        // Stopping keeps the variants written so far.
        host.AI.Hold = new TaskCompletionSource();
        host.AI.Answers.Enqueue(["[{\"question\": \"Kargo kaç para?\"},", " {\"question\": \"Yarım"]);
        var running = variations.StartCommand.ExecuteAsync(null);
        await DesktopTestHost.WaitUntilAsync(() => kargo.Variants.Count == 1 && variations.IsRunning);
        Assert.Equal((WritingState.Writing, "Writing…"), (kargo.State, kargo.StateText));
        Assert.Equal("Writing variations of record 1 of 2 with qwen2.5:7b…", variations.ProgressText);
        Assert.False(variations.AddCommand.CanExecute(null));
        variations.StopCommand.Execute(null);
        await running;
        Assert.Equal((WritingState.Done, WritingState.Waiting), (kargo.State, iade.State));
        Assert.Equal("Stopped; what was written is kept.", variations.ProgressText);
        host.AI.Hold = null;

        // An answer without questions is shown, and the record can be tried again.
        host.AI.Answers.Enqueue(["Üzgünüm, bu konuda yardımcı olamam."]);
        await variations.StartCommand.ExecuteAsync(null);
        Assert.Equal((WritingState.Unreadable, "Unreadable", "Üzgünüm, bu konuda yardımcı olamam."), (iade.State, iade.StateText, iade.RawAnswer));
        host.AI.Answers.Enqueue(["[\"İade süresi nedir?\"]"]);
        await variations.RewriteCommand.ExecuteAsync(iade);
        Assert.Equal((WritingState.Empty, "None"), (iade.State, iade.StateText));
        host.AI.Answers.Enqueue(["1. İadeyi kaç gün içinde yapabilirim?"]);
        await variations.RewriteCommand.ExecuteAsync(iade);
        Assert.Equal(["İadeyi kaç gün içinde yapabilirim?"], iade.Variants.Select(v => v.Question));
        Assert.False(variations.StartCommand.CanExecute(null));

        // Records can be left out; the language follows the app.
        variations.AllIncluded = false;
        Assert.False(variations.AllIncluded);
        kargo.IsIncluded = true;
        Assert.Null(variations.AllIncluded);
        Localizer.Instance.SetCulture(Localizer.Turkish);
        Assert.Equal(("1 varyant", "2 kayıt", "2 kaydı ekle"), (kargo.StateText, variations.RecordsText, variations.AddText));
        Localizer.Instance.SetCulture(Localizer.English);

        host.Dialogs.ConfirmAnswer = false;
        await variations.CloseCommand.ExecuteAsync(null);
        Assert.False(closed);
        Assert.Equal("2 variations have not been added to the dataset. Discard them?", host.Dialogs.ConfirmMessages[^1]);
        host.Dialogs.ConfirmAnswer = true;
        await variations.CloseCommand.ExecuteAsync(null);
        Assert.True(closed);
        Assert.Null(variations.AddedIds);
        Assert.Equal(2, dataset.RecordCount);
    });

    [Fact]
    public Task Only_records_with_a_question_of_a_supported_type_can_be_varied() => UiTest.RunAsync(async () =>
    {
        await using var host = new DesktopTestHost();
        await host.CreateDatasetAsync(DatasetType.Instruction, [new InstructionPayload(" ", "", "Yanıt.")]);
        Assert.False(host.Main.GenerateVariationsCommand.CanExecute(null));
        host.Main.Records.SelectedItem = host.Main.Records.Items[0];
        await host.WaitForDetailsAsync(host.Main.Records.Items[0].Id);
        Assert.True(host.Main.GenerateVariationsCommand.CanExecute(null));

        await host.Main.GenerateVariationsCommand.ExecuteAsync(null);

        var variations = host.Dialogs.LastVariations!;
        Assert.Equal((false, false), (variations.HasRecords, variations.HasSkipped));
        Assert.Equal(1, variations.SkippedCount);
        Assert.False(variations.StartCommand.CanExecute(null));

        using var evaluation = host.Get<RecordVariations>().Create(new QATarget(Guid.NewGuid(), "Değerlendirme", DatasetType.Evaluation, null), []);
        Assert.False(evaluation.IsSupported);
    });
}
