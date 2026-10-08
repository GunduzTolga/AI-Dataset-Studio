using AIDatasetStudio.Application.Analysis;
using AIDatasetStudio.Application.Versions;
using AIDatasetStudio.Desktop.Localization;
using AIDatasetStudio.Desktop.Tests.Support;
using AIDatasetStudio.Domain.Enums;
using AIDatasetStudio.Domain.Payloads;

namespace AIDatasetStudio.Desktop.Tests.ViewModels;

/// <summary>
/// Near-duplicate questions and the comparison of preference answers (M10) over a real workspace.
/// </summary>
[Collection(LanguageCollection.Name)]
public sealed class NearDuplicateFlowTests
{
    public NearDuplicateFlowTests() => Localizer.Instance.SetCulture(Localizer.English);

    private static RecordPayload[] Samples =>
    [
        new InstructionPayload("Kargo ücreti ne kadar?", "", "500 TL üzeri siparişlerde ücretsiz."),
        new InstructionPayload("Mesai saatleri nedir?", "", "09.00 – 18.00."),
        new InstructionPayload("Kargo ücreti ne kadardır?", "", "Ücretsiz kargo sınırı 500 TL."),
        new InstructionPayload("İade süresi kaç gün sürüyor?", "", "14 gün."),
    ];

    [Fact]
    public Task Near_questions_show_in_the_overview_list_and_editor_and_are_archived_but_the_first() => UiTest.RunAsync(async () =>
    {
        await using var host = new DesktopTestHost();
        await host.CreateDatasetAsync(DatasetType.Instruction, Samples);
        await host.AnalyzeAsync();
        var (records, overview, details) = (host.Main.Records, host.Main.Overview, host.Main.Details);

        Assert.Equal([90, 100, 90, 100], records.Items.Select(row => row.Score));
        var near = overview.Issues.Single(i => i.Rule == QualityRule.NearDuplicate);
        Assert.Equal(("Near-duplicate questions", 2L, true, QualityFilter.NearDuplicates), (near.Label, near.Count, near.CanArchive, near.Filter));

        records.SelectedItem = records.Items[2];
        await host.WaitForDetailsAsync(records.Items[2].Id);
        await DesktopTestHost.WaitUntilAsync(() => details.QualityIssues.Count > 0);
        Assert.Equal(["Nearly the same question as #1 (85% or more alike)."], details.QualityIssues.Select(i => i.Text));
        records.SelectedItem = null;

        overview.ShowRecordsCommand.Execute(near);
        await DesktopTestHost.WaitUntilAsync(() => !records.IsLoading && records.Items.Count == 2);
        Assert.Equal([1L, 3L], records.Items.Select(row => row.Ordinal));
        Assert.Equal("Near-duplicate questions", records.SelectedQualityFilter!.Label);

        await overview.ArchiveCommand.ExecuteAsync(near);
        Assert.Equal("Archive 1 records with “Near-duplicate questions”? Edit › Undo brings them back.", host.Dialogs.ConfirmMessages[^1]);
        Assert.Equal([RecordStatus.Draft, RecordStatus.Archived], records.Items.Select(row => row.Status));
        Assert.DoesNotContain(overview.Issues, i => i.Rule == QualityRule.NearDuplicate);
    });

    [Fact]
    public Task An_edit_that_makes_a_question_near_is_found_in_the_background() => UiTest.RunAsync(async () =>
    {
        await using var host = new DesktopTestHost();
        await host.CreateDatasetAsync(DatasetType.Instruction, Samples);
        await host.AnalyzeAsync();
        var (records, overview, details) = (host.Main.Records, host.Main.Overview, host.Main.Details);
        records.SelectedItem = records.Items[1];
        await host.WaitForDetailsAsync(records.Items[1].Id);

        details.Fields[0].Text = "İade süresi kaç gün sürüyor ki?";
        await details.SaveCommand.ExecuteAsync(null);

        // The edited row and the record it now resembles are scored again once the dataset is regrouped.
        await DesktopTestHost.WaitUntilAsync(() => overview.Issues.Any(i => i.Rule == QualityRule.NearDuplicate && i.Count == 4));
        await DesktopTestHost.WaitUntilAsync(() => records.Items.All(row => row.Score == 90));
        await DesktopTestHost.WaitUntilAsync(() => details.QualityIssues.Any(i => i.Rule == QualityRule.NearDuplicate));
        Assert.Equal("Nearly the same question as #4 (85% or more alike).", details.QualityIssues.Single(i => i.Rule == QualityRule.NearDuplicate).Text);
        Assert.Equal(2L, records.SelectedItem!.Ordinal);
    });

