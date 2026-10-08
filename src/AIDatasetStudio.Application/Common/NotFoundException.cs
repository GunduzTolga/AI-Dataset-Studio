namespace AIDatasetStudio.Application.Common;

/// <summary>
/// The requested project, dataset or record does not exist (it may have been deleted meanwhile).
/// </summary>
public sealed class NotFoundException(string entityName, Guid id)
    : Exception($"{entityName} '{id}' was not found.")
{
    public string EntityName { get; } = entityName;

    public Guid Id { get; } = id;
}
