using AIDatasetStudio.Application.Records;
using AIDatasetStudio.Application.Versions;
using AIDatasetStudio.Desktop.Localization;
using AIDatasetStudio.Desktop.Tests.Support;
using AIDatasetStudio.Desktop.ViewModels.Versions;
using AIDatasetStudio.Domain.Enums;
using AIDatasetStudio.Domain.Payloads;
using AIDatasetStudio.Domain.ValueObjects;

namespace AIDatasetStudio.Desktop.Tests.ViewModels;

/// <summary>
/// Dataset versions (M8): freezing, comparing, going back, deleting and exporting.
/// </summary>
[Collection(LanguageCollection.Name)]
public sealed class VersionFlowTests
{
    public VersionFlowTests() => Localizer.Instance.SetCulture(Localizer.English);

    private static RecordPayload[] Samples =>
    [
        new InstructionPayload("Kargo ne zaman gelir?", "", "1-3 iş günü içinde."),
        new InstructionPayload("İade süresi?", "", "14 gün."),
        new InstructionPayload("Mesai saatleri?", "", "09.00 – 18.00."),
    ];

    [Fact]
    public Task A_version_is_created_from_the_file_menu() => UiTest.RunAsync(async () =>
    {
        await using var host = new DesktopTestHost();
        var dataset = await host.CreateDatasetAsync(DatasetType.Instruction, Samples);

        Assert.True(host.Main.CreateVersionCommand.CanExecute(null));
        await host.Main.CreateVersionCommand.ExecuteAsync(null);

        var prompt = host.Dialogs.LastNewVersion!;
        Assert.Equal(("1.0.0", false), (prompt.Number, prompt.HasSuggestions));
        Assert.Equal("1.0.0", dataset.LatestVersion);
        Assert.EndsWith("· v1.0.0", dataset.Subtitle, StringComparison.Ordinal);
        Assert.Equal("Version v1.0.0 created with 3 records", host.Main.Status.Message);

        // The next one suggests the following numbers and refuses a taken one.
        host.Dialogs.NewVersionScript = next =>
        {
            Assert.Equal("1.1.0", next.Number);
            Assert.Equal(["1.0.1", "1.1.0", "2.0.0"], next.Suggestions.Select(s => s.Number));
            next.Number = "v1.0.0";
            Assert.Equal("v1.0.0 already exists.", next.ErrorText);
            Assert.False(next.ConfirmCommand.CanExecute(null));
            next.Number = "1.0";
            Assert.Equal("Use the form 1.2.0 (major.minor.patch).", next.ErrorText);
            next.UseSuggestionCommand.Execute("2.0.0");
            next.Description = "Büyük temizlik";
            return true;
        };
        await host.Main.CreateVersionCommand.ExecuteAsync(null);

        var versions = await host.Get<IVersionService>().ListAsync(dataset.Id, TestContext.Current.CancellationToken);
        Assert.Equal([("2.0.0", "Büyük temizlik"), ("1.0.0", null)], versions.Select(v => (v.Number, v.Description)));
        Assert.Equal("2.0.0", dataset.LatestVersion);
    });

