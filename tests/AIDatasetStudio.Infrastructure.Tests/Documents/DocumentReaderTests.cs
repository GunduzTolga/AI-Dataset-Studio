using AIDatasetStudio.Application.Documents;
using AIDatasetStudio.Application.Import;
using AIDatasetStudio.Infrastructure.Documents;
using AIDatasetStudio.Infrastructure.Tests.Support;
using static AIDatasetStudio.Infrastructure.Tests.Support.DocxFile;
using static AIDatasetStudio.Infrastructure.Tests.Support.PdfFile;

namespace AIDatasetStudio.Infrastructure.Tests.Documents;

/// <summary>
/// PDF, Word, Markdown and plain-text documents read as blocks (M13, SDD §12.3).
/// </summary>
public sealed class DocumentReaderTests : IDisposable
{
    private readonly DirectoryInfo _directory = Directory.CreateTempSubdirectory("documents-");
    private readonly DocumentReader _reader = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _directory.Delete(recursive: true);

    [Fact]
    public async Task A_pdf_gives_headings_by_size_paragraphs_lists_and_tables()
    {
        var path = PdfFile.Write(Path("kilavuz.pdf"),
        [
            new Text(56, 780, "Müşteri Destek Kılavuzu", 20, Bold: true),
            new Text(56, 745, "Kargo ve teslimat", 14, Bold: true),
            .. Lines(56, 725, 10,
                "Siparişler ödeme onayından sonra aynı gün kargoya veri-",
                "lir ve takip numarası e-posta ile gönderilir.",
                "Kargo ücreti 49,90 TL'dir.",
                "Teslimat süresi şehre göre değişir ve en çok beş iş günü",
                "sürer."),
            new Text(56, 650, "•", 10),
            new Text(70, 650, "İstanbul içi: 1 iş günü", 10),
            new Text(56, 637, "•", 10),
            new Text(70, 637, "Diğer iller: 2-3 iş günü", 10),
            new Text(56, 610, "İade", 14, Bold: true),
            new Text(56, 590, "1.", 10),
            new Text(70, 590, "Hesabım sayfasından talep oluşturun.", 10),
            new Text(56, 577, "2.", 10),
            new Text(70, 577, "Ürünü kargoya verin.", 10),
            new Text(56, 550, "Ürün grubu", 10),
            new Text(300, 550, "Neden", 10),
            new Text(56, 537, "Kişisel bakım", 10),
            new Text(300, 537, "Hijyen", 10),
        ]);

        var document = await _reader.ReadAsync(path, Ct);

        Assert.Equal((DocumentFormat.Pdf, 1), (document.Format, document.PageCount));
        Assert.Equal(
        [
            (DocumentBlockKind.Heading, 1, "Müşteri Destek Kılavuzu"),
            (DocumentBlockKind.Heading, 2, "Kargo ve teslimat"),
            (DocumentBlockKind.Paragraph, 0, "Siparişler ödeme onayından sonra aynı gün kargoya verilir ve takip numarası e-posta ile gönderilir."),
            (DocumentBlockKind.Paragraph, 0, "Kargo ücreti 49,90 TL'dir."),
            (DocumentBlockKind.Paragraph, 0, "Teslimat süresi şehre göre değişir ve en çok beş iş günü sürer."),
            (DocumentBlockKind.ListItem, 0, "- İstanbul içi: 1 iş günü"),
            (DocumentBlockKind.ListItem, 0, "- Diğer iller: 2-3 iş günü"),
            (DocumentBlockKind.Heading, 2, "İade"),
            (DocumentBlockKind.ListItem, 0, "1. Hesabım sayfasından talep oluşturun."),
            (DocumentBlockKind.ListItem, 0, "2. Ürünü kargoya verin."),
            (DocumentBlockKind.Table, 0, "Ürün grubu | Neden\nKişisel bakım | Hijyen"),
        ], document.Blocks.Select(b => (b.Kind, b.Level, b.Text)));
        Assert.All(document.Blocks, b => Assert.Equal(1, b.Position));
        Assert.Equal("kilavuz.pdf#page=1", document.Reference(1));
    }

