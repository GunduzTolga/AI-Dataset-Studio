using AIDatasetStudio.Application.Records;
using AIDatasetStudio.Domain.Enums;
using AIDatasetStudio.Domain.Payloads;
using AIDatasetStudio.Domain.ValueObjects;

namespace AIDatasetStudio.Desktop.Services;

/// <summary>
/// Synthetic records for trying out lists, search and paging before import exists (M2).
/// Mixes Turkish and English text so search can be exercised in both languages.
/// </summary>
internal static class SampleData
{
    public const string SourceReference = "sample-data";

    private static readonly (string Question, string Answer)[] Topics =
    [
        ("Yıllık izin hakkım ne zaman başlar?", "İş Kanunu uyarınca deneme süresi dahil 1 yıllık kıdeminizi doldurduğunuzda başlar."),
        ("Şifremi unuttum, nasıl sıfırlarım?", "Giriş ekranındaki \"Şifremi unuttum\" bağlantısından e-posta adresinize sıfırlama bağlantısı gönderilir."),
        ("Siparişim ne zaman kargoya verilir?", "Siparişler iş günlerinde 24 saat içinde kargoya teslim edilir."),
        ("Faturamı nereden indirebilirim?", "Hesabım › Siparişlerim sayfasında her siparişin yanında fatura bağlantısı bulunur."),
        ("Uzaktan çalışma limiti haftada kaç gündür?", "Şirket politikasına göre haftada en fazla 2 gün uzaktan çalışılabilir."),
        ("How do I reset my password?", "Use the \"Forgot password\" link on the sign-in page; a reset link is emailed to you."),
        ("When will my order ship?", "Orders placed on business days ship within 24 hours."),
        ("How many vacation days do I get?", "Employees with one to five years of service get at least 14 paid days per year."),
        ("Can I change my delivery address?", "Yes, until the order is handed to the carrier, from the order details page."),
        ("What is overfitting?", "When a model memorizes training data and fails to generalize; regularization and more data help."),
    ];

    private static readonly string[] Instructions =
    [
        "Aşağıdaki soruyu kısa ve net yanıtla.",
        "Müşteri sorusunu kibar bir dille cevapla.",
        "Answer the question in one or two sentences.",
        "Summarize the answer for a support agent.",
    ];

    public static IEnumerable<NewRecord> Create(DatasetType type, int count, int seed = 42)
    {
        var random = new Random(seed);
        var lineage = new RecordLineage(RecordSourceKind.Synthetic, SourceReference);
        for (var i = 1; i <= count; i++)
        {
            var (question, answer) = Topics[random.Next(Topics.Length)];
            var variant = $"{question} (#{i})";
            RecordPayload payload = type switch
            {
                // Alternate between the two common shapes: question as instruction, or a task
                // instruction with the question as input.
                DatasetType.Instruction => i % 2 == 0
                    ? new InstructionPayload(Instructions[random.Next(Instructions.Length)], variant, answer)
                    : new InstructionPayload(variant, string.Empty, answer),
                DatasetType.Chat => new ChatPayload(
                    "Sen yardımsever bir müşteri destek asistanısın.",
                    [new ChatMessage(ChatRole.User, variant), new ChatMessage(ChatRole.Assistant, answer)]),
                DatasetType.Preference => new PreferencePayload(variant, answer, "Bilmiyorum."),
                _ => throw new NotSupportedException($"Sample data is not available for {type} datasets."),
            };

            yield return new NewRecord(payload, lineage);
        }
    }
}
