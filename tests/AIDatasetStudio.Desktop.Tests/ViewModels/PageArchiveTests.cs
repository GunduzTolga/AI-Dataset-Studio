using System.IO.Compression;
using System.Text;
using AIDatasetStudio.Application.Documents;
using AIDatasetStudio.Application.Records;
using AIDatasetStudio.Desktop.Localization;
using AIDatasetStudio.Desktop.Tests.Support;
using AIDatasetStudio.Domain.Enums;
using AIDatasetStudio.Domain.Payloads;

namespace AIDatasetStudio.Desktop.Tests.ViewModels;

/// <summary>
/// HTML pages and Notion or Confluence exports as documents (M17): the import wizard and Q&amp;A extraction.
/// </summary>
[Collection(LanguageCollection.Name)]
public sealed class PageArchiveTests
{
    public PageArchiveTests() => Localizer.Instance.SetCulture(Localizer.English);

    [Fact]
    public Task Questions_are_written_from_a_notion_export_page_by_page() => UiTest.RunAsync(async () =>
    {
        await using var host = new DesktopTestHost();
        await host.CreateDatasetAsync(DatasetType.Instruction, []);
        host.AddAIConnection();
        host.Files.DocumentPath = WriteZip(host, "Export-42.zip", new Dictionary<string, string>
        {
            ["Destek 0123456789abcdef0123456789abcdef.md"] = "# Destek\n\n" + string.Join(' ', Enumerable.Repeat("Müşteri sorularının yanıtları bu sayfalardadır ve her sayfa bir konuyu anlatır.", 6)),
            ["Destek 0123456789abcdef0123456789abcdef/Kargo 11111111111111111111111111111111.md"] =
                "# Kargo\n\n## Ücret\n\nKargo ücreti 500 TL ve üzeri siparişlerde ücretsizdir, altında 49,90 TL alınır.",
        });
        host.AI.Answers.Enqueue(["[]"]);
        host.AI.Answers.Enqueue(["[{\"question\": \"Kargo ücreti ne kadar?\", \"answer\": \"500 TL altında 49,90 TL.\"}]"]);
        host.Dialogs.QAExtractionScript = async extraction =>
        {
            await extraction.InitializeAsync();
            Assert.Equal(DocumentFormat.Archive, extraction.Document!.Format);
            extraction.ChunkTokens = 200;
            Assert.Equal("· 2 pages · 2 passages", extraction.DocumentText);
            Assert.Equal("Kargo › Ücret", extraction.Chunks[1].FullTitle);
            Assert.StartsWith("#2 · page 2 · ", extraction.Chunks[1].Detail, StringComparison.Ordinal);
            await extraction.StartCommand.ExecuteAsync(null);
            await extraction.AddCommand.ExecuteAsync(null);
        };

        await host.Main.ExtractQuestionsCommand.ExecuteAsync(null);

        Assert.StartsWith("Document: Export-42.zip\nSection: Kargo › Ücret\n", host.AI.Requests.Last().Request.Messages[1].Content, StringComparison.Ordinal);
        var row = Assert.Single(host.Main.Records.Items);
        var record = await host.Get<IRecordQueryService>().GetAsync(row.Id);
        Assert.Equal(new InstructionPayload("Kargo ücreti ne kadar?", "", "500 TL altında 49,90 TL."), record!.Payload);
        Assert.Equal("Export-42.zip#page=2", record.Lineage.SourceReference);
        host.Main.Records.SelectedItem = row;
        await host.WaitForDetailsAsync(row.Id);
        Assert.Contains(host.Main.Details.Info, item => item.Label == "Section" && item.Value == "Kargo › Ücret · page 2");
    });

    [Fact]
    public Task The_import_wizard_reads_pages_and_explains_archives_without_them() => UiTest.RunAsync(async () =>
    {
        await using var host = new DesktopTestHost();
        await host.CreateDatasetAsync(DatasetType.Instruction, []);
        var page = Path.Combine(host.Directory, "sss.html");
        await File.WriteAllTextAsync(page, "<html><body><h2>Kargo ücreti ne kadar?</h2><p>49,90 TL.</p><h2>İade süresi?</h2><p>14 gün.</p></body></html>");
        host.Files.ImportPath = page;
        host.Dialogs.ImportWizardScript = async wizard =>
        {
            await wizard.InitializeAsync();
            Assert.Equal(("HTML page", "2 rows"), (wizard.FormatText, wizard.RowCountText));
            Assert.Equal("Each heading with text under it becomes a record: the heading asks and the text answers. Text before the first heading is left out.", wizard.TextLayoutHint);
            Assert.True(wizard.CanExtractQuestions);
            await wizard.ImportCommand.ExecuteAsync(null);
        };

        await host.Main.ImportCommand.ExecuteAsync(null);
        Assert.Equal(2, host.Main.Records.Items.Count);

        host.Files.ImportPath = WriteZip(host, "DESTEK.zip", new Dictionary<string, string>
        {
            ["DESTEK/Kargo_1001.html"] = "<html><body><h1 id=\"title-heading\">Destek : Kargo</h1><h2>Ücret</h2><p>49,90 TL.</p></body></html>",
        });
        host.Dialogs.ImportWizardScript = async wizard =>
        {
            await wizard.InitializeAsync();
            Assert.Equal("ZIP of pages (Notion, Confluence)", wizard.FormatText);
            Assert.StartsWith("Each page is read in turn under its title", wizard.TextLayoutHint, StringComparison.Ordinal);
        };
        await host.Main.ImportCommand.ExecuteAsync(null);

        host.Files.ImportPath = WriteZip(host, "resimler.zip", new Dictionary<string, string> { ["a.png"] = "png" });
        host.Dialogs.ImportWizardScript = async wizard =>
        {
            await wizard.InitializeAsync();
            Assert.Equal(
                "“resimler.zip” holds no HTML, Markdown or text page with text. Export Notion pages as Markdown or HTML, and a Confluence space as HTML.",
                wizard.ErrorMessage);
        };
        await host.Main.ImportCommand.ExecuteAsync(null);
    });

    private static string WriteZip(DesktopTestHost host, string name, Dictionary<string, string> files)
    {
        var path = Path.Combine(host.Directory, name);
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (entry, content) in files)
        {
            using var stream = zip.CreateEntry(entry).Open();
            stream.Write(Encoding.UTF8.GetBytes(content));
        }

        return path;
    }
}
