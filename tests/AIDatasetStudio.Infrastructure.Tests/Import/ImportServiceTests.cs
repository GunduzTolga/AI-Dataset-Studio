using System.Diagnostics;
using System.Globalization;
using System.Text;
using AIDatasetStudio.Application.Import;
using AIDatasetStudio.Application.Records;
using AIDatasetStudio.Domain.Enums;
using AIDatasetStudio.Domain.Payloads;
using AIDatasetStudio.Domain.ValueObjects;
using AIDatasetStudio.Infrastructure.Import;
using AIDatasetStudio.Infrastructure.Tests.Support;

namespace AIDatasetStudio.Infrastructure.Tests.Import;

public sealed class ImportServiceTests
{
    private static readonly Dictionary<TargetField, string> Alpaca = new()
    {
        [TargetField.Instruction] = "instruction",
        [TargetField.Output] = "output",
    };

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Analyzes_openai_messages_jsonl()
    {
        await using var ws = await TestWorkspace.CreateAsync(Ct);
        var path = ws.WriteFile("chat.jsonl", Lines(
            """{"messages": [{"role": "system", "content": "Kısa yanıt ver."}, {"role": "user", "content": "Merhaba"}, {"role": "assistant", "content": "Selam!"}]}""",
            "",
            """{"messages": [{"role": "user", "content": "Saat kaç?"}, {"role": "assistant", "content": "Bilmiyorum."}]}"""));

        var analysis = await ws.Import.AnalyzeAsync(path, DatasetType.Chat, cancellationToken: Ct);

        Assert.Equal(ImportFormat.JsonLines, analysis.Format);
        Assert.Null(analysis.Csv);
        Assert.Equal(SourceLayout.OpenAiMessages, analysis.Layout);
        Assert.Equal(["messages"], analysis.Columns);
        Assert.Equal(2, analysis.RowCount);
        Assert.Empty(analysis.SuggestedMapping);
        Assert.True(analysis.SupportsTargetType);

        var preference = await ws.Import.AnalyzeAsync(path, DatasetType.Preference, cancellationToken: Ct);
        Assert.False(preference.SupportsTargetType);
    }

    [Fact]
    public async Task Tool_calling_files_are_recognized_and_imported_with_their_calls()
    {
        await using var ws = await TestWorkspace.CreateAsync(Ct);
        var datasetId = await ws.CreateDatasetAsync(DatasetType.ToolCalling, Ct);
        var path = ws.WriteFile("tools.jsonl", Lines(
            """{"messages": [{"role": "user", "content": "Kadıköy?"}, {"role": "assistant", "content": null, "tool_calls": [{"id": "1", "type": "function", "function": {"name": "get_forecast", "arguments": "{\"location\": \"Kadıköy\"}"}}]}, {"role": "tool", "tool_call_id": "1", "content": "19"}, {"role": "assistant", "content": "19 derece."}], "tools": [{"type": "function", "function": {"name": "get_forecast"}}]}""",
            """{"messages": [{"role": "user", "content": "Merhaba"}, {"role": "assistant", "content": "Merhaba!"}], "tools": []}"""));

        var analysis = await ws.Import.AnalyzeAsync(path, DatasetType.ToolCalling, cancellationToken: Ct);
        var result = await ws.Import.ImportAsync(PlanFrom(analysis, DatasetType.ToolCalling), datasetId, cancellationToken: Ct);

        Assert.Equal((SourceLayout.OpenAiTools, true), (analysis.Layout, analysis.SupportsTargetType));
        Assert.False((await ws.Import.AnalyzeAsync(path, DatasetType.Chat, cancellationToken: Ct)).SupportsTargetType);
        Assert.Equal((2, 0), (result.Imported, result.Skipped));
        var page = await ws.Queries.GetPageAsync(datasetId, RecordFilter.None, after: null, pageSize: 10, Ct);
        var first = (ToolCallingPayload)(await ws.Queries.GetAsync(page.Items[0].Id, Ct))!.Payload;
        Assert.Equal(new ToolCall("get_forecast", "{\"location\":\"Kadıköy\"}", "19"), first.Messages[1].ToolCalls.Single());
    }

