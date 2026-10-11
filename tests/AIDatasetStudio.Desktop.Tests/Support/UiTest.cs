using Avalonia.Headless;

namespace AIDatasetStudio.Desktop.Tests.Support;

internal static class UiTest
{
    /// <summary>
    /// Runs <paramref name="test"/> on Avalonia's headless UI thread. The framework-agnostic session
    /// is used because the xUnit adapter package targets an older xUnit major version.
    /// </summary>
    public static Task RunAsync(Func<Task> test) =>
        HeadlessUnitTestSession.GetOrStartForAssembly(typeof(UiTest).Assembly)
            .Dispatch(
                async () =>
                {
                    await test();
                    return true;
                },
                TestContext.Current.CancellationToken);

    /// <summary>
    /// Lets the UI thread work until <paramref name="condition"/> holds, failing after 10 seconds.
    /// </summary>
    public static async Task UntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "The condition did not hold in time.");
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }
    }
}
