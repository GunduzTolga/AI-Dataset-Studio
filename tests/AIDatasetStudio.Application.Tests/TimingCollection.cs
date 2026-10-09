namespace AIDatasetStudio.Application.Tests;

/// <summary>
/// Tests that measure time run alone in this assembly, so other tests do not slow them down.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class TimingCollection
{
    public const string Name = "Timing";
}
