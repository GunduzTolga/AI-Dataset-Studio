using System.Globalization;
using System.Text.RegularExpressions;
using AIDatasetStudio.Application.AI;
using AIDatasetStudio.Desktop.Localization;
using AIDatasetStudio.Desktop.Tests.Support;
using AIDatasetStudio.Desktop.ViewModels.AI;
using AIDatasetStudio.Domain.Enums;
using AIDatasetStudio.Domain.Payloads;

namespace AIDatasetStudio.Desktop.Tests.ViewModels;

/// <summary>
/// Tools › AI Connections and the overview's AI card (M12, SDD §17).
/// </summary>
[Collection(LanguageCollection.Name)]
public sealed class AIConnectionsTests
{
    public AIConnectionsTests() => Localizer.Instance.SetCulture(Localizer.English);

    [Fact]
    public Task Adding_a_server_fills_in_its_address_and_first_model_and_makes_it_the_default() => UiTest.RunAsync(async () =>
    {
        await using var host = new DesktopTestHost();
        var store = host.Get<IAIConnectionStore>();
        await host.Main.ManageAIConnectionsCommand.ExecuteAsync(null);
        var connections = host.Dialogs.LastAIConnections!;
        Assert.True(connections.IsEmpty);
        Assert.False(connections.HasSelection);

        await connections.AddCommand.ExecuteAsync(connections.ProviderOptions.Single(o => o.Kind == AIProviderKind.Ollama));

        var item = Assert.Single(connections.Connections);
        Assert.Same(item, connections.SelectedConnection);
        Assert.Equal(("Ollama", "http://localhost:11434/v1", "qwen2.5:7b"), (item.Name, item.BaseUrl, item.Model));
        Assert.Equal(["qwen2.5:7b", "llama3.2"], item.Models);
        Assert.True(item.IsDefault);
        Assert.Equal("Ollama offers 2 models.", connections.Result);
        Assert.Equal(item.ToConnection(), store.DefaultConnection);

        // Another kind of server: its address and name follow, unless the user typed their own.
        item.SelectedProvider = connections.ProviderOptions.Single(o => o.Kind == AIProviderKind.LMStudio);
        Assert.Equal(("LM Studio", "http://localhost:1234/v1"), (item.Name, item.BaseUrl));
        item.BaseUrl = "http://gpu-sunucu:1234/v1";
        item.Name = "Ofis GPU";
        item.SelectedProvider = connections.ProviderOptions.Single(o => o.Kind == AIProviderKind.Vllm);
        Assert.Equal(("Ofis GPU", "http://gpu-sunucu:1234/v1"), (item.Name, item.BaseUrl));
        Assert.Equal("qwen2.5:7b · Ofis GPU", item.Title);
        Assert.Equal(AIProviderKind.Vllm, store.DefaultConnection!.Kind);
    });

    [Fact]
    public Task Testing_reports_the_answer_or_why_it_failed() => UiTest.RunAsync(async () =>
    {
        await using var host = new DesktopTestHost();
        host.AddAIConnection();
        var connections = host.Get<Services.AIAssistant>().CreateConnections();
        host.AI.Answers.Enqueue(["<think>Kısa yanıt.</think>", "OK"]);

        await connections.TestCommand.ExecuteAsync(null);

        Assert.False(connections.IsResultError);
        // The seconds follow the regional number format, like every number in the app.
        var separator = Regex.Escape(CultureInfo.CurrentCulture.NumberFormat.NumberDecimalSeparator);
        Assert.Matches($@"^Works: qwen2\.5:7b answered in \d+{separator}\d s — “OK”$", connections.Result);
        var (_, request) = Assert.Single(host.AI.Requests);
        Assert.Equal((0.0, 16), (request.Temperature, request.MaxTokens));

        host.AI.Failure = new AIException(AIErrorKind.Unreachable, "down");
        await connections.TestCommand.ExecuteAsync(null);
        Assert.True(connections.IsResultError);
        Assert.Equal("Nothing answers at http://localhost:11434/v1. Is Ollama running?", connections.Result);

        host.AI.Failure = new AIException(AIErrorKind.ModelNotFound, "missing");
        await connections.TestCommand.ExecuteAsync(null);
        Assert.Equal("The server has no model “qwen2.5:7b”. Choose another in AI Connections, or download it on the server (e.g. ollama pull qwen2.5:7b).", connections.Result);
    });

