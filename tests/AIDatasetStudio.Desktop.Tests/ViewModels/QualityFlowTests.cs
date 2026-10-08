using System.Globalization;
using AIDatasetStudio.Application.Analysis;
using AIDatasetStudio.Application.Records;
using AIDatasetStudio.Desktop.Localization;
using AIDatasetStudio.Desktop.Tests.Support;
using AIDatasetStudio.Desktop.ViewModels;
using AIDatasetStudio.Domain.Enums;
using AIDatasetStudio.Domain.Payloads;

namespace AIDatasetStudio.Desktop.Tests.ViewModels;

/// <summary>
/// Token counts, quality rules and personal-data masking (M4) over a real workspace.
/// </summary>
[Collection(LanguageCollection.Name)]
public sealed class QualityFlowTests
{
    private const string Masked = "Muhasebe ekibine [EMAIL_1] adresinden ulaşabilirsiniz.";

    public QualityFlowTests() => Localizer.Instance.SetCulture(Localizer.English);

    private static RecordPayload[] Samples =>
    [
        new InstructionPayload("Kargo ne zaman gelir?", "", "Siparişler 1-3 iş günü içinde kargoya verilir."),
        new InstructionPayload("Kargo ne zaman gelir?", "", "Siparişler 1-3 iş günü içinde kargoya verilir."), // copy
        new InstructionPayload("Mesai saatleri nedir?", "", ""),                                                  // empty answer
        new InstructionPayload("Fatura için kime yazayım?", "", "Muhasebe ekibine ali@firma.com adresinden ulaşabilirsiniz."),
        new InstructionPayload("Proje Kartal ne zaman başlıyor?", "", "Ekim ayında başlıyor, ekip hazır durumda."),
    ];

    [Fact]
    public Task Background_analysis_fills_the_list_and_the_overview() => UiTest.RunAsync(async () =>
    {
        await using var host = new DesktopTestHost();
        await host.CreateDatasetAsync(DatasetType.Instruction, Samples);
        await host.AnalyzeAsync();
        var (records, overview) = (host.Main.Records, host.Main.Overview);

        Assert.Equal([90, 90, 60, 90, 100], records.Items.Select(row => row.Score));
        Assert.All(records.Items, row => Assert.True(row.TotalTokens > 0));
        Assert.True(records.Items[2].IsFair);
        Assert.True(records.Items[4].IsGood);
        Assert.True(host.Main.Details.IsEmpty); // the inspector shows the overview

        Assert.Equal("86", overview.ScoreText);
        Assert.True(overview.IsFair);
        Assert.Equal("5 of 5 records analyzed", overview.AnalyzedText);
        Assert.Equal(records.Items.Sum(row => (long)row.TotalTokens!), overview.Summary!.TotalTokens);
        Assert.Equal("< 128", overview.Histogram[0].Label);
        Assert.Equal((5L, 1.0), (overview.Histogram[0].Count, overview.Histogram[0].Fraction));
        Assert.False(overview.HasOverLimit);

        var errors = overview.Issues[0]; // errors first
        Assert.Equal((QualityRule.EmptyContent, "Empty fields", 1L, true), (errors.Rule, errors.Label, errors.Count, errors.CanArchive));
        Assert.True(errors.IsError);
        Assert.Equal(2, overview.Issues.Single(i => i.Rule == QualityRule.Duplicate).Count);
        Assert.Equal(QualityFilter.PersonalData, overview.Issues.Single(i => i.Rule == QualityRule.PersonalData).Filter);
        Assert.False(overview.ShowNoIssues);
    });

    [Fact]
    public Task Issues_can_be_shown_and_archived_with_undo() => UiTest.RunAsync(async () =>
    {
        await using var host = new DesktopTestHost();
        await host.CreateDatasetAsync(DatasetType.Instruction, Samples);
        await host.AnalyzeAsync();
        var (records, overview) = (host.Main.Records, host.Main.Overview);
        var copies = overview.Issues.Single(i => i.Rule == QualityRule.Duplicate);

        overview.ShowRecordsCommand.Execute(copies);
        await DesktopTestHost.WaitUntilAsync(() => !records.IsLoading && records.Items.Count == 2);
        Assert.Equal(QualityFilter.Duplicates, records.SelectedQualityFilter!.Filter);
        Assert.Equal([1L, 2L], records.Items.Select(row => row.Ordinal));
        Assert.Equal(2, records.StatusFilters[0].Count);

        records.SelectedQualityFilter = records.QualityFilters[0];
        await DesktopTestHost.WaitUntilAsync(() => !records.IsLoading && records.Items.Count == 5);

        await overview.ArchiveCommand.ExecuteAsync(copies);
        Assert.Equal("Archive 1 records with “Duplicates”? Edit › Undo brings them back.", host.Dialogs.ConfirmMessages[^1]);
        Assert.Equal(RecordStatus.Archived, records.Items[1].Status); // the first copy stays
        Assert.Equal(RecordStatus.Draft, records.Items[0].Status);
        Assert.Equal("1 records with “Duplicates” archived.", host.Main.Status.Message);
        Assert.DoesNotContain(overview.Issues, i => i.Rule == QualityRule.Duplicate);

        await host.Main.UndoCommand.ExecuteAsync(null);
        await DesktopTestHost.WaitUntilAsync(() => records.Items.Count == 5 && records.Items[1].Status == RecordStatus.Draft
            && overview.Issues.Any(i => i.Rule == QualityRule.Duplicate));
    });