    [Fact]
    public async Task Analyzes_a_turkish_excel_csv()
    {
        await using var ws = await TestWorkspace.CreateAsync(Ct);
        var path = ws.WriteFile(
            "sss.csv",
            "Soru;Cevap\r\n\"Şifremi unuttum, ne yapmalıyım?\";\"Giriş ekranında \"\"Şifremi unuttum\"\"a tıklayın.\"\r\nİzin günü kaç?;14 gün\r\n;;\r\n",
            TextEncodings.Get(CsvOptions.Windows1254));

        var analysis = await ws.Import.AnalyzeAsync(path, DatasetType.Instruction, cancellationToken: Ct);

        Assert.Equal(ImportFormat.Csv, analysis.Format);
        Assert.Equal(new CsvOptions(';', CsvOptions.Windows1254, HasHeader: true), analysis.Csv);
        Assert.Equal(SourceLayout.Custom, analysis.Layout);
        Assert.Equal(["Soru", "Cevap"], analysis.Columns);
        Assert.Equal(2, analysis.RowCount);
        Assert.Equal("Soru", analysis.SuggestedMapping[TargetField.Instruction]);
        Assert.Equal("Cevap", analysis.SuggestedMapping[TargetField.Output]);

        var preview = await ws.Import.PreviewAsync(PlanFrom(analysis, DatasetType.Instruction), cancellationToken: Ct);

        Assert.Equal([2L, 3L], preview.Rows.Select(r => r.RowNumber));
        Assert.Equal(
            new InstructionPayload("Şifremi unuttum, ne yapmalıyım?", "", "Giriş ekranında \"Şifremi unuttum\"a tıklayın."),
            preview.Rows[0].Payload);
    }

    [Fact]
    public async Task Csv_options_can_be_overridden()
    {
        await using var ws = await TestWorkspace.CreateAsync(Ct);
        var path = ws.WriteFile("data.txt", "merhaba|dünya\nselam|evren\n");

        var detected = await ws.Import.AnalyzeAsync(path, DatasetType.Instruction, cancellationToken: Ct);
        Assert.Equal(new CsvOptions('|', CsvOptions.Utf8, HasHeader: true), detected.Csv);
        Assert.Equal(["merhaba", "dünya"], detected.Columns);
        Assert.Equal(1, detected.RowCount);

        var headerless = await ws.Import.AnalyzeAsync(path, DatasetType.Instruction, new ImportOverrides(Csv: detected.Csv! with { HasHeader = false }), Ct);

        Assert.Equal(["#1", "#2"], headerless.Columns);
        Assert.Equal(2, headerless.RowCount);
        var plan = PlanFrom(headerless, DatasetType.Instruction) with
        {
            Mapping = new Dictionary<TargetField, string> { [TargetField.Instruction] = "#1", [TargetField.Output] = "#2" },
        };
        var preview = await ws.Import.PreviewAsync(plan, cancellationToken: Ct);
        Assert.Equal(new InstructionPayload("merhaba", "", "dünya"), preview.Rows[0].Payload);
        Assert.Equal(new InstructionPayload("selam", "", "evren"), preview.Rows[1].Payload);
    }

    [Fact]
    public async Task Tsv_and_quoted_multiline_csv()
    {
        await using var ws = await TestWorkspace.CreateAsync(Ct);
        var path = ws.WriteFile("data.tsv", "instruction\tinput\toutput\n\"Listele:\nbir\niki\"\t\tTamam\n");

        var analysis = await ws.Import.AnalyzeAsync(path, DatasetType.Instruction, cancellationToken: Ct);
        var preview = await ws.Import.PreviewAsync(PlanFrom(analysis, DatasetType.Instruction), cancellationToken: Ct);

        Assert.Equal('\t', analysis.Csv!.Delimiter);
        Assert.Equal(SourceLayout.Alpaca, analysis.Layout);
        Assert.Equal(new InstructionPayload("Listele:\nbir\niki", "", "Tamam"), Assert.Single(preview.Rows).Payload);
    }

