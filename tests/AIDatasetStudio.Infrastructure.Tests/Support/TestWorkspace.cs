using AIDatasetStudio.Application.Workspaces;
using AIDatasetStudio.Domain.Enums;
using AIDatasetStudio.Infrastructure.Analysis;
using AIDatasetStudio.Infrastructure.Catalog;
using AIDatasetStudio.Infrastructure.Documents;
using AIDatasetStudio.Infrastructure.Export;
using AIDatasetStudio.Infrastructure.Import;
using AIDatasetStudio.Infrastructure.Judgments;
using AIDatasetStudio.Infrastructure.Records;
using AIDatasetStudio.Infrastructure.Tags;
using AIDatasetStudio.Infrastructure.Versions;
using AIDatasetStudio.Infrastructure.Workspaces;

namespace AIDatasetStudio.Infrastructure.Tests.Support;

/// <summary>
/// A real workspace file in a temporary folder with all services wired to it.
/// </summary>
internal sealed class TestWorkspace : IAsyncDisposable
{
    public static readonly DateTimeOffset Now = new(2026, 3, 1, 9, 0, 0, TimeSpan.Zero);

    private TestWorkspace(string directory)
    {
        Directory = directory;
        Clock = new FixedTimeProvider(Now);
        Workspace = new WorkspaceService(Clock);
        Catalog = new CatalogService(Workspace, Clock, Tokenizers);
        Commands = new RecordCommandService(Workspace, Clock);
        Queries = new RecordQueryService(Workspace);
        Import = new ImportService(Commands, Documents);
        Analysis = new RecordAnalysisService(Workspace, Tokenizers, Clock);
        Export = new ExportService(Workspace, Analysis, Clock);
        Versions = new VersionService(Workspace, Analysis, Clock);
        Tags = new TagService(Workspace, Clock);
        Judgments = new JudgmentService(Workspace);
        Semantic = new SemanticIndex(Workspace);
    }

    /// <summary>
    /// Shared: loading a vocabulary takes a moment.
    /// </summary>
    public static TiktokenProvider Tokenizers { get; } = new();

    public DocumentReader Documents { get; } = new();

    public RecordAnalysisService Analysis { get; }

    public ExportService Export { get; }

    public VersionService Versions { get; }

    public TagService Tags { get; }

    public JudgmentService Judgments { get; }

    public SemanticIndex Semantic { get; }

    public string Directory { get; }

    public string FilePath => Path.Combine(Directory, "test" + WorkspaceFile.Extension);

    public FixedTimeProvider Clock { get; }

    public WorkspaceService Workspace { get; }

    public CatalogService Catalog { get; }

    public RecordCommandService Commands { get; }

    public RecordQueryService Queries { get; }

    public ImportService Import { get; }

    public static async Task<TestWorkspace> CreateAsync(CancellationToken cancellationToken)
    {
        var directory = Path.Combine(Path.GetTempPath(), "adstudio-tests", Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(directory);
        var workspace = new TestWorkspace(directory);
        await workspace.Workspace.CreateAsync(workspace.FilePath, "Test", cancellationToken: cancellationToken);
        return workspace;
    }

    public async Task<Guid> CreateDatasetAsync(DatasetType type = DatasetType.Instruction, CancellationToken cancellationToken = default)
    {
        var projectId = await Catalog.CreateProjectAsync("Project", cancellationToken: cancellationToken);
        return await Catalog.CreateDatasetAsync(projectId, "Dataset", type, cancellationToken);
    }

    /// <summary>
    /// Writes a source file next to the workspace and returns its path.
    /// </summary>
    public string WriteFile(string name, string content, System.Text.Encoding? encoding = null)
    {
        var path = Path.Combine(Directory, name);
        File.WriteAllText(path, content, encoding ?? new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return path;
    }

    public async Task<long> ScalarAsync(string sql)
    {
        await using var connection = await Workspace.OpenConnectionAsync(CancellationToken.None);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
    }

    public async ValueTask DisposeAsync()
    {
        await Workspace.DisposeAsync();
        try
        {
            System.IO.Directory.Delete(Directory, recursive: true);
        }
        catch (IOException)
        {
            // Best effort; the OS temp folder is cleaned eventually.
        }
    }
}
