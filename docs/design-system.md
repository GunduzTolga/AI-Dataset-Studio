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

### Sözdizimi renkleri (editör alanları)

`Color` tokenları; `controls:CodeHighlighting` bunları editörün `.xshd` tanımlarındaki renk adlarına uygular. Her iki temada kartlar, sistem talimatı kartı ve kullanıcı balonu üzerinde kontrast 4,5:1'in üzerindedir (en düşük: açık temada bağlantı rengi, kullanıcı balonunda ≈ 4,56:1).

| Token | Açık | Koyu | Kullanım |
|---|---|---|---|
| `SyntaxCommentColor` | `#4B5563` | `#9CA3AF` | Kod yorumları (italik), Markdown alıntıları (italik), kod çitleri |
| `SyntaxStringColor` | `#047857` | `#6EE7B7` | Metin değerleri |
| `SyntaxKeywordColor` | `#7C3AED` | `#C4B5FD` | Anahtar sözcükler |
| `SyntaxNumberColor` | `#92400E` | `#FCD34D` | Sayılar, XML öznitelikleri, kabuk seçenekleri |
| `SyntaxLiteralColor` | `#C2410C` | `#FDBA74` | `true` / `false` / `null`, XML varlıkları |
| `SyntaxPropertyColor` | `#1D4ED8` | `#93C5FD` | JSON özellik adları, XML etiketleri |
| `SyntaxFunctionColor` | `#0E7490` | `#67E8F9` | Fonksiyon çağrıları, Python dekoratörleri |
| `SyntaxVariableColor` | `#BE185D` | `#F9A8D4` | Kabuk değişkenleri |
| `SyntaxHeadingColor` | `#4338CA` | `#A5B4FC` | Markdown başlıkları (kalın) |
| `SyntaxCodeColor` | `#BE185D` | `#F9A8D4` | `satır içi kod`, dili bilinmeyen kod blokları |
| `SyntaxLinkColor` | `#2563EB` | `#93C5FD` | Bağlantılar |
| `SyntaxMarkerColor` | `#4F46E5` | `#A5B4FC` | Liste işaretleri, yatay çizgiler |

Kalın (`**…**`) ve italik (`*…*`) metin rengini korur, yalnızca ağırlığı veya eğimi değişir.

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

Yazı tipi **Inter** (uygulamayla birlikte gelir). Sayı sütunlarında tablo rakamları (`.numeric` → `tnum`) kullanılır; içerik özetinde eş aralıklı yazı tipi (`.mono`). Editör alanlarındaki kod (çitli bloklar, `satır içi kod`, JSON belgeleri) `MonoFontFamily` ile yazılır: Cascadia Mono, Consolas, Menlo, SF Mono, DejaVu Sans Mono — sistemde ilk bulunan.

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

