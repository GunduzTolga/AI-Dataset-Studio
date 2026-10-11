using System.Text.Json.Serialization;
using AIDatasetStudio.Application.Import;
using AIDatasetStudio.Application.Records;
using AIDatasetStudio.Domain.Enums;
using AIDatasetStudio.Domain.Payloads;
using AIDatasetStudio.Infrastructure.Import;
using AIDatasetStudio.Infrastructure.Tests.Support;
using Parquet;
using Parquet.Serialization;

namespace AIDatasetStudio.Infrastructure.Tests.Import;

/// <summary>
/// Parquet files, as Hugging Face publishes datasets (M25).
/// </summary>
public sealed class ParquetImportTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task An_alpaca_parquet_file_is_read_like_json_lines_across_row_groups()
    {
        await using var ws = await TestWorkspace.CreateAsync(Ct);
        var rows = Enumerable.Range(1, 5).Select(i => new AlpacaRow { Instruction = $"Soru {i}", Input = i == 2 ? "bağlam" : "", Output = $"Cevap {i}", Score = i * 0.5 }).ToList();
        var path = await WriteAsync(ws, "train-00000-of-00001.parquet", rows, rowGroupSize: 2);
        await using (var reader = await ParquetReader.CreateAsync(path, cancellationToken: Ct))
        {
            Assert.Equal(3, reader.RowGroupCount);
        }

        var analysis = await ws.Import.AnalyzeAsync(path, DatasetType.Instruction, cancellationToken: Ct);

        Assert.Equal((ImportFormat.Parquet, SourceLayout.Alpaca, 5L), (analysis.Format, analysis.Layout, analysis.RowCount));
        Assert.Equal(["instruction", "input", "output", "score"], analysis.Columns);
        var dataset = await ws.CreateDatasetAsync(DatasetType.Instruction, Ct);
        var result = await ws.Import.ImportAsync(Plan(analysis, DatasetType.Instruction), dataset, cancellationToken: Ct);

        Assert.Equal((5, 0), (result.Imported, result.Skipped));
        var page = await ws.Queries.GetPageAsync(dataset, RecordFilter.None, after: null, pageSize: 10, Ct);
        var second = (await ws.Queries.GetAsync(page.Items[1].Id, Ct))!;
        Assert.Equal(new InstructionPayload("Soru 2", "bağlam", "Cevap 2"), second.Payload);
        Assert.Equal("train-00000-of-00001.parquet#row=2", second.Lineage.SourceReference);
    }

    [Fact]
    public async Task Nested_message_lists_become_chat_records()
    {
        await using var ws = await TestWorkspace.CreateAsync(Ct);
        var path = await WriteAsync(ws, "chat.parquet", new[]
        {
            new MessagesRow { Messages = [new() { Role = "system", Content = "Kısa yanıt ver." }, new() { Role = "user", Content = "Merhaba" }, new() { Role = "assistant", Content = "Selam!" }] },
            new MessagesRow { Messages = [new() { Role = "user", Content = "Kargo ne zaman gelir?" }, new() { Role = "assistant", Content = "1-3 iş günü." }] },
        });

        var analysis = await ws.Import.AnalyzeAsync(path, DatasetType.Chat, cancellationToken: Ct);
        Assert.Equal((ImportFormat.Parquet, SourceLayout.OpenAiMessages), (analysis.Format, analysis.Layout));

        var dataset = await ws.CreateDatasetAsync(DatasetType.Chat, Ct);
        var result = await ws.Import.ImportAsync(Plan(analysis, DatasetType.Chat), dataset, cancellationToken: Ct);

        Assert.Equal(2, result.Imported);
        var first = (await ws.Queries.GetAsync((await ws.Queries.GetPageAsync(dataset, RecordFilter.None, after: null, pageSize: 10, Ct)).Items[0].Id, Ct))!;
        var chat = Assert.IsType<ChatPayload>(first.Payload);
        Assert.Equal(("Kısa yanıt ver.", 2), (chat.SystemPrompt, chat.Messages.Count));
        Assert.Equal(new ChatMessage(ChatRole.Assistant, "Selam!"), chat.Messages[1]);
    }

    [Fact]
    public async Task Row_groups_too_large_to_hold_are_refused_before_any_is_read()
    {
        await using var ws = await TestWorkspace.CreateAsync(Ct);
        var path = await WriteAsync(ws, "big.parquet", Enumerable.Range(1, 50).Select(i => new AlpacaRow { Instruction = new string('x', 200), Output = "y" }));
        var limit = ParquetSource.MaxRowGroupBytes;
        ParquetSource.MaxRowGroupBytes = 1024;
        try
        {
            var error = await Assert.ThrowsAsync<ImportFileException>(() => ws.Import.AnalyzeAsync(path, DatasetType.Instruction, cancellationToken: Ct));
            Assert.Equal(ImportFileError.ParquetTooLarge, error.Error);
        }
        finally
        {
            ParquetSource.MaxRowGroupBytes = limit;
        }
    }

    [Fact]
    public async Task A_damaged_parquet_file_is_reported_as_such()
    {
        await using var ws = await TestWorkspace.CreateAsync(Ct);
        var path = Path.Combine(ws.Directory, "bozuk.parquet");
        await File.WriteAllBytesAsync(path, [.. "PAR1"u8, .. new byte[64], .. "PAR1"u8], Ct);

        var error = await Assert.ThrowsAsync<ImportFileException>(() => ws.Import.AnalyzeAsync(path, DatasetType.Instruction, cancellationToken: Ct));

        Assert.Equal(ImportFileError.InvalidParquet, error.Error);
        Assert.Contains(".parquet", ws.Import.SupportedExtensions);
    }

    private static async Task<string> WriteAsync<T>(TestWorkspace ws, string name, IEnumerable<T> rows, int? rowGroupSize = null)
    {
        var path = Path.Combine(ws.Directory, name);
        await using var file = File.Create(path);
        await ParquetSerializer.SerializeAsync(rows, file, rowGroupSize is { } size ? new ParquetOptions { RowGroupSize = size } : null, cancellationToken: Ct);
        return path;
    }

    private static ImportPlan Plan(ImportAnalysis analysis, DatasetType type) =>
        new(analysis.FilePath, analysis.Format, analysis.Csv, analysis.Layout, analysis.SuggestedMapping, type);

    private sealed class AlpacaRow
    {
        [JsonPropertyName("instruction")]
        public string Instruction { get; set; } = "";

        [JsonPropertyName("input")]
        public string Input { get; set; } = "";

        [JsonPropertyName("output")]
        public string Output { get; set; } = "";

        [JsonPropertyName("score")]
        public double Score { get; set; }
    }

    private sealed class MessagesRow
    {
        [JsonPropertyName("messages")]
        public List<Message> Messages { get; set; } = [];
    }

    private sealed class Message
    {
        [JsonPropertyName("role")]
        public string Role { get; set; } = "";

        [JsonPropertyName("content")]
        public string Content { get; set; } = "";
    }
}
