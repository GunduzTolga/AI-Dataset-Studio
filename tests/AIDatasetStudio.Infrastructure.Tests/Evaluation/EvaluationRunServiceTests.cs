using AIDatasetStudio.Application.AI;
using AIDatasetStudio.Infrastructure.Tests.Support;

namespace AIDatasetStudio.Infrastructure.Tests.Evaluation;

public sealed class EvaluationRunServiceTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Runs_are_kept_with_the_dataset_the_latest_first()
    {
        await using var ws = await TestWorkspace.CreateAsync(Ct);
        var datasetId = await ws.CreateDatasetAsync(cancellationToken: Ct);
        var before = new EvaluationSummary(20, 19, 2.6842105263, 0.31, [3, 6, 5, 4, 1]);
        var tuned = new EvaluationSummary(20, 20, 4.1, 0.58, [0, 1, 3, 7, 9]);

        var first = await ws.Evaluations.AddAsync(datasetId, "qwen2.5:7b", "qwen2.5:32b", EvaluationSource.TestPart, 42, before, Ct);
        ws.Clock.Advance(TimeSpan.FromHours(3));
        var second = await ws.Evaluations.AddAsync(datasetId, "destek-asistani:7b", "qwen2.5:32b", EvaluationSource.TestPart, 42, tuned, Ct);
        var runs = await ws.Evaluations.GetRunsAsync(datasetId, Ct);

        Assert.Equal([second.Id, first.Id], runs.Select(r => r.Id));
        var read = runs[1];
        Assert.Equal((first.Model, first.Judge, first.Source, first.Seed, first.RanAt), (read.Model, read.Judge, read.Source, read.Seed, read.RanAt));
        Assert.Equal((20, 19, 2.6842105263, 0.31), (read.Summary.Answered, read.Summary.Scored, read.Summary.AverageScore, read.Summary.AverageF1));
        Assert.Equal([3, 6, 5, 4, 1], read.Summary.Counts);

        // Deleted one by one, and with the dataset.
        await ws.Evaluations.DeleteAsync(second.Id, Ct);
        Assert.Equal([first.Id], (await ws.Evaluations.GetRunsAsync(datasetId, Ct)).Select(r => r.Id));
        await ws.Catalog.DeleteDatasetAsync(datasetId, Ct);
        Assert.Empty(await ws.Evaluations.GetRunsAsync(datasetId, Ct));
    }

    [Fact]
    public async Task A_run_without_scores_and_a_run_of_a_deleted_dataset()
    {
        await using var ws = await TestWorkspace.CreateAsync(Ct);
        var datasetId = await ws.CreateDatasetAsync(cancellationToken: Ct);

        // The grader's verdicts could not be read: no average score.
        await ws.Evaluations.AddAsync(datasetId, "m", "j", EvaluationSource.AllRecords, 7, new EvaluationSummary(3, 0, null, 0.2, [0, 0, 0, 0, 0]), Ct);
        var run = Assert.Single(await ws.Evaluations.GetRunsAsync(datasetId, Ct));
        Assert.Equal((EvaluationSource.AllRecords, 7, (double?)null), (run.Source, run.Seed, run.Summary.AverageScore));

        // The dataset was deleted while the model answered: nothing is kept.
        await ws.Catalog.DeleteDatasetAsync(datasetId, Ct);
        await ws.Evaluations.AddAsync(datasetId, "m", "j", EvaluationSource.TestPart, 42, new EvaluationSummary(1, 1, 5, 1, [0, 0, 0, 0, 1]), Ct);
        Assert.Empty(await ws.Evaluations.GetRunsAsync(datasetId, Ct));
    }
}
