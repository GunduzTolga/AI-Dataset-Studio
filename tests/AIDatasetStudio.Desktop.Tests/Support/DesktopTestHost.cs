using AIDatasetStudio.Application.AI;
using AIDatasetStudio.Application.Export;
using AIDatasetStudio.Application.Import;
using AIDatasetStudio.Application.Records;
using AIDatasetStudio.Application.Settings;
using AIDatasetStudio.Application.Training;
using AIDatasetStudio.Application.Workspaces;
using AIDatasetStudio.Desktop.Services;
using AIDatasetStudio.Desktop.ViewModels;
using AIDatasetStudio.Desktop.ViewModels.AI;
using AIDatasetStudio.Desktop.ViewModels.Dialogs;
using AIDatasetStudio.Desktop.ViewModels.Documents;
using AIDatasetStudio.Desktop.ViewModels.Export;
using AIDatasetStudio.Desktop.ViewModels.Import;
using AIDatasetStudio.Desktop.ViewModels.Tags;
using AIDatasetStudio.Desktop.ViewModels.Training;
using AIDatasetStudio.Desktop.ViewModels.Versions;
using AIDatasetStudio.Domain.Enums;
using AIDatasetStudio.Domain.Payloads;
using AIDatasetStudio.Domain.ValueObjects;
using AIDatasetStudio.Infrastructure;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;

namespace AIDatasetStudio.Desktop.Tests.Support;

/// <summary>
/// The real service graph (infrastructure + view models) over temporary files, with scripted dialogs.
/// Use from inside a headless UI session; view models expect the UI dispatcher.
/// </summary>
internal sealed class DesktopTestHost : IAsyncDisposable
{
    private readonly ServiceProvider _services;

