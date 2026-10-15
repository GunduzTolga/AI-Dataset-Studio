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

    // A shop assistant's functions (SDD §4.4).
    private static readonly string[] Tools =
    [
        """
        {
          "name": "get_order_status",
          "description": "Returns the status, carrier and expected delivery date of an order.",
          "parameters": {
            "type": "object",
            "properties": {
              "order_id": { "type": "string", "description": "The order number" }
            },
            "required": ["order_id"]
          }
        }
        """,
        """
        {
          "name": "get_shipping_fee",
          "description": "Returns the shipping fee for a cart total and the total above which shipping is free.",
          "parameters": {
            "type": "object",
            "properties": {
              "cart_total": { "type": "number", "description": "Cart total in TL" }
            },
            "required": ["cart_total"]
          }
        }
        """,
    ];

    private static ChatMessage[] ToolTurns(int i) => (i % 4) switch
    {
        0 =>
        [
            new(ChatRole.User, $"Siparişim nerede? Sipariş numaram {48200 + i}."),
            new(ChatRole.Assistant, string.Empty, toolCalls:
                [new ToolCall("get_order_status", $"{{\"order_id\": \"{48200 + i}\"}}", """{"status": "kargoda", "carrier": "Yurtiçi Kargo", "eta": "2026-10-17"}""")]),
            new(ChatRole.Assistant, $"{48200 + i} numaralı siparişiniz kargoda; Yurtiçi Kargo ile 17 Ekim'de teslim edilmesi bekleniyor."),
        ],
        1 =>
        [
            new(ChatRole.User, $"Sepetim {400 + i} TL, kargo ücreti ne kadar?"),
            new(ChatRole.Assistant, string.Empty, toolCalls:
                [new ToolCall("get_shipping_fee", $"{{\"cart_total\": {400 + i}}}", """{"fee": 49.90, "free_over": 500}""")]),
            new(ChatRole.Assistant, $"Sepetiniz 500 TL'nin altında olduğu için kargo ücreti 49,90 TL; {100 - i} TL'lik daha alışverişte kargo ücretsiz."),
        ],
        2 =>
        [
            new(ChatRole.User, $"Where is order {77100 + i}?"),
            new(ChatRole.Assistant, string.Empty, toolCalls:
                [new ToolCall("get_order_status", $"{{\"order_id\": \"{77100 + i}\"}}", """{"status": "delivered", "carrier": "Aras Kargo", "eta": "2026-10-12"}""")]),
            new(ChatRole.Assistant, $"Order {77100 + i} was delivered by Aras Kargo on 12 October."),
        ],
        // No function is needed: the assistant answers without calling one.
        _ =>
        [
            new(ChatRole.User, $"Merhaba! (#{i})"),
            new(ChatRole.Assistant, "Merhaba! Siparişleriniz ve kargo ücretleri hakkında yardımcı olabilirim."),
        ],
    };

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
                DatasetType.ToolCalling => new ToolCallingPayload("Sen bir online mağazanın asistanısın. Gerektiğinde araçları kullan.", Tools, ToolTurns(i)),
                _ => throw new NotSupportedException($"Sample data is not available for {type} datasets."),
            };

            yield return new NewRecord(payload, lineage);
        }
    }
}
