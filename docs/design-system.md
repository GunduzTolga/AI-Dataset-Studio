# Tasarım Sistemi

AI Dataset Studio arayüzünün renk, tipografi, boşluk ve bileşen kuralları. Kod karşılıkları:

| Dosya | İçerik |
|---|---|
| `src/AIDatasetStudio.Desktop/Themes/Tokens.axaml` | Renk, tipografi ve köşe yarıçapı tokenları (açık + koyu tema) |
| `src/AIDatasetStudio.Desktop/Themes/Controls.axaml` | Bileşen stilleri (düğmeler, girişler, çipler, rozetler, kartlar, liste satırları, ağaç) |
| `src/AIDatasetStudio.Desktop/Controls/StatusBadge.cs` | Kayıt durumu rozeti |
| `src/AIDatasetStudio.Desktop/Converters/IconConverters.cs` | Kavram → ikon eşlemesi |

Temel: Avalonia'nın **Fluent** teması (`DensityStyle="Compact"`, indigo vurgu paleti). Tasarım sistemi Fluent'ı değiştirmez, üzerine renk, yarıçap ve durum ayarları ekler. Böylece klavye ve erişilebilirlik davranışı Fluent'ta olduğu gibi kalır.

---

## 1. İlkeler

1. **İçerik önce gelir.** Arayüz sakin ve nötrdür; dikkati veri çeker. Renk yalnızca anlam taşıdığı yerde kullanılır: durum, seçim, birincil aksiyon ve tehlike.
2. **Her renk bir token'dır.** Bileşenler ham renk kullanmaz, `{DynamicResource ...Brush}` kullanır. Tema değişince her şey birlikte değişir.
3. **İki tema eşittir.** Açık ve koyu tema aynı tokenlarla tanımlanır; ikisi de WCAG AA'yı (metin/zemin ≥ 4,5:1) karşılar.
4. **Sadece geçerli aksiyonlar görünür.** Örneğin onaylanmış bir kayıtta "Onayla" düğmesi devre dışı bırakılmak yerine gizlenir.
5. **Yıkıcı aksiyonlar kırmızıdır ve onay ister.** Silme işlemleri `danger` düğmeli bir onay diyaloğundan geçer.
6. **Her metin iki dildedir.** Arayüz metinleri yalnızca `Strings.resx` ve `Strings.tr.resx` dosyalarındadır (bkz. Plan §23).

---

## 2. Renk tokenları

| Token | Açık | Koyu | Kullanım |
|---|---|---|---|
| `AppBackgroundBrush` | `#F6F6F7` | `#121215` | Pencere, kenar çubuğu, menü, durum çubuğu |
| `SurfaceBrush` | `#FFFFFF` | `#18181B` | Kayıt listesi, girişler, diyaloglar |
| `InspectorBackgroundBrush` | `#FAFAFB` | `#141417` | Sağ panel |
| `CardBrush` | `#FFFFFF` | `#1D1D21` | Kartlar, karşılama kutucukları |
| `SurfaceHoverBrush` / `SurfacePressedBrush` | `#F0F0F3` / `#E7E7EB` | `#232328` / `#2B2B31` | Üzerine gelme / basma |
| `SubtleFillBrush` | `#F4F4F5` | `#222226` | Etiketler, boş durum ikonları, sistem kartları |
| `BorderBrush` / `BorderStrongBrush` | `#E6E6EA` / `#D4D4DA` | `#2A2A30` / `#3A3A42` | Ayırıcılar / giriş ve düğme kenarları |
| `TextPrimaryBrush` | `#18181B` | `#F4F4F5` | Ana metin |
| `TextSecondaryBrush` | `#52525B` | `#C4C4CC` | İkincil metin |
| `TextMutedBrush` | `#63636B` | `#9C9CA6` | Açıklama, sayaç, başlık etiketleri |
| `AccentBrush` (+Hover/Pressed) | `#4F46E5` | `#5B54E8` | Birincil düğme, seçim çubuğu, odak kenarı |
| `AccentSubtleBrush` | `#EEF0FF` | `#25264A` | Seçili satır/düğüm/çip zemini, kullanıcı balonu |
| `AccentTextBrush` | `#4338CA` | `#A5B4FC` | Seçili öğe metni |
| `DangerBrush` / `DangerTextBrush` | `#DC2626` / `#B91C1C` | `#DC2626` / `#FCA5A5` | Silme, hata mesajları |
| `DangerSubtleBrush` | `#FEE2E2` | `#3F1720` | Hata bildirimi zemini |
| `WarningSubtleBrush` / `WarningTextBrush` | `#FEF3C7` / `#92400E` | `#3A2C0F` / `#FCD34D` | Uyarı bildirimi (ör. eşlenmemiş zorunlu alan) |
| `PositiveBrush` / `NegativeBrush` | `#059669` / `#E11D48` | `#34D399` / `#FB7185` | Tercih edilen / reddedilen yanıt |
| `DiffInsertedBrush` / `DiffDeletedBrush` | `#BBF7D0` / `#FECDD3` | `#1D5236` / `#62202E` | Sürüm farkında eklenen / silinen kelimelerin zemini (üstünde ana metin rengi) |
| `BrandGradientBrush` | `#6366F1 → #9333EA` | aynı | Uygulama simgesi |