    [Fact]
    public Task A_server_outside_the_network_needs_consent() => UiTest.RunAsync(async () =>
    {
        await using var host = new DesktopTestHost();
        host.AddAIConnection();
        var connections = host.Get<Services.AIAssistant>().CreateConnections();
        var item = connections.SelectedConnection!;

        item.BaseUrl = "https://llm.example.com/v1";

        Assert.True(item.IsRemote);
        Assert.True(item.NeedsConsent);
        await connections.TestCommand.ExecuteAsync(null);
        Assert.Equal("This server is outside your computer. Allow sending records to it in AI Connections first.", connections.Result);
        Assert.False(host.Get<IAIConnectionStore>().DefaultConnection!.AllowRemote);

        item.AllowRemote = true;
        Assert.False(item.NeedsConsent);
        await connections.TestCommand.ExecuteAsync(null);
        Assert.False(connections.IsResultError);
        Assert.True(host.Get<IAIConnectionStore>().DefaultConnection!.AllowRemote);

        // The consent is for that server only: another path on it keeps it, another host does not.
        item.BaseUrl = "https://llm.example.com/api/v1";
        Assert.True(host.Get<IAIConnectionStore>().DefaultConnection!.AllowRemote);
        item.BaseUrl = "https://other.example.org/v1";
        Assert.True(item.NeedsConsent);
        Assert.False(host.Get<IAIConnectionStore>().DefaultConnection!.AllowRemote);
        item.AllowRemote = true;
        item.BaseUrl = "http://localhost:11434/v1";
        Assert.False(item.AllowRemote);
        Assert.False(host.Get<IAIConnectionStore>().DefaultConnection!.AllowRemote);
        item.BaseUrl = "localhost:11434";
        Assert.True(item.IsUrlInvalid);
    });

    [Fact]
    public Task Connections_can_be_made_default_and_deleted() => UiTest.RunAsync(async () =>
    {
        await using var host = new DesktopTestHost();
        var store = host.Get<IAIConnectionStore>();
        var first = host.AddAIConnection();
        var connections = host.Get<Services.AIAssistant>().CreateConnections();
        await connections.AddCommand.ExecuteAsync(connections.ProviderOptions.Single(o => o.Kind == AIProviderKind.LMStudio));
        var second = connections.SelectedConnection!;
        Assert.False(second.IsDefault);

        connections.MakeDefaultCommand.Execute(null);
        Assert.Equal(second.Id, store.DefaultConnection!.Id);
        Assert.Equal([false, true], connections.Connections.Select(c => c.IsDefault));

        connections.DeleteCommand.Execute(null);
        Assert.Equal(first.Id, store.DefaultConnection!.Id);
        Assert.Equal("Deleted qwen2.5:7b · LM Studio.", connections.Result);
        Assert.True(Assert.Single(connections.Connections).IsDefault);
    });

    [Fact]
    public Task The_overview_shows_the_model_and_keeps_the_dataset_instructions() => UiTest.RunAsync(async () =>
    {
        await using var host = new DesktopTestHost();
        var dataset = await host.CreateDatasetAsync(DatasetType.Instruction, [new InstructionPayload("Soru", "", "")]);
        var overview = host.Main.Overview;
        Assert.Equal(("No AI connection", false), (overview.AIConnectionText, overview.HasAIConnection));

        host.AddAIConnection();
        Assert.Equal(("qwen2.5:7b · Ollama", true), (overview.AIConnectionText, overview.HasAIConnection));

        overview.OpenAIConnectionsCommand.Execute(null);
        await DesktopTestHost.WaitUntilAsync(() => host.Dialogs.LastAIConnections is not null);

        overview.AIGuidance = "Kısa ve resmî yanıtlar.";
        await overview.GuidanceSaving;
        Assert.Equal("Kısa ve resmî yanıtlar.", dataset.AIGuidance);
        Assert.Equal("Saved the AI instructions of “Dataset”", host.Main.Status.Message);

        // Typing then switching datasets saves at once.
        overview.AIGuidance = "Türkçe yanıtla.";
        await overview.ShowAsync(null);
        await host.Main.Explorer.ReloadAsync();
        Assert.Equal("Türkçe yanıtla.", host.Main.Explorer.Projects[0].Datasets[0].AIGuidance);
    });
}