    [Fact]
    public async Task Running_headers_page_numbers_and_contents_lines_are_left_out_and_paragraphs_go_on_across_pages()
    {
        IEnumerable<Text> Page(int number, params IEnumerable<Text>[] body) =>
        [
            new Text(56, 815, "ACME — Müşteri Hizmetleri El Kitabı", 8),
            .. body.SelectMany(b => b),
            new Text(280, 30, $"Sayfa {number}", 8),
        ];

        var path = PdfFile.Write(Path("elkitabi.pdf"),
            Page(1,
            [
                new Text(56, 760, "İçindekiler", 14, Bold: true),
                new Text(56, 735, "Garanti koşulları ........................ 2", 10),
                new Text(56, 720, "Sık sorulan sorular ........................ 3", 10),
            ]),
            Page(2,
            [
                new Text(56, 760, "Garanti koşulları", 14, Bold: true),
                .. Lines(56, 735, 10, "Tüm ürünlerimiz iki yıl garantilidir. Garanti süresi faturanın", "düzenlendiği tarihte başlar ve kullanım kılavuzuna uygun"),
            ]),
            Page(3,
            [
                .. Lines(56, 760, 10, "kullanım koşuluyla geçerlidir ve faturanın ibrazıyla birlikte", "kullanılır.", "Onarımlar ücretsizdir."),
            ]));

        var document = await _reader.ReadAsync(path, Ct);

        Assert.Equal(3, document.PageCount);
        Assert.Equal(
        [
            (DocumentBlockKind.Heading, 1, "İçindekiler"),
            (DocumentBlockKind.Heading, 2, "Garanti koşulları"),
            (DocumentBlockKind.Paragraph, 2, "Tüm ürünlerimiz iki yıl garantilidir. Garanti süresi faturanın düzenlendiği tarihte başlar ve kullanım kılavuzuna uygun kullanım koşuluyla geçerlidir ve faturanın ibrazıyla birlikte kullanılır."),
            (DocumentBlockKind.Paragraph, 3, "Onarımlar ücretsizdir."),
        ], document.Blocks.Select(b => (b.Kind, b.Position, b.Text)));
    }

    [Fact]
    public async Task Short_bold_lines_at_body_size_are_questions_and_columns_are_read_one_after_the_other()
    {
        var path = PdfFile.Write(Path("sss.pdf"),
        [
            new Text(56, 780, "Sık sorulan sorular", 16, Bold: true),
            new Text(56, 750, "Siparişimi nasıl iptal ederim?", 10, Bold: true),
            .. Lines(56, 737, 10, "Kargoya verilmemiş siparişleri", "Hesabım sayfasından iptal", "edebilirsiniz."),
            new Text(56, 690, "Faturamı nereden indiririm?", 10, Bold: true),
            .. Lines(56, 677, 10, "Fatura e-posta ile gönderilir."),
            new Text(320, 750, "Taksit var mı?", 10, Bold: true),
            .. Lines(320, 737, 10, "Anlaşmalı kartlara 2, 3, 6 ve", "9 taksit seçeneği vardır."),
        ]);

        var document = await _reader.ReadAsync(path, Ct);

        Assert.Equal(
        [
            (DocumentBlockKind.Heading, 1, "Sık sorulan sorular"),
            (DocumentBlockKind.Heading, 2, "Siparişimi nasıl iptal ederim?"),
            (DocumentBlockKind.Paragraph, 0, "Kargoya verilmemiş siparişleri Hesabım sayfasından iptal edebilirsiniz."),
            (DocumentBlockKind.Heading, 2, "Faturamı nereden indiririm?"),
            (DocumentBlockKind.Paragraph, 0, "Fatura e-posta ile gönderilir."),
            (DocumentBlockKind.Heading, 2, "Taksit var mı?"),
            (DocumentBlockKind.Paragraph, 0, "Anlaşmalı kartlara 2, 3, 6 ve 9 taksit seçeneği vardır."),
        ], document.Blocks.Select(b => (b.Kind, b.Level, b.Text)));
    }