### Kayıt durumu renkleri

| Durum | Açık (zemin / yazı) | Koyu (zemin / yazı) | Anlam |
|---|---|---|---|
| Taslak | `#F1F1F3` / `#3F3F46` | `#27272A` / `#D4D4D8` | Nötr: henüz bakılmadı |
| İncelemede | `#FEF3C7` / `#92400E` | `#3A2C0F` / `#FCD34D` | Amber: dikkat bekliyor |
| Onaylandı | `#D1FAE5` / `#065F46` | `#0E3A2C` / `#6EE7B7` | Yeşil: eğitime girebilir |
| Arşivlendi | `#FFE4E6` / `#9F1239` | `#3F1720` / `#FDA4AF` | Gül: eğitimden çıkarıldı |

Kullanılan tüm metin/zemin çiftlerinin kontrastı 4,5:1'in üzerindedir. En düşük değer kırmızı silme düğmesindeki beyaz yazıda ≈ 4,8:1; soluk metinde en düşük değer, üzerine gelinmiş satırda ≈ 5,2:1.

---

## 3. Tipografi

Yazı tipi **Inter** (uygulamayla birlikte gelir). Sayı sütunlarında tablo rakamları (`.numeric` → `tnum`) kullanılır; içerik özetinde eş aralıklı yazı tipi (`.mono`).

| Sınıf | Boyut / ağırlık | Kullanım |
|---|---|---|
| `.display` | 28 / Bold | Karşılama ekranı başlığı |
| `.title` | 20 / SemiBold | Veri seti adı, kayıt numarası |
| `.subtitle` | 15 / SemiBold | Boş durum ve diyalog başlıkları |
| (varsayılan) | 13 / Regular | Gövde metni |
| `.small` | 12 | Açıklamalar, durum çubuğu |
| `.caption` | 11 | Sayaçlar, rol etiketleri |
| `.overline` | 11 / SemiBold, harf aralığı 0,8, soluk | Bölüm başlıkları ("PROJELER", "BİLGİLER") |
| `.strong`, `.secondary`, `.muted` | — | Ağırlık ve renk düzenleyicileri |
| `.content` | satır yüksekliği 20, kaydırmalı | Kayıt içeriği |

Büyük harfli başlıklar kaynak dosyasında büyük harfle yazılır ("BİLGİLER"); kodda `ToUpper` kullanılmaz (Türkçe İ/ı).

---

## 4. Boşluk, yarıçap, yoğunluk

