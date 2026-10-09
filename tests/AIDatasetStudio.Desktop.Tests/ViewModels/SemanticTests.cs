using AIDatasetStudio.Application.AI;
using AIDatasetStudio.Application.Analysis;
using AIDatasetStudio.Desktop.Localization;
using AIDatasetStudio.Desktop.Tests.Support;
using AIDatasetStudio.Domain.Enums;
using AIDatasetStudio.Domain.Payloads;

namespace AIDatasetStudio.Desktop.Tests.ViewModels;

/// <summary>
/// Questions alike in meaning (M16): the embedding model on the AI connection and the overview's card.
/// </summary>
[Collection(LanguageCollection.Name)]
public sealed class SemanticTests
{
    public SemanticTests() => Localizer.Instance.SetCulture(Localizer.English);

    // The first two mean the same in other words: their words differ, their vectors do not.
    internal static readonly Dictionary<string, float[]> Vectors = new()
    {
        ["Parolamı unuttum, ne yapmalıyım?"] = [1, 0, 0, 0, 0, 0, 0, 0.1f],
        ["Şifremi nasıl sıfırlarım?"] = [1, 0.35f, 0, 0, 0, 0, 0, 0.1f],
        ["Mesai saatleri nedir?"] = [0, 1, 0, 0, 0, 0, 0, 0],
        ["Kargo ücreti ne kadar?"] = [0, 0, 1, 0, 0, 0, 0, 0],
    };

    internal static RecordPayload[] Samples => [.. Vectors.Keys.Select(question => new InstructionPayload(question, "", "Yanıt."))];

    [Fact]
    public Task Comparing_by_meaning_finds_questions_asked_in_other_words() => UiTest.RunAsync(async () =>
    {
        await using var host = new DesktopTestHost();
        await host.CreateDatasetAsync(DatasetType.Instruction, Samples);
        await host.AnalyzeAsync();
        var (overview, records) = (host.Main.Overview, host.Main.Records);
        host.AI.Embedder = text => Vectors[text];

        Assert.True(overview.CanCompareMeaning);
        Assert.Equal(new SemanticState(null, SemanticQuestions.DefaultThreshold, 4, 0), overview.Semantic);
        Assert.StartsWith("Questions are compared by their words.", overview.SemanticStatusText, StringComparison.Ordinal);
        Assert.Equal(("Compare by meaning", true, false), (overview.SemanticRunText, overview.ShowSemanticRun, overview.ShowSemanticOff));
        Assert.DoesNotContain(overview.Issues, i => i.Rule == QualityRule.NearDuplicate);

        // Without a connection or its embedding model, the card says what is missing.
        await overview.RunSemanticCommand.ExecuteAsync(null);
        Assert.Equal("Set up an AI connection with an embedding model first: Ollama or LM Studio on this computer is enough.", overview.SemanticError);
        var connection = host.AddAIConnection();
        await overview.RunSemanticCommand.ExecuteAsync(null);
        Assert.Equal("No embedding model is chosen for Ollama. Choose one in AI Connections (e.g. nomic-embed-text or bge-m3).", overview.SemanticError);
        Assert.Empty(host.AI.Embedded);

        host.Get<IAIConnectionStore>().Save(connection with { EmbeddingModel = "bge-m3" });
        await overview.RunSemanticCommand.ExecuteAsync(null);
        await overview.SemanticRun;

        Assert.Null(overview.SemanticError);
        Assert.Equal([.. Vectors.Keys], Assert.Single(host.AI.Embedded));
        Assert.Equal(new SemanticState("bge-m3", 0.9, 4, 4), overview.Semantic);
        Assert.Equal("All 4 questions are compared by meaning.", overview.SemanticStatusText);
        Assert.Equal("Model: bge-m3", overview.SemanticModelText);
        Assert.Equal((false, true), (overview.ShowSemanticRun, overview.ShowSemanticOff));
        Assert.Equal("4 questions turned into vectors with bge-m3", host.Main.Status.Message);
        Assert.Equal(2, overview.Issues.Single(i => i.Rule == QualityRule.NearDuplicate).Count);
        await DesktopTestHost.WaitUntilAsync(() => records.Items.Select(row => row.Score).SequenceEqual([90, 90, 100, 100]));

        // A stricter threshold parts the two; the list follows without reloading.
        overview.SemanticThreshold = 0.97m;
        await overview.SemanticRun;
        Assert.Equal(0.97, overview.Semantic!.Threshold, 6);
        Assert.DoesNotContain(overview.Issues, i => i.Rule == QualityRule.NearDuplicate);
        await DesktopTestHost.WaitUntilAsync(() => records.Items.All(row => row.Score == 100));
        overview.SemanticThreshold = 0.9m;
        await overview.SemanticRun;
        Assert.Contains(overview.Issues, i => i.Rule == QualityRule.NearDuplicate);

        // Off and on again: the vectors were kept, so the model is not asked again.
        await overview.TurnOffSemanticCommand.ExecuteAsync(null);
        Assert.False(overview.IsSemanticOn);
        Assert.Equal("“Dataset” compares questions by their words only", host.Main.Status.Message);
        Assert.DoesNotContain(overview.Issues, i => i.Rule == QualityRule.NearDuplicate);
        await overview.RunSemanticCommand.ExecuteAsync(null);
        await overview.SemanticRun;
        Assert.Single(host.AI.Embedded);
        Assert.Contains(overview.Issues, i => i.Rule == QualityRule.NearDuplicate);

        // Another model on the connection offers to start over with it.
        host.Get<IAIConnectionStore>().Save(connection with { EmbeddingModel = "nomic-embed-text" });
        Assert.True(overview.IsSemanticModelChanged);
        Assert.Equal("Compare with nomic-embed-text instead", overview.SemanticRunText);
    });