    [Fact]
    public async Task Analyzes_an_alpaca_json_array()
    {
        await using var ws = await TestWorkspace.CreateAsync(Ct);
        var path = ws.WriteFile("alpaca.json", """
            [
              {"instruction": "Çevir", "input": "Hello", "output": "Merhaba"},
              {"instruction": "Özetle", "input": "", "output": "Kısa."},
              "not an object"
            ]
            """);

        var analysis = await ws.Import.AnalyzeAsync(path, DatasetType.Instruction, cancellationToken: Ct);
        var preview = await ws.Import.PreviewAsync(PlanFrom(analysis, DatasetType.Instruction), cancellationToken: Ct);

        Assert.Equal(ImportFormat.JsonArray, analysis.Format);
        Assert.Equal(SourceLayout.Alpaca, analysis.Layout);
        Assert.Equal(["instruction", "input", "output"], analysis.Columns);
        Assert.Equal(3, analysis.RowCount);
        Assert.Equal(new InstructionPayload("Çevir", "Hello", "Merhaba"), preview.Rows[0].Payload);
        Assert.Equal(new ImportRowError(3, ImportErrorCode.NotAnObject), preview.Rows[2].Error);
    }

    [Fact]
    public async Task Json_file_holding_one_object_per_line_is_read_as_jsonl()
    {
        await using var ws = await TestWorkspace.CreateAsync(Ct);
        var path = ws.WriteFile("lines.json", "﻿" + Lines("""{"prompt": "a", "completion": "b"}""", """{"prompt": "c", "completion": "d"}"""));

        var analysis = await ws.Import.AnalyzeAsync(path, DatasetType.Instruction, cancellationToken: Ct);

        Assert.Equal(ImportFormat.JsonLines, analysis.Format);
        Assert.Equal(SourceLayout.PromptCompletion, analysis.Layout);
        Assert.Equal(2, analysis.RowCount);
    }

    [Fact]
    public async Task Preview_reports_bad_lines_without_stopping()
    {
        await using var ws = await TestWorkspace.CreateAsync(Ct);
        var path = ws.WriteFile("mixed.jsonl", Lines(
            """{"instruction": "a", "output": "b"}""",
            """{"instruction": "c", "output": """,
            "[1, 2]",
            """{"instruction": " ", "output": ""}""",
            """{"instruction": "e", "output": "f"}"""));
        var analysis = await ws.Import.AnalyzeAsync(path, DatasetType.Instruction, cancellationToken: Ct);

        var preview = await ws.Import.PreviewAsync(PlanFrom(analysis, DatasetType.Instruction), maxRows: 4, Ct);

        Assert.Equal(5, analysis.RowCount);
        Assert.Equal(4, preview.Rows.Count);
        Assert.True(preview.Rows[0].IsValid);
        Assert.Equal(new ImportRowError(2, ImportErrorCode.InvalidJson), preview.Rows[1].Error);
        Assert.Equal(new ImportRowError(3, ImportErrorCode.NotAnObject), preview.Rows[2].Error);
        Assert.Equal(new ImportRowError(4, ImportErrorCode.EmptyRecord), preview.Rows[3].Error);
    }

