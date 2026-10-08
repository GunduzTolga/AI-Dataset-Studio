using System.Text;
using AIDatasetStudio.Application.Import;
using AIDatasetStudio.Application.Records;
using AIDatasetStudio.Desktop.Localization;
using AIDatasetStudio.Desktop.Services;
using AIDatasetStudio.Desktop.Tests.Support;
using AIDatasetStudio.Desktop.ViewModels;
using AIDatasetStudio.Desktop.ViewModels.Import;
using AIDatasetStudio.Domain.Enums;
using AIDatasetStudio.Domain.Payloads;
using AIDatasetStudio.Infrastructure.Tests.Support;

namespace AIDatasetStudio.Desktop.Tests.ViewModels;

/// <summary>
/// The import wizard over real files and a real workspace.
/// </summary>
[Collection(LanguageCollection.Name)]
public sealed class ImportWizardTests
{
    public ImportWizardTests() => Localizer.Instance.SetCulture(Localizer.English);

    [Fact]
    public Task Csv_is_analyzed_mapped_and_previewed() => UiTest.RunAsync(async () =>
    {
        await using var host = new DesktopTestHost();
        var dataset = await CreateDatasetAsync(host, DatasetType.Instruction);
        var path = WriteFile(host, "faq.csv", "Soru;Bağlam;Cevap\nŞifre?;;Sıfırlayın.\nİzin?;Yıllık;14 gün.\n");
        var wizard = Wizard(host, path, dataset);

        await wizard.InitializeAsync();

        Assert.True(wizard.IsCsv);
        Assert.Equal(';', wizard.SelectedDelimiter?.Value);
        Assert.Equal(CsvOptions.Utf8, wizard.SelectedEncoding?.Name);
        Assert.Equal("Custom columns", wizard.LayoutText);
        Assert.Equal("2 rows", wizard.RowCountText);
        Assert.Equal(["Soru", "Bağlam", "Cevap"], Mapped(wizard));
        Assert.False(wizard.HasMissingFields);
        Assert.Equal(["Şifre?", "İzin?"], wizard.Preview.Select(r => r.Primary));
        Assert.Equal(["Sıfırlayın.", "14 gün."], wizard.Preview.Select(r => r.Secondary));
        Assert.Equal("First 2 rows: 2 ready, 0 will be skipped", wizard.PreviewSummary);
        Assert.Equal("Import 2 rows", wizard.StartText);
        Assert.True(wizard.ImportCommand.CanExecute(null));

        // Unmapping a required field warns and refreshes the preview.
        var output = wizard.Mappings.Single(m => m.Field == TargetField.Output);
        output.SelectedColumn = output.Options[0];
        await wizard.PendingWork;

        Assert.Equal("Not mapped: Output. These fields will be left empty.", wizard.MissingFieldsText);
        Assert.Equal(["(empty)", "(empty)"], wizard.Preview.Select(r => r.Secondary));
    });

    [Fact]
    public Task Changing_csv_settings_reads_the_file_again() => UiTest.RunAsync(async () =>
    {
        await using var host = new DesktopTestHost();
        var dataset = await CreateDatasetAsync(host, DatasetType.Instruction);
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var turkish = Encoding.GetEncoding("windows-1254");
        var path = WriteFile(host, "ansi.csv", "instruction,output\nŞehir,İstanbul\n", turkish);
        var wizard = Wizard(host, path, dataset);
        await wizard.InitializeAsync();
        Assert.Equal(CsvOptions.Windows1254, wizard.SelectedEncoding?.Name);
        Assert.Equal("Şehir", wizard.Preview[0].Primary);

        wizard.SelectedEncoding = wizard.Encodings.Single(e => e.Name == CsvOptions.Utf8);
        await wizard.PendingWork;
        Assert.Contains('�', wizard.Preview[0].Primary);

        wizard.HasHeader = false;
        await wizard.PendingWork;
        Assert.Equal(["#1", "#2"], wizard.Analysis!.Columns);
        Assert.Equal(2, wizard.Preview.Count);
        Assert.True(wizard.HasMissingFields);
    });

