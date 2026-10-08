namespace AIDatasetStudio.Desktop.Tests.Support;

/// <summary>
/// Tests that switch the process-wide UI language must not run in parallel with each other.
/// </summary>
[CollectionDefinition(Name)]
public sealed class LanguageCollection
{
    public const string Name = "UI language";
}
