using System.IO.Compression;
using System.Text;
using AIDatasetStudio.Application.Import;
using AIDatasetStudio.Application.Records;
using AIDatasetStudio.Domain.Enums;
using AIDatasetStudio.Domain.Payloads;
using AIDatasetStudio.Infrastructure.Tests.Support;

namespace AIDatasetStudio.Infrastructure.Tests.Import;

/// <summary>
/// HTML pages and ZIPs of pages in the import wizard, without a model (M17, SDD §12.3).
/// </summary>
public sealed class HtmlArchiveImportTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_page_is_imported_as_sections_with_their_line()
    {
        await using var ws = await TestWorkspace.CreateAsync(Ct);
        var datasetId = await ws.CreateDatasetAsync(DatasetType.Instruction, Ct);
        var path = ws.WriteFile("sss.html", """
            <html><body>
            <h1>Sık sorulan sorular</h1>
            <h2>Kargo ücreti ne kadar?</h2>
            <p>500 TL üzeri siparişlerde kargo ücretsizdir.</p>
            <h2>İade süresi nedir?</h2>
            <p>Ürünler 14 gün içinde iade edilebilir.</p>
            </body></html>
            """);

        var analysis = await ws.Import.AnalyzeAsync(path, DatasetType.Instruction, cancellationToken: Ct);

        Assert.Equal((ImportFormat.Html, SourceLayout.Sections, 2L), (analysis.Format, analysis.Layout, analysis.RowCount));
        Assert.Equal([ImportFormat.Html], analysis.ReadableAs);
        await ws.Import.ImportAsync(PlanFrom(analysis), datasetId, cancellationToken: Ct);
        var page = await ws.Queries.GetPageAsync(datasetId, RecordFilter.None, after: null, pageSize: 10, Ct);
        var second = (await ws.Queries.GetAsync(page.Items[1].Id, Ct))!;
        Assert.Equal(new InstructionPayload("İade süresi nedir?", "", "Ürünler 14 gün içinde iade edilebilir."), second.Payload);
        Assert.Equal("sss.html#line=5", second.Lineage.SourceReference);
    }

    [Fact]
    public async Task A_notion_export_is_imported_page_by_page()
    {
        await using var ws = await TestWorkspace.CreateAsync(Ct);
        var datasetId = await ws.CreateDatasetAsync(DatasetType.Instruction, Ct);
        var path = Path.Combine(ws.Directory, "Export-42.zip");
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            Add(zip, "Destek 0123456789abcdef0123456789abcdef/Kargo 11111111111111111111111111111111.md", "# Kargo\n\n## Ücret\n\n49,90 TL.");
            Add(zip, "Destek 0123456789abcdef0123456789abcdef.md", "# Destek\n\nGenel bilgiler.");
        }

        var analysis = await ws.Import.AnalyzeAsync(path, DatasetType.Instruction, cancellationToken: Ct);

        Assert.Equal((ImportFormat.Archive, SourceLayout.Sections), (analysis.Format, analysis.Layout));
        var preview = await ws.Import.PreviewAsync(PlanFrom(analysis), cancellationToken: Ct);
        Assert.Equal(
            [new InstructionPayload("Destek", "", "Genel bilgiler."), new InstructionPayload("Ücret", "", "49,90 TL.")],
            preview.Rows.Select(r => r.Payload));
        await ws.Import.ImportAsync(PlanFrom(analysis), datasetId, cancellationToken: Ct);
        var page = await ws.Queries.GetPageAsync(datasetId, RecordFilter.None, after: null, pageSize: 10, Ct);
        Assert.Equal("Export-42.zip#page=2", (await ws.Queries.GetAsync(page.Items[1].Id, Ct))!.Lineage.SourceReference);
        Assert.Contains(".zip", ws.Import.SupportedExtensions);
        Assert.Contains(".html", ws.Import.SupportedExtensions);
    }

    [Fact]
    public async Task Pages_are_told_from_workbooks_and_documents()
    {
        await using var ws = await TestWorkspace.CreateAsync(Ct);
        var page = ws.WriteFile("sayfa.txt", "<!DOCTYPE html><html><body><h1>Başlık</h1><p>Metin.</p></body></html>");
        var workbook = XlsxFile.Write(Path.Combine(ws.Directory, "kitap.zip"), new XlsxFile.Sheet("Sayfa1", [["Soru", "Yanıt"], ["a", "b"]]));
        var empty = Path.Combine(ws.Directory, "resimler.zip");
        using (var zip = ZipFile.Open(empty, ZipArchiveMode.Create))
        {
            Add(zip, "a.png", "png");
        }

        Assert.Equal(ImportFormat.Html, (await ws.Import.AnalyzeAsync(page, DatasetType.Instruction, cancellationToken: Ct)).Format);
        Assert.Equal(ImportFormat.Excel, (await ws.Import.AnalyzeAsync(workbook, DatasetType.Instruction, cancellationToken: Ct)).Format);
        var error = await Assert.ThrowsAsync<ImportFileException>(() => ws.Import.AnalyzeAsync(empty, DatasetType.Instruction, cancellationToken: Ct));
        Assert.Equal(ImportFileError.EmptyArchive, error.Error);
    }

    private static void Add(ZipArchive zip, string name, string content)
    {
        using var stream = zip.CreateEntry(name).Open();
        stream.Write(Encoding.UTF8.GetBytes(content));
    }

    private static ImportPlan PlanFrom(ImportAnalysis analysis) =>
        new(analysis.FilePath, analysis.Format, analysis.Csv, analysis.Layout, analysis.SuggestedMapping, DatasetType.Instruction);
}
