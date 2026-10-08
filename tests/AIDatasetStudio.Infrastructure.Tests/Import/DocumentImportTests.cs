using System.Text;
using AIDatasetStudio.Application.Import;
using AIDatasetStudio.Application.Records;
using AIDatasetStudio.Domain.Enums;
using AIDatasetStudio.Domain.Payloads;
using AIDatasetStudio.Infrastructure.Import;
using AIDatasetStudio.Infrastructure.Tests.Support;
using static AIDatasetStudio.Infrastructure.Tests.Support.XlsxFile;

namespace AIDatasetStudio.Infrastructure.Tests.Import;

/// <summary>
/// Excel workbooks, Markdown documents and plain text (M11, SDD §12.2).
/// </summary>
public sealed class DocumentImportTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Sheet Kargo => new("Kargo",
    [
        ["Soru", "Cevap"],
        ["Kargo ücreti ne kadar?", "500 TL üzeri ücretsiz."],
        null,
        ["Kargo kaç günde gelir?", "2–3 iş günü."],
    ]);

    private static Sheet Iade => new("İade & değişim",
    [
        [null, null, null],
        ["Not", "Cevap", "Soru"],
        ["iç", "14 gün.", "İade süresi nedir?"],
        [null, null, "Değişim ücretli mi?"],
    ]);

    [Fact]
    public async Task A_workbook_is_read_from_its_active_sheet_with_a_header()
    {
        await using var ws = await TestWorkspace.CreateAsync(Ct);
        var path = Write(Path.Combine(ws.Directory, "sss.xlsx"), Kargo, Iade, new Sheet("Gizli", [["x"]], Hidden: true));

        var analysis = await ws.Import.AnalyzeAsync(path, DatasetType.Instruction, cancellationToken: Ct);

        Assert.Equal(ImportFormat.Excel, analysis.Format);
        Assert.Equal([ImportFormat.Excel], analysis.ReadableAs);
        Assert.Equal(["Kargo", "İade & değişim"], analysis.Sheets);
        Assert.Equal(new SheetOptions("Kargo", HasHeader: true), analysis.Sheet);
        Assert.Equal(["Soru", "Cevap"], analysis.Columns);
        Assert.Equal(2, analysis.RowCount);
        Assert.Equal("Soru", analysis.SuggestedMapping[TargetField.Instruction]);
        Assert.Equal("Cevap", analysis.SuggestedMapping[TargetField.Output]);

        var preview = await ws.Import.PreviewAsync(PlanFrom(analysis, DatasetType.Instruction), cancellationToken: Ct);

        Assert.Equal([2L, 4L], preview.Rows.Select(r => r.RowNumber));
        Assert.Equal(new InstructionPayload("Kargo ücreti ne kadar?", "", "500 TL üzeri ücretsiz."), preview.Rows[0].Payload);
        Assert.All(preview.Rows, r => Assert.Equal("Kargo", r.Sheet));
    }

    [Fact]
    public async Task Every_sheet_can_be_read_with_its_own_header_and_lineage()
    {
        await using var ws = await TestWorkspace.CreateAsync(Ct);
        var datasetId = await ws.CreateDatasetAsync(DatasetType.Instruction, Ct);
        var path = Write(Path.Combine(ws.Directory, "sss.xlsx"), Kargo, Iade);

        var analysis = await ws.Import.AnalyzeAsync(path, DatasetType.Instruction, new ImportOverrides(Sheet: new SheetOptions(null, HasHeader: true)), Ct);

        Assert.Null(analysis.Sheet!.Sheet);
        Assert.Equal(["Soru", "Cevap", "Not"], analysis.Columns);
        Assert.Equal(4, analysis.RowCount);

        var result = await ws.Import.ImportAsync(PlanFrom(analysis, DatasetType.Instruction), datasetId, cancellationToken: Ct);

        Assert.Equal(4, result.Imported);
        var page = await ws.Queries.GetPageAsync(datasetId, RecordFilter.None, after: null, pageSize: 10, Ct);
        Assert.Equal(["Kargo ücreti ne kadar?", "Kargo kaç günde gelir?", "İade süresi nedir?", "Değişim ücretli mi?"], page.Items.Select(r => r.Preview));
        var details = await ws.Queries.GetAsync(page.Items[2].Id, Ct);
        Assert.Equal(new InstructionPayload("İade süresi nedir?", "", "14 gün."), details!.Payload);
        Assert.Equal("sss.xlsx#sheet=İade %26 değişim&row=3", details.Lineage.SourceReference);
        Assert.Equal("sss.xlsx#sheet=Kargo&row=4", (await ws.Queries.GetAsync(page.Items[1].Id, Ct))!.Lineage.SourceReference);
    }

    [Fact]
    public async Task Cells_are_read_as_excel_shows_them_and_columns_are_lettered_without_a_header()
    {
        await using var ws = await TestWorkspace.CreateAsync(Ct);
        var path = Write(Path.Combine(ws.Directory, "tipler.xlsx"), new Sheet("Sayfa1",
        [
            ["metin", 42, 0.1 + 0.2, true, new DateTime(2024, 3, 1), new DateTime(2024, 3, 1, 14, 30, 0)],
            [new Inline("satır 1_x000D_\nsatır 2"), new Formula("A1&\"!\"", "metin!"), null, false, 1234567.5, "  "],
        ]));

        var analysis = await ws.Import.AnalyzeAsync(path, DatasetType.Instruction, new ImportOverrides(Sheet: new SheetOptions("sayfa1", HasHeader: false)), Ct);

        Assert.Equal(["A", "B", "C", "D", "E", "F"], analysis.Columns);
        Assert.Equal(2, analysis.RowCount);
        var mapping = new Dictionary<TargetField, string> { [TargetField.Instruction] = "A", [TargetField.Input] = "B", [TargetField.Output] = "C" };
        var cells = new Dictionary<TargetField, string> { [TargetField.Instruction] = "D", [TargetField.Input] = "E", [TargetField.Output] = "F" };

        var first = await ws.Import.PreviewAsync(PlanFrom(analysis, DatasetType.Instruction) with { Mapping = mapping }, cancellationToken: Ct);
        var second = await ws.Import.PreviewAsync(PlanFrom(analysis, DatasetType.Instruction) with { Mapping = cells }, cancellationToken: Ct);

        Assert.Equal(new InstructionPayload("metin", "42", "0.3"), first.Rows[0].Payload);
        Assert.Equal(new InstructionPayload("satır 1\r\nsatır 2", "metin!", ""), first.Rows[1].Payload);
        Assert.Equal(new InstructionPayload("TRUE", "2024-03-01", "2024-03-01 14:30"), second.Rows[0].Payload);
        Assert.Equal(new InstructionPayload("FALSE", "1234567.5", "  "), second.Rows[1].Payload);
    }

    [Fact]
    public async Task Workbooks_that_cannot_be_read_are_explained()
    {
        await using var ws = await TestWorkspace.CreateAsync(Ct);
        var legacy = Path.Combine(ws.Directory, "eski.xls");
        File.WriteAllBytes(legacy, [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1, 0, 0, 0, 0]);
        var damaged = ws.WriteFile("bozuk.xlsx", "bu bir çalışma kitabı değil");
        var workbook = Write(Path.Combine(ws.Directory, "sss.xlsx"), Kargo);

        Assert.Equal(ImportFileError.LegacyWorkbook, (await Assert.ThrowsAsync<ImportFileException>(() => ws.Import.AnalyzeAsync(legacy, DatasetType.Instruction, cancellationToken: Ct))).Error);
        Assert.Equal(ImportFileError.InvalidWorkbook, (await Assert.ThrowsAsync<ImportFileException>(() => ws.Import.AnalyzeAsync(damaged, DatasetType.Instruction, cancellationToken: Ct))).Error);
        var missing = await Assert.ThrowsAsync<ImportFileException>(() =>
            ws.Import.AnalyzeAsync(workbook, DatasetType.Instruction, new ImportOverrides(Sheet: new SheetOptions("Yok", true)), Ct));
        Assert.Equal(ImportFileError.SheetNotFound, missing.Error);
    }

    [Theory]
    [InlineData("General", false)]
    [InlineData("0.00", false)]
    [InlineData("#,##0 [$TL-41F]", false)]
    [InlineData("0.0 \"gün\"", false)]
    [InlineData("dd/mm/yyyy", true)]
    [InlineData("[$-41F]d mmmm yyyy;@", true)]
    [InlineData("[h]:mm", true)]
    [InlineData("hh:mm AM/PM", true)]
    [InlineData("0;[Red]-0;\"y\"", false)]
    public void Date_formats_are_told_from_number_formats(string code, bool isDate)
    {
        Assert.Equal(isDate, XlsxWorkbook.IsDateFormat(code));
    }

    [Fact]
    public void Column_letters_round_trip()
    {
        Assert.Equal(["A", "Z", "AA", "AZ", "BA", "XFD"], new[] { 0, 25, 26, 51, 52, 16383 }.Select(XlsxWorkbook.ColumnName));
        Assert.Equal(16383, XlsxWorkbook.ColumnIndex("XFD1048576"));
        Assert.Null(XlsxWorkbook.ColumnIndex("12"));
    }

    [Fact]
    public async Task Markdown_is_split_into_sections_at_its_headings()
    {
        await using var ws = await TestWorkspace.CreateAsync(Ct);
        var datasetId = await ws.CreateDatasetAsync(DatasetType.Instruction, Ct);
        var path = ws.WriteFile("kilavuz.md", """
            ---
            title: Kılavuz
            ---
            Giriş metni bir bölüme ait değil.

            # Sipariş
            ## Kargo ücreti ne kadar? ##
            500 TL üzeri siparişlerde **ücretsiz**.

            ```bash
            # bu bir başlık değil
            curl https://ornek.com
            ```

            ## Boş bölüm
            İade
            ----
            14 gün içinde.

            ---

            ### Değişim ücretli mi?
            Hayır.
            """);

        var analysis = await ws.Import.AnalyzeAsync(path, DatasetType.Instruction, cancellationToken: Ct);

        Assert.Equal(ImportFormat.Markdown, analysis.Format);
        Assert.Equal([ImportFormat.Markdown, ImportFormat.PlainText], analysis.ReadableAs);
        Assert.Equal(SourceLayout.Sections, analysis.Layout);
        Assert.Equal(TextColumns.Markdown, analysis.Columns);
        Assert.Equal(3, analysis.RowCount);
        Assert.Equal(TextColumns.Heading, analysis.SuggestedMapping[TargetField.Instruction]);
        Assert.Equal(TextColumns.Content, analysis.SuggestedMapping[TargetField.Output]);

        var plan = PlanFrom(analysis, DatasetType.Instruction) with
        {
            Mapping = new Dictionary<TargetField, string>(analysis.SuggestedMapping) { [TargetField.Input] = TextColumns.ParentHeadings },
        };
        var preview = await ws.Import.PreviewAsync(plan, cancellationToken: Ct);

        Assert.Equal([7L, 16L, 22L], preview.Rows.Select(r => r.RowNumber));
        Assert.Equal(
            new InstructionPayload("Kargo ücreti ne kadar?", "Sipariş", "500 TL üzeri siparişlerde **ücretsiz**.\n\n```bash\n# bu bir başlık değil\ncurl https://ornek.com\n```"),
            preview.Rows[0].Payload);
        Assert.Equal(new InstructionPayload("İade", "Sipariş", "14 gün içinde.\n\n---"), preview.Rows[1].Payload);
        Assert.Equal(new InstructionPayload("Değişim ücretli mi?", "Sipariş › İade", "Hayır."), preview.Rows[2].Payload);

        await ws.Import.ImportAsync(plan, datasetId, cancellationToken: Ct);
        var page = await ws.Queries.GetPageAsync(datasetId, RecordFilter.None, after: null, pageSize: 10, Ct);
        Assert.Equal("kilavuz.md#line=16", (await ws.Queries.GetAsync(page.Items[1].Id, Ct))!.Lineage.SourceReference);
    }

    [Fact]
    public async Task Markdown_without_headings_is_read_as_paragraphs()
    {
        await using var ws = await TestWorkspace.CreateAsync(Ct);
        var path = ws.WriteFile("notlar.md", "Kargo ücreti ne kadar?\n500 TL üzeri ücretsiz.\n\nİade süresi?\n14 gün.\n");

        var analysis = await ws.Import.AnalyzeAsync(path, DatasetType.Chat, cancellationToken: Ct);

        Assert.Equal(ImportFormat.PlainText, analysis.Format);
        Assert.Equal([ImportFormat.Markdown, ImportFormat.PlainText], analysis.ReadableAs);
        Assert.Equal(2, analysis.RowCount);
        Assert.Equal(TextColumns.FirstLine, analysis.SuggestedMapping[TargetField.User]);
        Assert.Equal(TextColumns.Rest, analysis.SuggestedMapping[TargetField.Assistant]);

        // Chosen as Markdown on purpose, the file has nothing to read.
        var sections = await ws.Import.AnalyzeAsync(path, DatasetType.Chat, new ImportOverrides(ImportFormat.Markdown), Ct);
        Assert.Equal((ImportFormat.Markdown, 0L), (sections.Format, sections.RowCount));
    }

    [Fact]
    public async Task Plain_text_is_read_as_paragraphs_or_as_a_table()
    {
        await using var ws = await TestWorkspace.CreateAsync(Ct);
        var turkish = TextEncodings.Get(CsvOptions.Windows1254);
        var path = ws.WriteFile("sss.txt", "Şifremi unuttum, ne yapmalıyım?\nGiriş ekranından sıfırlayın.\nBağlantı e-postayla gelir.\n\n\nİzin günü kaç?\n14 gün.\n", turkish);

        var analysis = await ws.Import.AnalyzeAsync(path, DatasetType.Preference, cancellationToken: Ct);

        Assert.Equal(ImportFormat.PlainText, analysis.Format);
        Assert.Equal([ImportFormat.Csv, ImportFormat.PlainText], analysis.ReadableAs);
        Assert.Equal(SourceLayout.Paragraphs, analysis.Layout);
        Assert.Equal(2, analysis.RowCount);
        Assert.False(analysis.SuggestedMapping.ContainsKey(TargetField.Rejected));
        var preview = await ws.Import.PreviewAsync(PlanFrom(analysis, DatasetType.Preference), cancellationToken: Ct);
        Assert.Equal([1L, 6L], preview.Rows.Select(r => r.RowNumber));
        Assert.Equal(new PreferencePayload("Şifremi unuttum, ne yapmalıyım?", "Giriş ekranından sıfırlayın.\nBağlantı e-postayla gelir.", ""), preview.Rows[0].Payload);

        var table = await ws.Import.AnalyzeAsync(path, DatasetType.Preference, new ImportOverrides(ImportFormat.Csv), Ct);
        Assert.Equal(ImportFormat.Csv, table.Format);
        Assert.Equal(new CsvOptions(',', CsvOptions.Windows1254, HasHeader: true), table.Csv);

        // A format the file cannot be read as is ignored.
        var json = await ws.Import.AnalyzeAsync(path, DatasetType.Preference, new ImportOverrides(ImportFormat.JsonLines), Ct);
        Assert.Equal(ImportFormat.PlainText, json.Format);
    }

    [Theory]
    [InlineData("soru;cevap\nbir;iki\nüç;dört\n", ImportFormat.Csv)]
    [InlineData("Merhaba, nasılsın?\nİyiyim, sağ ol.\n\nYarın görüşürüz.\n", ImportFormat.PlainText)]
    [InlineData("Tek satırlık bir not.", ImportFormat.PlainText)]
    [InlineData("Ad: Ayşe; yaş: 30\nŞehir: İzmir\n", ImportFormat.PlainText)]
    public async Task Text_files_are_told_apart_from_tables(string content, ImportFormat expected)
    {
        await using var ws = await TestWorkspace.CreateAsync(Ct);
        var path = ws.WriteFile("dosya.txt", content);

        Assert.Equal(expected, (await ws.Import.AnalyzeAsync(path, DatasetType.Instruction, cancellationToken: Ct)).Format);
    }

    [Fact]
    public async Task Ten_thousand_sheet_rows_are_imported()
    {
        await using var ws = await TestWorkspace.CreateAsync(Ct);
        var datasetId = await ws.CreateDatasetAsync(DatasetType.Instruction, Ct);
        var rows = new List<object?[]?> { new object?[] { "instruction", "output" } };
        rows.AddRange(Enumerable.Range(0, 10_000).Select(i => new object?[] { $"Soru {i}", $"Yanıt {i}" }));
        var path = Write(Path.Combine(ws.Directory, "buyuk.xlsx"), new Sheet("Veri", [.. rows]));

        var analysis = await ws.Import.AnalyzeAsync(path, DatasetType.Instruction, cancellationToken: Ct);
        var result = await ws.Import.ImportAsync(PlanFrom(analysis, DatasetType.Instruction), datasetId, cancellationToken: Ct);

        Assert.Equal(SourceLayout.Alpaca, analysis.Layout);
        Assert.Equal(10_000, result.Imported);
        Assert.Equal(1, await ws.Queries.CountAsync(datasetId, new RecordFilter(SearchText: "yanıt 9999"), Ct));
    }

    private static ImportPlan PlanFrom(ImportAnalysis analysis, DatasetType type) =>
        new(analysis.FilePath, analysis.Format, analysis.Csv, analysis.Layout, analysis.SuggestedMapping, type, analysis.Sheet);
}
