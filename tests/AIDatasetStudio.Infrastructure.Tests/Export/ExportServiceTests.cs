using System.Diagnostics;
using System.Text.Json.Nodes;
using AIDatasetStudio.Application.Export;
using AIDatasetStudio.Application.Records;
using AIDatasetStudio.Domain.Enums;
using AIDatasetStudio.Domain.Payloads;
using AIDatasetStudio.Domain.ValueObjects;
using AIDatasetStudio.Infrastructure.Tests.Support;

namespace AIDatasetStudio.Infrastructure.Tests.Export;

public sealed class ExportServiceTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Exports_approved_records_in_order_without_copies_or_overlong_records()
    {
        await using var ws = await TestWorkspace.CreateAsync(Ct);
        var datasetId = await ws.CreateDatasetAsync(cancellationToken: Ct);
        await ws.Catalog.SetTokenSettingsAsync(datasetId, "o200k_base", 256, Ct);
        var ids = await AddAsync(ws, datasetId,
            new InstructionPayload("Başkent neresi?", "", "Ankara."),
            new InstructionPayload("Draft soru", "", "Taslak."),                                     // not approved
            new InstructionPayload("Başkent neresi?", "", "Ankara."),                                // copy
            new InstructionPayload("Uzun", "", string.Join(' ', Enumerable.Repeat("kelime", 400))),  // over 256 tokens
            new InstructionPayload("Kime yazayım?", "", "ali@firma.com adresine."));
        await ws.Commands.SetStatusAsync([ids[0], ids[2], ids[3], ids[4]], RecordStatus.Approved, Ct);
        await ws.Analysis.AnalyzeAsync(datasetId, cancellationToken: Ct); // as the background analysis does
        var options = ExportOptions.DefaultFor(ExportFormat.Alpaca);
        var path = Path.Combine(ws.Directory, "faq.jsonl");

        var preview = await ws.Export.PreviewAsync(datasetId, options, sampleLines: 5, cancellationToken: Ct);
        var result = await ws.Export.ExportAsync(datasetId, options, path, cancellationToken: Ct);

        string[] expected =
        [
            """{"instruction":"Başkent neresi?","input":"","output":"Ankara."}""",
            """{"instruction":"Kime yazayım?","input":"","output":"[EMAIL_1] adresine."}""",
        ];
        Assert.Equal(expected, await File.ReadAllLinesAsync(path, Ct));
        Assert.Equal(string.Join("\n", expected) + "\n", await File.ReadAllTextAsync(path, Ct)); // LF, no BOM
        Assert.Equal(expected, preview.SampleLines);
        Assert.Equal((4L, 0L, 1L, 1L, 0L, 2L), (preview.Selected, preview.Unsupported, preview.OverContextLimit, preview.Duplicates, preview.NotAnalyzed, preview.Exported));
        Assert.Equal((2L, 0L, 1L, 1L, 1L), (result.Exported, result.Unsupported, result.OverContextLimit, result.Duplicates, result.Masked));
        Assert.Equal([new ExportedFile(null, path, 2)], result.Files);
        Assert.DoesNotContain(Directory.EnumerateFiles(ws.Directory), f => f.EndsWith(".partial", StringComparison.Ordinal));

        // Remembered for next time, and recorded in the history.
        Assert.Equal(options, await ws.Export.GetLastOptionsAsync(datasetId, Ct));
        var last = await ws.Export.GetLastExportAsync(datasetId, Ct);
        Assert.Equal((ExportFormat.Alpaca, 2L, TestWorkspace.Now), (last!.Format, last.RecordCount, last.ExportedAt));
        Assert.Equal([path], last.Files);
    }

    [Fact]
    public async Task Options_can_keep_everything()
    {
        await using var ws = await TestWorkspace.CreateAsync(Ct);
        var datasetId = await ws.CreateDatasetAsync(cancellationToken: Ct);
        await AddAsync(ws, datasetId,
            new InstructionPayload("a", "", "aynı"),
            new InstructionPayload("a", "", "aynı"),
            new InstructionPayload("b", "", "0532 123 45 67"));
        var options = new ExportOptions(ExportFormat.OpenAi, [RecordStatus.Draft], SkipOverContextLimit: false, SkipDuplicates: false, MaskPersonalData: false);
        var path = Path.Combine(ws.Directory, "all.jsonl");

        var result = await ws.Export.ExportAsync(datasetId, options, path, cancellationToken: Ct);

        Assert.Equal(3, result.Exported);
        Assert.Contains("0532 123 45 67", await File.ReadAllTextAsync(path, Ct), StringComparison.Ordinal);
        Assert.Equal(0, (await ws.Export.PreviewAsync(datasetId, options, 0, cancellationToken: Ct)).Duplicates);
    }

    [Fact]
    public async Task Split_writes_one_file_per_part_and_keeps_questions_together()
    {
        await using var ws = await TestWorkspace.CreateAsync(Ct);
        var datasetId = await ws.CreateDatasetAsync(cancellationToken: Ct);
        var payloads = Enumerable.Range(0, 300).Select(i => (RecordPayload)new InstructionPayload($"Soru {i % 100}", "", $"Cevap {i}")).ToArray();
        await AddAsync(ws, datasetId, payloads);
        var options = new ExportOptions(ExportFormat.OpenAi, [RecordStatus.Draft], Split: SplitOptions.Standard);
        var folder = Path.Combine(ws.Directory, "split");

        var result = await ws.Export.ExportAsync(datasetId, options, folder, cancellationToken: Ct);

        Assert.Equal(["test.jsonl", "train.jsonl", "val.jsonl"], Directory.EnumerateFiles(folder).Select(Path.GetFileName).Order());
        Assert.Equal([SplitPart.Train, SplitPart.Validation, SplitPart.Test], result.Files.Select(f => f.Part!.Value));
        Assert.Equal(300, result.Files.Sum(f => f.Count));
        Assert.InRange(result.Files[0].Count, 180, 285);

        // The three answers to "Soru 7" are in the same file.
        var questionsByFile = result.Files.ToDictionary(
            f => f.Part!.Value,
            f => File.ReadLines(f.Path).Select(line => line[(line.IndexOf("Soru ", StringComparison.Ordinal) + 5)..line.IndexOf('"', line.IndexOf("Soru ", StringComparison.Ordinal))]).ToHashSet());
        Assert.Empty(questionsByFile[SplitPart.Train].Intersect(questionsByFile[SplitPart.Test]));
        Assert.Empty(questionsByFile[SplitPart.Train].Intersect(questionsByFile[SplitPart.Validation]));

        // Same seed, same files; adding records moves none of the existing ones.
        var before = result.Files.ToDictionary(f => f.Part!.Value, f => File.ReadAllLines(f.Path));
        await AddAsync(ws, datasetId, new InstructionPayload("Yeni soru", "", "Yeni cevap"));
        var again = await ws.Export.ExportAsync(datasetId, options, folder, cancellationToken: Ct);
        foreach (var file in again.Files)
        {
            var lines = File.ReadAllLines(file.Path);
            Assert.Equal(before[file.Part!.Value], lines.Where(l => !l.Contains("Yeni", StringComparison.Ordinal)));
        }

        // Another seed, another assignment.
        var other = await ws.Export.ExportAsync(datasetId, options with { Split = SplitOptions.Standard with { Seed = 7 } }, folder, cancellationToken: Ct);
        Assert.NotEqual(again.Files.Select(f => f.Count), other.Files.Select(f => f.Count));
    }

    [Fact]
    public async Task A_training_configuration_is_written_next_to_the_files_and_remembered()
    {
        await using var ws = await TestWorkspace.CreateAsync(Ct);
        var datasetId = await ws.CreateDatasetAsync(cancellationToken: Ct);
        await AddAsync(ws, datasetId, [.. Enumerable.Range(0, 40).Select(i => (RecordPayload)new InstructionPayload($"Soru {i}", "", $"Cevap {i}"))]);
        var training = new TrainingOptions(TrainingFramework.LlamaFactory, "Qwen/Qwen2.5-7B-Instruct");
        var options = new ExportOptions(ExportFormat.ShareGpt, [RecordStatus.Draft], Split: SplitOptions.Standard, Training: training);
        var folder = Path.Combine(ws.Directory, "egitim");

        var result = await ws.Export.ExportAsync(datasetId, options, folder, cancellationToken: Ct);

        Assert.Equal(
            ["dataset_info.json", "llamafactory.yaml", "test.jsonl", "train.jsonl", "val.jsonl"],
            Directory.EnumerateFiles(folder).Select(Path.GetFileName).Order(StringComparer.Ordinal));
        Assert.Equal([Path.Combine(folder, "dataset_info.json"), Path.Combine(folder, "llamafactory.yaml")], result.TrainingFiles);
        var yaml = await File.ReadAllTextAsync(Path.Combine(folder, "llamafactory.yaml"), Ct);
        Assert.Contains("# Fine-tuning \"Dataset\" with LoRA", yaml, StringComparison.Ordinal);
        Assert.Contains("cutoff_len: 4096\n", yaml, StringComparison.Ordinal);
        Assert.Contains("\"file_name\": \"val.jsonl\"", await File.ReadAllTextAsync(Path.Combine(folder, "dataset_info.json"), Ct), StringComparison.Ordinal);
        Assert.Equal(training, (await ws.Export.GetLastOptionsAsync(datasetId, Ct))!.Training);

        // A single file gets its configuration in the same folder.
        var single = Path.Combine(ws.Directory, "tek", "sss.jsonl");
        Directory.CreateDirectory(Path.GetDirectoryName(single)!);
        var alone = await ws.Export.ExportAsync(datasetId, options with { Split = null, Training = training with { Framework = TrainingFramework.Axolotl } }, single, cancellationToken: Ct);
        Assert.Equal([Path.Combine(ws.Directory, "tek", "axolotl.yaml")], alone.TrainingFiles);
        Assert.Contains("  - path: \"sss.jsonl\"\n", await File.ReadAllTextAsync(alone.TrainingFiles![0], Ct), StringComparison.Ordinal);

        // A tool that cannot read the format is refused before anything is written.
        await Assert.ThrowsAsync<ArgumentException>(() => ws.Export.ExportAsync(
            datasetId, options with { Format = ExportFormat.ChatMl }, Path.Combine(ws.Directory, "chatml"), cancellationToken: Ct));
        Assert.False(Directory.Exists(Path.Combine(ws.Directory, "chatml")));
    }

    [Fact]
    public async Task Alpaca_leaves_out_longer_chats_and_says_so()
    {
        await using var ws = await TestWorkspace.CreateAsync(Ct);
        var datasetId = await ws.CreateDatasetAsync(DatasetType.Chat, Ct);
        await AddAsync(ws, datasetId,
            new ChatPayload("Kısa yanıt ver.", [new ChatMessage(ChatRole.User, "Merhaba"), new ChatMessage(ChatRole.Assistant, "Merhaba!")]),
            new ChatPayload(null,
            [
                new ChatMessage(ChatRole.User, "1"), new ChatMessage(ChatRole.Assistant, "2"),
                new ChatMessage(ChatRole.User, "3"), new ChatMessage(ChatRole.Assistant, "4"),
            ]));
        var options = ExportOptions.DefaultFor(ExportFormat.Alpaca) with { Statuses = [RecordStatus.Draft] };

        var preview = await ws.Export.PreviewAsync(datasetId, options, 5, cancellationToken: Ct);
        var result = await ws.Export.ExportAsync(datasetId, options, Path.Combine(ws.Directory, "chat.jsonl"), cancellationToken: Ct);

        Assert.Equal((2L, 1L, 1L), (preview.Selected, preview.Unsupported, preview.Exported));
        Assert.Equal((1L, 1L), (result.Exported, result.Unsupported));
        Assert.Equal(["""{"instruction":"Merhaba","input":"","output":"Merhaba!","system":"Kısa yanıt ver."}"""], preview.SampleLines);
    }

    [Fact]
    public async Task Records_not_analysed_yet_are_checked_when_exporting()
    {
        await using var ws = await TestWorkspace.CreateAsync(Ct);
        var datasetId = await ws.CreateDatasetAsync(cancellationToken: Ct);
        await ws.Catalog.SetTokenSettingsAsync(datasetId, "o200k_base", 256, Ct);
        await AddAsync(ws, datasetId,
            new InstructionPayload("Uzun", "", string.Join(' ', Enumerable.Repeat("kelime", 400))),
            new InstructionPayload("Kısa", "", "Cevap"));
        var options = ExportOptions.DefaultFor(ExportFormat.OpenAi) with { Statuses = [RecordStatus.Draft] };

        var preview = await ws.Export.PreviewAsync(datasetId, options, 0, cancellationToken: Ct);
        var result = await ws.Export.ExportAsync(datasetId, options, Path.Combine(ws.Directory, "x.jsonl"), cancellationToken: Ct);

        Assert.Equal((2L, 0L), (preview.NotAnalyzed, preview.OverContextLimit));
        Assert.Equal((1L, 1L), (result.Exported, result.OverContextLimit));
    }

    [Fact]
    public async Task A_cancelled_export_leaves_existing_files_alone()
    {
        await using var ws = await TestWorkspace.CreateAsync(Ct);
        var datasetId = await ws.CreateDatasetAsync(cancellationToken: Ct);
        await AddAsync(ws, datasetId, [.. Enumerable.Range(0, 1200).Select(i => (RecordPayload)new InstructionPayload($"Soru {i}", "", "Cevap"))]);
        var path = ws.WriteFile("old.jsonl", "eski\n");
        using var cancellation = new CancellationTokenSource();
        var progress = new InlineProgress<ExportProgress>(p =>
        {
            if (p.Processed >= 500)
            {
                cancellation.Cancel();
            }
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ws.Export.ExportAsync(
            datasetId, ExportOptions.DefaultFor(ExportFormat.OpenAi) with { Statuses = [RecordStatus.Draft] }, path, progress, cancellationToken: cancellation.Token));

        Assert.Equal("eski\n", await File.ReadAllTextAsync(path, Ct));
        Assert.DoesNotContain(Directory.EnumerateFiles(ws.Directory), f => f.EndsWith(".partial", StringComparison.Ordinal));
        Assert.Null(await ws.Export.GetLastExportAsync(datasetId, Ct));
    }

    [Fact]
    public async Task A_split_part_holds_the_records_its_file_gets()
    {
        await using var ws = await TestWorkspace.CreateAsync(Ct);
        var datasetId = await ws.CreateDatasetAsync(cancellationToken: Ct);
        var payloads = Enumerable.Range(0, 120).Select(i => (RecordPayload)new InstructionPayload($"Soru {Code(i % 60)}", "", $"Cevap {i}")).ToArray();
        var ids = await AddAsync(ws, datasetId, payloads);
        var options = new ExportOptions(ExportFormat.Alpaca, [RecordStatus.Draft], Split: SplitOptions.Standard);

        var result = await ws.Export.ExportAsync(datasetId, options, Path.Combine(ws.Directory, "split"), cancellationToken: Ct);
        var test = await ws.Export.GetSplitPartAsync(datasetId, options, SplitPart.Test, Ct);
        var validation = await ws.Export.GetSplitPartAsync(datasetId, options, SplitPart.Validation, Ct);

        // The test file's records, in its order: the questions a model trained on the rest has not seen.
        var answers = File.ReadLines(result.Files.Single(f => f.Part == SplitPart.Test).Path).Select(line => (string?)JsonNode.Parse(line)!["output"]);
        Assert.NotEmpty(test);
        Assert.Equal(answers, test.Select(id => $"Cevap {ids.IndexOf(id)}"));
        Assert.Equal(result.Files.Single(f => f.Part == SplitPart.Validation).Count, validation.Count);
        Assert.Empty(test.Intersect(validation));

        // No test part, no split: nothing.
        Assert.Empty(await ws.Export.GetSplitPartAsync(datasetId, options with { Split = SplitOptions.TrainValidation }, SplitPart.Test, Ct));
        Assert.Empty(await ws.Export.GetSplitPartAsync(datasetId, options with { Split = null }, SplitPart.Test, Ct));
    }

    [Fact]
    public async Task Formats_and_splits_that_do_not_fit_are_rejected()
    {
        await using var ws = await TestWorkspace.CreateAsync(Ct);
        var datasetId = await ws.CreateDatasetAsync(cancellationToken: Ct);
        var path = Path.Combine(ws.Directory, "x.jsonl");

        await Assert.ThrowsAsync<ArgumentException>(() => ws.Export.ExportAsync(datasetId, ExportOptions.DefaultFor(ExportFormat.HuggingFaceDpo), path, cancellationToken: Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => ws.Export.PreviewAsync(
            datasetId, ExportOptions.DefaultFor(ExportFormat.OpenAi) with { Split = new SplitOptions(50, 10, 10) }, 1, cancellationToken: Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => ws.Export.PreviewAsync(
            datasetId, ExportOptions.DefaultFor(ExportFormat.OpenAi) with { Statuses = [RecordStatus.Archived] }, 1, cancellationToken: Ct));
        Assert.False(File.Exists(path));
        Assert.Null(await ws.Export.GetLastOptionsAsync(datasetId, Ct));
    }

    [Fact]
    public async Task Exports_ten_thousand_chats_with_a_split()
    {
        await using var ws = await TestWorkspace.CreateAsync(Ct);
        var datasetId = await ws.CreateDatasetAsync(DatasetType.Chat, Ct);
        await ws.Commands.AddRangeAsync(datasetId, Enumerable.Range(0, 10_000).Select(i => new NewRecord(
            new ChatPayload("Kısa ve nazik yanıt ver.", [
                new ChatMessage(ChatRole.User, $"Soru {i}: Siparişimin kargo durumunu nasıl öğrenebilirim? E-posta: musteri{i}@ornek.com"),
                new ChatMessage(ChatRole.Assistant, $"Yanıt {i}: Hesabım > Siparişlerim sayfasından takip numaranızla kargo durumunu görebilirsiniz."),
            ]),
            RecordLineage.Manual())), cancellationToken: Ct);
        await ws.Analysis.AnalyzeAsync(datasetId, cancellationToken: Ct);
        var options = new ExportOptions(ExportFormat.ShareGpt, [RecordStatus.Draft], Split: SplitOptions.Standard);
        var stopwatch = Stopwatch.StartNew();

        var result = await ws.Export.ExportAsync(datasetId, options, Path.Combine(ws.Directory, "big"), cancellationToken: Ct);

        TestContext.Current.TestOutputHelper?.WriteLine($"export {stopwatch.Elapsed.TotalMilliseconds:F0} ms");
        Assert.Equal((10_000L, 10_000L), (result.Exported, result.Masked));
        Assert.Equal(10_000, result.Files.Sum(f => File.ReadLines(f.Path).LongCount()));
    }

    [Fact]
    public async Task A_stratified_split_meets_the_shares_in_every_category()
    {
        await using var ws = await TestWorkspace.CreateAsync(Ct);
        var datasetId = await ws.CreateDatasetAsync(cancellationToken: Ct);
        var hukuk = await ws.Tags.CreateAsync("kategori:hukuk", Ct);
        var teknik = await ws.Tags.CreateAsync("kategori:teknik", Ct);
        var zor = await ws.Tags.CreateAsync("zor", Ct);
        await ws.Commands.AddRangeAsync(datasetId, Enumerable.Range(0, 70).Select(i => new NewRecord(
            new InstructionPayload($"Hukuk sorusu {Code(i % 50)}", "", $"Yanıt {i}"), RecordLineage.Manual())), cancellationToken: Ct);
        await ws.Commands.AddRangeAsync(datasetId, Enumerable.Range(0, 20).Select(i => new NewRecord(
            new InstructionPayload($"Teknik soru {Code(100 + i)}", "", $"Yanıt {i}"), RecordLineage.Manual())), cancellationToken: Ct);
        var ids = (await ws.Queries.GetPageAsync(datasetId, RecordFilter.None, null, 100, Ct)).Items.Select(r => r.Id).ToList();
        await ws.Commands.SetTagAsync(ids[..70], hukuk.Id, present: true, Ct);
        await ws.Commands.SetTagAsync(ids[70..], teknik.Id, present: true, Ct);
        await ws.Commands.SetTagAsync(ids[..10], zor.Id, present: true, Ct); // another group: ignored
        var options = new ExportOptions(ExportFormat.Alpaca, [RecordStatus.Draft], Split: SplitOptions.Standard with { StratifyBy = "kategori" });

        var result = await ws.Export.ExportAsync(datasetId, options, Path.Combine(ws.Directory, "strata"), cancellationToken: Ct);

        var lines = result.Files.ToDictionary(f => f.Part!.Value, f => File.ReadAllLines(f.Path));
        int Count(SplitPart part, string category) => lines.GetValueOrDefault(part, []).Count(l => l.Contains(category, StringComparison.Ordinal));
        // Teknik: 20 questions → exactly 16 / 2 / 2.
        Assert.Equal((16, 2, 2), (Count(SplitPart.Train, "Teknik"), Count(SplitPart.Validation, "Teknik"), Count(SplitPart.Test, "Teknik")));
        // Hukuk: 70 records but 50 questions; a question never spans two files, so the cut lands near 56 / 7 / 7.
        Assert.Equal(70, Count(SplitPart.Train, "Hukuk") + Count(SplitPart.Validation, "Hukuk") + Count(SplitPart.Test, "Hukuk"));
        Assert.InRange(Count(SplitPart.Train, "Hukuk"), 56, 57);
        foreach (var question in Enumerable.Range(0, 50).Select(i => $"\"Hukuk sorusu {Code(i)}\""))
        {
            Assert.Single(lines, file => file.Value.Any(l => l.Contains(question, StringComparison.Ordinal)));
        }

        // The same options write the same files again.
        var again = await ws.Export.ExportAsync(datasetId, options, Path.Combine(ws.Directory, "strata-again"), cancellationToken: Ct);
        Assert.Equal(result.Files.Select(f => f.Count), again.Files.Select(f => f.Count));
        Assert.Equal(options, await ws.Export.GetLastOptionsAsync(datasetId, Ct));
    }

    // Distinct letters for each question, so no two are near-duplicates ("sorusu 1" and "sorusu 10" are).
    private static string Code(int i) => new(new Random(i).GetItems("abcçdefgğhıijklmnoöprsştuüvyz".ToCharArray(), 8));

    private static async Task<List<Guid>> AddAsync(TestWorkspace ws, Guid datasetId, params RecordPayload[] payloads)
    {
        var ids = new List<Guid>();
        foreach (var payload in payloads)
        {
            ids.Add(await ws.Commands.AddAsync(datasetId, payload, RecordLineage.Manual(), Ct));
        }

        return ids;
    }

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