    [Fact]
    public Task A_filtered_list_keeps_its_rows_and_selection_when_an_edit_ends_a_near_duplicate() => UiTest.RunAsync(async () =>
    {
        await using var host = new DesktopTestHost();
        await host.CreateDatasetAsync(DatasetType.Instruction, Samples);
        await host.AnalyzeAsync();
        var (records, overview, details) = (host.Main.Records, host.Main.Overview, host.Main.Details);
        records.SelectedQualityFilter = records.QualityFilters.Single(f => f.Filter == QualityFilter.NearDuplicates);
        await DesktopTestHost.WaitUntilAsync(() => !records.IsLoading && records.Items.Count == 2);
        var row = records.Items[1];
        records.SelectedItem = row;
        await host.WaitForDetailsAsync(row.Id);

        details.Fields[0].Text = "Kampanya ne zaman bitiyor?";
        await details.SaveCommand.ExecuteAsync(null);

        await DesktopTestHost.WaitUntilAsync(() => overview.Issues.All(i => i.Rule != QualityRule.NearDuplicate));
        await DesktopTestHost.WaitUntilAsync(() => records.Items.All(r => r.Score == 100));
        Assert.Equal(2, records.Items.Count); // not reloaded: the rows stay until the filter is applied again
        Assert.Same(row, records.SelectedItem);
        Assert.Equal(row.Id, details.Record!.Id);
    });

    [Fact]
    public Task Preference_answers_are_compared_word_by_word_while_typing() => UiTest.RunAsync(async () =>
    {
        await using var host = new DesktopTestHost();
        await host.CreateDatasetAsync(DatasetType.Preference,
        [
            new PreferencePayload("İade süresi nedir?", "Ürünü teslim aldıktan sonra 14 gün içinde iade edebilirsiniz.", "Ürünü 14 gün içinde iade edemezsiniz."),
            new PreferencePayload("Kargo ücreti?", "500 TL üzeri ücretsiz.", "500 TL üzeri ücretsiz."),
        ]);
        var (records, details) = (host.Main.Records, host.Main.Details);
        records.SelectedItem = records.Items[0];
        await host.WaitForDetailsAsync(records.Items[0].Id);
        Assert.True(details.IsPreference);
        Assert.False(details.ShowPreferenceCompare);

        details.TogglePreferenceCompareCommand.Execute(null);

        Assert.True(details.ShowPreferenceCompare);
        Assert.Equal("4 words only in the chosen answer · 1 only in the rejected one", details.PreferenceCompareSummary); // "teslim aldıktan sonra", "edebilirsiniz"
        Assert.Contains(new DiffSegment(DiffKind.Deleted, "edemezsiniz"), details.RejectedSegments);
        Assert.Contains(new DiffSegment(DiffKind.Inserted, "edebilirsiniz"), details.ChosenSegments);
        Assert.DoesNotContain(details.ChosenSegments, s => s.Kind == DiffKind.Deleted);

        // Typing in the chosen answer updates the comparison.
        details.Fields[1].Text = "Ürünü 14 gün içinde iade edebilirsiniz.";
        Assert.Equal("1 words only in the chosen answer · 1 only in the rejected one", details.PreferenceCompareSummary);

        // The masked preview hides it; the next record keeps it on.
        details.TogglePiiPreviewCommand.Execute(null);
        Assert.False(details.ShowPreferenceCompare);
        details.TogglePiiPreviewCommand.Execute(null);
        Assert.True(details.ShowPreferenceCompare);
        records.SelectedItem = records.Items[1];
        await host.WaitForDetailsAsync(records.Items[1].Id);
        Assert.True(details.ShowPreferenceCompare);
        Assert.Equal("The two answers say the same.", details.PreferenceCompareSummary);
    });

    [Fact]
    public Task Instruction_records_have_no_answer_comparison() => UiTest.RunAsync(async () =>
    {
        await using var host = new DesktopTestHost();
        await host.CreateDatasetAsync(DatasetType.Instruction, Samples);
        var details = host.Main.Details;
        host.Main.Records.SelectedItem = host.Main.Records.Items[0];
        await host.WaitForDetailsAsync(host.Main.Records.Items[0].Id);

        Assert.False(details.IsPreference);
        Assert.False(details.TogglePreferenceCompareCommand.CanExecute(null));
    });
}
