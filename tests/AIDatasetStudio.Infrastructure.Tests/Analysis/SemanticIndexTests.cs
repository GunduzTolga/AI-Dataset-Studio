using AIDatasetStudio.Application.Analysis;
using AIDatasetStudio.Application.Common;
using AIDatasetStudio.Application.Records;
using AIDatasetStudio.Domain.Enums;
using AIDatasetStudio.Domain.Payloads;
using AIDatasetStudio.Domain.ValueObjects;
using AIDatasetStudio.Infrastructure.Tests.Support;

namespace AIDatasetStudio.Infrastructure.Tests.Analysis;

/// <summary>
/// Questions alike in meaning (M16): the dataset's choice, its vectors, and their pairs in the
/// near-duplicate groups.
/// </summary>
public sealed class SemanticIndexTests
{
    private const string Model = "nomic-embed-text";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // The first two mean the same in other words: MinHash cannot tell, the vectors can.
    private static readonly (string Question, float[] Vector)[] Support =
    [
        ("Parolamı unuttum, ne yapmalıyım?", [1, 0, 0, 0, 0, 0, 0, 0.1f]),
        ("Şifremi nasıl sıfırlarım?", [1, 0.35f, 0, 0, 0, 0, 0, 0.1f]), // cosine 0.94 with the first
        ("Mesai saatleri nedir?", [0, 1, 0, 0, 0, 0, 0, 0]),
        ("Kargo ücreti ne kadar?", [0, 0, 1, 0, 0, 0, 0, 0]),
    ];

