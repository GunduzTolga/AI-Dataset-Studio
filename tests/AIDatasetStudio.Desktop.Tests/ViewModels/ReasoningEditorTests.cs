using AIDatasetStudio.Application.Records;
using AIDatasetStudio.Desktop.Localization;
using AIDatasetStudio.Desktop.Tests.Support;
using AIDatasetStudio.Desktop.ViewModels;
using AIDatasetStudio.Domain.Enums;
using AIDatasetStudio.Domain.Payloads;

namespace AIDatasetStudio.Desktop.Tests.ViewModels;

/// <summary>
/// The reasoning before an answer in the editor (M31, ADR-052): an optional field of an
/// instruction, and a box above an assistant turn.
/// </summary>
[Collection(LanguageCollection.Name)]
public sealed class ReasoningEditorTests
{
    public ReasoningEditorTests() => Localizer.Instance.SetCulture(Localizer.English);

    [Fact]
    public Task An_instruction_gets_reasoning_when_it_is_added_and_loses_it_when_it_is_removed() => UiTest.RunAsync(async () =>
    {
        await using var host = new DesktopTestHost();
        await host.CreateDatasetAsync(DatasetType.Instruction, [new InstructionPayload("2+2?", "", "4")]);
        var details = host.Main.Details;
        var row = host.Main.Records.Items[0];
        host.Main.Records.SelectedItem = row;
        await host.WaitForDetailsAsync(row.Id);

        // Closed until added, between the input and the output.
        var reasoning = details.Fields[2];
        Assert.Equal(("Reasoning", FieldTone.Reasoning, true, false), (reasoning.Label, reasoning.Tone, reasoning.IsOptional, reasoning.IsOpen));

        reasoning.OpenCommand.Execute(null);
        Assert.True(reasoning.IsOpen);
        Assert.False(details.IsDirty);
        reasoning.Text = "İki artı iki dört eder.";
        await details.SaveCommand.ExecuteAsync(null);
        Assert.Equal(new InstructionPayload("2+2?", "", "4", "İki artı iki dört eder."), (await PayloadAsync(host, row.Id)));

        // Shown open next time; removing it clears it.
        details.Fields[2].CloseCommand.Execute(null);
        Assert.False(details.Fields[2].IsOpen);
        await details.SaveCommand.ExecuteAsync(null);
        Assert.Equal(new InstructionPayload("2+2?", "", "4"), await PayloadAsync(host, row.Id));
    });

    [Fact]
    public Task An_assistant_turn_can_have_reasoning_and_a_user_turn_cannot() => UiTest.RunAsync(async () =>
    {
        await using var host = new DesktopTestHost();
        await host.CreateDatasetAsync(DatasetType.Chat,
            [new ChatPayload(null, [new(ChatRole.User, "2+2?"), new(ChatRole.Assistant, "4", "İki artı iki."), new(ChatRole.User, "3+3?"), new(ChatRole.Assistant, "6")])]);
        var details = host.Main.Details;
        var row = host.Main.Records.Items[0];
        host.Main.Records.SelectedItem = row;
        await host.WaitForDetailsAsync(row.Id);

        Assert.Equal([false, true, false, false], details.Messages.Select(m => m.ShowReasoning));
        Assert.Equal([false, false, false, true], details.Messages.Select(m => m.CanAddReasoning));
        Assert.Equal("İki artı iki.", details.Messages[1].Reasoning);

        details.Messages[1].RemoveReasoningCommand.Execute(null);
        details.Messages[3].AddReasoningCommand.Execute(null);
        details.Messages[3].Reasoning = "Üç artı üç.";
        await details.SaveCommand.ExecuteAsync(null);

        Assert.Equal(
            new ChatPayload(null, [new(ChatRole.User, "2+2?"), new(ChatRole.Assistant, "4"), new(ChatRole.User, "3+3?"), new(ChatRole.Assistant, "6", "Üç artı üç.")]),
            await PayloadAsync(host, row.Id));

        // A turn switched to the user's side keeps no reasoning.
        details.Messages[3].SwitchRoleCommand.Execute(null);
        Assert.False(details.Messages[3].ShowReasoning);
        await details.SaveCommand.ExecuteAsync(null);
        Assert.Null(((ChatPayload)(await PayloadAsync(host, row.Id))).Messages[3].Reasoning);
    });

    private static async Task<RecordPayload> PayloadAsync(DesktopTestHost host, Guid id) =>
        (await host.Get<IRecordQueryService>().GetAsync(id))!.Payload;
}
