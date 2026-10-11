using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace AIDatasetStudio.Application.Training;

/// <summary>
/// Reads a training tool's console output as it arrives (M28, ADR-048). LLaMA-Factory and Axolotl
/// both train with Hugging Face's Trainer, so one reader serves both: its progress bar gives the
/// step, the dictionaries it prints give the losses, and "Total optimization steps = N" tells the
/// training bar from the evaluation bars that run in between.
/// </summary>
public sealed partial class TrainingOutputReader
{
    private readonly StringBuilder _line = new();
    private readonly Queue<string> _lastLines = new();
    private readonly int _keepLines;
    private int? _totalSteps;
    private int _lastStep = -1;
    private bool _pendingReturn;

    public TrainingOutputReader(int keepLines = 200) => _keepLines = keepLines;

    /// <summary>
    /// The last complete lines written, oldest first.
    /// </summary>
    public IReadOnlyList<string> LastLines => [.. _lastLines];

    /// <summary>
    /// Takes the next piece of output and returns what it reported. Progress bars redraw with a
    /// carriage return; each redraw is read, but only finished lines go to the log.
    /// </summary>
    public IReadOnlyList<TrainingEvent> Read(ReadOnlySpan<char> output)
    {
        var events = new List<TrainingEvent>();
        foreach (var c in output)
        {
            // "\r\n" ends a line (Windows); a lone "\r" redraws a progress bar.
            if (_pendingReturn)
            {
                _pendingReturn = false;
                Complete(events, logged: c == '\n');
                if (c == '\n')
                {
                    continue;
                }
            }

            if (c == '\r')
            {
                _pendingReturn = true;
            }
            else if (c == '\n')
            {
                Complete(events, logged: true);
            }
            else
            {
                _line.Append(c);
            }
        }

        // A progress bar that has not been redrawn yet is still read.
        if (_line.Length > 0)
        {
            ReadProgress(_line.ToString(), events);
        }

        return events;
    }

    /// <summary>
    /// Reads what is left once the tool has exited.
    /// </summary>
    public IReadOnlyList<TrainingEvent> Finish()
    {
        var events = new List<TrainingEvent>();
        _pendingReturn = false;
        Complete(events, logged: true);
        return events;
    }

    /// <summary>
    /// The most likely cause of a failed run, from its output.
    /// </summary>
    public static TrainingFailure Classify(IEnumerable<string> lines)
    {
        var text = string.Join('\n', lines);
        return text switch
        {
            _ when text.Contains("out of memory", StringComparison.OrdinalIgnoreCase) || text.Contains("OutOfMemoryError", StringComparison.Ordinal) => TrainingFailure.OutOfMemory,
            _ when text.Contains("No module named", StringComparison.Ordinal) || text.Contains("ModuleNotFoundError", StringComparison.Ordinal) => TrainingFailure.MissingPackage,
            _ when text.Contains("doesn't support bf16/gpu", StringComparison.Ordinal) || text.Contains("Torch not compiled with CUDA", StringComparison.Ordinal)
                || text.Contains("No GPU found", StringComparison.OrdinalIgnoreCase) => TrainingFailure.NoGpu,
            _ when text.Contains("is not a valid model identifier", StringComparison.Ordinal) || text.Contains("Repository Not Found", StringComparison.Ordinal)
                || text.Contains("GatedRepoError", StringComparison.Ordinal) || text.Contains("401 Client Error", StringComparison.Ordinal) => TrainingFailure.ModelUnavailable,
            _ when text.Contains("DatasetGenerationError", StringComparison.Ordinal) || text.Contains("Cannot find valid samples", StringComparison.Ordinal) => TrainingFailure.DatasetError,
            _ => TrainingFailure.Unknown,
        };
    }

    private void Complete(List<TrainingEvent> events, bool logged)
    {
        if (_line.Length == 0)
        {
            return;
        }

        var line = _line.ToString();
        _line.Clear();
        ReadProgress(line, events);
        ReadLine(line, events);
        if (logged && !string.IsNullOrWhiteSpace(line))
        {
            var text = line.TrimEnd();
            _lastLines.Enqueue(text);
            if (_lastLines.Count > _keepLines)
            {
                _lastLines.Dequeue();
            }

            events.Add(new TrainingLogLine(text));
        }
    }

    private void ReadLine(string line, List<TrainingEvent> events)
    {
        if (TotalSteps().Match(line) is { Success: true } total)
        {
            _totalSteps = int.Parse(total.Groups[1].ValueSpan, NumberStyles.AllowThousands, CultureInfo.InvariantCulture);
        }

        // {'loss': '6.392', 'grad_norm': '0.4496', 'learning_rate': '9.972e-05', 'epoch': '0.4706'}
        // (numbers are quoted from Transformers 5, plain before).
        var open = line.IndexOf("{'", StringComparison.Ordinal);
        if (open < 0)
        {
            return;
        }

        var values = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (Match pair in MetricPair().Matches(line[open..]))
        {
            if (double.TryParse(pair.Groups[2].ValueSpan, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
            {
                values[pair.Groups[1].Value] = value;
            }
        }

        double? Get(string key) => values.TryGetValue(key, out var value) ? value : null;
        var loss = Get("loss");
        var evalLoss = Get("eval_loss");
        var trainLoss = Get("train_loss");
        if (loss is not null || evalLoss is not null || trainLoss is not null)
        {
            events.Add(new TrainingMetrics(Get("epoch"), loss ?? trainLoss, evalLoss, Get("learning_rate"), IsFinal: trainLoss is not null && loss is null));
        }
    }

    private void ReadProgress(string line, List<TrainingEvent> events)
    {
        // " 15%|█▌        | 10/66 [00:00<00:03, 16.81it/s]": the last bar drawn on the line counts.
        var bars = ProgressBar().Matches(line);
        if (bars.Count == 0)
        {
            return;
        }

        var bar = bars[^1];
        var step = int.Parse(bar.Groups[1].ValueSpan, CultureInfo.InvariantCulture);
        var total = int.Parse(bar.Groups[2].ValueSpan, CultureInfo.InvariantCulture);

        // Only the training bar counts: the data is prepared with bars of its own before the
        // trainer states its step count, and evaluations draw theirs in between.
        if (_totalSteps != total || step == _lastStep)
        {
            return;
        }

        _lastStep = step;
        events.Add(new TrainingStep(step, total, Duration(bar.Groups[3].Value), Duration(bar.Groups[4].Value)));
    }

    private static TimeSpan? Duration(string text)
    {
        if (text.Length == 0 || text == "?")
        {
            return null;
        }

        var parts = text.Split(':');
        var seconds = 0;
        foreach (var part in parts)
        {
            if (!int.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out var value))
            {
                return null;
            }

            seconds = (seconds * 60) + value;
        }

        return TimeSpan.FromSeconds(seconds);
    }

    [GeneratedRegex(@"Total optimization steps = ([\d,]+)")]
    private static partial Regex TotalSteps();

    [GeneratedRegex(@"'([A-Za-z_/]+)':\s*'?(-?(?:\d+\.?\d*|\.\d+)(?:[eE][-+]?\d+)?|nan|inf)'?")]
    private static partial Regex MetricPair();

    // Read only once the times are complete: a bar cut off mid-way ("[00:00<00:0") is read on the next piece.
    [GeneratedRegex(@"(\d+)/(\d+) \[(\d+(?::\d+)+)<(\d+(?::\d+)+|\?)(?=[,\]])")]
    private static partial Regex ProgressBar();
}