    [Fact]
    public async Task Questions_alike_in_meaning_join_the_near_duplicate_groups()
    {
        await using var ws = await TestWorkspace.CreateAsync(Ct);
        var datasetId = await ws.CreateDatasetAsync(cancellationToken: Ct);
        var ids = new List<Guid>();
        foreach (var (question, _) in Support)
        {
            ids.Add(await ws.Commands.AddAsync(datasetId, new InstructionPayload(question, "", "Yanıt."), RecordLineage.Manual(), Ct));
        }

        await ws.Analysis.AnalyzeAsync(datasetId, cancellationToken: Ct);
        Assert.Null(await NearDetailAsync(ws, ids[1]));
        Assert.Equal(new SemanticState(null, SemanticQuestions.DefaultThreshold, 4, 0), await ws.Semantic.GetStateAsync(datasetId, Ct));
        Assert.Empty(await ws.Semantic.FindPendingAsync(datasetId, 10, Ct));

        // Turned on, every question waits for its vector, first records first.
        await ws.Semantic.SetAsync(datasetId, $" {Model} ", 0.9, Ct);
        var pending = await ws.Semantic.FindPendingAsync(datasetId, 10, Ct);
        Assert.Equal(Support.Select(s => s.Question), pending.Select(p => p.Text));
        Assert.Equal(2, (await ws.Semantic.FindPendingAsync(datasetId, 2, Ct)).Count);

        Assert.Equal(4, await ws.Semantic.SaveAsync(datasetId, Model, [.. pending.Select((p, i) => new QuestionEmbedding(p.QuestionKey, Support[i].Vector))], Ct));
        Assert.Equal(new SemanticState(Model, 0.9, 4, 4), await ws.Semantic.GetStateAsync(datasetId, Ct));
        Assert.Empty(await ws.Semantic.FindPendingAsync(datasetId, 10, Ct));

        Assert.True(await ws.Analysis.GroupNearDuplicatesAsync(datasetId, Ct));
        Assert.Equal("2", await NearDetailAsync(ws, ids[0]));
        Assert.Equal("1", await NearDetailAsync(ws, ids[1]));
        Assert.Null(await NearDetailAsync(ws, ids[2]));
        Assert.False(await ws.Analysis.GroupNearDuplicatesAsync(datasetId, Ct));
        var near = await ws.Queries.GetPageAsync(datasetId, new RecordFilter(Quality: QualityFilter.NearDuplicates), null, 10, Ct);
        Assert.Equal([ids[0], ids[1]], near.Items.Select(r => r.Id));
        Assert.Equal([ids[1]], await ws.Analysis.FindAsync(datasetId, QualityRule.NearDuplicate, Ct));

        // A stricter threshold parts them; so does turning the comparison off, which keeps the vectors.
        await ws.Semantic.SetAsync(datasetId, Model, 0.97, Ct);
        Assert.True(await ws.Analysis.GroupNearDuplicatesAsync(datasetId, Ct));
        Assert.Null(await NearDetailAsync(ws, ids[1]));
        await ws.Semantic.SetAsync(datasetId, Model, 0.5, Ct);
        Assert.Equal(SemanticQuestions.MinThreshold, (await ws.Semantic.GetStateAsync(datasetId, Ct)).Threshold);
        Assert.True(await ws.Analysis.GroupNearDuplicatesAsync(datasetId, Ct));
        Assert.Equal("1", await NearDetailAsync(ws, ids[1]));

        await ws.Semantic.SetAsync(datasetId, null, 0.9, Ct);
        Assert.True(await ws.Analysis.GroupNearDuplicatesAsync(datasetId, Ct));
        Assert.Null(await NearDetailAsync(ws, ids[1]));
        Assert.Equal(new SemanticState(null, 0.9, 4, 0), await ws.Semantic.GetStateAsync(datasetId, Ct));
        Assert.Equal(0, await ws.Semantic.SaveAsync(datasetId, Model, [new QuestionEmbedding(pending[0].QuestionKey, [1, 2])], Ct));
        await ws.Semantic.SetAsync(datasetId, Model, 0.9, Ct);
        Assert.Equal(4, (await ws.Semantic.GetStateAsync(datasetId, Ct)).EmbeddedCount);
        Assert.True(await ws.Analysis.GroupNearDuplicatesAsync(datasetId, Ct));
        Assert.Equal("1", await NearDetailAsync(ws, ids[1]));

        // Another model's vectors are not comparable: they go, and late ones of the old model are not kept.
        await ws.Semantic.SetAsync(datasetId, "bge-m3", 0.9, Ct);
        Assert.Equal(new SemanticState("bge-m3", 0.9, 4, 0), await ws.Semantic.GetStateAsync(datasetId, Ct));
        Assert.Equal(0, await ws.Semantic.SaveAsync(datasetId, Model, [new QuestionEmbedding(pending[0].QuestionKey, [1, 2])], Ct));
        Assert.True(await ws.Analysis.GroupNearDuplicatesAsync(datasetId, Ct));
        Assert.Null(await NearDetailAsync(ws, ids[1]));
    }

