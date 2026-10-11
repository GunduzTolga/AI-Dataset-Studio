using AIDatasetStudio.Application.Import;
using AIDatasetStudio.Desktop.Localization;
using AIDatasetStudio.Desktop.Tests.Support;
using AIDatasetStudio.Desktop.ViewModels.Import;
using AIDatasetStudio.Domain.Enums;

namespace AIDatasetStudio.Desktop.Tests.ViewModels;

/// <summary>
/// File › Import from Hugging Face (M25), against a fake Hub.
/// </summary>
[Collection(LanguageCollection.Name)]
public sealed class HubImportTests
{
    public HubImportTests() => Localizer.Instance.SetCulture(Localizer.English);

    [Fact]
    public Task A_file_of_a_hub_dataset_is_downloaded_and_imported_through_the_wizard() => UiTest.RunAsync(async () =>
    {
        await using var host = new DesktopTestHost();
        await host.CreateDatasetAsync(DatasetType.Instruction, []);
        host.Hub.Datasets["tatsu-lab/alpaca"] =
        [
            (new HubFile("data/test.jsonl", 60), """{"instruction": "x", "output": "y"}"""),
            (new HubFile("data/train.jsonl", 2_500_000), "{\"instruction\": \"Kargo?\", \"output\": \"1-3 gün.\"}\n{\"instruction\": \"İade?\", \"output\": \"14 gün.\"}\n"),
        ];
        HubImportViewModel? seen = null;
        host.Dialogs.HubScript = async hub =>
        {
            seen = hub;
            Assert.Equal("Into “Dataset”: a file of the dataset is downloaded, then opened in the import wizard.", hub.TargetText);
            Assert.False(hub.ListCommand.CanExecute(null));

            // Not an id, then not on the Hub.
            hub.DatasetId = "alpaca";
            await hub.ListCommand.ExecuteAsync(null);
            Assert.Equal("Type a dataset id such as tatsu-lab/alpaca, or its address on huggingface.co.", hub.ErrorMessage);
            hub.DatasetId = "yok/yok";
            await hub.ListCommand.ExecuteAsync(null);
            Assert.Equal("No such dataset on Hugging Face. Check the id; private datasets need a token.", hub.ErrorMessage);
            Assert.False(hub.DownloadCommand.CanExecute(null));

            // A page address works too; the training file is chosen.
            hub.DatasetId = "https://huggingface.co/datasets/tatsu-lab/alpaca";
            await hub.ListCommand.ExecuteAsync(null);
            Assert.Null(hub.ErrorMessage);
            Assert.Equal([("data/test.jsonl", "1 KB"), ("data/train.jsonl", "2.4 MB")], hub.Files.Select(f => (f.Path, f.SizeText)));
            Assert.Equal("data/train.jsonl", hub.SelectedFile?.Path);
            Assert.Equal("2 files in tatsu-lab/alpaca", hub.ProgressText);

            await hub.DownloadCommand.ExecuteAsync(null);
        };
        await host.Main.ImportFromHubCommand.ExecuteAsync(null);

        Assert.Equal(["tatsu-lab/alpaca:data/train.jsonl"], host.Hub.Downloads);
        Assert.EndsWith("train.jsonl", seen!.DownloadedPath, StringComparison.Ordinal);
        Assert.Equal(["Kargo?", "İade?"], host.Main.Records.Items.Select(r => r.Preview));
    });
}