    public DesktopTestHost()
    {
        Directory = Path.Combine(Path.GetTempPath(), "adstudio-tests", Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(Directory);
        _services = new ServiceCollection()
            .AddInfrastructure(Path.Combine(Directory, "settings"))
            .AddDesktop(() => null)
            .AddSingleton<IDialogService>(Dialogs)
            .AddSingleton<IFileDialogService>(Files)
            .AddSingleton<IUpdateService>(Updates)
            .AddSingleton<IShellService>(Shell)
            .AddSingleton<IAIProviderFactory>(AI)

            // Never the real Hugging Face.
            .AddSingleton<IHuggingFaceHub>(Hub)

            // Never a real training tool or GPU.
            .AddSingleton<ITrainingRunner>(Trainer)
            .AddSingleton<IGpuMonitor>(Gpu)
            .AddSingleton<IModelServer>(Server)

            // Never the developer's own keychain.
            .AddSingleton<ISecretStore>(Secrets)
            .BuildServiceProvider();
    }

    public string Directory { get; }

    public FakeDialogs Dialogs { get; } = new();

    public FakeFileDialogs Files { get; } = new();

    public FakeUpdates Updates { get; } = new();

    public FakeShell Shell { get; } = new();

    public FakeAIProviders AI { get; } = new();

    public FakeHuggingFaceHub Hub { get; } = new();

    public FakeTrainingRunner Trainer { get; } = new();

    public FakeGpuMonitor Gpu { get; } = new();

    public FakeModelServer Server { get; } = new();

    public FakeSecretStore Secrets { get; } = new();

    /// <summary>
    /// Sets up a local Ollama connection with the model "qwen2.5:7b" as the default.
    /// </summary>
    public AIConnection AddAIConnection(string? embeddingModel = null)
    {
        var connection = new AIConnection(
            Guid.CreateVersion7(), "Ollama", AIProviderKind.Ollama, AIProviders.DefaultUrl(AIProviderKind.Ollama), "qwen2.5:7b", EmbeddingModel: embeddingModel);
        Get<IAIConnectionStore>().Save(connection);
        return connection;
    }

    public MainWindowViewModel Main => Get<MainWindowViewModel>();

    public T Get<T>()
        where T : notnull => _services.GetRequiredService<T>();

    public string PathFor(string name) => Path.Combine(Directory, name + WorkspaceFile.Extension);

    /// <summary>
    /// Creates a workspace through the File › New command and waits until the tree is ready.
    /// </summary>
    public async Task<string> CreateWorkspaceAsync(string name = "Test")
    {
        var path = PathFor(name);
        Files.CreatePath = path;
        await Main.NewWorkspaceCommand.ExecuteAsync(null);
        await WaitUntilAsync(() => Main.Explorer.IsWorkspaceOpen);
        return path;
    }

    /// <summary>
    /// Creates a workspace with one project and one dataset holding <paramref name="count"/>
    /// instruction records ("Soru i" / "üçlü" for every third, else "diğer"), and shows it.
    /// </summary>
    public Task<DatasetNodeViewModel> CreateDatasetWithRecordsAsync(int count, DatasetType type = DatasetType.Instruction) =>
        CreateDatasetAsync(type, [.. Enumerable.Range(1, count).Select(i => new InstructionPayload($"Soru {i}", "", i % 3 == 0 ? "üçlü" : "diğer"))]);

    /// <summary>
    /// Creates a workspace with one project and one dataset holding <paramref name="payloads"/>, and shows it.
    /// </summary>
    public async Task<DatasetNodeViewModel> CreateDatasetAsync(DatasetType type, IReadOnlyList<RecordPayload> payloads)
    {
        await CreateWorkspaceAsync();
        var explorer = Main.Explorer;
        Dialogs.TextAnswers.Enqueue("Project");
        await explorer.NewProjectCommand.ExecuteAsync(null);
        Dialogs.DatasetAnswer = new NewDatasetRequest("Dataset", type);
        await explorer.NewDatasetCommand.ExecuteAsync(null);
        var dataset = explorer.SelectedDataset!;

        if (payloads.Count > 0)
        {
            await Get<IRecordCommandService>().AddRangeAsync(dataset.Id, payloads.Select(payload => new NewRecord(payload, RecordLineage.Manual())));
        }

        await Main.Records.ReloadAsync();
        await WaitUntilAsync(() => Main.Records.Dataset == dataset && !Main.Records.IsLoading);
        return dataset;
    }

    /// <summary>
    /// Runs the background analysis of the shown dataset, as after an import, and waits until the
    /// list and the overview show its results.
    /// </summary>
    public async Task AnalyzeAsync()
    {
        await Main.Overview.Running; // the run started when the dataset was shown
        Main.Overview.StartAnalysis();
        await Main.Overview.Running;
        await WaitUntilAsync(() => !Main.Records.IsLoading && Main.Records.Items.All(row => row.IsAnalyzed) && Main.Overview.Summary is { PendingCount: 0 });
    }

    /// <summary>
    /// Waits until the inspector shows <paramref name="recordId"/>.
    /// </summary>
    public Task WaitForDetailsAsync(Guid recordId) => WaitUntilAsync(() => Main.Details.Record?.Id == recordId);

    /// <summary>
    /// Runs posted UI work until <paramref name="condition"/> holds.
    /// </summary>
    public static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 10_000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (!condition())
        {
            if (Environment.TickCount64 > deadline)
            {
                throw new TimeoutException("Condition was not met in time.");
            }

            Dispatcher.UIThread.RunJobs();
            await Task.Delay(10);
        }

        Dispatcher.UIThread.RunJobs();
    }

    public IUserSettings Settings => Get<IUserSettings>();

    public async ValueTask DisposeAsync()
    {
        // The background analysis must not touch the workspace while it closes.
        await Main.Overview.StopAsync();
        await Get<IWorkspaceService>().CloseAsync();
        await _services.DisposeAsync();
        try
        {
            System.IO.Directory.Delete(Directory, recursive: true);
            System.IO.Directory.Delete(Hub.Folder, recursive: true);
        }
        catch (IOException)
        {
            // Best effort.
        }
    }
}

internal sealed class FakeDialogs : IDialogService
{
    public Queue<string?> TextAnswers { get; } = new();

    public NewDatasetRequest? DatasetAnswer { get; set; }

    public object? ChoiceAnswer { get; set; }