    [Fact]
    public Task Making_vectors_can_be_stopped_and_failures_are_explained() => UiTest.RunAsync(async () =>
    {
        await using var host = new DesktopTestHost();
        await host.CreateDatasetAsync(DatasetType.Instruction, Samples);
        await host.AnalyzeAsync();
        var overview = host.Main.Overview;
        host.AddAIConnection("bge-m3");
        host.AI.Embedder = text => Vectors[text];

        host.AI.Hold = new TaskCompletionSource();
        overview.RunSemanticCommand.Execute(null);
        await DesktopTestHost.WaitUntilAsync(() => overview.IsEmbedding && overview.EmbeddingText is not null);
        Assert.Equal("Turning questions into vectors with bge-m3: 0 of 4…", overview.EmbeddingText);
        Assert.Equal((false, false), (overview.ShowSemanticRun, overview.ShowSemanticOff));
        overview.StopSemanticCommand.Execute(null);
        await overview.SemanticRun;
        Assert.False(overview.IsEmbedding);
        Assert.Equal("Stopped; the 0 vectors made are kept", host.Main.Status.Message);
        Assert.Equal(new SemanticState("bge-m3", 0.9, 4, 0), overview.Semantic);
        Assert.Equal("0 of 4 questions are compared by meaning; new and edited ones by their words until they get a vector.", overview.SemanticStatusText);
        Assert.Equal("Compare 4 more questions", overview.SemanticRunText);
        host.AI.Hold = null;

        host.AI.Failure = new AIException(AIErrorKind.EmbeddingModelNotFound, "missing");
        await overview.RunSemanticCommand.ExecuteAsync(null);
        await overview.SemanticRun;
        Assert.Equal("The server has no embedding model “bge-m3”. Choose another in AI Connections, or download it on the server (e.g. ollama pull bge-m3).", overview.SemanticError);
        overview.DismissSemanticErrorCommand.Execute(null);
        Assert.False(overview.HasSemanticError);
        host.AI.Failure = null;

        await overview.RunSemanticCommand.ExecuteAsync(null);
        await overview.SemanticRun;
        Assert.Equal(4, overview.Semantic!.EmbeddedCount);
    });

    [Fact]
    public Task Preference_pairs_are_not_compared_by_meaning() => UiTest.RunAsync(async () =>
    {
        await using var host = new DesktopTestHost();
        await host.CreateDatasetAsync(DatasetType.Preference, [new PreferencePayload("Soru", "İyi", "Kötü")]);
        await host.AnalyzeAsync();

        Assert.False(host.Main.Overview.CanCompareMeaning);
        Assert.Null(host.Main.Overview.Semantic);
    });

    [Fact]
    public Task The_connection_keeps_its_embedding_model_and_its_test_tries_it() => UiTest.RunAsync(async () =>
    {
        await using var host = new DesktopTestHost();
        host.AddAIConnection();
        var connections = host.Get<Services.AIAssistant>().CreateConnections();
        var item = connections.SelectedConnection!;

        item.EmbeddingModel = " bge-m3 ";
        Assert.Equal("bge-m3", host.Get<IAIConnectionStore>().DefaultConnection!.EmbeddingModel);
        host.AI.Answers.Enqueue(["OK"]);
        await connections.TestCommand.ExecuteAsync(null);
        Assert.False(connections.IsResultError);
        Assert.EndsWith(Environment.NewLine + "bge-m3 turns a text into 64 numbers.", connections.Result, StringComparison.Ordinal);

        item.EmbeddingModel = "";
        Assert.Null(host.Get<IAIConnectionStore>().DefaultConnection!.EmbeddingModel);
        host.AI.Answers.Enqueue(["OK"]);
        await connections.TestCommand.ExecuteAsync(null);
        Assert.DoesNotContain("numbers", connections.Result, StringComparison.Ordinal);
    });
}