    [Fact]
    public Task Conversation_files_need_no_mapping_and_must_fit_the_dataset() => UiTest.RunAsync(async () =>
    {
        await using var host = new DesktopTestHost();
        var chat = await CreateDatasetAsync(host, DatasetType.Chat);
        var path = WriteFile(host, "chat.jsonl", """
            {"messages": [{"role": "user", "content": "Merhaba"}, {"role": "assistant", "content": "Selam"}, {"role": "user", "content": "Nasılsın?"}, {"role": "assistant", "content": "İyiyim"}]}
            {"messages": [{"role": "tool", "content": "{}"}]}
            """);

        var wizard = Wizard(host, path, chat);
        await wizard.InitializeAsync();

        Assert.True(wizard.IsConversation);
        Assert.False(wizard.IsColumnBased);
        Assert.Equal("4 messages", wizard.Preview[0].Badge);
        Assert.Equal("Unsupported message role “tool”", wizard.Preview[1].Primary);
        Assert.True(wizard.Preview[1].IsInvalid);

        var preference = Wizard(host, path, new ImportTarget(chat.Id, chat.Name, DatasetType.Preference));
        await preference.InitializeAsync();

        Assert.True(preference.IsUnsupported);
        Assert.Equal("OpenAI messages files cannot be imported into Preference (DPO) datasets. Choose a dataset of a matching type.", preference.UnsupportedText);
        Assert.Empty(preference.Preview);
        Assert.False(preference.ImportCommand.CanExecute(null));
    });

    [Fact]
    public Task Unreadable_files_are_explained() => UiTest.RunAsync(async () =>
    {
        await using var host = new DesktopTestHost();
        var dataset = await CreateDatasetAsync(host, DatasetType.Instruction);
        var path = WriteFile(host, "broken.json", "[\n{\"instruction\": 1},\n{oops}\n]");

        var wizard = Wizard(host, path, dataset);
        await wizard.InitializeAsync();

        Assert.Equal("“broken.json” is not a valid JSON array (line 3).", wizard.ErrorMessage);
        Assert.False(wizard.HasAnalysis);
        Assert.False(wizard.ImportCommand.CanExecute(null));
    });

    [Fact]
    public Task Import_reports_progress_and_skipped_rows() => UiTest.RunAsync(async () =>
    {
        await using var host = new DesktopTestHost();
        var dataset = await CreateDatasetAsync(host, DatasetType.Instruction);
        var path = WriteFile(host, "alpaca.jsonl", """
            {"instruction": "Topla", "input": "2+2", "output": "4"}
            not json
            {"instruction": "Çıkar", "input": "5-3", "output": "2"}
            """);
        var wizard = Wizard(host, path, dataset);
        await wizard.InitializeAsync();

        await wizard.ImportCommand.ExecuteAsync(null);

        Assert.True(wizard.IsDone);
        Assert.False(wizard.IsSetup);
        Assert.Equal((2, 1), (wizard.Result!.Imported, wizard.Result.Skipped));
        Assert.Equal([new ImportRowError(2, ImportErrorCode.InvalidJson)], wizard.Result.Errors);
        Assert.Equal("Added 2 records to “Dataset” as drafts.", wizard.ResultText);
        Assert.Equal("Skipped rows (1)", wizard.SkippedText);
        var skipped = Assert.Single(wizard.SkippedRows);
        Assert.Equal("Not valid JSON", skipped.Primary);
        Assert.False(wizard.HasMoreSkipped);
        Assert.False(wizard.ImportCommand.CanExecute(null));
    });