    [Fact]
    public Task The_editor_counts_tokens_and_checks_quality_while_typing() => UiTest.RunAsync(async () =>
    {
        await using var host = new DesktopTestHost();
        await host.CreateDatasetAsync(DatasetType.Instruction, Samples);
        await host.AnalyzeAsync();
        var (records, details) = (host.Main.Records, host.Main.Details);
        await SelectAsync(host, 2);

        Assert.Equal(60, details.QualityScore);
        var issue = Assert.Single(details.QualityIssues);
        Assert.Equal(("Empty field: Output", true), (issue.Text, issue.IsError));
        Assert.Equal(string.Format(CultureInfo.CurrentCulture, "{0:N0} / {1:N0}", details.Tokens!.Value.Total, 4096), details.TokenText);
        Assert.Equal(0, details.Tokens.Value.Output);
        Assert.True(details.IsTokenGood);
        Assert.EndsWith("(o200k_base)", details.TokenDetail);

        details.Fields[2].Text = "Hafta içi 09.00 ile 18.00 arası açığız.";

        Assert.Empty(details.QualityIssues);
        Assert.True(details.HasNoQualityIssues);
        Assert.Equal(100, details.QualityScore);
        Assert.True(details.Tokens.Value.Output > 0);
        Assert.Equal(60, records.Items[2].Score); // the list shows the saved content

        await details.SaveCommand.ExecuteAsync(null);
        await DesktopTestHost.WaitUntilAsync(() => records.Items[2].Score == 100);
        Assert.Equal(details.Tokens.Value.Total, records.Items[2].TotalTokens);
    });

    [Fact]
    public Task Masked_preview_shows_placeholders_and_masking_is_saved_and_undone_like_an_edit() => UiTest.RunAsync(async () =>
    {
        await using var host = new DesktopTestHost();
        await host.CreateDatasetAsync(DatasetType.Instruction, Samples);
        await host.AnalyzeAsync();
        var details = host.Main.Details;
        await SelectAsync(host, 3);
        Assert.Equal("Contains personal or secret data: e-mail address", Assert.Single(details.QualityIssues).Text);
        Assert.False(details.ApplyMaskCommand.CanExecute(null));

        details.TogglePiiPreviewCommand.Execute(null);

        Assert.True(details.IsPiiPreview);
        Assert.False(details.IsEditing);
        Assert.True(details.HasPii);
        Assert.Equal("e-mail address: 1", details.PiiSummary);
        Assert.Equal(["Instruction", "Input", "Output"], details.PiiPreview.Select(block => block.Label));
        Assert.Equal(("(empty)", true), (details.PiiPreview[1].Text, details.PiiPreview[1].IsEmpty));
        Assert.Equal(Masked, details.PiiPreview[2].Text);
        Assert.Contains("ali@firma.com", details.Fields[2].Text); // the editor is untouched

        details.ApplyMaskCommand.Execute(null);

        Assert.False(details.IsPiiPreview);
        Assert.Equal(Masked, details.Fields[2].Text);
        Assert.True(details.IsDirty);
        Assert.Empty(details.QualityIssues);
        Assert.StartsWith("Masked text is in the editor.", host.Main.Status.Message);

        await details.SaveCommand.ExecuteAsync(null);
        var saved = await host.Get<IRecordQueryService>().GetAsync(details.Record!.Id);
        Assert.Equal(Masked, ((InstructionPayload)saved!.Payload).Output);

        await host.Main.UndoCommand.ExecuteAsync(null);
        await DesktopTestHost.WaitUntilAsync(() => details.Fields.Count == 3 && details.Fields[2].Text.Contains("ali@firma.com", StringComparison.Ordinal));
    });

    [Fact]
    public Task Clean_records_have_nothing_to_mask() => UiTest.RunAsync(async () =>
    {
        await using var host = new DesktopTestHost();
        await host.CreateDatasetAsync(DatasetType.Instruction, Samples);
        var details = host.Main.Details;
        await SelectAsync(host, 4);

        details.TogglePiiPreviewCommand.Execute(null);

        Assert.False(details.HasPii);
        Assert.Equal("No personal or secret data found.", details.PiiSummary);
        Assert.False(details.ApplyMaskCommand.CanExecute(null));
        details.TogglePiiPreviewCommand.Execute(null);
        Assert.True(details.IsEditing);
        Assert.Empty(details.PiiPreview);
    });

