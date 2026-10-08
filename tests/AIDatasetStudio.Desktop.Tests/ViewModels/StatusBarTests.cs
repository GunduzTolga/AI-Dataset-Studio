using AIDatasetStudio.Desktop.Localization;
using AIDatasetStudio.Desktop.Tests.Support;
using AIDatasetStudio.Desktop.ViewModels;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace AIDatasetStudio.Desktop.Tests.ViewModels;

[Collection(LanguageCollection.Name)]
public sealed class StatusBarTests
{
    public StatusBarTests() => Localizer.Instance.SetCulture(Localizer.English);

    [Fact]
    public void Storage_failures_are_reported_also_when_wrapped_by_the_data_layer()
    {
        var status = new StatusBarViewModel(Localizer.Instance);

        Assert.True(status.TryReport(new SqliteException("database is locked", 5)));
        Assert.StartsWith("The workspace could not be saved;", status.Message, StringComparison.Ordinal);
        Assert.EndsWith("database is locked", status.Message, StringComparison.Ordinal);

        Assert.True(status.TryReport(new DbUpdateException("An error occurred while saving.", new SqliteException("disk I/O error", 10))));
        Assert.EndsWith("disk I/O error", status.Message, StringComparison.Ordinal);

        Assert.False(status.TryReport(new InvalidOperationException("a bug")));
    }
}