    [Fact]
    public Task Import_command_adds_records_and_refreshes_the_dataset() => UiTest.RunAsync(async () =>
    {
        await using var host = new DesktopTestHost();
        var dataset = await CreateDatasetAsync(host, DatasetType.Preference);
        Assert.True(host.Main.Records.IsEmptyDataset);
        host.Files.ImportPath = WriteFile(host, "dpo.json", """
            [
              {"prompt": "Başkent?", "chosen": "Ankara.", "rejected": "İstanbul."},
              {"prompt": "Plaka 06?", "chosen": "Ankara.", "rejected": "İzmir."}
            ]
            """);

        await host.Main.ImportCommand.ExecuteAsync(null);

        Assert.Contains(".jsonl", host.Files.ImportExtensions);
        Assert.Equal(SourceLayout.HuggingFaceDpo, host.Dialogs.LastImportWizard!.Analysis!.Layout);
        Assert.Equal(2, dataset.RecordCount);
        await DesktopTestHost.WaitUntilAsync(() => !host.Main.Records.IsLoading);
        Assert.Equal(["Başkent?", "Plaka 06?"], host.Main.Records.Items.Select(r => r.Preview));
        Assert.Equal("2 records imported into “Dataset”", host.Main.Status.Message);

        var details = await host.Get<IRecordQueryService>().GetAsync(host.Main.Records.Items[0].Id);
        Assert.Equal(new PreferencePayload("Başkent?", "Ankara.", "İstanbul."), details!.Payload);
        Assert.Equal("dpo.json#item=1", details.Lineage.SourceReference);
    });

    [Fact]
    public Task Closing_the_wizard_without_importing_changes_nothing() => UiTest.RunAsync(async () =>
    {
        await using var host = new DesktopTestHost();
        var dataset = await CreateDatasetAsync(host, DatasetType.Instruction);
        host.Files.ImportPath = WriteFile(host, "faq.csv", "instruction,output\na,b\n");
        var closed = false;
        host.Dialogs.ImportWizardScript = async wizard =>
        {
            wizard.CloseRequested += (_, _) => closed = true;
            await wizard.InitializeAsync();
            wizard.CancelCommand.Execute(null);
        };
        host.Main.Status.Set("Status_Ready");

        await host.Main.ImportCommand.ExecuteAsync(null);

        Assert.True(closed);
        Assert.Equal(0, dataset.RecordCount);
        Assert.Equal("Ready", host.Main.Status.Message);
    });

    [Fact]
    public Task Import_needs_a_selected_dataset() => UiTest.RunAsync(async () =>
    {
        await using var host = new DesktopTestHost();
        await host.CreateWorkspaceAsync();

        Assert.False(host.Main.ImportCommand.CanExecute(null));

        await CreateDatasetAsync(host, DatasetType.Chat, createWorkspace: false);
        Assert.True(host.Main.ImportCommand.CanExecute(null));
    });