    [Fact]
    public Task Custom_terms_are_edited_per_workspace_and_flag_records() => UiTest.RunAsync(async () =>
    {
        await using var host = new DesktopTestHost();
        await host.CreateDatasetAsync(DatasetType.Instruction, Samples);
        await host.AnalyzeAsync();
        var (records, details) = (host.Main.Records, host.Main.Details);
        Assert.Equal(100, records.Items[4].Score);

        host.Dialogs.TextAnswers.Enqueue("Kartal\n  \nkartal\r\nAtlas");
        await host.Main.EditCustomTermsCommand.ExecuteAsync(null);

        Assert.Equal(string.Empty, host.Dialogs.LastLinesInitialText);
        Assert.Equal(["Kartal", "Atlas"], details.CustomTerms);
        Assert.Equal("2 custom terms saved; records are analyzed again.", host.Main.Status.Message);
        await host.AnalyzeAsync();
        Assert.Equal(90, records.Items[4].Score);
        await SelectAsync(host, 4);
        Assert.Equal("Contains personal or secret data: custom term", Assert.Single(details.QualityIssues).Text);

        host.Dialogs.TextAnswers.Enqueue(null); // cancelled
        await host.Main.EditCustomTermsCommand.ExecuteAsync(null);
        Assert.Equal($"Kartal{Environment.NewLine}Atlas", host.Dialogs.LastLinesInitialText);
        Assert.Equal(["Kartal", "Atlas"], details.CustomTerms);
    });

    [Fact]
    public Task Token_settings_change_the_limit_and_the_counts() => UiTest.RunAsync(async () =>
    {
        await using var host = new DesktopTestHost();
        var dataset = await host.CreateDatasetAsync(DatasetType.Instruction,
        [
            new InstructionPayload("Bu metni özetle.", string.Join(' ', Enumerable.Repeat("bir", 3000)), "Metin tek bir kelimeden oluşuyor."),
            new InstructionPayload("Kısa soru", "", "Kısa ama yeterli bir cevap."),
        ]);
        await host.AnalyzeAsync();
        var (records, details, overview) = (host.Main.Records, host.Main.Details, host.Main.Overview);
        Assert.InRange(records.Items[0].TotalTokens!.Value, 2049, 4096);
        Assert.False(records.Items[0].IsOverContextLimit);
        Assert.Equal(4096, overview.SelectedContextLength!.Value);
        Assert.Equal(Tokenizers.O200kBase, overview.SelectedTokenizer!.Id);

        await SelectAsync(host, 0);
        Assert.True(details.IsTokenFair); // more than half of the context
        var changed = new List<string?>();
        details.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        overview.SelectedContextLength = overview.ContextOptions.Single(option => option.Value == 2048);

        await DesktopTestHost.WaitUntilAsync(() => records.Items[0].IsOverContextLimit && overview.HasOverLimit);
        Assert.Equal(2048, dataset.ContextLength);
        Assert.StartsWith("Token settings of Dataset: o200k_base", host.Main.Status.Message);
        Assert.Equal(1, overview.Issues.Single(i => i.Rule == QualityRule.OverContextLimit).Count);
        Assert.Equal([false, true], overview.Histogram.Where(bar => bar.Count == 1).Select(bar => bar.IsOverLimit));
        // The open record is checked against the new limit right away.
        Assert.Contains(nameof(RecordDetailsViewModel.IsTokenPoor), changed);
        Assert.True(details.IsTokenPoor);
        Assert.Contains(details.QualityIssues, i => i.Rule == QualityRule.OverContextLimit && i.Text == "Longer than the dataset's context length.");
        changed.Clear();

        overview.SelectedTokenizer = overview.TokenizerOptions.Single(option => option.Id == Tokenizers.Cl100kBase);

        await DesktopTestHost.WaitUntilAsync(() => dataset.Tokenizer == Tokenizers.Cl100kBase);
        Assert.Contains(nameof(RecordDetailsViewModel.Tokens), changed); // counted again with the new tokenizer
        Assert.EndsWith("(cl100k_base)", details.TokenDetail);
        await host.AnalyzeAsync();
        Assert.All(records.Items, row => Assert.True(row.IsAnalyzed));
    });

    private static async Task SelectAsync(DesktopTestHost host, int index)
    {
        var row = host.Main.Records.Items[index];
        host.Main.Records.SelectedItem = row;
        await host.WaitForDetailsAsync(row.Id);
    }
}