    public bool ConfirmAnswer { get; set; } = true;

    public List<string> ConfirmMessages { get; } = [];

    public List<bool> ConfirmWasDestructive { get; } = [];

    public Task<string?> PromptTextAsync(string title, string message, string initialText) =>
        Task.FromResult(TextAnswers.Count > 0 ? TextAnswers.Dequeue() : null);

    /// <summary>
    /// The text the last multi-line prompt started with.
    /// </summary>
    public string? LastLinesInitialText { get; private set; }

    public Task<string?> PromptLinesAsync(string title, string message, string initialText)
    {
        LastLinesInitialText = initialText;
        return PromptTextAsync(title, message, initialText);
    }

    public Task<NewDatasetRequest?> PromptNewDatasetAsync() => Task.FromResult(DatasetAnswer);

    public Task<T?> ChooseAsync<T>(string title, string message, IReadOnlyList<(T Value, string Label)> options, int selectedIndex)
        where T : struct => Task.FromResult(ChoiceAnswer is T value ? value : (T?)null);

    public Task<bool> ConfirmAsync(string title, string message, string confirmText, bool isDestructive = false)
    {
        ConfirmMessages.Add(message);
        ConfirmWasDestructive.Add(isDestructive);
        return Task.FromResult(ConfirmAnswer);
    }

    /// <summary>
    /// What the user does in the import wizard; by default reads the file and imports it as detected.
    /// </summary>
    public Func<ImportWizardViewModel, Task> ImportWizardScript { get; set; } = async wizard =>
    {
        await wizard.InitializeAsync();
        await wizard.ImportCommand.ExecuteAsync(null);
    };

    public ImportWizardViewModel? LastImportWizard { get; private set; }

    /// <summary>
    /// What the user does in the export wizard; by default shows the preview and exports.
    /// </summary>
    public Func<ExportWizardViewModel, Task> ExportWizardScript { get; set; } = async wizard =>
    {
        await wizard.InitializeAsync();
        await wizard.ExportCommand.ExecuteAsync(null);
    };

    public ExportWizardViewModel? LastExportWizard { get; private set; }

    public async Task<ExportResult?> ShowExportWizardAsync(ExportWizardViewModel wizard)
    {
        LastExportWizard = wizard;
        await ExportWizardScript(wizard);
        return wizard.Result;
    }

    public AboutViewModel? LastAbout { get; private set; }

    public Task ShowAboutAsync(AboutViewModel about)
    {
        LastAbout = about;
        return Task.CompletedTask;
    }

    /// <summary>
    /// What the user enters for a new version; by default the suggested number without a note.
    /// <c>null</c> cancels.
    /// </summary>
    public Func<NewVersionViewModel, bool>? NewVersionScript { get; set; } = _ => true;

    public NewVersionViewModel? LastNewVersion { get; private set; }

    public Task<bool> PromptNewVersionAsync(NewVersionViewModel prompt)
    {
        LastNewVersion = prompt;
        return Task.FromResult(NewVersionScript?.Invoke(prompt) == true && !prompt.HasError);
    }

    /// <summary>
    /// What the user does in the versions window; by default opens it and waits for the comparison.
    /// </summary>
    public Func<VersionsViewModel, Task> VersionsScript { get; set; } = versions => versions.InitializeAsync();

    public VersionsViewModel? LastVersions { get; private set; }

    public async Task ShowVersionsAsync(VersionsViewModel versions)
    {
        LastVersions = versions;
        await VersionsScript(versions);
    }

    /// <summary>
    /// What the user does in Tools › Tags; by default opens it.
    /// </summary>
    public Func<TagManagerViewModel, Task> TagManagerScript { get; set; } = tags => tags.InitializeAsync();

    public TagManagerViewModel? LastTagManager { get; private set; }

    public async Task ShowTagManagerAsync(TagManagerViewModel tags)
    {
        LastTagManager = tags;
        await TagManagerScript(tags);
    }