**Fluent UI System Icons** ([FluentIcons.Avalonia](https://github.com/davidxuang/FluentIcons), MIT). Varsayılan stil `Regular`; `Filled` yalnızca marka simgesinde ve Durdur düğmelerinde (dolu kare; çerçeveli kare onay kutusuyla karışır) kullanılır. Boyutlar: menü ve düğmelerde 16, durum çubuğunda 14, başlık rozetlerinde 20, boş durumlarda 24.

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
| Tercih yanıtlarını karşılaştır | `ColumnDoubleCompare` (açıkken `accent`) |
| Yapay zekâyla yaz / Belgeden soru-cevap / Varyant üret / Modelle değerlendir | `Sparkle` / `ChatBubblesQuestion` / `BranchFork` / `Gavel` · model: `BrainCircuit` · yeniden yaz: `ArrowClockwise` · eski değerlendirme: `History` |
| Anlamca benzer sorular | `ShapeIntersect` (kartın başında `AccentTextBrush` ile; "Anlamca karşılaştır" düğmesinde) |
| Etiket / Etiket ekle / Etiketler (yönetici, menü) | `Tag` / `TagAdd` / `TagMultiple` · etiket grubu: `Folder` · kayıtlarını göster: `Filter` · yeniden adlandır: `Rename` |
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
| `ToggleButton.subtle` | `subtle` gibi; açıkken `AccentSubtleBrush` zemin ve `AccentTextBrush` yazı | Aç/kapa bir panel (kaydın kaynak metni) |
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
- `Border.kind`: sürüm farkının türü. `.added` onaylı yeşili, `.removed` arşiv kırmızısı, `.modified` inceleme amberi kullanır; durum ve etiket değişikliği nötrdür. Alan kartlarında "Değişti / Eklendi / Kaldırıldı" rozeti `.modified` ile gösterilir.

### Yanıt karşılaştırması (tercih kayıtları)
Başlıkta maskelenmiş önizlemenin solunda `ColumnDoubleCompare` simge düğmesi (açıkken `accent`). Yanıt kartlarının altında bir `Border.card`: üst başlık "YANITLARI KARŞILAŞTIR", soluk özet ("13 kelime yalnızca tercih edilende · 5 yalnızca reddedilende"), sonra alt alta reddedilen (`ThumbDislike`, olumsuz renk) ve tercih edilen (`ThumbLike`, olumlu renk) yanıt; metinler `controls:DiffTextBlock` ile, yalnızca birinde olan kelimeler `DiffDeletedBrush` / `DiffInsertedBrush` zeminli. Sağ panel dar olduğu için yan yana değil alt alta.

### Etiketler
- **Renk:** Etiket rengi kullanıcı verisidir, tema tokenı değildir; açık ve koyu zeminde okunan 8 renklik paletten seçilir (`Tag.Palette`: indigo, gök mavisi, zümrüt, amber, kırmızı, pembe, mor, arduvaz). Renk yalnızca 7–10 px'lik noktada kullanılır; metin her zaman tema rengindedir, böylece kontrast etiket renginden etkilenmez.
- `Border.tag-chip`: editörde ve toplu panelde kaydın etiketi. Hap, 1 px kenar, kart zemini; nokta + ad (en fazla 220 px, kırpılır) + `Button.mini` ×. Toplu panelde adın yanında soluk sayı (kaç seçili kayıtta).
- `Border.tag-pill`: listede, içerik sütununun sağında. Kenarsız nötr dolgu, küçük ikincil metin, nokta + ad (en fazla 110 px). En fazla iki hap; fazlası "+N" hapı, tüm adlar ipucunda.
- Model puanı: listede içerik ile etiketler arasında `Border.score` hapı, `Gavel` (11 px) + puan; 4–5 `.good`, 3 `.fair`, 1–2 `.poor`; ipucu "Model puanı: 4/5". Yalnızca güncel değerlendirmesi olan satırlarda görünür; ayrı sütun yoktur.
- `Border.popup` + `Button.menu-item`: "Etiket ekle" kutusu. 280 px, `RadiusLarge`, kenar ve gölge; üstte arama kutusu, altında eşleşen etiketler (nokta · ad · sağda soluk kısayol), "“…” etiketini oluştur" satırı, boşken açıklama ve en altta grup öneki ipucu. Enter seçer/oluşturur, Esc kapatır.
- `Button.small`: çiplerin yanında "Etiket ekle" (`subtle`, 26 px).

### Fark metni
`controls:DiffTextBlock`: seçilebilir metin. Silinen kısımlar `DiffDeletedBrush`, eklenenler `DiffInsertedBrush` zeminle, satır içinde gösterilir. Değişen alan iki sütundur: solda eski metin ve silinenler, sağda yeni metin ve eklenenler. Değişmeyen alan tek sütun ve ikincil renktir. Boş alan "(boş)" yazar. Etiketler son alan olarak virgülle ayrılmış adlardır; kalkan etiket silinen, eklenen etiket eklenen zeminle gösterilir. Tema değişince renkler yeniden uygulanır.

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
`controls:CodeEditor` (`.field`; AvaloniaEdit `TextEditor` temasıyla): kendi kutusu olmayan, kartın (`Border.card`) veya balonun (`Border.bubble`) içine yerleşen, kaydırmalı ve içeriğe göre uzayan metin alanı. Boşken "Boş" yer tutucusu soluk renkte gösterilir. Markdown, çitli kod blokları ve JSON belgeleri sözdizimi tokenlarıyla renklenir. JSON belgesi olan alan `.json` sınıfını alır ve tamamen eş aralıklı yazılır. Seçim zemini `AccentSubtleHoverBrush`'tır. Sağ tıkta Kes / Kopyala / Yapıştır / Tümünü seç menüsü açılır; salt okunurken yalnızca kopyalama ve tümünü seçme etkindir. Odak, kabın etrafında 3 px yarı saydam indigo halkayla (`BoxShadow`, `:focus-within`) gösterilir; böylece tercih edilen/reddedilen kartların yeşil/kırmızı kenarı korunur. Sohbet turlarının başlığında rol ikonu ve etiketinin yanında 22 px'lik `Button.mini` düğmeleri (rolü çevir, kaldır) bulunur. Kaydedilmemiş değişiklikler başlığın altında bilgi bildirimi + "Vazgeç" (`subtle`) + "Kaydet" (`accent`) olarak gösterilir; arşivlenmiş kayıtta uyarı bildirimi ve salt okunur alanlar vardır.

### Toplu panel
Birden fazla kayıt seçilince sağ panel: ikon kutusu + "N kayıt seçili" başlığı, durum rozetleri ve sayıları, uygun aksiyonlar (en az bir seçili kayda uygulanabilenler) ve kısa açıklama. Silme metinli `danger-subtle` düğmedir.

### Veri seti özeti
Hiçbir kayıt seçili değilken sağ panel: "VERİ SETİ ÖZETİ" üst başlığı ve veri seti adı · skor kartı (büyük skor kutusu, "Kalite skoru", "x / y kayıt analiz edildi", analiz sürerken ilerleme çubuğu) · TOKENLAR kartı (tokenizer 3* ve bağlam uzunluğu 2* açılır listeleri, üç istatistik: toplam / en uzun / sınırı aşan, histogram) · YAPAY ZEKÂ kartı (`Sparkle` + varsayılan model "qwen2.5:7b · Ollama" ya da soluk "Yapay zekâ bağlantısı yok", sağda "Bağlantılar…" `subtle`; altında "Bu veri setine özel talimatlar" çok satırlı metin kutusu ve kısa açıklama; en altta "Belgeden Soru-Cevap Çıkar…" `subtle`, `ChatBubblesQuestion`) · SORUNLAR kartı (`ItemsControl.lines`: ciddiyet ikonu, kural adı + soluk sayı, sağda "Göster" `subtle` ve uygun kurallarda "Arşivle"; sorun yoksa olumlu onay satırı) · MODEL DEĞERLENDİRMESİ kartı (Talimat, Sohbet, Tercih; büyük tonlu kare içinde ortalama "3,2", "Ortalama puan", "6 kayıttan 5 tanesi değerlendirildi", eski değerlendirme sayısı; 5/5'ten 1/5'e çubuklar, 1–2 kırmızı; bulgular `ItemsControl.lines` ile `Warning` + ad + sayı + "Göster"; altta "N kaydı değerlendir…" `subtle`, `Gavel`; hiç değerlendirme yoksa soluk açıklama) · ANLAMCA BENZER SORULAR kartı (Talimat, Sohbet; SORUNLAR'ın altında: `ShapeIntersect` + durum metni ve açıkken soluk "Model: bge-m3"; açıkken "Benzerlik eşiği (kosinüs)" sayı kutusu (120 px, `0.00`, 0,80–0,98) ve yanında soluk açıklama; çalışırken ilerleme çubuğu + soluk "Sorular … ile vektöre çevriliyor: 120 sorudan 32…"; hata bildirimi `.danger` ("Yapay zekâ bağlantıları…" `accent` + "Kapat" `subtle`); düğmeler: "Anlamca karşılaştır" / "N soruyu daha karşılaştır" / "Bunun yerine … ile karşılaştır" (`subtle`, `ShapeIntersect`; ipucu modele gönderileni söyler), çalışırken dolu `Stop` + "Durdur", açıkken "Kapat" `subtle`) · altta seçme ipucu.

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
| Çalışma alanı | Sol: çalışma alanı başlığı + "PROJELER" + ağaç · Orta: veri seti başlığı (alt satırda son sürüm, analiz sürerken küçük ilerleme; sağda "İçe aktar", "Dışa aktar" ve "Sürümler"), arama + etiket filtresi (150–220 px; `Tag` ikonu ya da etiketin noktası) + kalite filtresi, durum çipleri, kolon başlıkları, kayıt listesi · Sağ: seçim yokken veri seti özeti; kayıtta başlık + durum + token ve skor hapları, aksiyonlar, içerik kartları/balonları, "KALİTE", "BİLGİLER" |
| Sürümler | 1220×760, yeniden boyutlandırılabilir.<br>• **Üst:** `History` ikon kutusu, başlık, veri seti.<br>• **Sol (270 px, uygulama zemini):** `ListBox.records` içinde önce çalışma kopyası (`Edit` ikonu), sonra en yeniden eskiye sürümler (`Tag` ikonu, "v1.1.0", tarih, sayılar, not).<br>• **Sağ üst:** seçilen öğenin adı ve ayrıntısı. Sağda eylemler: çalışma kopyasında "Sürüm Oluştur" (`accent`); sürümde "Dışa aktar", "Bu Sürüme Dön" (`subtle`) ve sil (`danger-subtle`). Altında "Karşılaştır" açılır listesi ve yanında "v1.0.0 → v1.1.0" ile sayılar.<br>• **Gövde:** farklar (340 px; tür çipleri ve satırlar: numara, `Border.kind`, durum geçişi, önizleme) · ayırıcı · seçilen kaydın alan kartları (sütun başlıkları eski / yeni).<br>• Sürüm yokken ve fark yokken boş durum gösterilir.<br>• **Alt çubuk:** ilerleme / sonuç bildirimi + Kapat |
| Etiketler | 680×580, yeniden boyutlandırılabilir.<br>• **Üst:** `TagMultiple` ikon kutusu, başlık, açıklama; altında yeni etiket kutusu + "Ekle" (`accent`, Enter da ekler).<br>• **Liste:** satır başına renk düğmesi (noktaya tıklayınca 4×2 palet), ad + "N kayıt" + varsa `Folder` ile grup, kısayol açılır listesi (Kısayol yok / Ctrl+1…5), "kayıtlarını göster", yeniden adlandır ve sil (`danger-subtle`, onaylı).<br>• Etiket yokken boş durum.<br>• **Alt çubuk:** sonuç ya da hata + Kapat |
| Yeni sürüm | 480 px.<br>• Başlık (`BookmarkAdd` ikonu) ve veri seti.<br>• Sürüm numarası: kutuda "v" ön eki; altında "Yama · Küçük · Büyük" öneri düğmeleri ve doğrulama uyarısı.<br>• Not (çok satırlı).<br>• Dondurmayı anlatan bilgi bildirimi (`LockClosed`).<br>• İptal + "Sürüm Oluştur" |
| Hakkında | 460 px; marka işareti (56 px) + ad + sürüm · kısa tanım · gizlilik bildirimi (`ShieldLock`, telemetri yok) · ayar ve günlük klasörü (`mono`, seçilebilir) · "Günlük klasörünü aç" ve "Üçüncü taraf bildirimleri" (`subtle`) · telif + Kapat (`accent`) |
| Diyaloglar | 460 px; başlık, açıklama, giriş, sağa yaslı İptal (`subtle`) + onay (`accent` veya `danger`) |
| Kayıt editörü (sağ panel) | Başlık + durum rozeti + token ve skor hapları, duruma uygun aksiyonlar ve sağda maskelenmiş önizleme + sil · etiket çipleri + "Etiket ekle" · maskelenmiş önizleme bildirimi · kaydedilmemiş değişiklik çubuğu · yapay zekâ hata bildirimi (`.danger`, `Sparkle`; "Kapat" + "Yapay zekâ bağlantıları…") · alan kartları veya sohbet balonları (düzenlenebilir) · "Mesaj ekle" + "Yanıtı yaz" (`Sparkle`) · KALİTE · BİLGİLER. Belgeden çıkarılan kayıtta BİLGİLER'de "Bölüm: Sipariş › Kargo · sayfa 3" satırı ve altında "Kaynak metin" (`ToggleButton.subtle.small`, `DocumentText`); açılınca `SubtleFillBrush` zeminli, en çok 280 px kaydırmalı bir kutuda parçanın metni (`SelectableTextBlock`). Açık/kapalı kayıttan kayda korunur. Yanıt tarafındaki kartların başlığının sağında `Button.mini` ✨ (yapay zekâyla yaz); yazarken onun yerinde "qwen2.5:7b ile yazılıyor…" (`.caption .muted`) ve `Stop` düğmesi, alan salt okunur. Başlıktaki ikon düğmelerinin başında `BranchFork` "Varyant üret" (`subtle icon`; Talimat, Sohbet, Tercih). Varyantın BİLGİLER'inde kaynak "Sentetik (#12 kaydının varyantı)". KALİTE ile BİLGİLER arasında MODEL DEĞERLENDİRMESİ (yalnızca Talimat, Sohbet, Tercih): başlıkta `Border.score` hapı (`Gavel` + "2/5"; 4–5 `.good`, 3 `.fair`, 1–2 `.poor`), kartta gerekçe (`SelectableTextBlock`), `Warning` ikonlu bulgu ve çelişki satırları, eskiyse `History` + soluk not, altta soluk "model · tarih" (değerlendirirken "… ile değerlendiriliyor…") ve sağda `subtle small` düğme ("Modelle değerlendir" / "Yeniden değerlendir"; değerlendirirken dolu `Stop` + "Durdur"). Değerlendirilmemişse soluk açıklama. Çoklu seçimde toplu panel (aksiyonlar arasında "Değerlendir" `Gavel` ve "Varyant üret" `BranchFork`; altında ETİKETLER: sayılı çipler + "Hepsine etiket ekle") |
| Yapay zekâ bağlantıları | 820×640, yeniden boyutlandırılabilir.<br>• **Üst:** `Sparkle` ikon kutusu, başlık, gizlilik açıklaması.<br>• **Sol (250 px, uygulama zemini):** BAĞLANTILAR listesi (yerel sunucuda `Server`, bulut hizmetinde `Cloud` ikonu, "model · ad", sunucu türü, varsayılanda "Varsayılan" etiketi); altta "Bağlantı ekle" (sunucu türlerinin menüsü: yerel sunucular, sonra OpenAI, Anthropic, Azure OpenAI, OpenRouter, Groq, DeepSeek).<br>• **Sağ:** sunucu türü + ad, adres (eş aralıklı, geçersizse hata satırı; Azure OpenAI'de kaynak adını anlatan soluk satır), adres yerel ağ dışındaysa uyarı bildirimi + izin onay kutusu (bulut hizmetinde uyarı hizmetin adını ve koşullarını anar), model (yazılabilir açılır liste + `ArrowSync` "modelleri getir"), embedding modeli (isteğe bağlı; aynı listeden yazılabilir açılır liste, altında soluk küçük açıklama; embedding sunmayan Anthropic, Groq ve DeepSeek'te gizli), sıcaklık + en uzun yanıt (sayı kutuları), API anahtarı (gizli; ipucu "Bu hizmet için gerekli" ya da depodaysa "Windows Kimlik Bilgisi Yöneticisi içinde kayıtlı; değiştirmek için yazın"; depodaysa yanında `KeyReset` "Anahtarı sil" `subtle icon`; altında anahtarın nerede saklandığını ve ortam değişkenini söyleyen soluk küçük metin, anahtar deposu yoksa `.warning` renginde), "Dene" (embedding modeli varsa sonuçta ikinci satır: "bge-m3 bir metni 1.024 sayıya çeviriyor.") (`accent`, `PlugConnected`) + "Varsayılan yap" (`subtle`, `Star`) + sağda "Sil" (`danger-subtle`). Altta çalışırken ilerleme, sonra sonuç bildirimi (bilgi ya da `.danger`).<br>• Bağlantı yokken boş durum ("Bir model bağlayın").<br>• **Alt çubuk:** "Değişiklikler hemen kaydedilir…" + Kapat |
| Belgeden soru-cevap çıkar | 1080×740, yeniden boyutlandırılabilir.<br>• **Üst:** `ChatBubblesQuestion` ikon kutusu, başlık; altında dosya adı · "12 sayfa · 24 parça" → hedef veri seti; sağda MODEL: `BrainCircuit` + "qwen2.5:7b · Ollama" + "Bağlantılar…" (`subtle`).<br>• **Ayar çubuğu (uygulama zemini):** "Parça uzunluğu" sayı kutusu + "token" (model yazmaya başlayınca kilitlenir, ipucu bağlam penceresini anlatır), "Parça başına en çok" + sayı kutusu + "soru"; sağda "Soruları çıkar" (`accent`, `Sparkle`) ya da yazarken "Durdur" (dolu `Stop`).<br>• **Bildirimler:** yapay zekâ hatası (`.danger`, "Kapat" + "Yapay zekâ bağlantıları…"), ekleme hatası (`.danger`), Tercih veri setinde reddedilen yanıtların sonra yazılacağı (bilgi), desteklenmeyen tür (`.warning`).<br>• **Sol (330 px, uygulama zemini):** PARÇALAR + "Tümü" üç durumlu onay kutusu; satır: onay kutusu, başlık yolu (bütün parçaların ortak üst başlığı yazılmaz) + soluk "#3 · sayfa 4 · 640 token", sağda durum etiketi ("Yazılıyor…", "3 soru", "Soru yok", `.warning` "Okunamadı").<br>• **Sağ:** parçanın tam başlık yolu + konum, "Yeniden yaz" (`subtle`, `ArrowClockwise`); KAYNAK METİN kartı (en çok 220 px kaydırmalı); okunamayan yanıtta `.warning` bildirim + modelin yazdığı (eş aralıklı); SORU-CEVAPLAR: çift başına kart (onay kutusu "Veri setine ekle", Soru tek satır, Yanıt çok satırlı, `Delete` `Button.mini`); dışarıda bırakılan çift soluk (`.left-out`, %55 opaklık).<br>• **Durumlar:** okunurken belirsiz ilerleme, okunamayan dosyada `DocumentError` + açıklama, metinsiz belgede `DocumentDismiss`.<br>• **Alt çubuk:** çalışırken ilerleme çubuğu, "qwen2.5:7b ile 3/24. parça yazılıyor…" ya da sonuç; "12 sorudan 11 tanesi taslak kayıt olarak eklenecek"; Kapat + "N kaydı ekle" (`accent`). Eklenmemiş çiftle kapatmak onay ister |
| Varyant üret | 1080×740, yeniden boyutlandırılabilir; belgeden soru-cevap çıkarma penceresinin iskeleti.<br>• **Üst:** `BranchFork` ikon kutusu, başlık; altında `TextBulletListSquare` + "3 kayıt" (tek kayıtta "#12 kaydı") → hedef veri seti; sağda MODEL.<br>• **Ayar çubuğu:** "Kayıt başına varyant" sayı kutusu (1–10), "Değişecek" açılır listesi ("Yalnızca soru" / "Soru ve yanıt", ipucu yeniden yazılan yanıtın okunmasını ister), "Günlük dil, yazım hataları" onay kutusu; sağda "Varyant üret" (`accent`, `Sparkle`) ya da "Durdur". Yazarken ayarlar kilitlidir.<br>• **Bildirimler:** yapay zekâ ve ekleme hataları (`.danger`), sorusu olmadığı için atlanan kayıtlar (bilgi), desteklenmeyen tür (`.warning`).<br>• **Sol (330 px; tek kayıtta gizli):** KAYITLAR + "Tümü"; satır: onay kutusu, soru (tek satır) + soluk "#12", sağda durum etiketi ("Yazılıyor…", "3 varyant", "Varyant yok", `.warning` "Okunamadı").<br>• **Sağ:** soru + numara ve durum etiketi, "Yeniden yaz"; ASIL KAYIT kartı (Soru, varsa Girdi, Yanıt; seçilebilir metin); okunamayan yanıtta `.warning` bildirim; VARYANTLAR: varyantlar asıl yanıtı koruyorsa soluk not, varyant başına kart (onay kutusu, "Soru" etiketi + gerekirse `.tag.warning` "Asıl soruya çok yakın" / "Asıl soruyla aynı", tek satır soru kutusu, yanıtı yeniden yazılmışsa çok satırlı yanıt kutusu, `Delete` `Button.mini`); dışarıda bırakılan varyant soluk (`.left-out`).<br>• **Boş durum:** hiçbir kaydın sorusu yoksa `BranchFork` + açıklama.<br>• **Alt çubuk:** ilerleme ve "11 varyanttan 8 tanesi taslak kayıt olarak eklenecek"; Kapat + "N kaydı ekle" (`accent`). Eklenmemiş varyantla kapatmak onay ister |
| Modelle değerlendir | 1080×740, varyant penceresinin iskeleti.<br>• **Üst:** `Gavel` ikon kutusu, başlık, kayıtlar → hedef, MODEL.<br>• **Ayar çubuğu:** "Aynı şeyi soran kayıtlarla karşılaştır" onay kutusu (ipucu çelişki denetimini anlatır; çalışırken kilitli); sağda "Değerlendir" (`accent`, `Gavel`) ya da "Durdur".<br>• **Sol (330 px; tek kayıtta gizli):** KAYITLAR + "Tümü"; satır: onay kutusu, soru + soluk numara, sağda puan hapı (`Border.score`, tonlu) ya da durum etiketi ("Değerlendiriliyor…", "Boş", `.warning` "Okunamadı").<br>• **Sağ:** soru + numara, "Yeniden değerlendir"; KAYIT kartı (Soru, Yanıt); DEĞERLENDİRME kartı: değerlendirirken belirsiz ilerleme + "… ile değerlendiriliyor…" / "#5 ile karşılaştırılıyor…", okunamayan yanıtta uyarı + modelin yazdığı (eş aralıklı), değerlendirmede solda puan hapı, sağda gerekçe, bulgular, eski notu ve soluk "model · tarih".<br>• **Alt çubuk:** ilerleme ve "12 kayıttan 8 tanesi değerlendirildi · ortalama 3,8 · 2 düşük puan"; sağda soluk "Değerlendirmeler geldikçe kaydedilir." + Kapat (`accent`; sonuçlar zaten kayıtlı olduğundan onay sorulmaz) |
| Menüler | Dosya (… İçe aktar, Belgeden Soru-Cevap Çıkar, Dışa aktar `Mod+E`, Sürüm oluştur, Sürümler …) · Düzen (Geri al/Yinele: son adımın adıyla, Kayıtlarda ara) · Görünüm · Kayıt (Kaydet, Onayla ve sonrakine geç, Arşivle ve sonrakine geç, Sonraki/Önceki, Maskelenmiş önizleme, Yapay zekâyla yaz `Mod+G`, Varyant Üret `Mod+Shift+G`, Modelle Değerlendir `Mod+J`, Etiketle › etiketler noktası ve `Mod+N` ile, Sil) · Araçlar (Örnek kayıt üret, Özel hassas terimler, Veri Setlerini Birleştir, Etiketler, Yapay Zekâ Bağlantıları) · Yardım (Güncellemeleri denetle, Açılışta denetle onay işareti, Hakkında); kısayollar menüde gösterilir |
| Dışa aktarma sihirbazı | 1040×720, içe aktarma sihirbazıyla aynı iskelet. Sol (360 px): veri setinin sürümü varsa KAYNAK (güncel kayıtlar / sürüm açılır listesi, sürümde dondurmayı anlatan not) · BİÇİM seçenek kartları · KAYITLAR (durum onay kutuları, bağlamı aşanları/kopyaları atla, kişisel verileri maskele) · BÖLME (ön ayar açılır listesi, özel oranlar için üç sayı kutusu, "Tabakalara göre böl" açılır listesi — etiket yoksa kapalı —, seed ve seçime göre değişen açıklaması) · EĞİTİM YAPILANDIRMASI (Yok / Axolotl / LLaMA-Factory açılır listesi; seçilince "Temel model (Hugging Face kimliği)" eş aralıklı metin kutusu ve yazılacak dosyalarla çalıştırma komutunu anlatan soluk açıklama; araç biçimi okuyamıyorsa önizleme yerine nedeni) · Sağ: ÖNİZLEME kartı (büyük sayı + "kayıt dışa aktarılacak", bölmede "eğitim ≈ … · doğrulama ≈ …", dışarıda kalanlar `SubtractCircle` satırlarıyla, onaylı kayıt yoksa bilgi bildirimi) · İLK SATIRLAR (3 satır, `mono` kartlar) · klasörde dosya varsa üstte uyarı bildirimi + "Başka klasör seç" / "Üzerine yaz" · Alt çubuk: son dışa aktarım + İptal + "N kaydı dışa aktar"; sonuç ekranında yazılan dosyalar (ad, klasör, kayıt sayısı; yapılandırma dosyalarında "eğitim yapılandırması") |
| Veri setlerini birleştir | 820×600, yeniden boyutlandırılabilir.<br>• **Üst:** `Merge` ikon kutusu, başlık, kaynakların değişmediğini söyleyen açıklama.<br>• **Sol:** BİRLEŞTİRİLECEK VERİ SETLERİ: proje başına `Folder` + kalın proje adı, altında girintili onay kutuları (veri seti adı + soluk "Talimat · 42 kayıt"); ilk seçilenden farklı türdekiler kapalı (soluk).<br>• **Sağ (320 px, uygulama zemini):** YENİ VERİ SETİ: ad, proje açılır listesi, üç onay kutusu (kopyaları atla, arşivlenmişleri al, taslak olarak al), soluk açıklama; birleştirirken ilerleme çubuğu + "87 kaydın 32 tanesi kopyalandı"; hata `.danger` bildirimi.<br>• **Sonuç:** boş durum düzeninde `CheckmarkCircle`, "Veri setleri birleştirildi" ve sonuç metni; Kapat.<br>• **Alt çubuk:** "3 veri seti · 87 kayıt · Talimat" ya da eksik olan + İptal + "Birleştir" (`accent`, `Merge`) |
| İçe aktarma sihirbazı | 1040×700, yeniden boyutlandırılabilir.<br>• **Üst:** ikon kutusu, başlık, hedef veri seti.<br>• **Sol (340 px, uygulama zemini):** KAYNAK kartı (dosya, satır sayısı, biçim ve düzen etiketleri). Dosya birden çok biçimde okunabiliyorsa altında "Okuma biçimi" açılır listesi. Sonra bildirimler: Markdown, düz metin, PDF, Word, HTML, sayfa arşivleri ve konuşma kayıtlarında nasıl bölündüğünü anlatan bilgi bildirimi (`TextBulletListSquare`; arşivde sayfaların başlıkları altında sırayla okunduğunu, konuşma kaydında konuşmaların nerede ayrıldığını ve turların nasıl kurulduğunu söyler); belgelerde (PDF, Word, Markdown, metin, HTML, Notion/Confluence ZIP'i) soru-cevap çıkarımını öneren bilgi bildirimi (`ChatBubblesQuestion`) ve altında sağa yaslı "Soru-cevap çıkar…" (`accent.small`). Ardından CSV AYARLARI ya da ÇALIŞMA KİTABI (sayfa açılır listesi, sonda "Tüm sayfalar (N)"; başlık satırı onay kutusu; tüm sayfalarda soluk açıklama). En altta SÜTUN EŞLEME; Markdown ve düz metin sütunları yerelleştirilmiş adlarla gösterilir. Konuşma kaydında (WhatsApp, döküm) bölümün adı KONUŞMACILAR'dır: "Müşteri (kullanıcı turları)" ve "Temsilci (asistan turları)" alanlarının seçenekleri konuşmacılardır (en çok yazan önce). KAYNAK kartı satır yerine konuşma sayısını ("12 konuşma"), alt çubuk "12 konuşmayı içe aktar"ı gösterir.<br>• **Sağ:** ÖNİZLEME (ilk 20 satır). Tüm sayfalar okunurken satır numarasının önünde sayfa adı yazar (`Kargo · 3`); numara sütunu satırlar arasında ortak genişliktedir.<br>• **Alt çubuk:** adıma göre özet + İptal + "N satırı içe aktar" (`accent`). Aktarımda ilerleme çubuğu + İptal, bitince sonuç ekranı ve Kapat |

**Tercihler:** Görünüm › Tema (Sistem / Açık / Koyu) ve Görünüm › Dil (English / Türkçe). İkisi de `app.settings.db` dosyasında saklanır ve yeniden başlatmadan uygulanır.