    [Fact]
    public async Task Pdfs_without_text_with_a_password_or_damaged_are_explained()
    {
        var scanned = PdfFile.Write(Path("tarama.pdf"), [], []);
        var locked = PdfFile.Write(Path("gizli.pdf"), encrypt: true, [new Text(56, 780, "Gizli")]);
        var damaged = Path("bozuk.pdf");
        await File.WriteAllTextAsync(damaged, "%PDF-1.7\nbu bir PDF değil", Ct);

        Assert.Equal(ImportFileError.NoText, (await Assert.ThrowsAsync<ImportFileException>(() => _reader.ReadAsync(scanned, Ct))).Error);
        Assert.Equal(ImportFileError.ProtectedPdf, (await Assert.ThrowsAsync<ImportFileException>(() => _reader.ReadAsync(locked, Ct))).Error);
        Assert.Equal(ImportFileError.InvalidDocument, (await Assert.ThrowsAsync<ImportFileException>(() => _reader.ReadAsync(damaged, Ct))).Error);
        Assert.Equal(ImportFileError.NotFound, (await Assert.ThrowsAsync<ImportFileException>(() => _reader.ReadAsync(Path("yok.pdf"), Ct))).Error);
    }

    [Fact]
    public async Task A_word_document_gives_headings_by_style_numbered_lists_and_tables()
    {
        var path = DocxFile.Write(Path("kilavuz.docx"), string.Concat(
            P("İçindekiler", "T1"),
            """<w:sdt><w:sdtPr><w:docPartObj><w:docPartGallery w:val="Table of Contents"/></w:docPartObj></w:sdtPr><w:sdtContent>""",
            P("Kargo\t2"),
            "</w:sdtContent></w:sdt>",
            P("Müşteri Destek Kılavuzu", "KonuBal"),
            P("Bu kılavuz   destek ekibi için yazıldı."),
            P("Kargo", "Balk1"),
            """<w:p><w:pPr><w:pStyle w:val="Balk2"/><w:numPr><w:ilvl w:val="0"/><w:numId w:val="3"/></w:numPr></w:pPr><w:r><w:t>Ücret</w:t></w:r></w:p>""",
            P("Kargo ücreti ne kadar?", "SoruBasligi"),
            "<w:p><w:r><w:rPr><w:b/></w:rPr><w:t>500 TL</w:t></w:r><w:r><w:tab/><w:t>üzeri</w:t><w:br/><w:t>ücretsiz</w:t></w:r>",
            "<w:ins><w:r><w:t xml:space=\"preserve\"> ve hızlı</w:t></w:r></w:ins><w:del><w:r><w:delText> ve yavaş</w:delText></w:r></w:del>",
            """<w:r><w:fldChar w:fldCharType="begin"/></w:r><w:r><w:instrText xml:space="preserve"> HYPERLINK "https://example.com" </w:instrText></w:r><w:r><w:fldChar w:fldCharType="separate"/></w:r><w:r><w:t xml:space="preserve"> (ayrıntılar)</w:t></w:r><w:r><w:fldChar w:fldCharType="end"/></w:r>""",
            """<w:r><mc:AlternateContent><mc:Choice Requires="wps"><w:drawing><wps:txbx><w:txbxContent><w:p><w:r><w:t>KUTU</w:t></w:r></w:p></w:txbxContent></wps:txbx></w:drawing></mc:Choice><mc:Fallback><w:pict><v:textbox><w:txbxContent><w:p><w:r><w:t>KUTU</w:t></w:r></w:p></w:txbxContent></v:textbox></w:pict></mc:Fallback></mc:AlternateContent></w:r></w:p>""",
            """<w:p><w:pPr><w:numPr><w:ilvl w:val="0"/><w:numId w:val="1"/></w:numPr></w:pPr><w:r><w:t>Talep oluşturun.</w:t></w:r></w:p>""",
            """<w:p><w:pPr><w:numPr><w:ilvl w:val="1"/><w:numId w:val="1"/></w:numPr></w:pPr><w:r><w:t>Kodu alın.</w:t></w:r></w:p>""",
            """<w:p><w:pPr><w:numPr><w:ilvl w:val="0"/><w:numId w:val="1"/></w:numPr></w:pPr><w:r><w:t>Gönderin.</w:t></w:r></w:p>""",
            P("Hijyen ürünleri", "ListeMaddesi"),
            """<w:p><w:pPr><w:pStyle w:val="ListeMaddesi"/><w:numPr><w:numId w:val="0"/></w:numPr></w:pPr><w:r><w:t>Listeden çıkarılmış paragraf.</w:t></w:r></w:p>""",
            """<w:p><w:pPr><w:outlineLvl w:val="2"/></w:pPr><w:r><w:t>Doğrudan düzey</w:t></w:r></w:p>""",
            "<w:p/>",
            "<w:tbl><w:tr><w:tc>", P("Ürün"), "</w:tc><w:tc>", P("Neden"), "</w:tc></w:tr>",
            "<w:tr><w:tc>", P("Bakım"), P("ürünleri"), "</w:tc><w:tc><w:tbl><w:tr><w:tc>", P("Hijyen"), "</w:tc><w:tc>", P("Sağlık"), "</w:tc></w:tr></w:tbl></w:tc></w:tr>",
            "<w:tr><w:tc>", P(""), "</w:tc><w:tc/></w:tr></w:tbl>",
            "<w:tbl><w:tr><w:tc>", P("Not: iade kodu 30 gün geçerlidir."), "</w:tc></w:tr></w:tbl>",
            P("Son paragraf.")));

        var document = await _reader.ReadAsync(path, Ct);

        Assert.Equal((DocumentFormat.Word, 0), (document.Format, document.PageCount));
        Assert.Equal(
        [
            (DocumentBlockKind.Heading, 0, "Müşteri Destek Kılavuzu"),
            (DocumentBlockKind.Paragraph, 0, "Bu kılavuz destek ekibi için yazıldı."),
            (DocumentBlockKind.Heading, 1, "Kargo"),
            (DocumentBlockKind.Heading, 2, "5. Ücret"),
            (DocumentBlockKind.Heading, 2, "Kargo ücreti ne kadar?"),
            (DocumentBlockKind.Paragraph, 0, "500 TL üzeri\nücretsiz ve hızlı (ayrıntılar)"),
            (DocumentBlockKind.ListItem, 0, "1. Talep oluşturun."),
            (DocumentBlockKind.ListItem, 1, "a) Kodu alın."),
            (DocumentBlockKind.ListItem, 0, "2. Gönderin."),
            (DocumentBlockKind.ListItem, 0, "- Hijyen ürünleri"),
            (DocumentBlockKind.Paragraph, 0, "Listeden çıkarılmış paragraf."),
            (DocumentBlockKind.Heading, 3, "Doğrudan düzey"),
            (DocumentBlockKind.Table, 0, "Ürün | Neden\nBakım ürünleri | Hijyen | Sağlık"),
            (DocumentBlockKind.Paragraph, 0, "Not: iade kodu 30 gün geçerlidir."),
            (DocumentBlockKind.Paragraph, 0, "Son paragraf."),
        ], document.Blocks.Select(b => (b.Kind, b.Level, b.Text)));

        // Positions count the body's paragraphs, those in tables included.
        Assert.Equal([2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 15, 22, 23], document.Blocks.Select(b => b.Position));
        Assert.Equal("kilavuz.docx#paragraph=5", document.Reference(5));
    }

