# Plan İncelemesi (SDD v1.0.0)

- **Tarih:** 2026-10-08
- **Kapsam:** `Plan.txt` (SDD v1.0.0) ve `README.md`
- **Durum:** Tüm öneriler kabul edildi (2026-10-08) ve `Plan.txt` v1.1.0'a işlendi. Kodu etkileyenler M0'da uygulandı.

Genel olarak plan güçlü: problem tanımı net; "dahili format ≠ model formatı" ve "dönüşüm yalnızca export anında" ilkeleri doğru; MVP kapsamı (§20) gerçekçi bir sınırla çizilmiş. Aşağıdaki notlar, kod yazıldıkça pahalılaşacak noktaları erkenden yakalamak için.

---

## 1. M0'da uygulanan kararlar

Bu maddeler planın teknolojik varsayımlarının güncellenmesi veya netleştirilmesi gerektiği için iskelette doğrudan uygulandı. `Plan.txt` başlığı ve ADR listesi buna göre güncellendi (ADR-012 – ADR-016). Kabul edilen kararlardan koda yansıyanlar tablonun sonuna eklendi (12–15).

| # | Plan | Uygulanan | Gerekçe |
|---|------|-----------|---------|
| 1 | .NET 9 / C# 13 | **.NET 10 (LTS) / C# 14** | .NET 9 bir STS sürümü ve desteği **10 Kasım 2026'da** bitiyor (bir ay sonra). .NET 10 LTS, Kasım 2028'e kadar destekli. EF Core 10 de buna bağlı. |
| 2 | Avalonia 11+ | **Avalonia 12.1** | Güncel kararlı sürüm 12.x. AvaloniaEdit'in güncel paketi `Avalonia.AvaloniaEdit` 12.0 (eski `AvaloniaEdit` paket kimliği 0.10'da kaldı). `Avalonia.Controls.DataGrid` MIT; `TreeDataGrid`'in 12.x paketinde açık kaynak lisans ifadesi yok, kullanmadan önce lisansı kontrol edilmeli. |
| 3 | "CQRS" (araç belirtilmemiş) | **MediatR / AutoMapper yok** | Bu ikisi 2025'te ticari lisans modeline geçti. Masaüstü uygulaması için basit uygulama servisleri / handler arayüzleri yeterli. Testlerde de FluentAssertions (v8 ticari) yerine xUnit v3 `Assert` kullanıldı. |
| 4 | `Microsoft.ML.Tokenizers` + `Tiktoken-CSharp` | **Yalnızca `Microsoft.ML.Tokenizers`** (M4'te) | `Microsoft.ML.Tokenizers` 2.0, `cl100k_base` ve `o200k_base` sözlüklerini ayrı veri paketleriyle (tamamen çevrimdışı, MIT) içeriyor; ikinci kütüphane gereksiz. |
| 5 | `DataPayload` domain'de `string` | **Tipli payload'lar** (`InstructionPayload`, `ChatPayload`, `PreferencePayload`) | JSON serileştirme altyapı katmanının işi. Domain tipli kayıtlarla çalışıyor, diskteki JSON biçimi (§4 alan adlarıyla, `$type` ayırıcıyla) testlerle kilitlendi. |
| 6 | `Workspaces` tablosu + `Projects.WorkspaceId` | **Tek satırlık `Workspace` tablosu**, `Projects`'te `WorkspaceId` yok | §3'e göre bir çalışma alanı dosyası = bir çalışma alanı. Dosya ayrıca SQLite başlığında `application_id = 'ADST'` taşıyor; başka SQLite dosyaları açılmaya çalışıldığında reddediliyor. |
| 7 | — | **`ContentHash` ve `Ordinal` kolonları** | SHA-256 içerik özeti birebir kopya tespiti (§8.2-1) ve ileride sürümleme için; `Ordinal` grid ve export sırasını kararlı kılmak için (GUID sıralanamaz). |
| 8 | — | **GUID v7** | Zaman sıralı kimlikler, SQLite B-tree indekslerinde daha iyi yerellik sağlar. |
| 9 | — | **Tarihler UTC Unix milisaniye (INTEGER)** | EF Core'un SQLite sağlayıcısı metin olarak saklanan `DateTimeOffset` üzerinde sıralama/karşılaştırma yapamıyor. |
| 10 | — | **Kapatırken WAL checkpoint + bağlantı havuzunu boşaltma** | WAL modu açıkken dosyanın yanında `-wal`/`-shm` dosyaları oluşur. Kapatınca tek, taşınabilir dosya kalması test ile doğrulanıyor. |
| 11 | — | **Daha yeni sürümün dosyasını açmayı reddetme** | Eski uygulamanın bilmediği bir şemaya yazmasını engeller (ileri uyumluluk). |
| 12 | `ExportReady` durumu | **Durumlar: Draft / InReview / Approved / Archived** | §2.3'teki karar. Export'a hazırlık export profiline göre hesaplanacak (ADR-019). |
| 13 | `.aids` | **`.adstudio`**, `application_id = 'ADST'` | §4'teki karar (ADR-021). Henüz yayımlanmış dosya olmadığı için ilk migration yeniden üretildi. |
| 14 | Arayüz dili belirtilmemiş | **Türkçe + İngilizce, çalışırken değiştirilebilir** | Kaynak dosyaları (`Strings.resx`, `Strings.tr.resx`), `{l:Tr ...}` XAML uzantısı, Görünüm › Dil menüsü, Türkçe-I analizörleri ve testler (Plan §23, ADR-020). |
| 15 | — | **`AIDatasetStudio.Desktop.Tests`** | Çeviri tutarlılığı ve dil değişiminin ekrana yansıdığını doğrulayan headless UI testi. |

---

## 2. Kritik bulgular (kabul edildi)

### 2.1. Sürümleme modeli eski içeriği saklayamıyor (§14.2, §18)
`VersionRecordMappings(VersionId, RecordId, ChangeType)` yalnızca kaydın *değiştiğini* söyler; kayıt tablosu yerinde güncellendiği için "Modified" bir kaydın eski metni kaybolur. Bu durumda dondurulmuş (frozen) bir sürümü yeniden üretmek, diff almak ve rollback yapmak mümkün değil.

**Öneri:** Değişmez, içerik adresli revizyonlar.
- `RecordRevisions(Id, RecordId, ContentHash, DataPayload, Status, CreatedAt)` — her içerik değişikliğinde yeni satır (aynı hash tekrar yazılmaz).
- `VersionRecordMappings(VersionId, RecordId, RevisionId)` — sürüm, her kaydın *hangi revizyonunu* içerdiğini tutar. ChangeType iki sürümün karşılaştırılmasından türetilir.
- Etiketler ve durum da sürüme dahil edilecekse revizyonda veya eşleme tablosunda tutulmalı.

`ContentHash` kolonu bu modele hazırlık olarak eklendi.

**Karar:** Kabul edildi → Plan §14.2, §18, ADR-017. Uygulama V2'de.

### 2.2. Train/Val/Test bölmesi: sıralı karıştırma yerine hash tabanlı atama (§10)
`Random(seed)` ile karıştırıp oranlara göre kesmek, veri setine **tek bir kayıt eklendiğinde** bile pek çok mevcut kaydın train ile test arasında yer değiştirmesine yol açar. Bu da sürümler arasında test verisinin eğitime sızması demektir. Ayrıca `System.Random`'ın seed'li dizisinin .NET sürümleri arasında aynı kalacağı belgelenmiş bir garanti değil.

**Öneri:** `bucket = hash(seed, recordId) mod 10000` ile her kaydı bağımsız olarak bir bölmeye atamak. Böylece mevcut kayıtlar yer değiştirmez, tekrarlanabilirlik platformdan bağımsız olur. Ek olarak, birebir/yakın kopya kümeleri **aynı bölmeye** atanmalı; aksi halde aynı soru hem train hem test'te yer alır. Tabakalı (stratified) dağıtım bu yaklaşımla etiket başına uygulanabilir.

**Karar:** Kabul edildi → Plan §10.1, ADR-018. Uygulama M5'te.

### 2.3. `ExportReady` hem iş akışı durumu hem hesaplanan bir koşul (§5.2)
Tanıma göre ExportReady "token sınırından geçmiş, PII temizliği tamamlanmış" kayıt. Bu, kullanıcının elle verdiği bir karar değil, ayarlara bağlı hesaplanan bir koşul: hedef context uzunluğu veya tokenizer değişince bayatlar.

**Öneri:** Durumlar `Draft → InReview → Approved` (+ `Archived`) olarak kalsın; "export'a hazır" export profiline göre hesaplanan bir filtre/rozet olsun. Şimdilik plana sadık kalındı (5 durum). Uygulanan geçiş tablosu:

| Kaynak | İzin verilen hedefler |
|---|---|
| Draft | InReview, Approved (Ctrl+Enter), Archived |
| InReview | Draft, Approved, Archived |
| Approved | InReview, ExportReady, Archived |
| ExportReady | InReview, Approved, Archived |
| Archived | Draft (geri yükleme) |

Ayrıca §5.2'deki tanıma uygun olarak, içeriği değişen kayıt otomatik olarak `InReview`'a dönüyor; arşivlenmiş kayıt düzenlenemiyor. Diyagramdaki tek yönlü akış ile §6.2 kısayolları (Draft'tan doğrudan Approved) çelişiyordu; tablo bunu çözüyor. Ctrl+Backspace için geçen "Rejected" durumu ise şemada yok; ya eklenmeli ya da metinden çıkarılmalı.

**Karar:** Kabul edildi → `ExportReady` kaldırıldı, "Rejected" yerine `Archived` kullanılıyor (Plan §5.2, ADR-019). Güncel geçişler: Draft → InReview / Approved / Archived; InReview → Draft / Approved / Archived; Approved → InReview / Archived; Archived → Draft.

### 2.4. Klavye kısayolları metin editörüyle çakışıyor (§6.2)
- `Ctrl+Backspace` metin kutularında "önceki kelimeyi sil" demek; `Ctrl+Up/Down` AvaloniaEdit'te paragraf/kaydırma hareketi.
- macOS'ta beklenen değiştirici tuş `Cmd`.

**Öneri:** İnceleme kısayolları yalnızca grid odaktayken çalışsın veya editörde çakışmayan kombinasyonlar seçilsin (ör. `Ctrl+Shift+Enter` / `Ctrl+Shift+Backspace`). Platforma göre değiştirici tuş kullanımı iskelette uygulandı (`Ctrl+N`/`Ctrl+O` macOS'ta `Cmd`).

**Karar:** Kabul edildi → Plan §6.2'deki yeni şema: `Mod+Enter` onayla, `Mod+Shift+Backspace` arşivle, `↑/↓` (grid) ve `F8 / Shift+F8` gezin, `Mod+1..5` etiket, `Mod+Shift+M` PII önizleme, `Mod+Z / Mod+Shift+Z` geri al / yinele. Uygulama M3'te.

---

## 3. Önemli bulgular (kabul edildi; Plan §3, §7–§13, §18'e işlendi)

1. **Şemada eksik/çakışan alanlar (§11.2, §18):**
   - Sorgu örneklerindeki `HealthScore`, `GeneratedByAI`, `AIModelName` şemada yok. Köken bilgisi (lineage) için kolon/tablo tanımlanmamış. → İskelette `SourceKind`, `SourceReference`, `CreatedBy`, `GeneratedByModel` kolonları eklendi.
   - `ExportProfiles` ve `EvaluationSets` hiyerarşide var, şemada yok.
   - `Category` ile `Tags` aynı işi görüyor; `Relation` tablosu ile `RelationType` alanı yinelenmiş. Birer tanesine indirgenmesi öneriliyor.
   - `QualityRuleResults`, sonucun hangi içerik için hesaplandığını bilmeli (`ContentHash`, `RunId`, `CheckedAt`); aksi halde kayıt düzenlendiğinde eski uyarılar ekranda kalır.
2. **Token sayıları tokenizer'a bağlı (§5.1, §7):** `TokenCount` kolonları hangi tokenizer ile hesaplandığını bilmiyor; veri setinin tokenizer'ı değişince hepsi geçersizleşir. Öneri: sayıların yanında `TokenizerId` tutmak veya `RecordTokenStats(RecordId, TokenizerId, ...)` tablosu. Sohbet şablonlarının (ChatML, Llama-3) özel token yükü export formatına göre değişir: "içerik tokenı" saklanmalı, şablon yükü export anında eklenmeli. Rozet eşikleri (2048/4096) sabit değil, seçilen hedef context uzunluğuna göre olmalı.
3. **Veri erişimi ve sanallaştırma (§2.8, §11):**
   - 100 binlik import EF Core change tracker ile yavaş olur. Öneri: CRUD ve migration için EF Core; toplu import/export ve grid sayfalama için hazırlanmış `Microsoft.Data.Sqlite` komutları, tek transaction içinde.
   - Avalonia, *UI* sanallaştırması yapar ama *veri* sanallaştırması yapmaz. Grid, keyset sayfalama ile (Ordinal, Id) sadece hafif projeksiyonları (Id, durum, önizleme, token) yükleyen bir koleksiyona bağlanmalı; tam payload yalnızca seçili kayıt için okunmalı.
   - Specification Pattern, FTS5 `MATCH` ve `json_extract` ile iyi birleşmez. Filtre chiplerini parametreli SQL'e derleyen küçük bir sorgu derleyicisi daha uygun.
4. **FTS5 ve polimorfik JSON (§11.1):** FTS tablosunun beslenmesi için her payload'dan aranabilir metin çıkarılmalı (`SearchText`); external-content FTS5 tablosu trigger veya uygulama tarafından senkron tutulur. Türkçe için `unicode61` tokenizer'ında `İ/ı` büyük-küçük harf dönüşümü kusurlu; `remove_diacritics` "şifre" ile "sifre"yi eşleştirir (istenebilir). Alt dize araması gerekiyorsa `trigram` tokenizer'ı değerlendirilmeli. Türkçe veriyle erken test edilmeli.
5. **Tek dosya taşınabilirliğinin sınırları (§3.1, ADR-001):** Dosya açıkken kopyalanırsa WAL'daki son değişiklikler kaybolabilir; ağ paylaşımlarında (SMB/NFS) WAL desteklenmez. Öneriler: "Farklı Kaydet / Dışa Aktar" için `VACUUM INTO`, ikinci bir uygulama örneğinin aynı dosyayı açmasını engelleyen kilit, ağ sürücüsü uyarısı. Git LFS'te her değişiklik dosyanın tamamını yeni bir blob yapar; Git ile çalışmak için JSONL tabanlı bir "metin olarak dışa aktar" seçeneği daha kullanışlı olabilir.
6. **Planda eksik olan konular:**
   - **Geri al / yinele (Undo/Redo):** Editörde ve toplu işlemlerde ("142 kaydı arşivle") şart.
   - **Kaydetme modeli:** SQLite her yazmayı hemen diske işler. Figma gibi otomatik kayıt + geri alma geçmişi mi, yoksa açık "Kaydet" mi?
   - **Çoklu seçim ve toplu işlemler** (durum, etiket).
   - **Arka plan işleri:** Import, sağlık taraması ve export için ilerleme + iptal; SQLite tek yazar olduğundan yazma kuyruğu.
   - **Yerelleştirme:** Arayüz dili TR/EN; baştan kaynak dosyalarıyla kurulmalı.
   - **Paketleme ve dağıtım:** Windows imzalama, macOS imzalama + notarization, Linux paketleri, güncelleme. Sıfır telemetri ilkesi gereği güncelleme kontrolü isteğe bağlı olmalı.
   - **Test stratejisi:** Exporter'lar için golden-file testleri, bölme determinizmi için özellik tabanlı testler, 100k import için performans ölçümleri.
7. **Export uyumluluk matrisi (§13):** Her veri tipinin hangi formata kayıpsız gidebileceği tanımlanmalı. Örnekler: çok turlu Chat → Alpaca kayıplı; Preference → OpenAI için OpenAI'ın kendi DPO formatı (`input` / `preferred_output` / `non_preferred_output`) var; ChatML / Llama-3 metin şablonları JSONL'de `{"text": "..."}` olarak yazılır. Ortak bir ara model (konuşma + isteğe bağlı tercih çifti) exporter'ları sadeleştirir.
8. **PII kuralları (§9):**
   - E-posta deseni TLD'yi 2–5 harfle sınırlıyor; `.istanbul`, `.technology` gibi uzantıları kaçırır.
   - Kredi kartları 13–19 hane olabilir (Amex 15), yalnızca 16 değil.
   - Eksikler: **TR IBAN** (mod-97 doğrulamalı), JWT, `-----BEGIN PRIVATE KEY-----` blokları, `sk-ant-`, Slack `xox*`, Google `AIza`, bağlantı dizelerindeki parolalar.
   - Aynı varlık için tutarlı yer tutucular (`[EMAIL_1]`, `[EMAIL_2]`) konuşma içi atıfları korur.
   - Maskeleme kaynak veriyi yerinde değiştirirse ADR-004 ("kaynak veri asla bozulmayacak") ile çelişir. Önizleme + export anında maskeleme varsayılan olmalı.
9. **Sağlık skoru formülü tanımsız (§8.1):** 0–100 skorunun ağırlıkları (ciddiyet başına ceza vb.) belirlenmeli. "Çelişki tespiti" V1'de gerçekçi olarak "aynı soru, farklı yanıt" uyarısı şeklinde yapılabilir; anlamsal zıtlık tespiti V3 (LLM-as-a-Judge) işi.
10. **Tokenizer lisansları (§7.2):** Llama-3 ve Gemma tokenizer sözlükleri kendi lisanslarıyla gelir (Llama 3 Community License, Gemma Terms). Uygulamaya gömmek yerine kullanıcının `tokenizer.json` / `tokenizer.model` dosyasını göstermesi daha güvenli olabilir.

---

## 4. Küçük notlar

- **`.aids` uzantısı:** İngilizce konuşan kullanıcılar için hastalık adını çağrıştırıyor. **Karar:** `.adstudio` (ADR-021). Uzantı kodda tek bir sabitte (`WorkspaceFile.Extension`) duruyor.
- **macOS ayar konumu:** Platform konvansiyonu `~/Library/Application Support/AIDatasetStudio`, `~/.config` değil. (Plan §3.1 düzeltildi.)
- **README:** "AvaloniaEdit (Monaco/TextMate)" ifadesi yanıltıcı; AvaloniaEdit Monaco kullanmaz, TextMate gramerlerini `AvaloniaEdit.TextMate` ile destekler. (README güncellendi.)
- **Maliyet hesaplayıcı (§7.3):** "Güncel fiyatlar" çevrimdışı bir uygulamada otomatik çekilemez; tarihli, kullanıcı tarafından düzenlenebilir bir fiyat tablosu olmalı.
- **API anahtar kasası (§19.13):** Windows DPAPI, macOS Keychain, Linux libsecret. Linux'ta libsecret bulunmayabilir (headless/minimal masaüstü); bir `ISecretStore` soyutlaması ve açık bir yedek davranış gerekir.

---

## 5. Önerilen MVP kilometre taşları

| Taşı | İçerik | Durum |
|---|---|---|
| **M0** | Çözüm iskeleti, CI, domain modeli, `.adstudio` oluştur/aç/kapat, üç panelli kabuk, TR/EN arayüz altyapısı | ✅ PR #1 |
| **M1** | Kayıt CRUD servisleri, proje/veri seti ağacı, FTS5 arama (Türkçe I katlaması), keyset sayfalama, toplu ekleme, global ayarlar (kalıcı dil tercihi, son açılanlar), salt okunur kayıt detayı + inceleme aksiyonları, örnek veri üretici | ✅ PR #1 |
| **M2** | Import: JSONL (OpenAI/ShareGPT/Alpaca/DPO otomatik tanıma), akış hâlinde JSON dizisi, CSV sihirbazı (ayraç + kodlama tespiti, Türkçe Excel desteği, sütun eşleme), 20 satır önizleme, atlanan satır raporu, iptal edilebilir tek transaction (ADR-025) | ✅ PR #1 |
| **M3** | Tipine göre düzenlenebilir editör (otomatik kayıt, salt okunur arşiv), §6.2 kısayolları (`Mod+Enter`, `Mod+Shift+Backspace`, `F8`, `Mod+S`, `Mod+F`, `Delete`), çoklu seçim + toplu işlemler, geri al/yinele (ADR-026). Etiket kısayolları etiket arayüzüyle | ✅ PR #2 |
| **M4** | Token motoru (`o200k_base`/`cl100k_base`, veri seti başına bağlam uzunluğu, histogram), 11 kalite kuralı + skor, kayıtlar arası kuralların sorgu anında hesaplanması (ADR-027), veri seti özeti ve temizleme, listede token/kalite sütunları ve filtresi, canlı editör denetimi, kişisel veri tarayıcı + maskelenmiş önizleme (`Mod+Shift+M`) + özel terimler | ✅ PR #3 |
| **M5** | Dışa aktarma sihirbazı: OpenAI JSONL (+ OpenAI DPO), ShareGPT, Alpaca, Hugging Face DPO, ChatML ve Llama 3 metni; durum, bağlam sınırı, kopya ve PII maskeleme seçenekleri, canlı önizleme; soru tabanlı hash bölmesi (80/10/10, 90/10, özel, seed), güvenli yazım, profil ve geçmiş (ADR-028) | ✅ PR #3 |
| **M6** | Velopack ile Windows/macOS/Linux kurulumları ve taşınabilir paketler, isteğe bağlı imzalama + notarization (sırlarla), etiketle yayınlanan GitHub sürümü, isteğe bağlı güncelleme denetimi (varsayılan kapalı), Hakkında penceresi, yerel hata günlüğü, ikon seti, CHANGELOG ve üçüncü taraf bildirimleri; sürüm 1.0.0 (ADR-029, `docs/release.md`) | ✅ PR #4 · yayın `v1.0.0` etiketiyle |
| **M7** | Görsel tasarım sistemi: renk tokenları (açık/koyu), tipografi, ikonlar, bileşenler, karşılama ekranı, boş durumlar, tema tercihi | ✅ PR #1 (öne alındı) |
| **M8** (V2) | Sürümleme: dondurulmuş sürümler (semver, not, tokenizer ve bağlam uzunluğuyla), içerik başına bir kez saklanan revizyonlar, iki durumun karşılaştırılması ve kelime düzeyinde renkli fark, sürüme geri dönüş (geri alınabilir; yeni kayıtlar arşivlenir), sürüm silme ve sürümü dondurulduğu hâliyle dışa aktarma (ADR-030, Plan §14.3) | ✅ PR #5 |
| **M9** (V2) | Etiketler: editörde etiket çipleri ve ara / oluştur kutusu, `Mod+1..5` hızlı etiketleme (etiketin kısayolu, yazarken de), listede etiket hapları ve filtresi (etiketsiz dahil), toplu etiketleme, etiket yöneticisi (ad, palet rengi, kısayol, silme), etiketlemenin geri alınması, sürümlerde etiket farkı ve etiketlerin geri dönüşte geri gelmesi, etikete / etiket grubuna göre tabakalı bölme (ADR-031, Plan §6, §10.1) | ✅ PR #5 |
| **M10** (V2) | Benzer sorular: karakter 5-gramlarıyla MinHash + LSH aday bulma, tam Jaccard ile kesin %85 eşiği, geçişli gruplar; kural, filtre, editörde ilk benzer kaydın numarası, özetten temizleme (her grubun ilki kalır), aynı bölmeye düşürme ve sürümde donmuş gruplar. Tercih (DPO) editöründe tercih edilen ↔ reddedilen kelime farkı (ADR-032, Plan §8.2, §6.1) | ✅ PR #5 |
| **M11** (V2) | Excel (.xlsx/.xlsm) kendi akış okuyucusuyla: etkin sayfa, başka bir sayfa ya da tüm sayfalar (sütunlar adla birleşir), tarih biçimleri, sayfa + satır kökeni, `.xls`/parolalı kitap açıklaması. Markdown başlıklara göre bölümler (başlık, üst başlıklar, metin), düz metin paragraflar; `.txt` için tablo ↔ metin seçimi. Editör alanları AvaloniaEdit: Markdown, çitli kod blokları (JSON, Python, JS/TS, C#, SQL, kabuk, XML) ve JSON belgeleri tema tokenlarıyla renklenir, kod eş aralıklı (ADR-033, Plan §12.2, §6.1) | ✅ PR #5 |

| **M12** (V3) | Yerel model bağlantıları: Ollama, LM Studio, vLLM, LocalAI ve diğer OpenAI uyumlu sunucular. Araçlar › Yapay Zekâ Bağlantıları'nda sunucudan model listesi, deneme, varsayılan bağlantı ve uzak adres için açık onay. Bağlantılar kullanıcı ayarlarındadır. Editörde yapay zekâyla yazma (`Mod+G`): Çıktı, sohbet yanıtı, tercih edilen ve "daha kötü" reddedilen yanıt; akışla yazma, durdurma, hata açıklaması. Model adı kökene yazılır, geri alınabilir. Veri setine özel talimatlar genel bakışta (ADR-034, Plan §17.4) | ✅ PR #5 |
| **M13** (V3) | PDF (PdfPig) ve Word (.docx, kendi okuyucusu) içe aktarma: başlıklar, listeler, tablolar; üst/alt bilgi, sayfa numarası ve içindekiler ayıklanır. Dosya › Belgeden Soru-Cevap Çıkar: başlıkları izleyen parçalama, model ile parça parça soru-cevap (akışla, durdurma, yeniden yazma), inceleme ve düzenleme. Eklenen kayıtlar Taslak, `DocumentChunk` kökenli ve kaynak parçalarıyla; ekleme tek adımda geri alınır, editörde "Kaynak metin" (ADR-035, Plan §12.3, §17.5) | ✅ PR #5 |
| **M14** (V3) | Sentetik çeşitlendirme: Kayıt › Varyant Üret (`Mod+Shift+G`), editör başlığı ve toplu işlem paneli. Kayıt başına 1–10 varyant; yalnızca soru ya da soru ve yanıt; günlük dil ve yazım hataları seçeneği. Akışla yazma, durdurma, yeniden yazma; asıl soruyla aynı varyant atılır, %85 ve üzeri benzeyen işaretsiz gelir. Eklenen kayıtlar Taslak ve `Synthetic` kökenli; ailenin ilk kaydı meta veride (`variantOf`), kaynak parça korunur. Aile dışa aktarmada aynı dosyaya düşer ve kendi içinde benzer soru sayılmaz; ekleme tek adımda geri alınır (ADR-036, Plan §17.6) | ✅ PR #5 |
| **M15** (V3) | LLM-as-a-Judge: Kayıt › Modelle Değerlendir (`Mod+J`), editördeki MODEL DEĞERLENDİRMESİ bölümü, toplu işlem paneli ve özetteki kart. Model kaydı 1–5 arası puanlar, gerekçeyi uygulamanın dilinde yazar; belgeden gelen yanıtı kaynak parçayla, tercih çiftinin sırasını da denetler (sıcaklık 0). Aynı ya da benzer soruyu soran veya aynı aileden en çok 3 kayıtla anlamsal çelişki denetimi. Değerlendirmeler içerikle birlikte saklanır, kayıt değişince eski sayılır; listede puan rozeti, kalite filtresinde düşük puan / değerlendirilmemiş / kaynakta olmayan / şüpheli tercih / çelişen yanıtlar; özette ortalama ve dağılım (ADR-037, Plan §17.7) | ✅ PR #5 |
| **M16** (V3) | Anlamca benzer sorular ("Parolamı unuttum" / "Şifremi nasıl sıfırlarım?"): AI bağlantısına isteğe bağlı embedding modeli (ör. `bge-m3`), veri seti özetinde ANLAMCA BENZER SORULAR kartı (karşılaştır, kalanları karşılaştır, eşik 0,80–0,98, kapat, durdur). Vektörler yalnızca kullanıcı başlattığında, farklı soru başına bir kez yapılır ve bayt olarak çalışma alanında saklanır; SimHash imzalarıyla aday, tam kosinüsle karar. Anlamca benzer çiftler MinHash'in benzer soru gruplarına katılır: kural, filtre, temizleme, bölme, sürümler ve çelişki adayları aynen çalışır; aile içi sayılmaz (ADR-038, Plan §17.8) | ✅ PR #5 |
| **M17** (V3) | HTML sayfaları (`.html`, `.htm`) ve sayfa arşivleri (`.zip`): bağımlılıksız hoşgörülü HTML okuyucusu (başlık, paragraf, iç içe madde, tablo, `pre`; betik, stil, gezinme, form, gizli öğeler ve site başlığı/altlığı atılır; `charset`), Confluence alan dışa aktarımı (yol, meta veri, ekler, altlık ve sayfa ağacı atılır; "Alan : " öneki) ve Notion Markdown/HTML dışa aktarımı (iç ZIP parçaları, 32 haneli kimlikler, özellik tablosu). Arşiv tek belgedir: sayfalar yola göre sıralanır, her biri başlığıyla başlar, konum sayfa numarasıdır (`Export.zip#page=2`); ZIP bombasına karşı sınırlar. İçe aktarma ve soru-cevap çıkarımı değişmeden çalışır (ADR-039, Plan §12.3) | ✅ PR #5 |

> Not: V3 (Plan §21) beş aşamaya bölündü. Model bağlantısı diğer dört aşamanın ön koşulu olduğu için ilk sırada. Notion/Confluence/HTML kaynakları M17'de, ağ erişimi gerektirmeyen dışa aktarım dosyalarıyla eklendi. Bulut sağlayıcıları ve müşteri sohbeti ayrıştırma sonraya kaldı.

> Not: V2 (Plan §21) dört aşamaya bölündü; Llama 3 ve Hugging Face DPO şablonları M5'te yapıldığı için V2'de yer almıyor. Sıralama kullanıcıyla belirlendi: önce sürümleme, çünkü 1.0.0'dan sonra yapılan düzenlemelerin eski hâli ancak bir sürümde tutulabiliyor.

> Not: Tasarım, kullanıcının isteğiyle sona bırakılmak yerine M1'in hemen ardından yapıldı. Kurallar `docs/design-system.md` dosyasında; M2–M6'daki yeni ekranlar bu sisteme göre yapılacak.