    [Fact]
    public async Task One_vector_serves_a_question_asked_twice_and_edits_ask_for_new_ones()
    {
        await using var ws = await TestWorkspace.CreateAsync(Ct);
        var datasetId = await ws.CreateDatasetAsync(cancellationToken: Ct);
        var first = await ws.Commands.AddAsync(datasetId, new InstructionPayload("Parolamı unuttum, ne yapmalıyım?", "", "Yanıt A."), RecordLineage.Manual(), Ct);
        var copy = await ws.Commands.AddAsync(datasetId, new InstructionPayload("parolamı unuttum ne yapmalıyım", "", "Yanıt B."), RecordLineage.Manual(), Ct);
        var other = await ws.Commands.AddAsync(datasetId, new InstructionPayload("Şifremi nasıl sıfırlarım?", "", "Yanıt C."), RecordLineage.Manual(), Ct);
        var shipping = await ws.Commands.AddAsync(datasetId, new InstructionPayload("Kargo ücreti ne kadar?", "", "49,90 TL."), RecordLineage.Manual(), Ct);
        await ws.Analysis.AnalyzeAsync(datasetId, cancellationToken: Ct);
        await ws.Semantic.SetAsync(datasetId, Model, 0.9, Ct);

        var pending = await ws.Semantic.FindPendingAsync(datasetId, 10, Ct);
        Assert.Equal(["Parolamı unuttum, ne yapmalıyım?", "Şifremi nasıl sıfırlarım?", "Kargo ücreti ne kadar?"], pending.Select(p => p.Text));
        Assert.Equal(3, (await ws.Semantic.GetStateAsync(datasetId, Ct)).QuestionCount);
        await ws.Semantic.SaveAsync(datasetId, Model, [new(pending[0].QuestionKey, Support[0].Vector), new(pending[1].QuestionKey, Support[1].Vector), new(pending[2].QuestionKey, [0, 0, 0])], Ct);
        Assert.Equal(1, (await ws.Semantic.GetStateAsync(datasetId, Ct)).PendingCount); // a vector of zeros is not kept

        await ws.Analysis.GroupNearDuplicatesAsync(datasetId, Ct);
        Assert.Equal("3", await NearDetailAsync(ws, first));
        Assert.Equal("3", await NearDetailAsync(ws, copy));
        Assert.Equal("1", await NearDetailAsync(ws, other));
        Assert.Null(await NearDetailAsync(ws, shipping));

        // A new question needs its vector; until it has one, it groups by its words only.
        await ws.Commands.UpdatePayloadAsync(other, new InstructionPayload("Şifremi nasıl değiştiririm?", "", "Yanıt C."), Ct);
        await ws.Analysis.AnalyzeAsync(datasetId, cancellationToken: Ct);
        Assert.Null(await NearDetailAsync(ws, other));
        Assert.Equal(["Şifremi nasıl değiştiririm?", "Kargo ücreti ne kadar?"], (await ws.Semantic.FindPendingAsync(datasetId, 10, Ct)).Select(p => p.Text));

        // The old question's vector goes when the comparison is set again (as each run does).
        Assert.Equal(new SemanticState(Model, 0.9, 3, 1), await ws.Semantic.GetStateAsync(datasetId, Ct));
        await ws.Semantic.SetAsync(datasetId, Model, 0.9, Ct);
        await using var connection = await ws.Workspace.OpenConnectionAsync(Ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM QuestionEmbeddings;";
        Assert.Equal(1L, await command.ExecuteScalarAsync(Ct));

        await Assert.ThrowsAsync<NotFoundException>(() => ws.Semantic.SetAsync(Guid.NewGuid(), Model, 0.9, Ct));
    }

    [Fact]
    public async Task Variations_alike_in_meaning_stay_a_family()
    {
        await using var ws = await TestWorkspace.CreateAsync(Ct);
        var datasetId = await ws.CreateDatasetAsync(cancellationToken: Ct);
        var root = await ws.Commands.AddAsync(datasetId, new InstructionPayload(Support[0].Question, "", "Yanıt."), RecordLineage.Manual(), Ct);
        var source = (await ws.Queries.GetAsync(root, Ct))!;
        var variation = Assert.Single(await ws.Commands.AddManyAsync(
            datasetId,
            [new NewRecord(new InstructionPayload(Support[1].Question, "", "Yanıt."), new RecordLineage(RecordSourceKind.Synthetic, "#1"), VariantOf.For(source))],
            Ct));
        await ws.Analysis.AnalyzeAsync(datasetId, cancellationToken: Ct);
        await ws.Semantic.SetAsync(datasetId, Model, 0.9, Ct);
        var pending = await ws.Semantic.FindPendingAsync(datasetId, 10, Ct);
        await ws.Semantic.SaveAsync(datasetId, Model, [new(pending[0].QuestionKey, Support[0].Vector), new(pending[1].QuestionKey, Support[1].Vector)], Ct);

        await ws.Analysis.GroupNearDuplicatesAsync(datasetId, Ct);

        Assert.Null(await NearDetailAsync(ws, root));
        Assert.Null(await NearDetailAsync(ws, variation));
    }

    private static async Task<string?> NearDetailAsync(TestWorkspace ws, Guid recordId) =>
        (await ws.Analysis.GetRecordQualityAsync(recordId, Ct))!.Issues.SingleOrDefault(i => i.Rule == QualityRule.NearDuplicate)?.Detail;
}
