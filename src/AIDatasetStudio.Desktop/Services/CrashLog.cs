using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace AIDatasetStudio.Desktop.Services;

/// <summary>
/// Writes unexpected errors to a local log file (one per day) so problems can be reported. Nothing
/// is ever sent anywhere (SDD §2: no telemetry).
/// </summary>
internal static class CrashLog
{
    private static readonly Lock Gate = new();

    /// <summary>
    /// Logs exceptions that would otherwise go unseen: crashes and faulted tasks nobody awaited.
    /// </summary>
    public static void Install()
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Write(e.ExceptionObject as Exception ?? new InvalidOperationException(Convert.ToString(e.ExceptionObject, CultureInfo.InvariantCulture)));
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Write(e.Exception);
            e.SetObserved();
        };
    }

    /// <summary>
    /// Appends the error to today's log.
    /// </summary>
    /// <param name="directory">The log folder; <see cref="AppInfo.LogDirectory"/> by default.</param>
    /// <returns>The log file, or <c>null</c> if it could not be written.</returns>
    public static string? Write(Exception exception, string? directory = null)
    {
        ArgumentNullException.ThrowIfNull(exception);
        try
        {
            directory ??= AppInfo.LogDirectory;
            Directory.CreateDirectory(directory);
            var now = DateTimeOffset.Now;
            var path = Path.Combine(directory, string.Create(CultureInfo.InvariantCulture, $"aidatasetstudio-{now:yyyyMMdd}.log"));
            var entry = new StringBuilder()
                .Append(CultureInfo.InvariantCulture, $"[{now:yyyy-MM-dd HH:mm:ss zzz}] AI Dataset Studio {AppInfo.Version}")
                .Append(CultureInfo.InvariantCulture, $" · {RuntimeInformation.OSDescription} {RuntimeInformation.OSArchitecture} · .NET {Environment.Version}")
                .AppendLine()
                .AppendLine(exception.ToString())
                .AppendLine();
            lock (Gate)
            {
                File.AppendAllText(path, entry.ToString());
            }

            return path;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null; // logging must never cause a second failure
        }
    }
}
