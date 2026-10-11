using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using AIDatasetStudio.Application.Training;

namespace AIDatasetStudio.Infrastructure.Training;

/// <summary>
/// Reads the first GPU's memory use from <c>nvidia-smi</c> or <c>rocm-smi</c>, whichever answers
/// (M28), and on Windows from its own GPU counters when nvidia-smi does not (M29). The tools come
/// with the vendor's driver; without any, there is nothing to show.
/// </summary>
internal sealed class GpuMonitor : IGpuMonitor
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);
    private bool _nvidiaMissing;
    private bool _rocmMissing;

    public async Task<GpuMemory?> ReadAsync(CancellationToken cancellationToken = default)
    {
        if (!_nvidiaMissing)
        {
            var output = await RunAsync("nvidia-smi", ["--query-gpu=name,memory.used,memory.total", "--format=csv,noheader,nounits"], cancellationToken).ConfigureAwait(false);
            _nvidiaMissing = output is null;
            if (ParseNvidia(output) is { } nvidia)
            {
                return nvidia;
            }
        }

        // AMD's driver on Windows has no rocm-smi; Windows' own counters cover every card.
        if (OperatingSystem.IsWindows())
        {
            return await Task.Run(WindowsGpuMemory.Read, cancellationToken).ConfigureAwait(false);
        }

        if (!_rocmMissing)
        {
            var output = await RunAsync("rocm-smi", ["--showproductname", "--showmeminfo", "vram", "--csv"], cancellationToken).ConfigureAwait(false);
            _rocmMissing = output is null;
            return ParseRocm(output);
        }

        return null;
    }

    /// <summary>
    /// "NVIDIA GeForce RTX 4050 Laptop GPU, 1234, 6141": name, used and total MiB.
    /// </summary>
    internal static GpuMemory? ParseNvidia(string? output)
    {
        var line = output?.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
        var parts = line?.Split(',', StringSplitOptions.TrimEntries);
        return parts is { Length: 3 } && long.TryParse(parts[1], CultureInfo.InvariantCulture, out var used) && long.TryParse(parts[2], CultureInfo.InvariantCulture, out var total)
            ? new GpuMemory(parts[0], used, total)
            : null;
    }

    /// <summary>
    /// rocm-smi's CSV: a header naming the columns, then a line per card; memory in bytes.
    /// </summary>
    internal static GpuMemory? ParseRocm(string? output)
    {
        var lines = output?.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(l => l.Contains(',', StringComparison.Ordinal)).ToList();
        if (lines is not { Count: >= 2 })
        {
            return null;
        }

        var header = lines[0].Split(',', StringSplitOptions.TrimEntries);
        var values = lines[1].Split(',', StringSplitOptions.TrimEntries);
        int Column(string name) => Array.FindIndex(header, h => h.Contains(name, StringComparison.OrdinalIgnoreCase));
        long? Bytes(int index) => index >= 0 && index < values.Length && long.TryParse(values[index], CultureInfo.InvariantCulture, out var value) ? value : null;
        var total = Bytes(Column("VRAM Total Memory"));
        var used = Bytes(Column("VRAM Total Used Memory"));
        if (total is null || used is null)
        {
            return null;
        }

        var nameColumn = new[] { "Card Series", "Card SKU", "Card model" }.Select(Column).FirstOrDefault(i => i >= 0 && i < values.Length, -1);
        return new GpuMemory(nameColumn >= 0 ? values[nameColumn] : null, used.Value / (1024 * 1024), total.Value / (1024 * 1024));
    }

    // The tool's output, or null when it is not installed; an answer that takes too long is no answer.
    private static async Task<string?> RunAsync(string command, string[] arguments, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(command)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = start };
        try
        {
            process.Start();
        }
        catch (Win32Exception)
        {
            return null;
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(Timeout);
        try
        {
            var output = await process.StandardOutput.ReadToEndAsync(timeout.Token).ConfigureAwait(false);
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            return process.ExitCode == 0 ? output : "";
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
            }

            cancellationToken.ThrowIfCancellationRequested();
            return "";
        }
    }
}