    [Fact]
    public async Task Imports_valid_rows_and_reports_the_rest()
    {
        await using var ws = await TestWorkspace.CreateAsync(Ct);
        var datasetId = await ws.CreateDatasetAsync(DatasetType.Instruction, Ct);
        var path = ws.WriteFile("sharegpt.jsonl", Lines(
            """{"conversations": [{"from": "human", "value": "Başkent neresi?"}, {"from": "gpt", "value": "Ankara."}]}""",
            """{"conversations": [{"from": "human", "value": "a"}, {"from": "gpt", "value": "b"}, {"from": "human", "value": "c"}, {"from": "gpt", "value": "d"}]}""",
            """{"conversations": [{"from": "human", "value": "Şehir sayısı?"}, {"from": "gpt", "value": "81."}]}"""));
        var analysis = await ws.Import.AnalyzeAsync(path, DatasetType.Instruction, cancellationToken: Ct);
        var reports = new List<ImportProgress>();

        var result = await ws.Import.ImportAsync(PlanFrom(analysis, DatasetType.Instruction), datasetId, new InlineProgress<ImportProgress>(reports.Add), Ct);

        Assert.Equal(2, result.Imported);
        Assert.Equal(1, result.Skipped);
        Assert.Equal([new ImportRowError(2, ImportErrorCode.MultiTurnNotSupported)], result.Errors);
        Assert.Equal(new ImportProgress(3, 2), reports[^1]);

        var page = await ws.Queries.GetPageAsync(datasetId, RecordFilter.None, after: null, pageSize: 10, Ct);
        Assert.Equal(["Başkent neresi?", "Şehir sayısı?"], page.Items.Select(r => r.Preview));
        var details = await ws.Queries.GetAsync(page.Items[1].Id, Ct);
        Assert.Equal(new RecordLineage(RecordSourceKind.FileImport, "sharegpt.jsonl#line=3"), details!.Lineage);
        Assert.Equal(RecordStatus.Draft, details.Status);

        // Imported records are searchable straight away.
        Assert.Equal(1, await ws.Queries.CountAsync(datasetId, new RecordFilter(SearchText: "ankara"), Ct));
    }

    [Theory]
    [InlineData(ImportFormat.Csv, "faq.csv#row=3")]
    [InlineData(ImportFormat.JsonArray, "faq.json#item=2")]
    public async Task Lineage_points_into_the_source(ImportFormat format, string expected)
    {
        await using var ws = await TestWorkspace.CreateAsync(Ct);
        var datasetId = await ws.CreateDatasetAsync(DatasetType.Instruction, Ct);
        var path = format == ImportFormat.Csv
            ? ws.WriteFile("faq.csv", "instruction,output\na,b\nc,d\n")
            : ws.WriteFile("faq.json", """[{"instruction": "a", "output": "b"}, {"instruction": "c", "output": "d"}]""");
        var analysis = await ws.Import.AnalyzeAsync(path, DatasetType.Instruction, cancellationToken: Ct);

        await ws.Import.ImportAsync(PlanFrom(analysis, DatasetType.Instruction), datasetId, cancellationToken: Ct);

        var page = await ws.Queries.GetPageAsync(datasetId, RecordFilter.None, after: null, pageSize: 10, Ct);
        var details = await ws.Queries.GetAsync(page.Items[1].Id, Ct);
        Assert.Equal(expected, details!.Lineage.SourceReference);
    }

    [Fact]
    public async Task Only_the_first_errors_are_kept()
    {
        await using var ws = await TestWorkspace.CreateAsync(Ct);
        var datasetId = await ws.CreateDatasetAsync(DatasetType.Instruction, Ct);
        var path = ws.WriteFile("broken.jsonl", Lines(Enumerable.Repeat("not json", 150).Append("""{"instruction": "ok", "output": "ok"}""").ToArray()));
        var plan = new ImportPlan(path, ImportFormat.JsonLines, null, SourceLayout.Alpaca, Alpaca, DatasetType.Instruction);

        var result = await ws.Import.ImportAsync(plan, datasetId, cancellationToken: Ct);

        Assert.Equal(1, result.Imported);
        Assert.Equal(150, result.Skipped);
        Assert.Equal(ImportResult.MaxReportedErrors, result.Errors.Count);
        Assert.Equal(Enumerable.Range(1, 100).Select(n => (long)n), result.Errors.Select(e => e.RowNumber));
    }