    [Fact]
    public Task The_versions_window_shows_what_changed_since_the_latest_version() => UiTest.RunAsync(async () =>
    {
        await using var host = new DesktopTestHost();
        var dataset = await host.CreateDatasetAsync(DatasetType.Instruction, Samples);
        var ids = host.Main.Records.Items.Select(r => r.Id).ToList();
        await CreateVersionAsync(host, dataset.Id, "1.0.0");
        var commands = host.Get<IRecordCommandService>();
        await commands.UpdatePayloadAsync(ids[1], new InstructionPayload("İade süresi?", "", "30 gün."));
        await commands.SetStatusAsync([ids[2]], RecordStatus.Approved);
        await commands.AddAsync(dataset.Id, new InstructionPayload("Yeni soru", "", "Yeni yanıt"), RecordLineage.Manual());

        await host.Main.VersionsCommand.ExecuteAsync(null);

        var window = host.Dialogs.LastVersions!;
        Assert.Equal(["Working copy", "v1.0.0"], window.Items.Select(i => i.Title));
        Assert.True(window.IsWorkingCopySelected);
        Assert.Equal("v1.0.0 → Working copy", window.ComparisonTitle);
        Assert.Equal("1 added · 1 changed · 1 status changes · 1 unchanged", window.SummaryText);
        Assert.Equal(["All · 3", "Added · 1", "Changed · 1", "Status · 1"], window.KindFilters.Select(f => f.Label));
        Assert.True(window.CreateVersionCommand.CanExecute(null));
        Assert.False(window.RollbackCommand.CanExecute(null));

        // The changed record, field by field, with the edit marked on both sides.
        window.SelectedKindFilter = window.KindFilters.Single(f => f.Kind == DifferenceKind.Modified);
        await DesktopTestHost.WaitUntilAsync(() => window.Fields.Count == 3);
        Assert.Equal("#2 · Changed · Draft → In review", window.SelectedChangeTitle);
        Assert.Equal(["Instruction", "Input", "Output"], window.Fields.Select(f => f.Label));
        var output = window.Fields[2];
        Assert.True(output.ShowBoth);
        Assert.Equal("Changed", output.Badge);
        Assert.Equal(new DiffSegment(DiffKind.Deleted, "14"), Assert.Single(output.OldSegments, s => s.Kind != DiffKind.Equal));
        Assert.Equal(new DiffSegment(DiffKind.Inserted, "30"), Assert.Single(output.NewSegments, s => s.Kind != DiffKind.Equal));
        Assert.Equal("(empty)", Assert.Single(window.Fields[1].SingleSegments).Text);
        Assert.False(window.Fields[0].IsChanged);

        // A version compared with the working copy, the other way round.
        window.SelectedItem = window.Items[1];
        await window.PendingWork;
        Assert.Equal("v1.0.0 → Working copy", window.ComparisonTitle);
        Assert.True(window.RollbackCommand.CanExecute(null));
        Assert.False(window.HasChangedData);
    });

    [Fact]
    public Task Going_back_to_a_version_reloads_the_list_and_can_be_undone() => UiTest.RunAsync(async () =>
    {
        await using var host = new DesktopTestHost();
        var dataset = await host.CreateDatasetAsync(DatasetType.Instruction, Samples);
        var ids = host.Main.Records.Items.Select(r => r.Id).ToList();
        await CreateVersionAsync(host, dataset.Id, "1.0.0");
        var commands = host.Get<IRecordCommandService>();
        await commands.UpdatePayloadAsync(ids[0], new InstructionPayload("Kargo ne zaman gelir?", "", "Bilmiyorum."));
        await commands.DeleteAsync([ids[2]]);
        host.Dialogs.VersionsScript = async window =>
        {
            await window.InitializeAsync();
            window.SelectedItem = window.Items[1];
            await window.PendingWork;
            await window.RollbackCommand.ExecuteAsync(null);
        };

        await host.Main.VersionsCommand.ExecuteAsync(null);

        Assert.Equal(
            "Make the records match v1.0.0? 1 contents and 0 statuses go back, 1 deleted records return and 0 records added since are archived (not deleted). You can take this back with Edit › Undo.",
            host.Dialogs.ConfirmMessages[^1]);
        var window = host.Dialogs.LastVersions!;
        Assert.True(window.HasChangedData);
        Assert.Equal("Went back to v1.0.0: 2 records changed. Edit › Undo takes it back.", window.Notice);
        Assert.True(window.IsIdentical);
        Assert.Equal(3, host.Main.Records.Items.Count);
        Assert.Equal(3, dataset.RecordCount);
        var queries = host.Get<IRecordQueryService>();
        Assert.Equal(Samples[0], (await queries.GetAsync(ids[0]))!.Payload);

        Assert.Equal("_Undo Go Back to v1.0.0", host.Main.UndoHeader);
        await host.Main.UndoCommand.ExecuteAsync(null);

        Assert.Equal("Bilmiyorum.", ((InstructionPayload)(await queries.GetAsync(ids[0]))!.Payload).Output);
        Assert.Null(await queries.GetAsync(ids[2]));
        Assert.Equal(2, host.Main.Records.Items.Count);

        await host.Main.RedoCommand.ExecuteAsync(null);
        Assert.Equal(Samples[0], (await queries.GetAsync(ids[0]))!.Payload);
        Assert.Equal(3, host.Main.Records.Items.Count);
    });

