using AIDatasetStudio.Application.Records;
using AIDatasetStudio.Desktop.Localization;
using AIDatasetStudio.Desktop.Tests.Support;
using AIDatasetStudio.Domain.Enums;
using AIDatasetStudio.Domain.Payloads;

namespace AIDatasetStudio.Desktop.Tests.ViewModels;

/// <summary>
/// Record › New Record: a record written by hand (M24).
/// </summary>
[Collection(LanguageCollection.Name)]
public sealed class NewRecordTests
{
    public NewRecordTests() => Localizer.Instance.SetCulture(Localizer.English);

    [Fact]
    public Task A_new_record_is_an_empty_draft_at_the_end_shown_in_the_editor_and_undone_in_one_step() => UiTest.RunAsync(async () =>
    {
        await using var host = new DesktopTestHost();
        var dataset = await host.CreateDatasetWithRecordsAsync(2);
        var main = host.Main;
        Assert.True(main.NewRecordCommand.CanExecute(null));

        await main.NewRecordCommand.ExecuteAsync(null);

        Assert.Equal(3, main.Records.Items.Count);
        var row = Assert.Single(main.Records.SelectedRows);
        Assert.Equal(3L, row.Ordinal);
        var record = (await host.Get<IRecordQueryService>().GetAsync(row.Id))!;
        Assert.Equal((new InstructionPayload("", "", ""), RecordStatus.Draft, RecordSourceKind.Manual), (record.Payload, record.Status, record.Lineage.SourceKind));
        Assert.Equal(row.Id, main.Details.Record?.Id);
        Assert.Equal(3, main.Explorer.FindDataset(dataset.Id)!.RecordCount);
        Assert.Equal("Added a new draft record to “Dataset”; write it in the editor", main.Status.Message);

        // Written and saved like any record.
        main.Details.Fields[0].Text = "Kargo ne zaman gelir?";
        main.Details.Fields[3].Text = "1-3 iş günü içinde.";
        await main.Details.SaveCommand.ExecuteAsync(null);
        Assert.Equal("Kargo ne zaman gelir?", main.Records.Items[2].Preview);

        // The edit, then the new record, are undone.
        await main.UndoCommand.ExecuteAsync(null);
        Assert.Equal("_Undo Add #3 (by hand)", main.UndoHeader);
        await main.UndoCommand.ExecuteAsync(null);
        Assert.Equal(2, main.Records.Items.Count);
    });

    [Fact]
    public Task A_new_chat_starts_with_a_turn_to_ask_and_one_to_answer() => UiTest.RunAsync(async () =>
    {
        Localizer.Instance.SetCulture(Localizer.Turkish);
        await using var host = new DesktopTestHost();
        await host.CreateDatasetAsync(DatasetType.Chat, []);

        await host.Main.NewRecordCommand.ExecuteAsync(null);

        Assert.Equal([ChatRole.User, ChatRole.Assistant], host.Main.Details.Messages.Select(m => m.Role));
        Assert.All(host.Main.Details.Messages, m => Assert.Equal("", m.Text));
        Assert.Equal("“Dataset” veri setine yeni bir taslak kayıt eklendi; düzenleyicide yazın", host.Main.Status.Message);
    });
}
