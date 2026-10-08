using System.Data.Common;
using AIDatasetStudio.Application.Common;
using AIDatasetStudio.Application.Workspaces;
using AIDatasetStudio.Domain.Common;

namespace AIDatasetStudio.Desktop.ViewModels;

/// <summary>
/// Maps the exceptions the UI expects to localized status messages.
/// </summary>
internal static class UiErrors
{
    public sealed record Error(string Key, object?[] Args);

    public static Error? TryDescribe(Exception exception) => exception switch
    {
        WorkspaceFileException ex => new Error(WorkspaceErrorKey(ex.Error), [Path.GetFileName(ex.FilePath), ex.InnerException?.Message]),
        NotFoundException => new Error("Error_ItemNotFound", []),
        DomainException ex => new Error("Error_InvalidInput", [ex.Message]),
        IOException or UnauthorizedAccessException => new Error("Error_FileAccess", [exception.Message]),
        // Storage failures (file locked by another program, disk full, read-only file); EF Core wraps them.
        DbException ex => new Error("Error_Storage", [ex.Message]),
        { InnerException: DbException ex } => new Error("Error_Storage", [ex.Message]),
        _ => null,
    };

    public static string WorkspaceErrorKey(WorkspaceFileError error) => error switch
    {
        WorkspaceFileError.NotFound => "Error_WorkspaceNotFound",
        WorkspaceFileError.AlreadyExists => "Error_WorkspaceAlreadyExists",
        WorkspaceFileError.NotAWorkspace => "Error_NotAWorkspace",
        WorkspaceFileError.CreatedByNewerVersion => "Error_CreatedByNewerVersion",
        WorkspaceFileError.Corrupted => "Error_WorkspaceCorrupted",
        WorkspaceFileError.Unavailable => "Error_WorkspaceUnavailable",
        _ => throw new ArgumentOutOfRangeException(nameof(error), error, null),
    };
}