    /// <summary>
    /// What the user does in Tools › Merge Datasets; by default nothing.
    /// </summary>
    public Func<Desktop.ViewModels.Catalog.DatasetMergeViewModel, Task> MergeScript { get; set; } = _ => Task.CompletedTask;

    public Desktop.ViewModels.Catalog.DatasetMergeViewModel? LastMerge { get; private set; }

    /// <summary>
    /// What the user does in File › Import from Hugging Face; by default nothing.
    /// </summary>
    public Func<HubImportViewModel, Task> HubScript { get; set; } = _ => Task.CompletedTask;

    public Task ShowHubImportAsync(HubImportViewModel hub) => HubScript(hub);

    public async Task ShowDatasetMergeAsync(Desktop.ViewModels.Catalog.DatasetMergeViewModel merge)
    {
        LastMerge = merge;
        await MergeScript(merge);
    }

    /// <summary>
    /// What the user does in Tools › Evaluate a Model; by default nothing.
    /// </summary>
    public Func<ModelEvaluationViewModel, Task> EvaluationScript { get; set; } = _ => Task.CompletedTask;

    public ModelEvaluationViewModel? LastEvaluation { get; private set; }

    public Func<TrainingViewModel, Task> TrainingScript { get; set; } = _ => Task.CompletedTask;

    public TrainingViewModel? LastTraining { get; private set; }

    public async Task ShowTrainingAsync(TrainingViewModel training)
    {
        LastTraining = training;
        await TrainingScript(training);
    }

    public async Task ShowModelEvaluationAsync(ModelEvaluationViewModel evaluation)
    {
        LastEvaluation = evaluation;
        await EvaluationScript(evaluation);
    }

    /// <summary>
    /// What the user does in Tools › AI Connections; by default nothing.
    /// </summary>
    public Func<AIConnectionsViewModel, Task> AIConnectionsScript { get; set; } = _ => Task.CompletedTask;

    public AIConnectionsViewModel? LastAIConnections { get; private set; }

    public async Task ShowAIConnectionsAsync(AIConnectionsViewModel connections)
    {
        LastAIConnections = connections;
        await AIConnectionsScript(connections);
    }

    /// <summary>
    /// What the user does in Q&amp;A extraction; by default opens it and waits for the document.
    /// </summary>
    public Func<QAExtractionViewModel, Task> QAExtractionScript { get; set; } = extraction => extraction.InitializeAsync();

    public QAExtractionViewModel? LastQAExtraction { get; private set; }

    public async Task ShowQAExtractionAsync(QAExtractionViewModel extraction)
    {
        LastQAExtraction = extraction;
        await QAExtractionScript(extraction);
    }

    /// <summary>
    /// What the user does in Generate Variations; by default closes it.
    /// </summary>
    public Func<VariationsViewModel, Task> VariationsScript { get; set; } = _ => Task.CompletedTask;

    public VariationsViewModel? LastVariations { get; private set; }

    public async Task ShowVariationsAsync(VariationsViewModel variations)
    {
        LastVariations = variations;
        await VariationsScript(variations);
    }

    /// <summary>
    /// What the user does in the judge window; by default closes it.
    /// </summary>
    public Func<JudgeViewModel, Task> JudgeScript { get; set; } = _ => Task.CompletedTask;

    public JudgeViewModel? LastJudge { get; private set; }

    public async Task ShowJudgeAsync(JudgeViewModel judge)
    {
        LastJudge = judge;
        await JudgeScript(judge);
    }

    public async Task<ImportResult?> ShowImportWizardAsync(ImportWizardViewModel wizard)
    {
        LastImportWizard = wizard;
        await ImportWizardScript(wizard);
        return wizard.Result;
    }
}

internal sealed class FakeFileDialogs : IFileDialogService
{
    public string? CreatePath { get; set; }

    public string? OpenPath { get; set; }

    public Task<string?> PickWorkspaceToOpenAsync() => Task.FromResult(OpenPath);

    public string? TrainingTool { get; set; }

    public string? TrainingFolder { get; set; }

    public Task<string?> PickTrainingToolAsync() => Task.FromResult(TrainingTool);