    [Fact]
    public Task Deleting_a_version_asks_first() => UiTest.RunAsync(async () =>
    {
        await using var host = new DesktopTestHost();
        var dataset = await host.CreateDatasetAsync(DatasetType.Instruction, Samples);
        await CreateVersionAsync(host, dataset.Id, "1.0.0");
        await host.Main.Explorer.ReloadAsync();
        dataset = host.Main.Explorer.SelectedDataset!;
        Assert.Equal("1.0.0", dataset.LatestVersion);
        host.Dialogs.VersionsScript = async window =>
        {
            await window.InitializeAsync();
            window.SelectedItem = window.Items[1];
            await window.PendingWork;
            host.Dialogs.ConfirmAnswer = false;
            await window.DeleteVersionCommand.ExecuteAsync(null);
            Assert.Equal(2, window.Items.Count);
            host.Dialogs.ConfirmAnswer = true;
            await window.DeleteVersionCommand.ExecuteAsync(null);
        };

        await host.Main.VersionsCommand.ExecuteAsync(null);

        Assert.True(host.Dialogs.ConfirmWasDestructive[^1]);
        var window = host.Dialogs.LastVersions!;
        Assert.Equal(["Working copy"], window.Items.Select(i => i.Title));
        Assert.True(window.HasNoVersions);
        Assert.Equal("v1.0.0 deleted.", window.Notice);
        Assert.Null(dataset.LatestVersion);
    });

    [Fact]
    public Task A_version_is_exported_as_frozen_from_the_versions_window() => UiTest.RunAsync(async () =>
    {
        await using var host = new DesktopTestHost();
        var dataset = await host.CreateDatasetAsync(DatasetType.Instruction, Samples);
        var ids = host.Main.Records.Items.Select(r => r.Id).ToList();
        await host.Get<IRecordCommandService>().SetStatusAsync(ids, RecordStatus.Approved);
        await CreateVersionAsync(host, dataset.Id, "1.0.0");
        await host.Get<IRecordCommandService>().UpdatePayloadAsync(ids[0], new InstructionPayload("Kargo ne zaman gelir?", "", "Değişti."));
        host.Files.ExportPath = Path.Combine(host.Directory, "v1.jsonl");
        host.Dialogs.ExportWizardScript = async wizard =>
        {
            await wizard.InitializeAsync();
            Assert.True(wizard.HasVersions);
            Assert.Equal("Version v1.0.0", wizard.SelectedSource!.Label[..14]);
            Assert.True(wizard.HasSourceHint);
            Assert.Equal(3, wizard.Preview!.Exported); // the edited record is still approved in the version
            await wizard.ExportCommand.ExecuteAsync(null);
        };
        host.Dialogs.VersionsScript = async window =>
        {
            await window.InitializeAsync();
            window.SelectedItem = window.Items[1];
            await window.PendingWork;
            await window.ExportVersionCommand.ExecuteAsync(null);
        };

        await host.Main.VersionsCommand.ExecuteAsync(null);

        Assert.Equal("Dataset v1.0.0.jsonl", host.Files.SuggestedExportName);
        var lines = await File.ReadAllLinesAsync(host.Files.ExportPath, TestContext.Current.CancellationToken);
        Assert.Equal(3, lines.Length);
        Assert.Contains("1-3 iş günü içinde.", lines[0], StringComparison.Ordinal);

        // The next export starts from the current records and remembers the version in its history.
        host.Dialogs.ExportWizardScript = async wizard =>
        {
            await wizard.InitializeAsync();
            Assert.Null(wizard.SelectedSource!.VersionId);
            Assert.StartsWith("Last export: ", wizard.LastExportText, StringComparison.Ordinal);
            Assert.Contains("· v1.0.0 ·", wizard.LastExportText, StringComparison.Ordinal);
        };
        await host.Main.ExportCommand.ExecuteAsync(null);
    });

    [Fact]
    public Task Without_versions_the_window_invites_to_create_one() => UiTest.RunAsync(async () =>
    {
        await using var host = new DesktopTestHost();
        await host.CreateDatasetAsync(DatasetType.Instruction, Samples);

        await host.Main.VersionsCommand.ExecuteAsync(null);

        var window = host.Dialogs.LastVersions!;
        Assert.True(window.HasNoVersions);
        Assert.False(window.HasComparison);
        Assert.True(window.CreateVersionCommand.CanExecute(null));

        await window.CreateVersionCommand.ExecuteAsync(null);

        Assert.Equal(["Working copy", "v1.0.0"], window.Items.Select(i => i.Title));
        Assert.Equal("v1.0.0", window.SelectedItem!.Title);
        Assert.Equal("v1.0.0 created with 3 records.", window.Notice);
        Assert.True(window.IsIdentical);
        Assert.True(window.HasChangedData);
    });

    private static Task CreateVersionAsync(DesktopTestHost host, Guid datasetId, string number) =>
        host.Get<IVersionService>().CreateAsync(datasetId, number, null);
}
