namespace AIDatasetStudio.Application.Import;

public enum HubErrorKind
{
    /// <summary>The text is not a dataset id ("owner/name") or a dataset's page address.</summary>
    InvalidId,

    /// <summary>No such dataset, file or revision.</summary>
    NotFound,

    /// <summary>A gated or private dataset: access must be granted, then a token given (HF_TOKEN).</summary>
    AccessDenied,

    /// <summary>Hugging Face could not be reached, or answered with an error.</summary>
    Unavailable,
}

/// <summary>
/// The Hugging Face Hub could not give what was asked. <see cref="Exception.Message"/> is English
/// text for logs; user interfaces show localized text based on <see cref="Kind"/>.
/// </summary>
public sealed class HubException(HubErrorKind kind, string message, Exception? innerException = null) : Exception(message, innerException)
{
    public HubErrorKind Kind { get; } = kind;
}

/// <summary>
/// A file of a Hub dataset that can be imported.
/// </summary>
/// <param name="Size">Bytes; <c>null</c> when the Hub does not say.</param>
public sealed record HubFile(string Path, long? Size);

/// <summary>
/// Datasets on the Hugging Face Hub (M25): their importable files, and downloading one to import
/// it. Only what the user asks for is fetched; public datasets need no account, and a token in the
/// <c>HF_TOKEN</c> environment variable opens gated and private ones.
/// </summary>
public interface IHuggingFaceHub
{
    /// <summary>
    /// "owner/name" from an id or from a dataset's page address; <c>null</c> when it is neither.
    /// </summary>
    static string? ParseDatasetId(string text)
    {
        var value = text.Trim();
        foreach (var prefix in new[] { "https://huggingface.co/datasets/", "http://huggingface.co/datasets/", "huggingface.co/datasets/", "hf://datasets/" })
        {
            if (value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                value = value[prefix.Length..];
                break;
            }
        }

        var parts = value.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2 || parts.Take(2).Any(part => !part.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.') || part.StartsWith('.')))
        {
            return null;
        }

        return parts[0] + "/" + parts[1];
    }

    /// <summary>
    /// The dataset's files that can be imported (Parquet, JSON Lines, JSON, CSV), by path.
    /// </summary>
    /// <exception cref="HubException">Not a dataset id, not found, not allowed, or the Hub cannot be reached.</exception>
    Task<IReadOnlyList<HubFile>> ListFilesAsync(string datasetId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Downloads a file of the dataset to this computer and returns its path there. A file already
    /// downloaded whole is not fetched again.
    /// </summary>
    /// <param name="progress">Bytes received so far.</param>
    /// <exception cref="HubException">Not found, not allowed, or the Hub cannot be reached.</exception>
    Task<string> DownloadAsync(string datasetId, HubFile file, IProgress<long>? progress = null, CancellationToken cancellationToken = default);
}