    public Task<string?> PickTrainingFolderAsync() => Task.FromResult(TrainingFolder);

    public Task<string?> PickWorkspaceToCreateAsync(string suggestedName) => Task.FromResult(CreatePath);

    public string? ImportPath { get; set; }

    public IReadOnlyList<string> ImportExtensions { get; private set; } = [];

    public Task<string?> PickImportFileAsync(IReadOnlyList<string> extensions)
    {
        ImportExtensions = extensions;
        return Task.FromResult(ImportPath);
    }

    public string? DocumentPath { get; set; }

    public IReadOnlyList<string>? DocumentExtensions { get; private set; }

    public Task<string?> PickDocumentAsync(IReadOnlyList<string> extensions)
    {
        DocumentExtensions = extensions;
        return Task.FromResult(DocumentPath);
    }

    public string? ExportPath { get; set; }

    public string? ExportFolder { get; set; }

    public string? SuggestedExportName { get; private set; }

    public Task<string?> PickExportFileAsync(string suggestedName)
    {
        SuggestedExportName = suggestedName;
        return Task.FromResult(ExportPath);
    }

    public Task<string?> PickExportFolderAsync() => Task.FromResult(ExportFolder);
}

internal sealed class FakeUpdates : IUpdateService
{
    public bool IsSupported { get; set; } = true;

    /// <summary>
    /// What the next check finds; <c>null</c> for "up to date".
    /// </summary>
    public AvailableUpdate? Next { get; set; }

    public Exception? Failure { get; set; }

    public int Checks { get; private set; }

    public AvailableUpdate? Downloaded { get; private set; }

    public AvailableUpdate? Applied { get; private set; }

    public Task<AvailableUpdate?> CheckAsync(CancellationToken cancellationToken = default)
    {
        Checks++;
        return Failure is { } failure ? Task.FromException<AvailableUpdate?>(failure) : Task.FromResult(Next);
    }

    public Task DownloadAsync(AvailableUpdate update, IProgress<int>? progress = null, CancellationToken cancellationToken = default)
    {
        progress?.Report(100);
        Downloaded = update;
        return Task.CompletedTask;
    }

    public void RestartToApply(AvailableUpdate update) => Applied = update;
}

internal sealed class FakeShell : IShellService
{
    public List<string> Opened { get; } = [];

    public Task OpenFolderAsync(string path)
    {
        Opened.Add(path);
        return Task.CompletedTask;
    }

    public Task OpenFileAsync(string path)
    {
        Opened.Add(path);
        return Task.CompletedTask;
    }
}

/// <summary>
/// A Hugging Face Hub with the files given; a download copies the file's content to a local file.
/// </summary>
internal sealed class FakeHuggingFaceHub : IHuggingFaceHub
{
    public Dictionary<string, (HubFile File, string Content)[]> Datasets { get; } = [];

    public string Folder { get; } = Directory.CreateTempSubdirectory("adstudio-hub-").FullName;

    public List<string> Downloads { get; } = [];

    public Task<IReadOnlyList<HubFile>> ListFilesAsync(string datasetId, CancellationToken cancellationToken = default) =>
        Datasets.TryGetValue(datasetId, out var files)
            ? Task.FromResult<IReadOnlyList<HubFile>>([.. files.Select(f => f.File)])
            : Task.FromException<IReadOnlyList<HubFile>>(new HubException(HubErrorKind.NotFound, datasetId));

    public async Task<string> DownloadAsync(string datasetId, HubFile file, IProgress<long>? progress = null, CancellationToken cancellationToken = default)
    {
        var content = Datasets[datasetId].Single(f => f.File == file).Content;
        var path = Path.Combine(Folder, Path.GetFileName(file.Path));
        await File.WriteAllTextAsync(path, content, cancellationToken);
        progress?.Report(content.Length);
        Downloads.Add(datasetId + ":" + file.Path);
        return path;
    }
}

/// <summary>
/// A training tool that reports what the test scripts and ends when the test says.
/// </summary>
internal sealed class FakeTrainingRunner : ITrainingRunner
{
    public string? Tool { get; set; } = "/opt/venv/bin/llamafactory-cli";