    [Fact]
    public Task A_workbook_sheet_or_every_sheet_is_read() => UiTest.RunAsync(async () =>
    {
        await using var host = new DesktopTestHost();
        var dataset = await CreateDatasetAsync(host, DatasetType.Instruction);
        var path = XlsxFile.Write(
            Path.Combine(host.Directory, "sss.xlsx"),
            new XlsxFile.Sheet("Kargo", [["Soru", "Cevap"], ["Kargo ücreti?", "Ücretsiz."], ["Kaç günde?", "2 gün."]]),
            new XlsxFile.Sheet("İade", [["Cevap", "Soru"], ["14 gün.", "İade süresi?"]]));
        var wizard = Wizard(host, path, dataset);

        await wizard.InitializeAsync();

        Assert.True(wizard.IsExcel);
        Assert.False(wizard.IsCsv);
        Assert.False(wizard.HasFormatChoice);
        Assert.Equal("Excel workbook", wizard.FormatText);
        Assert.Equal(["Kargo", "İade", "All sheets (2)"], wizard.Sheets.Select(s => s.Label));
        Assert.Equal("Kargo", wizard.SelectedSheet?.Name);
        Assert.True(wizard.HasHeader);
        Assert.Equal(["Soru", null, "Cevap"], Mapped(wizard));
        Assert.Equal(["2", "3"], wizard.Preview.Select(r => r.NumberText));
        Assert.Equal("Import 2 rows", wizard.StartText);

        wizard.SelectedSheet = wizard.Sheets[^1];
        await wizard.PendingWork;

        Assert.True(wizard.IsAllSheets);
        Assert.Equal(["Soru", null, "Cevap"], Mapped(wizard));
        Assert.Equal(["Kargo · 2", "Kargo · 3", "İade · 2"], wizard.Preview.Select(r => r.NumberText));
        Assert.Equal(["Kargo ücreti?", "Kaç günde?", "İade süresi?"], wizard.Preview.Select(r => r.Primary));

        wizard.HasHeader = false;
        await wizard.PendingWork;

        Assert.Equal(["A", "B"], wizard.Analysis!.Columns);
        Assert.Equal(5, wizard.Analysis.RowCount);
        Assert.Null(wizard.SelectedSheet?.Name);
        Assert.True(wizard.HasMissingFields);

        var instruction = wizard.Mappings.Single(m => m.Field == TargetField.Instruction);
        instruction.SelectedColumn = instruction.Options.Single(o => o.Column == "A");
        await wizard.PendingWork;
        await wizard.ImportCommand.ExecuteAsync(null);

        Assert.Equal(5, wizard.Result!.Imported);
    });

    [Fact]
    public Task A_text_file_can_be_read_as_paragraphs_or_as_a_table() => UiTest.RunAsync(async () =>
    {
        await using var host = new DesktopTestHost();
        var dataset = await CreateDatasetAsync(host, DatasetType.Chat);
        var path = WriteFile(host, "notlar.txt", "Kargo ücreti ne kadar?\n500 TL üzeri ücretsiz.\n\nİade süresi, kaç gün?\n14 gün.\n");
        var wizard = Wizard(host, path, dataset);

        await wizard.InitializeAsync();

        Assert.True(wizard.HasFormatChoice);
        Assert.Equal(["CSV", "Plain text"], wizard.FormatOptions.Select(f => f.Label));
        Assert.Equal(ImportFormat.PlainText, wizard.SelectedFormat?.Value);
        Assert.Equal("Paragraphs", wizard.LayoutText);
        Assert.True(wizard.HasTextLayoutHint);
        Assert.False(wizard.IsCsv);
        var user = wizard.Mappings.Single(m => m.Field == TargetField.User);
        Assert.Equal(["— Not mapped —", "First line", "Lines after the first", "Whole paragraph"], user.Options.Select(o => o.Label));
        Assert.Equal([null, TextColumns.FirstLine, TextColumns.Rest], Mapped(wizard));
        Assert.Equal(["Kargo ücreti ne kadar?", "İade süresi, kaç gün?"], wizard.Preview.Select(r => r.Primary));
        Assert.Equal(["1", "4"], wizard.Preview.Select(r => r.NumberText));

        wizard.SelectedFormat = wizard.FormatOptions[0];
        await wizard.PendingWork;

        Assert.True(wizard.IsCsv);
        Assert.False(wizard.HasTextLayoutHint);
        Assert.Equal(',', wizard.SelectedDelimiter?.Value);
        Assert.Equal(["Kargo ücreti ne kadar?"], wizard.Analysis!.Columns);

        // CSV settings keep the chosen reading.
        wizard.HasHeader = false;
        await wizard.PendingWork;
        Assert.Equal(ImportFormat.Csv, wizard.Analysis!.Format);
        Assert.Equal(["#1", "#2"], wizard.Analysis.Columns);
    });

