using AIDatasetStudio.Application.Records;
using AIDatasetStudio.Desktop.Localization;
using AIDatasetStudio.Desktop.Tests.Support;
using AIDatasetStudio.Domain.Enums;
using AIDatasetStudio.Domain.Payloads;

namespace AIDatasetStudio.Desktop.Tests.ViewModels;

/// <summary>
/// Conversation logs in the import wizard (M18): speakers mapped to the customer and the agent.
/// </summary>
[Collection(LanguageCollection.Name)]
public sealed class TranscriptImportTests
{
    public TranscriptImportTests() => Localizer.Instance.SetCulture(Localizer.Turkish);

    [Fact]
    public Task A_whatsapp_chat_is_imported_with_its_speakers_as_customer_and_agent() => UiTest.RunAsync(async () =>
    {
        await using var host = new DesktopTestHost();
        await host.CreateDatasetAsync(DatasetType.Chat, []);
        var path = Path.Combine(host.Directory, "WhatsApp Sohbeti.txt");
        await File.WriteAllTextAsync(path, """
            08.10.2026 14:32 - Ayşe Yılmaz: Merhaba, siparişim nerede?
            08.10.2026 14:35 - Mağaza Destek: Kargonuz yarın elinizde olur.
            08.10.2026 22:10 - Ayşe Yılmaz: İade nasıl yapılır?
            08.10.2026 22:15 - Mağaza Destek: Hesabım sayfasından talep oluşturabilirsiniz.
            08.10.2026 22:16 - Can (Depo): Ürünü depoya gönderebilirsiniz.
            """);
        host.Files.ImportPath = path;
        host.Dialogs.ImportWizardScript = async wizard =>
        {
            await wizard.InitializeAsync();
            Assert.Equal(("Konuşma kaydı (WhatsApp, döküm)", "Konuşmacıya göre konuşmalar", "2 konuşma"), (wizard.FormatText, wizard.LayoutText, wizard.RowCountText));
            Assert.Equal(("KONUŞMACILAR", "2 konuşmayı içe aktar"), (wizard.MappingTitle, wizard.StartText));
            Assert.StartsWith("Her konuşma bir kayıt olur.", wizard.TextLayoutHint, StringComparison.Ordinal);
            Assert.Equal(["Müşteri (kullanıcı turları)", "Temsilci (asistan turları)"], wizard.Mappings.Select(m => m.Label));
            Assert.Equal(["Ayşe Yılmaz", "Mağaza Destek"], wizard.Mappings.Select(m => m.SelectedColumn!.Label));
            Assert.Equal(["— Eşlenmedi —", "Ayşe Yılmaz", "Mağaza Destek", "Can (Depo)"], wizard.Mappings[1].Options.Select(o => o.Label));
            Assert.True(wizard.HasFormatChoice);

            // Without an agent, the depot's message answers too.
            wizard.Mappings[1].SelectedColumn = wizard.Mappings[1].Options[0];
            await DesktopTestHost.WaitUntilAsync(() => wizard.Preview.Count == 2 && wizard.Preview[1].Secondary!.Contains("depoya", StringComparison.Ordinal));
            Assert.Equal("İlk 2 konuşma: 2 hazır, 0 atlanacak", wizard.PreviewSummary);
            await wizard.ImportCommand.ExecuteAsync(null);
        };

        await host.Main.ImportCommand.ExecuteAsync(null);

        Assert.Equal(2, host.Main.Records.Items.Count);
        var second = await host.Get<IRecordQueryService>().GetAsync(host.Main.Records.Items[1].Id);
        Assert.Equal(
            new ChatPayload(null,
            [
                new ChatMessage(ChatRole.User, "İade nasıl yapılır?"),
                new ChatMessage(ChatRole.Assistant, "Hesabım sayfasından talep oluşturabilirsiniz.\nÜrünü depoya gönderebilirsiniz."),
            ]),
            second!.Payload);
        Assert.Equal("WhatsApp Sohbeti.txt#line=3", second.Lineage.SourceReference);
    });
}
