using AIDatasetStudio.Application.Import;
using AIDatasetStudio.Application.Records;
using AIDatasetStudio.Domain.Enums;
using AIDatasetStudio.Domain.Payloads;
using AIDatasetStudio.Infrastructure.Tests.Support;
using static AIDatasetStudio.Infrastructure.Tests.Support.DocxFile;
using static AIDatasetStudio.Infrastructure.Tests.Support.PdfFile;

namespace AIDatasetStudio.Infrastructure.Tests.Import;

/// <summary>
/// PDF and Word documents in the import wizard, without a model (M13, SDD §12.3).
/// </summary>
public sealed class PdfWordImportTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_pdf_is_imported_as_sections_with_their_page()
    {
        await using var ws = await TestWorkspace.CreateAsync(Ct);
        var datasetId = await ws.CreateDatasetAsync(DatasetType.Instruction, Ct);
        var path = PdfFile.Write(Path.Combine(ws.Directory, "sss.pdf"),
            [
                new Text(56, 780, "Sık sorulan sorular", 16, Bold: true),
                new Text(56, 750, "Kargo ücreti ne kadar?", 13, Bold: true),
                .. Lines(56, 730, 10, "500 TL üzeri siparişlerde kargo ücretsizdir."),
            ],
            [
                new Text(56, 780, "İade süresi nedir?", 13, Bold: true),
                .. Lines(56, 760, 10, "Ürünler 14 gün içinde iade edilebilir.", "Ücret 3 iş gününde kartınıza yansır."),
            ]);

        var analysis = await ws.Import.AnalyzeAsync(path, DatasetType.Instruction, cancellationToken: Ct);

        Assert.Equal((ImportFormat.Pdf, SourceLayout.Sections, 2L), (analysis.Format, analysis.Layout, analysis.RowCount));
        Assert.Equal([ImportFormat.Pdf], analysis.ReadableAs);
        Assert.Equal(TextColumns.Markdown, analysis.Columns);
        Assert.Equal(TextColumns.Heading, analysis.SuggestedMapping[TargetField.Instruction]);

        var result = await ws.Import.ImportAsync(PlanFrom(analysis), datasetId, cancellationToken: Ct);

        Assert.Equal(2, result.Imported);
        var page = await ws.Queries.GetPageAsync(datasetId, RecordFilter.None, after: null, pageSize: 10, Ct);
        var second = await ws.Queries.GetAsync(page.Items[1].Id, Ct);
        Assert.Equal(new InstructionPayload("İade süresi nedir?", "", "Ürünler 14 gün içinde iade edilebilir. Ücret 3 iş gününde kartınıza yansır."), second!.Payload);
        Assert.Equal(new Domain.ValueObjects.RecordLineage(RecordSourceKind.FileImport, "sss.pdf#page=2"), second.Lineage);
    }

    [Fact]
    public async Task A_word_document_without_headings_is_imported_as_paragraphs()
    {
        await using var ws = await TestWorkspace.CreateAsync(Ct);
        var datasetId = await ws.CreateDatasetAsync(DatasetType.Chat, Ct);
        var path = DocxFile.Write(Path.Combine(ws.Directory, "notlar.docx"), string.Concat(
            P("Kargo ücreti"),
            "<w:p><w:r><w:t>Kargo kaç gün sürer?</w:t><w:br/><w:t>İki üç iş günü.</w:t></w:r></w:p>"));

        var analysis = await ws.Import.AnalyzeAsync(path, DatasetType.Chat, cancellationToken: Ct);

        Assert.Equal((ImportFormat.Word, SourceLayout.Paragraphs, 2L), (analysis.Format, analysis.Layout, analysis.RowCount));
        var preview = await ws.Import.PreviewAsync(PlanFrom(analysis, DatasetType.Chat), cancellationToken: Ct);
        Assert.Equal([1L, 2L], preview.Rows.Select(r => r.RowNumber));
        Assert.Equal(
            new ChatPayload(string.Empty, [new ChatMessage(ChatRole.User, "Kargo kaç gün sürer?"), new ChatMessage(ChatRole.Assistant, "İki üç iş günü.")]),
            preview.Rows[1].Payload);

        await ws.Import.ImportAsync(PlanFrom(analysis, DatasetType.Chat), datasetId, cancellationToken: Ct);
        var page = await ws.Queries.GetPageAsync(datasetId, RecordFilter.None, after: null, pageSize: 10, Ct);
        Assert.Equal("notlar.docx#paragraph=2", (await ws.Queries.GetAsync(page.Items[1].Id, Ct))!.Lineage.SourceReference);
    }

    [Fact]
    public async Task Word_files_are_told_from_workbooks_and_old_ones_are_explained()
    {
        await using var ws = await TestWorkspace.CreateAsync(Ct);
        var renamed = DocxFile.Write(Path.Combine(ws.Directory, "belge.zip"), P("Merhaba"));
        var legacy = Path.Combine(ws.Directory, "eski.doc");
        await File.WriteAllBytesAsync(legacy, [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1], Ct);

        Assert.Equal(ImportFormat.Word, (await ws.Import.AnalyzeAsync(renamed, DatasetType.Instruction, cancellationToken: Ct)).Format);
        var error = await Assert.ThrowsAsync<ImportFileException>(() => ws.Import.AnalyzeAsync(legacy, DatasetType.Instruction, cancellationToken: Ct));
        Assert.Equal(ImportFileError.LegacyDocument, error.Error);
        Assert.Contains(".pdf", ws.Import.SupportedExtensions);
        Assert.Contains(".docx", ws.Import.SupportedExtensions);
    }

    private static ImportPlan PlanFrom(ImportAnalysis analysis, DatasetType type = DatasetType.Instruction) =>
        new(analysis.FilePath, analysis.Format, analysis.Csv, analysis.Layout, analysis.SuggestedMapping, type);
}
