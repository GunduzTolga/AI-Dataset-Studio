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
| **M3** | Sanallaştırılmış grid, tipine göre dinamik editör, inceleme kısayolları, geri al/yinele | |
| **M4** | Token motoru (`cl100k_base`/`o200k_base`), temel sağlık kuralları, PII maskeleme önizlemesi | |
| **M5** | Export (OpenAI JSONL, ShareGPT, Alpaca) + hash tabanlı Train/Val/Test bölmesi | |
| **M6** | Paketleme, imzalama, ilk sürüm | |
| **M7** | Görsel tasarım sistemi: renk tokenları (açık/koyu), tipografi, ikonlar, bileşenler, karşılama ekranı, boş durumlar, tema tercihi | ✅ PR #1 (öne alındı) |

> Not: Tasarım, kullanıcının isteğiyle sona bırakılmak yerine M1'in hemen ardından yapıldı. Kurallar `docs/design-system.md` dosyasında; M2–M6'daki yeni ekranlar bu sisteme göre yapılacak.
