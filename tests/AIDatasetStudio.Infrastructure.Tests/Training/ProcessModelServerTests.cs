using System.Collections.Concurrent;
using AIDatasetStudio.Application.AI;
using AIDatasetStudio.Application.Export;
using AIDatasetStudio.Application.Training;
using AIDatasetStudio.Infrastructure.Training;

namespace AIDatasetStudio.Infrastructure.Tests.Training;

/// <summary>
/// Serving a trained adapter with LLaMA-Factory (M30): a script stands in for
/// <c>llamafactory-cli</c>, a shell script on Linux and macOS and a batch file on Windows. It
/// writes what it was started with and says it is ready by leaving a file the fake server
/// looks for.
/// </summary>
public sealed class ProcessModelServerTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "adstudio-tests", Guid.NewGuid().ToString("N"), "egitim");
    private readonly Servers _servers;

    public ProcessModelServerTests()
    {
        Directory.CreateDirectory(_folder);
        File.WriteAllText(
            Path.Combine(_folder, TrainingConfigs.LlamaFactoryFile),
            "model_name_or_path: \"Qwen/Qwen2.5-7B-Instruct\"\nfinetuning_type: lora\ntemplate: qwen\noutput_dir: \"saves/destek/lora/sft\"\n");
        _servers = new Servers(Path.Combine(_folder, "ready"));
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void An_adapter_is_found_once_the_training_has_saved_it()
    {
        var server = new ProcessModelServer(_servers);
        Assert.Null(server.FindAdapter(_folder));
        Assert.Null(server.FindAdapter(Path.Combine(_folder, "yok")));

        SaveAdapter();

        var adapter = server.FindAdapter(_folder)!;
        Assert.Equal(("Qwen/Qwen2.5-7B-Instruct", "saves/destek/lora/sft"), (adapter.BaseModel, adapter.AdapterDirectory));
    }

    [Fact]
    public async Task The_adapter_is_served_on_this_computer_with_a_key_of_its_own_until_it_is_disposed()
    {
        SaveAdapter();
        var server = new ProcessModelServer(_servers) { PollInterval = TimeSpan.FromMilliseconds(50) };
        var adapter = server.FindAdapter(_folder)!;
        var log = new ConcurrentQueue<string>();

        var served = await server.StartAsync(adapter, WriteTool(fails: false), new Inline(log.Enqueue), Ct);
        try
        {
            var connection = served.Connection;
            Assert.Matches(@"^http://127\.0\.0\.1:\d+/v1$", connection.BaseUrl);
            Assert.Equal(("Qwen2.5-7B-Instruct + LoRA (egitim)", 32), (connection.Model, connection.ApiKey!.Length));
            Assert.False(served.HasExited);

            // The tool serves what the configuration written next to the training's says.
            Assert.Contains("adapter_name_or_path: \"saves/destek/lora/sft\"\n", await File.ReadAllTextAsync(Path.Combine(_folder, TrainedAdapters.ServeFile), Ct), StringComparison.Ordinal);
            var port = new Uri(connection.BaseUrl).Port;
            Assert.Equal(
                $"env: 127.0.0.1 {port} {connection.Model} {connection.ApiKey}",
                (await File.ReadAllTextAsync(Path.Combine(_folder, "env.txt"), Ct)).Trim());
            Assert.Contains("args: api llamafactory-serve.yaml", log);
            Assert.Contains("Loading checkpoint shards: 100%", log);
        }
        finally
        {
            served.Dispose();
        }

        Assert.True(served.HasExited);
    }

    [Fact]
    public async Task A_server_that_stops_before_it_answers_is_named_from_its_last_lines()
    {
        SaveAdapter();
        var server = new ProcessModelServer(_servers) { PollInterval = TimeSpan.FromMilliseconds(50) };
        var adapter = server.FindAdapter(_folder)!;

        var error = await Assert.ThrowsAsync<ModelServerException>(() => server.StartAsync(adapter, WriteTool(fails: true), new Inline(_ => { }), Ct));

        Assert.Equal(TrainingFailure.OutOfMemory, error.Failure);
        Assert.Contains(error.LastLines, l => l.Contains("CUDA out of memory", StringComparison.Ordinal));

        var missing = await Assert.ThrowsAsync<ModelServerException>(() => server.StartAsync(adapter, Path.Combine(_folder, "yok", "llamafactory-cli"), new Inline(_ => { }), Ct));
        Assert.Equal(TrainingFailure.ToolNotFound, missing.Failure);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path.GetDirectoryName(_folder)!, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private void SaveAdapter()
    {
        var adapter = Path.Combine(_folder, "saves", "destek", "lora", "sft");
        Directory.CreateDirectory(adapter);
        File.WriteAllText(Path.Combine(adapter, TrainedAdapters.AdapterFile), "{}");
    }

    // A stand-in for llamafactory-cli api: writes its arguments and environment, then serves
    // (leaves "ready" and waits) or runs out of memory.
    private string WriteTool(bool fails)
    {
        if (OperatingSystem.IsWindows())
        {
            var path = Path.Combine(_folder, (fails ? "oom" : "serve") + ".cmd");
            File.WriteAllText(path, string.Join("\r\n",
                "@echo off",
                "echo args: %1 %2",
                "echo env: %API_HOST% %API_PORT% %API_MODEL_NAME% %API_KEY%> env.txt",
                fails ? "echo torch.OutOfMemoryError: CUDA out of memory." : "echo Loading checkpoint shards: 100%%",
                fails ? "exit /b 1" : "type nul > ready",
                fails ? "" : "ping -n 60 127.0.0.1 >nul",
                ""));
            return path;
        }

        var script = Path.Combine(_folder, fails ? "oom" : "serve");
        File.WriteAllText(script, string.Join("\n",
            "#!/bin/sh",
            "echo \"args: $1 $2\"",
            "echo \"env: $API_HOST $API_PORT $API_MODEL_NAME $API_KEY\" > env.txt",
            fails ? "echo 'torch.OutOfMemoryError: CUDA out of memory.' >&2" : "echo 'Loading checkpoint shards: 100%'",
            fails ? "exit 1" : "touch ready",
            fails ? "" : "sleep 60",
            ""));
        File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return script;
    }

    private sealed class Inline(Action<string> report) : IProgress<string>
    {
        public void Report(string value) => report(value);
    }

    // A model server answers once the stand-in has left its "ready" file.
    private sealed class Servers(string ready) : IAIProviderFactory
    {
        public IAIProvider Create(AIConnection connection) => new Server(connection, ready);

        private sealed class Server(AIConnection connection, string ready) : IAIProvider
        {
            public AIConnection Connection => connection;

            public Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken cancellationToken = default) =>
                File.Exists(ready)
                    ? Task.FromResult<IReadOnlyList<string>>([connection.Model])
                    : throw new AIException(AIErrorKind.Unreachable, "Nothing listens yet.");

            public IAsyncEnumerable<string> StreamChatAsync(AIChatRequest request, CancellationToken cancellationToken = default) =>
                throw new NotSupportedException();

            public Task<IReadOnlyList<float[]>> EmbedAsync(IReadOnlyList<string> inputs, CancellationToken cancellationToken = default) =>
                throw new NotSupportedException();
        }
    }
}