    [Fact]
    public async Task Old_or_damaged_word_documents_are_explained()
    {
        var legacy = Path("eski.doc");
        await File.WriteAllBytesAsync(legacy, [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1, 0, 0, 0, 0], Ct);
        var damaged = Path("bozuk.docx");
        await File.WriteAllBytesAsync(damaged, [0x50, 0x4B, 0x03, 0x04, 1, 2, 3, 4, 5], Ct);
        var workbook = XlsxFile.Write(Path("tablo.xlsx"), new XlsxFile.Sheet("A", [["x"]]));
        File.Move(workbook, Path("tablo.docx"));

        Assert.Equal(ImportFileError.LegacyDocument, (await Assert.ThrowsAsync<ImportFileException>(() => _reader.ReadAsync(legacy, Ct))).Error);
        Assert.Equal(ImportFileError.InvalidDocument, (await Assert.ThrowsAsync<ImportFileException>(() => _reader.ReadAsync(damaged, Ct))).Error);
        Assert.Equal(ImportFileError.InvalidDocument, (await Assert.ThrowsAsync<ImportFileException>(() => _reader.ReadAsync(Path("tablo.docx"), Ct))).Error);
    }

    [Fact]
    public async Task Markdown_and_plain_text_are_read_as_blocks()
    {
        var markdown = Path("sss.md");
        await File.WriteAllTextAsync(markdown, """
            ---
            title: SSS
            ---
            # Kargo

            Kargo ücreti 500 TL üzeri
            siparişlerde ücretsizdir.

            - İstanbul: 1 gün
            - Diğer: 2-3 gün

            | Bölge | Süre |
            |---|---|

            ```json
            {"a": 1,

             "b": 2}
            ```

            İade
            ----
            14 gün.
            """, Ct);
        var text = Path("notlar.txt");
        await File.WriteAllTextAsync(text, "Birinci paragraf\nikinci satır\n\n\nİkinci paragraf\n", Ct);

        var document = await _reader.ReadAsync(markdown, Ct);

        Assert.Equal(DocumentFormat.Markdown, document.Format);
        Assert.Equal(
        [
            (DocumentBlockKind.Heading, 1, 4, "Kargo"),
            (DocumentBlockKind.Paragraph, 0, 6, "Kargo ücreti 500 TL üzeri\nsiparişlerde ücretsizdir."),
            (DocumentBlockKind.ListItem, 0, 9, "- İstanbul: 1 gün\n- Diğer: 2-3 gün"),
            (DocumentBlockKind.Table, 0, 12, "| Bölge | Süre |\n|---|---|"),
            (DocumentBlockKind.Paragraph, 0, 15, "```json\n{\"a\": 1,\n\n \"b\": 2}\n```"),
            (DocumentBlockKind.Heading, 2, 21, "İade"),
            (DocumentBlockKind.Paragraph, 0, 23, "14 gün."),
        ], document.Blocks.Select(b => (b.Kind, b.Level, b.Position, b.Text)));

        var plain = await _reader.ReadAsync(text, Ct);

        Assert.Equal(DocumentFormat.PlainText, plain.Format);
        Assert.Equal([(1, "Birinci paragraf\nikinci satır"), (5, "İkinci paragraf")], plain.Blocks.Select(b => (b.Position, b.Text)));
        Assert.Equal("notlar.txt#line=5", plain.Reference(5));
    }

    [Fact]
    public async Task The_last_document_is_kept_until_its_file_changes()
    {
        var path = Path("notlar.txt");
        await File.WriteAllTextAsync(path, "Bir.\n", Ct);

        var first = await _reader.ReadAsync(path, Ct);
        Assert.Same(first, await _reader.ReadAsync(path, Ct));

        await File.WriteAllTextAsync(path, "Bir.\n\nİki.\n", Ct);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(1));
        var changed = await _reader.ReadAsync(path, Ct);

        Assert.NotSame(first, changed);
        Assert.Equal(2, changed.Blocks.Count);
    }

    private string Path(string name) => System.IO.Path.Combine(_directory.FullName, name);
}