    [Fact]
    public async Task Cancelling_rolls_the_import_back()
    {
        await using var ws = await TestWorkspace.CreateAsync(Ct);
        var datasetId = await ws.CreateDatasetAsync(DatasetType.Instruction, Ct);
        var path = ws.WriteFile("big.jsonl", Lines(Enumerable.Range(1, 3_000).Select(i => $$"""{"instruction": "q{{i}}", "output": "a{{i}}"}""").ToArray()));
        var plan = new ImportPlan(path, ImportFormat.JsonLines, null, SourceLayout.Alpaca, Alpaca, DatasetType.Instruction);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var progress = new InlineProgress<ImportProgress>(p =>
        {
            if (p.Imported >= 1_000)
            {
                cancellation.Cancel();
            }
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ws.Import.ImportAsync(plan, datasetId, progress, cancellation.Token));

        Assert.Equal(0, await ws.ScalarAsync("SELECT COUNT(*) FROM DatasetRecords"));
        Assert.Equal(0, await ws.ScalarAsync("SELECT COUNT(*) FROM RecordSearchKeys"));
    }

    [Fact]
    public async Task Imports_ten_thousand_rows()
    {
        await using var ws = await TestWorkspace.CreateAsync(Ct);
        var datasetId = await ws.CreateDatasetAsync(DatasetType.Chat, Ct);
        var builder = new StringBuilder();
        for (var i = 0; i < 10_000; i++)
        {
            builder.Append(CultureInfo.InvariantCulture, $$"""{"messages": [{"role": "user", "content": "Soru {{i}}: İstanbul'da hava nasıl?"}, {"role": "assistant", "content": "Yanıt {{i}}: Güneşli ve ılık."}]}""").Append('\n');
        }

        var path = ws.WriteFile("big.jsonl", builder.ToString());
        var stopwatch = Stopwatch.StartNew();

        var analysis = await ws.Import.AnalyzeAsync(path, DatasetType.Chat, cancellationToken: Ct);
        var analyzed = stopwatch.Elapsed;
        var result = await ws.Import.ImportAsync(PlanFrom(analysis, DatasetType.Chat), datasetId, cancellationToken: Ct);

        TestContext.Current.TestOutputHelper?.WriteLine($"analyze {analyzed.TotalMilliseconds:F0} ms, import {(stopwatch.Elapsed - analyzed).TotalMilliseconds:F0} ms");
        Assert.Equal(10_000, analysis.RowCount);
        Assert.Equal(10_000, result.Imported);
        Assert.Equal(10_000, await ws.Queries.CountAsync(datasetId, RecordFilter.None, Ct));
        Assert.Equal(1, await ws.Queries.CountAsync(datasetId, new RecordFilter(SearchText: "yanıt 9999"), Ct));
    }

    [Fact]
    public async Task Missing_file_is_reported()
    {
        await using var ws = await TestWorkspace.CreateAsync(Ct);
        var path = Path.Combine(ws.Directory, "missing.csv");

        var error = await Assert.ThrowsAsync<ImportFileException>(() => ws.Import.AnalyzeAsync(path, DatasetType.Instruction, cancellationToken: Ct));

        Assert.Equal(ImportFileError.NotFound, error.Error);
        Assert.Equal(path, error.FilePath);
    }

    [Fact]
    public async Task Malformed_json_array_is_reported_with_its_line()
    {
        await using var ws = await TestWorkspace.CreateAsync(Ct);
        var path = ws.WriteFile("bad.json", "[\n  {\"instruction\": \"a\"},\n  {\"instruction\": oops}\n]");

        var error = await Assert.ThrowsAsync<ImportFileException>(() => ws.Import.AnalyzeAsync(path, DatasetType.Instruction, cancellationToken: Ct));

        Assert.Equal(ImportFileError.InvalidJson, error.Error);
        Assert.Equal(3, error.Line);
    }

    [Fact]
    public void Supported_extensions_are_lowercase_with_a_dot()
    {
        Assert.All(new ImportService(null!, null!).SupportedExtensions, e => Assert.Matches("^\\.[a-z]+$", e));
    }

    private static ImportPlan PlanFrom(ImportAnalysis analysis, DatasetType type) =>
        new(analysis.FilePath, analysis.Format, analysis.Csv, analysis.Layout, analysis.SuggestedMapping, type);

    private static string Lines(params string[] lines) => string.Join('\n', lines) + "\n";

    /// <summary>
    /// Reports synchronously, unlike <see cref="Progress{T}"/> which posts to the thread pool.
    /// </summary>
    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