- **4 px ızgarası:** boşluklar 4, 6, 8, 12, 16, 20, 24, 28.
- **Yarıçap:** `RadiusSmall` 4, `RadiusMedium` 6 (düğme, giriş, ağaç düğümü), `RadiusLarge` 10 (kart, kutucuk), `RadiusPill` (rozet, çip).
- **Yoğunluk:** Fluent `Compact`; satır yüksekliği yaklaşık 36 px, düğmeler 30 px.
- **Panel ölçüleri:** kenar çubuğu 272 px, liste esnek, inceleme paneli 420 px. Paneller arasındaki 5 px'lik görünmez ayırıcılarla genişlikler değiştirilebilir.

---

## 5. İkonlar

**Fluent UI System Icons** ([FluentIcons.Avalonia](https://github.com/davidxuang/FluentIcons), MIT). Varsayılan stil `Regular`; yalnızca marka simgesinde `Filled` kullanılır. Boyutlar: menü ve düğmelerde 16, durum çubuğunda 14, başlık rozetlerinde 20, boş durumlarda 24.

| Kavram | İkon |
|---|---|
| Uygulama / marka | `Sparkle` (dolu, gradyan zemin); uygulama ikonları (`Assets/app.ico`, `.icns`, `.png`) aynı işaretten `build/make-icon.cs` ile üretilir: 1024 px tuvalde %6 boşluk, köşe yarıçapı karenin %27'si, simge %56 |
| Güncellemeleri denetle / Hakkında | `ArrowSync` / `Info` |
| Proje | `Folder` · yeni: `FolderAdd` |
| Veri seti: Talimat / Sohbet / Tercih | `TextBulletListSquare` / `ChatMultiple` / `ThumbLike` · yeni: `DocumentAdd` |
| Kullanıcı / Asistan / Sistem mesajı | `Person` / `BotSparkle` / `Settings` |
| Tercih edilen / Reddedilen | `ThumbLike` / `ThumbDislike` |
| Onayla / İncelemeye al / Arşivle / Geri yükle / Sil | `Checkmark` / `Eye` / `Archive` / `ArrowUndo` / `Delete` |
| Ara | `Search` |
| Aç / Son açılanlar / Kapat | `FolderOpen` / `History` / `Dismiss` |
| Tema / Dil | `DarkTheme` / `LocalLanguage` |
| İçe aktar / Kaynak dosya | `ArrowImport` / `DocumentTable` |
| Dışa aktar / Yazılan dosya / Son dışa aktarım | `ArrowExport` / `DocumentText` / `History` |
| Dışarıda bırakılan kayıt | `SubtractCircle` (soluk) |
| Geçerli / Atlanacak satır | `CheckmarkCircle` (olumlu renk) / `ErrorCircle` (hata rengi) |
| Bilgi / Uyarı / Hata bildirimi | `Info` (konuşma biçiminde `Chat`) / `Warning` / `ErrorCircle`, `DismissCircle` |
| Kaydet / Kaydedilmemiş değişiklik / Salt okunur | `Save` / `DocumentEdit` / `LockClosed` |
| Geri al / Yinele | `ArrowUndo` / `ArrowRedo` |
| Önceki / Sonraki kayıt | `ArrowUp` / `ArrowDown` |
| Mesaj ekle / Rolü çevir / Mesajı kaldır | `Add` / `ArrowSwap` / `Dismiss` |
| Çoklu seçim | `Multiselect` |
| Gizlilik / Maskelenmiş önizleme | `ShieldLock` · özel terimler: `ShieldKeyhole` |
| Token sayısı / Kalite skoru | `TextWordCount` / `HeartPulse` |
| Bağlamı aşan kayıt | `Flag` (hata rengi) |
| Sorun: hata / uyarı / yok | `ErrorCircle` / `Warning` / `CheckmarkCircle` (olumlu renk) |

Yeni bir kavram ikonu `IconConverters` içine eklenir; görünümlerde sabit `Symbol` yalnızca tekil kullanımlar içindir.

---

## 6. Bileşenler

### Düğmeler
| Sınıf | Görünüm | Ne zaman |
|---|---|---|
| (varsayılan) | Yüzey zemin, ince kenar | İkincil aksiyonlar (Arşivle, İncelemeye al) |
| `accent` | İndigo dolgu, beyaz yazı | Ekrandaki tek birincil aksiyon (Onayla, Örnek kayıt üret) |
| `subtle` | Zeminsiz, üzerine gelince dolgu | Araç çubukları, liste benzeri düğmeler |
| `danger` | Kırmızı dolgu | Silme onayı |
| `danger-subtle` | Zeminsiz kırmızı ikon | Panel içi sil düğmesi (onay ister) |
| `icon` | 30×30 kare | Yalnızca ikon; her zaman `ToolTip.Tip` ile |
| `tile` | Büyük kart, üzerine gelince vurgu kenarı | Karşılama ekranındaki ana seçenekler |

İkon + metin: `StackPanel Orientation="Horizontal" Spacing="6"` içinde `SymbolIcon` + `TextBlock`.

### Girişler
- `TextBox`: yüzey zemin, odakta 1,5 px vurgu kenarı.
- `TextBox.search`: soldaki `Search` ikonu (`InnerLeftContent`) ve Fluent'ın temizleme düğmesi (`clearButton`).

### Seçenek kartları
`ListBox.options` + `Border.option`: birkaç açıklamalı seçenekten biri (dışa aktarma biçimi). Kart: kalın ad, altında soluk tek satır biçim örneği (`mono`), gerekirse amber uyarı satırı. Üzerine gelince güçlü kenar; seçiliyken vurgu zemini, vurgu kenarı ve vurgu renkli ad. `SelectionMode="AlwaysSelected"`.

### Çipler (durum filtresi)
`ListBox.chips` + `Border.chip`: yatay, hap biçiminde; etiket ve soluk sayaç. Seçili çip vurgu zemini, vurgu kenarı ve vurgu metniyle gösterilir. `SelectionMode="AlwaysSelected"`.

### Rozetler
- `controls:StatusBadge`: duruma göre renklenen hap (`:draft`, `:review`, `:approved`, `:archived`).
- `Border.tag`: nötr hap (veri seti tipi).
- `Border.kind`: sürüm farkının türü. `.added` onaylı yeşili, `.removed` arşiv kırmızısı, `.modified` inceleme amberi kullanır; durum değişikliği nötrdür. Alan kartlarında "Değişti / Eklendi / Kaldırıldı" rozeti `.modified` ile gösterilir.

### Fark metni
`controls:DiffTextBlock`: seçilebilir metin. Silinen kısımlar `DiffDeletedBrush`, eklenenler `DiffInsertedBrush` zeminle, satır içinde gösterilir. Değişen alan iki sütundur: solda eski metin ve silinenler, sağda yeni metin ve eklenenler. Değişmeyen alan tek sütun ve ikincil renktir. Boş alan "(boş)" yazar. Tema değişince renkler yeniden uygulanır.

### Kayıt satırı
`ListBox.records` + `Border.row`: alt ayırıcı çizgi, `16,9` iç boşluk; kolonlar `#` (64) · durum (116) · içerik · token (76, sağa yaslı; bağlamı aşınca `Flag` + hata rengi) · kalite (64, `Border.score`). Üzerine gelince hafif dolgu; seçiliyken vurgu zemini ve solda 3 px vurgu çubuğu (`Border.row-indicator`).

### Skor ve token hapları
`Border.score`: hap biçiminde, küçük yarı kalın yazı; analizden önce nötr ("—"). `.good` (90–100, onaylı yeşili), `.fair` (60–89, inceleme amberi), `.poor` (< 60, arşiv kırmızısı); token rozetinde aynı renkler bağlam doluluğunu gösterir (< %50 / ≤ %100 / aşıyor). İçinde isteğe bağlı 13 px ikon. `Border.score.large`: 64×64, `RadiusLarge`, `FontSizeDisplay` rakam — veri seti özetindeki skor kutusu.

### Histogram
Satır: kova etiketi (78 px, soluk tablo rakamı) · 200 px `Border.bar-track` içinde `Border.bar` (vurgu rengi; bağlamın üstündeki kovalar `.over` ile hata rengi; genişlik `LayoutConverters.BarWidth`, sıfır olmayan pay en az 2 px) · sağa yaslı sayı.

### Gezinme ağacı
`TreeView.nav`: 32 px düğümler, 6 px yuvarlatılmış seçim, seçili düğümde vurgu metni. Proje düğümü kalın yazılır; veri seti düğümünde tip ikonu, ad ve sağda tablo rakamlarıyla kayıt sayısı bulunur.

### Kartlar ve balonlar
- `Border.card`: kayıt alanları (Talimat, Girdi, Çıktı…). `.positive` / `.negative` kenar ve başlık rengiyle tercih edilen / reddedilen yanıtı, `.system` soluk zeminle sistem talimatını gösterir.
- `Border.bubble.user` (sağa yaslı, vurgu zemin) ve `Border.bubble.assistant` (sola yaslı, kenarlı): sohbet turları. En fazla 440 px genişlik.

### Boş durumlar
`StackPanel.empty`: dairesel ikon (`Border.empty-icon`), başlık (`.subtitle`), açıklama (`.muted`) ve varsa bir `accent` aksiyon (yanında en fazla bir `subtle` alternatif; ör. "Dosyadan içe aktar" + "Örnek kayıt üret"). Her boş ekran ne olduğunu ve bir sonraki adımı söyler.

### Editör alanları
`TextBox.field`: kendi kutusu olmayan, kartın (`Border.card`) veya balonun (`Border.bubble`) içine yerleşen çok satırlı metin alanı; boşken "Boş" yer tutucusu gösterir. Odak, kabın etrafında 3 px yarı saydam indigo halkayla (`BoxShadow`, `:focus-within`) gösterilir; böylece tercih edilen/reddedilen kartların yeşil/kırmızı kenarı korunur. Sohbet turlarının başlığında rol ikonu ve etiketinin yanında 22 px'lik `Button.mini` düğmeleri (rolü çevir, kaldır) bulunur. Kaydedilmemiş değişiklikler başlığın altında bilgi bildirimi + "Vazgeç" (`subtle`) + "Kaydet" (`accent`) olarak gösterilir; arşivlenmiş kayıtta uyarı bildirimi ve salt okunur alanlar vardır.

### Toplu panel
Birden fazla kayıt seçilince sağ panel: ikon kutusu + "N kayıt seçili" başlığı, durum rozetleri ve sayıları, uygun aksiyonlar (en az bir seçili kayda uygulanabilenler) ve kısa açıklama. Silme metinli `danger-subtle` düğmedir.

### Veri seti özeti
Hiçbir kayıt seçili değilken sağ panel: "VERİ SETİ ÖZETİ" üst başlığı ve veri seti adı · skor kartı (büyük skor kutusu, "Kalite skoru", "x / y kayıt analiz edildi", analiz sürerken ilerleme çubuğu) · TOKENLAR kartı (tokenizer 3* ve bağlam uzunluğu 2* açılır listeleri, üç istatistik: toplam / en uzun / sınırı aşan, histogram) · SORUNLAR kartı (`ItemsControl.lines`: ciddiyet ikonu, kural adı + soluk sayı, sağda "Göster" `subtle` ve uygun kurallarda "Arşivle"; sorun yoksa olumlu onay satırı) · altta seçme ipucu.

### Kalite bölümü ve maskelenmiş önizleme
Editörde içerikten sonra, BİLGİLER'den önce "KALİTE" başlığı (sağda skor hapı) ve bir kart: sorunlar ciddiyete göre sıralı, ikon + kaydırmalı metin; sorun yoksa `CheckmarkCircle` + "Sorun bulunamadı". Maskelenmiş önizlemede başlıktaki `ShieldLock` düğmesi `accent` olur; altında bir bildirim (bulgu varsa `.warning`): başlık, "e-posta adresi: 1 · telefon numarası: 1" özeti ve ikinci satırda sağa yaslı "Kapat" (`subtle`) + "Maskelemeyi Uygula" (`accent`). Alanlar yerine salt okunur kartlar (`SelectableTextBlock`) gösterilir; yer tutucular `[EMAIL_1]` biçimindedir.

### Bildirimler (callout)
`Border.callout`: ikon + metin, `RadiusMedium`, `12,10` iç boşluk. Varsayılan bilgi (vurgu zemin); `.warning` (amber) devam etmeyi engellemeyen sorunlar, `.danger` (kırmızı) devam edilemeyen durumlar için. İçinde `DockPanel`: solda `SymbolIcon`, kalan alanda kaydırmalı `TextBlock`.

### Form alanları
Etiket `TextBlock.label` (küçük, yarı kalın, ikincil renk) kontrolün üstünde durur; zorunlu alanın yanında hata renginde `*` ve "Zorunlu" ipucu bulunur. Açılır listeler genişliği doldurur.

### İlerleme ve listeler
- `ProgressBar`: vurgu rengi, hap biçimi, 6 px. Belirsiz süreler için `IsIndeterminate`.
- `ItemsControl.lines`: seçimsiz, alt çizgiyle ayrılmış satırlar (önizleme, atlanan satırlar). Satır: numara (tablo rakamları) · durum ikonu · birincil metin + soluk ikincil metin, tek satırda kırpılır.

---

## 7. Ekranlar

| Ekran | Düzen |
|---|---|
| Karşılama (çalışma alanı yok) | Marka simgesi, başlık, slogan; "Yeni çalışma alanı" ve "Çalışma alanı aç" kutucukları; son açılanlar; gizlilik notu |
| Çalışma alanı | Sol: çalışma alanı başlığı + "PROJELER" + ağaç · Orta: veri seti başlığı (alt satırda son sürüm, analiz sürerken küçük ilerleme; sağda "İçe aktar", "Dışa aktar" ve "Sürümler"), arama + kalite filtresi, durum çipleri, kolon başlıkları, kayıt listesi · Sağ: seçim yokken veri seti özeti; kayıtta başlık + durum + token ve skor hapları, aksiyonlar, içerik kartları/balonları, "KALİTE", "BİLGİLER" |
| Sürümler | 1220×760, yeniden boyutlandırılabilir.<br>• **Üst:** `History` ikon kutusu, başlık, veri seti.<br>• **Sol (270 px, uygulama zemini):** `ListBox.records` içinde önce çalışma kopyası (`Edit` ikonu), sonra en yeniden eskiye sürümler (`Tag` ikonu, "v1.1.0", tarih, sayılar, not).<br>• **Sağ üst:** seçilen öğenin adı ve ayrıntısı. Sağda eylemler: çalışma kopyasında "Sürüm Oluştur" (`accent`); sürümde "Dışa aktar", "Bu Sürüme Dön" (`subtle`) ve sil (`danger-subtle`). Altında "Karşılaştır" açılır listesi ve yanında "v1.0.0 → v1.1.0" ile sayılar.<br>• **Gövde:** farklar (340 px; tür çipleri ve satırlar: numara, `Border.kind`, durum geçişi, önizleme) · ayırıcı · seçilen kaydın alan kartları (sütun başlıkları eski / yeni).<br>• Sürüm yokken ve fark yokken boş durum gösterilir.<br>• **Alt çubuk:** ilerleme / sonuç bildirimi + Kapat |
| Yeni sürüm | 480 px.<br>• Başlık (`BookmarkAdd` ikonu) ve veri seti.<br>• Sürüm numarası: kutuda "v" ön eki; altında "Yama · Küçük · Büyük" öneri düğmeleri ve doğrulama uyarısı.<br>• Not (çok satırlı).<br>• Dondurmayı anlatan bilgi bildirimi (`LockClosed`).<br>• İptal + "Sürüm Oluştur" |
| Hakkında | 460 px; marka işareti (56 px) + ad + sürüm · kısa tanım · gizlilik bildirimi (`ShieldLock`, telemetri yok) · ayar ve günlük klasörü (`mono`, seçilebilir) · "Günlük klasörünü aç" ve "Üçüncü taraf bildirimleri" (`subtle`) · telif + Kapat (`accent`) |
| Diyaloglar | 460 px; başlık, açıklama, giriş, sağa yaslı İptal (`subtle`) + onay (`accent` veya `danger`) |
| Kayıt editörü (sağ panel) | Başlık + durum rozeti + token ve skor hapları, duruma uygun aksiyonlar ve sağda maskelenmiş önizleme + sil · maskelenmiş önizleme bildirimi · kaydedilmemiş değişiklik çubuğu · alan kartları veya sohbet balonları (düzenlenebilir) · "Mesaj ekle" · KALİTE · BİLGİLER. Çoklu seçimde toplu panel |
| Menüler | Dosya (… İçe aktar, Dışa aktar `Mod+E`, Sürüm oluştur, Sürümler …) · Düzen (Geri al/Yinele: son adımın adıyla, Kayıtlarda ara) · Görünüm · Kayıt (Kaydet, Onayla ve sonrakine geç, Arşivle ve sonrakine geç, Sonraki/Önceki, Maskelenmiş önizleme, Sil) · Araçlar (Örnek kayıt üret, Özel hassas terimler) · Yardım (Güncellemeleri denetle, Açılışta denetle onay işareti, Hakkında); kısayollar menüde gösterilir |
| Dışa aktarma sihirbazı | 1040×720, içe aktarma sihirbazıyla aynı iskelet. Sol (360 px): veri setinin sürümü varsa KAYNAK (güncel kayıtlar / sürüm açılır listesi, sürümde dondurmayı anlatan not) · BİÇİM seçenek kartları · KAYITLAR (durum onay kutuları, bağlamı aşanları/kopyaları atla, kişisel verileri maskele) · BÖLME (ön ayar açılır listesi, özel oranlar için üç sayı kutusu, seed ve açıklaması) · Sağ: ÖNİZLEME kartı (büyük sayı + "kayıt dışa aktarılacak", bölmede "eğitim ≈ … · doğrulama ≈ …", dışarıda kalanlar `SubtractCircle` satırlarıyla, onaylı kayıt yoksa bilgi bildirimi) · İLK SATIRLAR (3 satır, `mono` kartlar) · klasörde dosya varsa üstte uyarı bildirimi + "Başka klasör seç" / "Üzerine yaz" · Alt çubuk: son dışa aktarım + İptal + "N kaydı dışa aktar"; sonuç ekranında yazılan dosyalar (ad, klasör, kayıt sayısı) |
| İçe aktarma sihirbazı | 1040×700, yeniden boyutlandırılabilir. Üst: ikon kutusu, başlık, hedef veri seti · Sol (340 px, uygulama zemini): KAYNAK kartı (dosya, satır sayısı, biçim ve düzen etiketleri), bildirimler, CSV AYARLARI, SÜTUN EŞLEME · Sağ: ÖNİZLEME (ilk 20 satır) · Alt çubuk: adıma göre özet + İptal + "N satırı içe aktar" (`accent`), aktarımda ilerleme çubuğu + İptal, bitince sonuç ekranı ve Kapat |

**Tercihler:** Görünüm › Tema (Sistem / Açık / Koyu) ve Görünüm › Dil (English / Türkçe). İkisi de `app.settings.db` dosyasında saklanır ve yeniden başlatmadan uygulanır.