    public List<TrainingRequest> Requests { get; } = [];

    /// <summary>What the run reports, in order, before it waits for <see cref="Finish"/>.</summary>
    public List<TrainingEvent> Events { get; } = [];

    /// <summary>How the run ends once finished; a cancelled run ends as stopped.</summary>
    public TrainingResult Result { get; set; } = new(0, Cancelled: false, "/data/saves/lora/sft", Failure: null, []);

    public TaskCompletionSource Finish { get; private set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public TaskCompletionSource Started { get; private set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public string? FindTool(TrainingFramework framework) => Tool;

    public TrainingFramework? DetectFramework(string folder) =>
        File.Exists(Path.Combine(folder, "llamafactory.yaml")) ? TrainingFramework.LlamaFactory
        : File.Exists(Path.Combine(folder, "axolotl.yaml")) ? TrainingFramework.Axolotl
        : null;

    public async Task<TrainingResult> RunAsync(TrainingRequest request, IProgress<TrainingEvent> progress, CancellationToken cancellationToken = default)
    {
        Requests.Add(request);
        foreach (var item in Events)
        {
            progress.Report(item);
        }

        Started.TrySetResult();
        try
        {
            await Finish.Task.WaitAsync(cancellationToken);
            return Result;
        }
        catch (OperationCanceledException)
        {
            return new TrainingResult(-1, Cancelled: true, Result.OutputDirectory, Failure: null, []);
        }
        finally
        {
            (Finish, Started) = (new(TaskCreationOptions.RunContinuationsAsynchronously), new(TaskCreationOptions.RunContinuationsAsynchronously));
        }
    }
}

/// <summary>
/// Serves no model: a served adapter answers through the fake AI providers, at an address of
/// its own. Adapters are found as the real server finds them.
/// </summary>
internal sealed class FakeModelServer : IModelServer
{
    public List<(TrainedAdapter Adapter, string Tool)> Starts { get; } = [];

    /// <summary>The lines the tool writes while the model loads.</summary>
    public List<string> Log { get; } = ["Loading checkpoint shards: 100%"];

    /// <summary>Thrown by the next start instead of serving.</summary>
    public ModelServerException? Failure { get; set; }

    public List<Served> Models { get; } = [];

    public TrainedAdapter? FindAdapter(string folder) =>
        File.Exists(Path.Combine(folder, TrainingConfigs.LlamaFactoryFile))
        && TrainedAdapters.Read(folder, File.ReadAllText(Path.Combine(folder, TrainingConfigs.LlamaFactoryFile))) is { } adapter
        && File.Exists(Path.Combine(folder, adapter.AdapterDirectory, TrainedAdapters.AdapterFile))
            ? adapter
            : null;

    public Task<IServedModel> StartAsync(TrainedAdapter adapter, string toolPath, IProgress<string> log, CancellationToken cancellationToken = default)
    {
        Starts.Add((adapter, toolPath));
        foreach (var line in Log)
        {
            log.Report(line);
        }

        if (Failure is { } failure)
        {
            Failure = null;
            throw failure;
        }

        var served = new Served(adapter, new AIConnection(
            Guid.NewGuid(), adapter.Name, AIProviderKind.OpenAICompatible, $"http://127.0.0.1:{18000 + Models.Count}/v1", adapter.Name, ApiKey: "key", Temperature: 0));
        Models.Add(served);
        return Task.FromResult<IServedModel>(served);
    }

    internal sealed class Served(TrainedAdapter adapter, AIConnection connection) : IServedModel
    {
        public TrainedAdapter Adapter => adapter;

        public AIConnection Connection => connection;

        public bool HasExited { get; set; }

        public bool Disposed { get; private set; }

        public void Dispose() => Disposed = true;
    }
}

internal sealed class FakeGpuMonitor : IGpuMonitor
{
    public GpuMemory? Memory { get; set; }

    public Task<GpuMemory?> ReadAsync(CancellationToken cancellationToken = default) => Task.FromResult(Memory);
}