    [Fact]
    public Task Markdown_sections_are_mapped_by_heading() => UiTest.RunAsync(async () =>
    {
        await using var host = new DesktopTestHost();
        var dataset = await CreateDatasetAsync(host, DatasetType.Instruction);
        var path = WriteFile(host, "kilavuz.md", "# Sipariş\n## Kargo ücreti?\nÜcretsiz.\n## İade süresi?\n14 gün.\n");
        var wizard = Wizard(host, path, dataset);

        await wizard.InitializeAsync();

        Assert.Equal(("Markdown", "Sections by heading"), (wizard.FormatText, wizard.LayoutText));
        Assert.Equal(["Markdown", "Plain text"], wizard.FormatOptions.Select(f => f.Label));
        Assert.Equal([TextColumns.Heading, null, TextColumns.Content], Mapped(wizard));
        Assert.Equal(["Kargo ücreti?", "İade süresi?"], wizard.Preview.Select(r => r.Primary));
        Assert.Equal(["Ücretsiz.", "14 gün."], wizard.Preview.Select(r => r.Secondary));

        var input = wizard.Mappings.Single(m => m.Field == TargetField.Input);
        input.SelectedColumn = input.Options.Single(o => o.Label == "Headings above");
        await wizard.PendingWork;
        await wizard.ImportCommand.ExecuteAsync(null);

        var queries = host.Get<IRecordQueryService>();
        var page = await queries.GetPageAsync(dataset.Id, RecordFilter.None, after: null, pageSize: 10);
        var record = await queries.GetAsync(page.Items[0].Id);
        Assert.Equal(new InstructionPayload("Kargo ücreti?", "Sipariş", "Ücretsiz."), record!.Payload);
        Assert.Equal("kilavuz.md#line=2", record.Lineage.SourceReference);
    });

    [Fact]
    public Task Old_excel_workbooks_are_explained() => UiTest.RunAsync(async () =>
    {
        await using var host = new DesktopTestHost();
        var dataset = await CreateDatasetAsync(host, DatasetType.Instruction);
        var path = Path.Combine(host.Directory, "eski.xls");
        File.WriteAllBytes(path, [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1]);
        var wizard = Wizard(host, path, dataset);

        await wizard.InitializeAsync();

        Assert.Equal(
            "“eski.xls” is an Excel 97–2003 or password-protected workbook. Save it in Excel as an .xlsx workbook without a password and try again.",
            wizard.ErrorMessage);
        Assert.False(wizard.ImportCommand.CanExecute(null));
    });

    private static ImportWizardViewModel Wizard(DesktopTestHost host, string path, ImportTarget target) =>
        new(host.Get<IImportService>(), Localizer.Instance, path, target);

    private static ImportWizardViewModel Wizard(DesktopTestHost host, string path, DatasetNodeViewModel dataset) =>
        Wizard(host, path, new ImportTarget(dataset.Id, dataset.Name, dataset.Type));

    private static IEnumerable<string?> Mapped(ImportWizardViewModel wizard) => wizard.Mappings.Select(m => m.SelectedColumn?.Column);

    private static string WriteFile(DesktopTestHost host, string name, string content, Encoding? encoding = null)
    {
        var path = Path.Combine(host.Directory, name);
        File.WriteAllText(path, content, encoding ?? new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return path;
    }

    private static async Task<DatasetNodeViewModel> CreateDatasetAsync(DesktopTestHost host, DatasetType type, bool createWorkspace = true)
    {
        if (createWorkspace)
        {
            await host.CreateWorkspaceAsync();
        }

        host.Dialogs.TextAnswers.Enqueue("Project");
        await host.Main.Explorer.NewProjectCommand.ExecuteAsync(null);
        host.Dialogs.DatasetAnswer = new NewDatasetRequest("Dataset", type);
        await host.Main.Explorer.NewDatasetCommand.ExecuteAsync(null);
        var dataset = host.Main.Explorer.SelectedDataset!;
        await DesktopTestHost.WaitUntilAsync(() => host.Main.Records.Dataset == dataset && !host.Main.Records.IsLoading);
        return dataset;
    }
}
