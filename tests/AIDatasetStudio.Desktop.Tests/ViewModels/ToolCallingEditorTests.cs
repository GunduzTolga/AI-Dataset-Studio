using AIDatasetStudio.Application.Analysis;
using AIDatasetStudio.Application.Export;
using AIDatasetStudio.Application.Records;
using AIDatasetStudio.Desktop.Localization;
using AIDatasetStudio.Desktop.Services;
using AIDatasetStudio.Desktop.Tests.Support;
using AIDatasetStudio.Desktop.ViewModels;
using AIDatasetStudio.Desktop.ViewModels.Export;
using AIDatasetStudio.Domain.Enums;
using AIDatasetStudio.Domain.Payloads;

namespace AIDatasetStudio.Desktop.Tests.ViewModels;

/// <summary>
/// Tool calling records in the editor (M32, SDD §4.4): tools, calls with their results, and
/// new records that start with the tools of the record on screen.
/// </summary>
[Collection(LanguageCollection.Name)]
public sealed class ToolCallingEditorTests
{
    private const string Forecast = """{"name": "get_forecast", "description": "Hava tahmini", "parameters": {"type": "object", "required": ["location"]}}""";

    public ToolCallingEditorTests() => Localizer.Instance.SetCulture(Localizer.English);

    private static ToolCallingPayload Example => new("Hava asistanısın.", [Forecast],
    [
        new(ChatRole.User, "Kadıköy?"),
        new(ChatRole.Assistant, "", toolCalls: [new ToolCall("get_forecast", """{"location": "Kadıköy"}""", "19 derece")]),
        new(ChatRole.Assistant, "19 derece."),
    ]);

    [Fact]
    public Task Tools_and_calls_are_edited_and_saved_with_the_record() => UiTest.RunAsync(async () =>
    {
        await using var host = new DesktopTestHost();
        await host.CreateDatasetAsync(DatasetType.ToolCalling, [Example]);
        var details = host.Main.Details;
        var row = host.Main.Records.Items[0];
        host.Main.Records.SelectedItem = row;
        await host.WaitForDetailsAsync(row.Id);

        Assert.True(details.ShowToolsEditor && details.ShowChatEditor && !details.ShowWriteReply);
        var tool = Assert.Single(details.Tools);
        Assert.Equal(("get_forecast", "Hava tahmini", false), (tool.Title, tool.Description, tool.IsExpanded));
        Assert.Equal([false, true, true], details.Messages.Select(m => m.ShowCalls));
        Assert.Equal(("get_forecast", "19 derece"), (details.Messages[1].Calls[0].Name, details.Messages[1].Calls[0].Result));

        // A new tool opens with a template and is named by its number until it has a name.
        details.AddToolCommand.Execute(null);
        var added = details.Tools[1];
        Assert.Equal(("Tool 2", true), (added.Title, added.IsExpanded));
        added.Json = """{"name": "get_time"}""";
        Assert.Equal("get_time", added.Title);

        // A second call, then a question and the answer that follows the calls.
        details.Messages[1].AddCallCommand.Execute(null);
        var call = details.Messages[1].Calls[1];
        (call.Name, call.Arguments, call.Result) = ("get_time", "{}", "14:00");
        await details.SaveCommand.ExecuteAsync(null);

        var saved = (ToolCallingPayload)(await host.Get<IRecordQueryService>().GetAsync(row.Id))!.Payload;
        Assert.Equal([Forecast, """{"name": "get_time"}"""], saved.Tools);
        Assert.Equal(new ToolCall("get_time", "{}", "14:00"), saved.Messages[1].ToolCalls[1]);
        Assert.True(ToolCalls.IsWellFormed(saved));

        // After a turn that calls functions comes the answer from their results.
        details.Messages.RemoveAt(2);
        details.AddMessageCommand.Execute(null);
        Assert.Equal(ChatRole.Assistant, details.Messages[^1].Role);
        details.Messages[^1].Text = "Hava 19 derece, saat 14.00.";
        details.Tools[1].RemoveCommand.Execute(null);
        await details.SaveCommand.ExecuteAsync(null);
        saved = (ToolCallingPayload)(await host.Get<IRecordQueryService>().GetAsync(row.Id))!.Payload;
        Assert.Equal([Forecast], saved.Tools);
        Assert.Contains(details.QualityIssues, i => i.Text == "Calls “get_time”, which is not among the tools");
    });

    [Fact]
    public Task A_new_record_starts_with_the_tools_of_the_record_on_screen() => UiTest.RunAsync(async () =>
    {
        await using var host = new DesktopTestHost();
        await host.CreateDatasetAsync(DatasetType.ToolCalling, [Example]);
        var row = host.Main.Records.Items[0];
        host.Main.Records.SelectedItem = row;
        await host.WaitForDetailsAsync(row.Id);

        await host.Main.NewRecordCommand.ExecuteAsync(null);

        var record = (await host.Get<IRecordQueryService>().GetAsync(host.Main.Details.Record!.Id))!;
        Assert.Equal(
            new ToolCallingPayload("Hava asistanısın.", [Forecast], [new(ChatRole.User, ""), new(ChatRole.Assistant, "")]),
            record.Payload);
        Assert.Contains(DatasetType.ToolCalling, Labels.CreatableDatasetTypes);
    });

    [Fact]
    public void Sample_data_is_well_formed_with_no_quality_issues_but_its_tokens()
    {
        var analyzer = new RecordAnalyzer(new Counter(), new PiiScanner());

        Assert.All(SampleData.Create(DatasetType.ToolCalling, 8), record =>
        {
            var payload = Assert.IsType<ToolCallingPayload>(record.Payload);
            Assert.True(ToolCalls.IsWellFormed(payload));
            Assert.Empty(analyzer.Analyze(payload).Issues);
        });
    }

    [Fact]
    public Task The_export_offers_the_formats_that_hold_calls_and_names_what_it_leaves_out() => UiTest.RunAsync(async () =>
    {
        await using var host = new DesktopTestHost();
        var dataset = await host.CreateDatasetAsync(DatasetType.ToolCalling,
            [Example, new ToolCallingPayload(null, [Forecast], [new(ChatRole.User, "Eksik")])]);
        var wizard = new ExportWizardViewModel(host.Get<IExportService>(), host.Files, Localizer.Instance, new ExportTarget(dataset.Id, "Dataset", DatasetType.ToolCalling));

        await wizard.InitializeAsync();
        wizard.IncludeDraft = true;
        await wizard.PendingWork;

        Assert.Equal([ExportFormat.OpenAi, ExportFormat.ShareGpt], wizard.Formats.Select(f => f.Format));
        Assert.Equal(1, wizard.Preview!.Exported);
        Assert.Contains(wizard.SkipItems, s => s.Text == "Left out: turns out of order or a call without its result (see Quality)" && s.CountText == "1");
        Assert.Contains("\"tool_calls\"", Assert.Single(wizard.Samples), StringComparison.Ordinal);
    });

    private sealed class Counter : ITokenCounter
    {
        public int CountTokens(string text) => text.Length;
    }
}
