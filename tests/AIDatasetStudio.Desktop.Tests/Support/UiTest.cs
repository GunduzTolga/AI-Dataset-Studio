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
}
