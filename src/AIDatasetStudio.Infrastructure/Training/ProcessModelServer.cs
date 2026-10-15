using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using AIDatasetStudio.Application.AI;
using AIDatasetStudio.Application.Export;
using AIDatasetStudio.Application.Training;

namespace AIDatasetStudio.Infrastructure.Training;

/// <summary>
/// Serves a trained adapter with <c>llamafactory-cli api</c> on a free port of this computer
/// (M30, ADR-051). The server listens on 127.0.0.1 only and asks for a key made for this run,
/// so neither the network nor other programs can use it; it is ready once its model list
/// answers.
/// </summary>
internal sealed class ProcessModelServer(IAIProviderFactory providers) : IModelServer
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>How often the server is asked whether it answers while the model loads.</summary>
    internal TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(1);

    public TrainedAdapter? FindAdapter(string folder)
    {
        var config = Path.Combine(folder, TrainingConfigs.LlamaFactoryFile);
        try
        {
            return File.Exists(config) && TrainedAdapters.Read(folder, File.ReadAllText(config)) is { } adapter
                && File.Exists(Path.Combine(folder, adapter.AdapterDirectory, TrainedAdapters.AdapterFile))
                    ? adapter
                    : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    public async Task<IServedModel> StartAsync(TrainedAdapter adapter, string toolPath, IProgress<string> log, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(adapter);
        ArgumentNullException.ThrowIfNull(log);
        await File.WriteAllTextAsync(Path.Combine(adapter.Folder, TrainedAdapters.ServeFile), TrainedAdapters.ServeConfig(adapter), Utf8NoBom, cancellationToken).ConfigureAwait(false);

        var port = FreePort();
        var key = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
        var start = new ProcessStartInfo(toolPath)
        {
            WorkingDirectory = adapter.Folder,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        start.ArgumentList.Add("api");
        start.ArgumentList.Add(TrainedAdapters.ServeFile);
        start.Environment["PYTHONUNBUFFERED"] = "1";
        start.Environment["PYTHONIOENCODING"] = "utf-8";
        start.Environment["API_HOST"] = "127.0.0.1";
        start.Environment["API_PORT"] = port.ToString(System.Globalization.CultureInfo.InvariantCulture);
        start.Environment["API_MODEL_NAME"] = adapter.Name;
        start.Environment["API_KEY"] = key;

        var process = new Process { StartInfo = start };
        try
        {
            process.Start();
        }
        catch (Win32Exception ex)
        {
            process.Dispose();
            throw new ModelServerException(TrainingFailure.ToolNotFound, [], $"'{toolPath}' could not be started.", ex);
        }

        var served = new ServedModel(process, adapter, new AIConnection(
            Guid.NewGuid(), adapter.Name, AIProviderKind.OpenAICompatible, $"http://127.0.0.1:{port}/v1", adapter.Name, ApiKey: key, Temperature: 0), log);
        try
        {
            var provider = providers.Create(served.Connection);
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (served.HasExited)
                {
                    // Let the pumps read what it wrote last; the cause is usually there.
                    await Task.WhenAny(served.Drained, Task.Delay(TimeSpan.FromSeconds(5), CancellationToken.None)).ConfigureAwait(false);
                    var lines = served.LastLines;
                    throw new ModelServerException(TrainingOutputReader.Classify(lines), lines, $"The server stopped with exit code {process.ExitCode} before it answered.");
                }

                try
                {
                    await provider.ListModelsAsync(cancellationToken).ConfigureAwait(false);
                    return served;
                }
                catch (AIException)
                {
                    // Not listening yet: the model is still loading.
                }

                await Task.Delay(PollInterval, cancellationToken).ConfigureAwait(false);
            }
        }
        catch
        {
            served.Dispose();
            throw;
        }
    }

    // A port no other program listens on; the server binds it a moment later.
    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private sealed class ServedModel : IServedModel
    {
        private readonly Process _process;
        private readonly TrainingOutputReader _reader = new(keepLines: 40);
        private readonly Lock _gate = new();
        private int _disposed;

        public ServedModel(Process process, TrainedAdapter adapter, AIConnection connection, IProgress<string> log)
        {
            _process = process;
            Adapter = adapter;
            Connection = connection;
            Drained = Task.WhenAll(
                PumpAsync(process.StandardOutput, TrainingOutputStream.StandardOutput, log),
                PumpAsync(process.StandardError, TrainingOutputStream.StandardError, log));
        }

        public TrainedAdapter Adapter { get; }

        public AIConnection Connection { get; }

        public bool HasExited
        {
            get
            {
                try
                {
                    return _process.HasExited;
                }
                catch (InvalidOperationException)
                {
                    return true;
                }
            }
        }

        /// <summary>
        /// Completes once both of the tool's streams have closed.
        /// </summary>
        public Task Drained { get; }

        public IReadOnlyList<string> LastLines
        {
            get
            {
                lock (_gate)
                {
                    _reader.Finish();
                    return _reader.LastLines;
                }
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1)
            {
                return;
            }

            try
            {
                // The tool and the Python processes it started.
                _process.Kill(entireProcessTree: true);
                _process.WaitForExit(TimeSpan.FromSeconds(10));
            }
            catch (InvalidOperationException)
            {
                // It had already exited.
            }

            _process.Dispose();
        }

        // Both streams are read to the end, or a full pipe would stop the server.
        private async Task PumpAsync(StreamReader stream, TrainingOutputStream from, IProgress<string> log)
        {
            var buffer = new char[4096];
            try
            {
                int read;
                while ((read = await stream.ReadAsync(buffer.AsMemory(), CancellationToken.None).ConfigureAwait(false)) > 0)
                {
                    IReadOnlyList<TrainingEvent> events;
                    lock (_gate)
                    {
                        events = _reader.Read(buffer.AsSpan(0, read), from);
                    }

                    foreach (var line in events.OfType<TrainingLogLine>())
                    {
                        log.Report(line.Text);
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
            {
                // The process was stopped and its streams closed.
            }
        }
    }
}
