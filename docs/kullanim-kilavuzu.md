# AI Dataset Studio — Kullanım Kılavuzu

AI Dataset Studio, dil modelleri (LLM) için eğitim verisi hazırlamaya yarayan bir masaüstü uygulamasıdır. Verilerinizi içe aktarır, tek tek inceler, temizler ve eğitim araçlarının okuyacağı biçimde dışa aktarırsınız. Her şey kendi bilgisayarınızda çalışır. Bir bulut modeli bağlamadığınız sürece verileriniz bilgisayarınızdan çıkmaz.

Bu kılavuz uygulamanın Türkçe arayüzüne göre yazılmıştır. Menü adları uygulamadakiyle birebir aynıdır.

> **Mod tuşu:** Kısayollarda geçen **Mod**, Windows ve Linux'ta **Ctrl**, macOS'ta **Cmd** tuşudur.

---

## İçindekiler

1. [Kurulum ve ilk açılış](#1-kurulum-ve-ilk-açılış)
2. [Temel kavramlar](#2-temel-kavramlar)
3. [Çalışma alanı, proje ve veri seti oluşturma](#3-çalışma-alanı-proje-ve-veri-seti-oluşturma)
4. [Veri içe aktarma](#4-veri-içe-aktarma)
5. [Kayıtları inceleme ve düzenleme](#5-kayıtları-inceleme-ve-düzenleme)
6. [Etiketler](#6-etiketler)
7. [Kalite denetimi ve kişisel veriler](#7-kalite-denetimi-ve-kişisel-veriler)
8. [Sürümler](#8-sürümler)
9. [Dışa aktarma](#9-dışa-aktarma)
10. [Yapay zekâ özellikleri](#10-yapay-zekâ-özellikleri)
11. [Veri setlerini birleştirme](#11-veri-setlerini-birleştirme)
12. [Klavye kısayolları](#12-klavye-kısayolları)
13. [Ayarlar, güncellemeler ve sorun giderme](#13-ayarlar-güncellemeler-ve-sorun-giderme)

---

## 1. Kurulum ve ilk açılış

1. GitHub'daki **Releases** sayfasından işletim sisteminize uygun paketi indirin: Windows (x64), macOS (Intel ya da Apple Silicon) veya Linux (x64).
2. Paketi kurup uygulamayı açın. Uygulama, yeni sürümleri kendisi bulup kurabilir (bkz. [13. bölüm](#13-ayarlar-güncellemeler-ve-sorun-giderme)).
3. Açılış ekranında iki seçenek vardır:
   - **Yeni çalışma alanı:** boş bir `.adstudio` dosyası oluşturur.
   - **Çalışma alanı aç:** var olan bir dosyayla devam eder. Son açtığınız dosyalar **SON AÇILANLAR** altında listelenir.

**Dil ve tema:** **Görünüm › Dil** menüsünden Türkçe ya da İngilizce seçilir; uygulamayı yeniden başlatmak gerekmez. **Görünüm › Tema** menüsünden açık, koyu ya da sistem teması seçilir.

> Hemen denemek isterseniz bir veri seti oluşturduktan sonra **Araçlar › Örnek Kayıt Üret…** ile seçili veri setine örnek kayıtlar ekleyebilirsiniz.

---

## 2. Temel kavramlar

| Kavram | Açıklama |
|---|---|
| **Çalışma alanı** | Bütün işinizi tutan tek bir `.adstudio` dosyası. Taşınabilir: kopyalayıp başka bilgisayarda açabilirsiniz. |
| **Proje** | İlgili veri setlerini bir arada tutan klasör. Örneğin her asistan için bir proje. |
| **Veri seti** | Aynı türden kayıtların listesi. |
| **Kayıt** | Tek bir eğitim örneği: bir soru-cevap, bir sohbet ya da bir tercih çifti. |

### Veri seti türleri

| Tür | İçerik | Ne için |
|---|---|---|
| **Talimat** | Talimat, isteğe bağlı girdi ve yanıt | SFT, LoRA / QLoRA ince ayarı |
| **Sohbet** | Sistem mesajı ve sırayla kullanıcı/asistan mesajları | Çok turlu sohbet asistanları |
| **Tercih (DPO)** | Bir istem, tercih edilen yanıt ve reddedilen yanıt | DPO, ORPO gibi tercih eğitimleri |

### Kayıt durumları

Her kaydın bir durumu vardır: **Taslak → İncelemede → Onaylandı**. İstemediğiniz kayıtları silmek yerine **Arşivlendi** durumuna alabilirsiniz. Dışa aktarmada hangi durumların yazılacağını siz seçersiniz; arşivlenmiş kayıtlar yazılmaz.

---

## 3. Çalışma alanı, proje ve veri seti oluşturma

1. **Dosya › Yeni Çalışma Alanı…** (Mod+N) ile dosyanın yerini ve adını seçin.
2. Soldaki gezginde **Yeni proje** ile bir proje oluşturun.
3. Projeyi seçip **Yeni veri seti** ile veri setini oluşturun: adını ve türünü (Talimat, Sohbet, Tercih) seçin. Tür sonradan değiştirilemez.
4. Proje ve veri setlerini sağ tıklayıp **Yeniden Adlandır…** ya da **Sil…** diyebilirsiniz.

Bir veri seti seçildiğinde ortada kayıt listesi, sağda seçili kaydın düzenleyicisi, üstte **VERİ SETİ ÖZETİ** görünür.

---

## 4. Veri içe aktarma

Bir veri seti seçin ve **Dosya › Dosyadan İçe Aktar…** komutunu kullanın (kayıt listesinin üstündeki İçe Aktar düğmesi de aynı işi yapar). Sihirbaz dosyanın biçimini ve düzenini kendisi tanır. Canlı önizlemede ayarları ve sütun eşlemesini değiştirebilirsiniz. Atlanan her satır gerekçesiyle gösterilir.

| Kaynak | Nasıl okunur |
|---|---|
| **JSON Lines / JSON** (`.jsonl`, `.ndjson`, `.json`) | OpenAI mesajları, ShareGPT, Alpaca, prompt/completion, Hugging Face DPO, OpenAI tercih biçimleri ve düz nesneler tanınır. |
| **Parquet** (`.parquet`) | Hugging Face veri setlerinin yayımlandığı biçim. Satırlar JSON Lines gibi okunur; iç içe mesaj listeleri (`messages`, `conversations`) de tanınır. |
| **CSV / TSV** (`.csv`, `.tsv`, `.txt`) | Sütunlar adlarına göre eşlenir; Türkçe sütun adları da önerilir. UTF-8, UTF-16 ve Excel'in Türkçe (Windows-1254) dosyaları okunur. |
| **Excel** (`.xlsx`, `.xlsm`) | Tek bir sayfa ya da bütün sayfalar; her sayfanın kendi başlık satırıyla. |
| **Markdown** (`.md`) | Her başlık bir kayıttır: başlık soru, altındaki metin yanıttır. |
| **PDF ve Word** (`.pdf`, `.docx`) | Başlıklar, listeler ve tablolar tanınır; sayfa numaraları, üst bilgiler ve içindekiler atlanır. Taranmış (yazısız) PDF'ler okunamaz. |
| **Düz metin** (`.txt`) | Her paragraf bir kayıttır: ilk satır soru, devamı yanıttır. |
| **HTML ve Notion / Confluence dışa aktarımları** (`.html`, `.zip`) | Sayfalar belge olarak okunur; menüler, betikler, üst ve alt bilgiler atlanır. Ağ bağlantısı gerekmez. |
| **Konuşma kayıtları** (WhatsApp dışa aktarımı, `Müşteri: …` biçiminde dökümler) | Her konuşma bir sohbet kaydı olur. Konuşmacılar sütun gibi eşlenir: müşteri soran, temsilci yanıtlayan. Medya ve silinen mesajlar atlanır; 6 saatlik bir aradan sonra yeni konuşma başlar. |

### Hugging Face'ten içe aktarma

Hugging Face'teki hazır veri setlerini doğrudan alabilirsiniz: bir veri seti seçin ve **Dosya › Hugging Face'ten İçe Aktar…** komutunu kullanın.

1. Veri setinin kimliğini (ör. `tatsu-lab/alpaca`) ya da huggingface.co'daki sayfa adresini yazıp **Dosyaları listele**'ye basın.
2. İçe aktarılabilen dosyalar (Parquet, JSON Lines, JSON, CSV) boyutlarıyla listelenir; eğitim dosyası (`train`) önceden seçilir.
3. **İndir ve içe aktar** dosyayı indirir ve her dosyada olduğu gibi içe aktarma sihirbazında açar. İçe aldıktan sonra kayıtları düzenleyebilir, kendi kayıtlarınızı ekleyebilirsiniz.

Yalnızca seçtiğiniz dosya indirilir; herkese açık veri setleri hesap gerektirmez. Erişim izni isteyen (gated) ya da özel veri setleri için Hugging Face'te erişim isteyin, bir erişim jetonunu `HF_TOKEN` ortam değişkenine yazın ve uygulamayı yeniden başlatın.

### Belgeden soru-cevap çıkarma

PDF, Word, Markdown, HTML ya da Notion/Confluence belgelerinden soru-cevap çiftlerini bir model çıkarabilir: **Dosya › Belgeden Soru-Cevap Çıkar…**. Belge başlıklarına göre parçalara bölünür ve model her parça için sorular yazar. Siz hepsini inceleyip seçtiklerinizi ekleyene kadar hiçbir şey veri setine girmez. Eklenen kayıtlar taslak olarak gelir ve kaynak paragrafı düzenleyicide görünür. Bu özellik için bir yapay zekâ bağlantısı gerekir (bkz. [10. bölüm](#10-yapay-zekâ-özellikleri)).

---

## 5. Kayıtları inceleme ve düzenleme

- **Elle yeni kayıt yazmak** için **Kayıt › Yeni Kayıt** (Mod+Shift+N) ya da listenin üstündeki **Yeni kayıt** düğmesini kullanın. Veri setinin türünde boş bir taslak listenin sonuna eklenir ve imleç ilk alana geçer; sohbet kaydı boş bir kullanıcı ve bir asistan turuyla başlar. Geri Al (Mod+Z) eklenen kaydı kaldırır.
- Listeden bir kayıt seçin; alanlarını sağdaki düzenleyicide doğrudan değiştirin.
- Değişiklikler **Mod+S** ile ya da başka bir kayda geçtiğinizde otomatik olarak kaydedilir.
- Alanlar Markdown'ı, kod bloklarını (` ```json `, `python`, `sql` vb.) ve JSON'u renklendirir.
- **Mod+Enter** kaydı onaylar ve sonrakine geçer; **Mod+Shift+Backspace** arşivler ve sonrakine geçer. Bu ikisiyle büyük veri setleri klavyeden hızla incelenir.
- **F8 / Shift+F8** sonraki ve önceki kayda gider; listede ↑/↓ tuşları da çalışır.
- **Kayıtlarda Ara** (Mod+F) metin araması yapar. Listenin üstündeki filtrelerle duruma, kalite sorununa ve etikete göre süzebilirsiniz.

### Toplu işlemler

Shift ya da Ctrl ile tıklayarak birden çok kayıt seçin. Seçili kayıtları toplu olarak onaylayabilir, arşivleyebilir, etiketleyebilir ya da silebilirsiniz (**Delete**).

### Geri alma

Düzenlemeler, durum değişiklikleri, silmeler, toplu işlemler ve etiketlemeler **Mod+Z** ile geri alınır, **Mod+Shift+Z** (ya da Mod+Y) ile yinelenir. **Düzen** menüsü neyin geri alınacağını adıyla gösterir.

---

## 6. Etiketler

Etiketler kayıtları konuya ya da kaynağa göre gruplamanızı sağlar ve çalışma alanındaki bütün veri setlerinde ortaktır.

- **Ekleme:** düzenleyicideki etiket kutusuna yazın; etiket yoksa oluşturulur. Toplu seçimde bütün seçili kayıtlara birden eklenir.
- **Hızlı etiketleme:** **Mod+1 … Mod+5**, o kısayola atanmış etiketi ekler ya da kaldırır; yazı yazarken de çalışır. Kısayolları **Araçlar › Etiketler…** penceresinden atayın.
- **Etiket yöneticisi** (**Araçlar › Etiketler…**): etiketlerin adını, rengini ve kısayolunu değiştirin, etiketleri silin.
- **Gruplar:** `kategori:hukuk` gibi iki noktalı adlar bir etiket grubu oluşturur.
- **Süzme:** kayıt listesini bir etikete ya da "etiketsiz" kayıtlara göre süzebilirsiniz.
- **Bölmede kullanım:** dışa aktarırken eğitim, doğrulama ve test oranlarını her etiket ya da etiket grubu içinde ayrı ayrı uygulayabilirsiniz (tabakalı bölme).

---

## 7. Kalite denetimi ve kişisel veriler

**VERİ SETİ ÖZETİ** kayıtları otomatik denetler ve bulunan sorunları sayar. Bir sorunun üstüne tıklayınca liste o sorunlu kayıtlara süzülür.

| Kural | Ne bulur |
|---|---|
| Boş alanlar | Doldurulmamış soru ya da yanıt |
| Aynı yanıtlar | Tercih çiftinde tercih edilen ve reddedilen yanıtın aynı olması |
| Geçiştirici yanıtlar | Çok kısa, işe yaramaz yanıtlar |
| Dengesiz uzunluk | Soruya göre aşırı uzun ya da kısa yanıtlar |
| Soruyu tekrarlayan yanıtlar | Yanıtın soruyu aynen tekrar etmesi |
| Kapanmamış kod blokları, Geçersiz JSON | Biçim hataları |
| Kişisel veri | T.C. kimlik numarası, IBAN, kart numarası, telefon, e-posta, iç IP ve URL adresleri, API anahtarı ve parolalar |
| Kopyalar, Benzer sorular | Birebir aynı ya da çok benzer sorular |
| Çelişen yanıtlar | Aynı soruya birbiriyle çelişen yanıtlar |
| Bağlam uzunluğunu aşanlar | Seçili modelin bağlam uzunluğuna sığmayan kayıtlar |

- **Token sayımı:** veri setinin tokenizer'ı ve bağlam uzunluğu özetten seçilir. Her kaydın token sayısı düzenleyicide görünür.
- **Benzer sorular:** aynı soruyu biraz farklı yazılmış hâlleriyle bulur ve bunları temizlemenizi sağlar. Bir embedding modeli bağlarsanız **anlamca benzer** soruları da bulur.
- **Kişisel veriler:** **Kayıt › Maskelenmiş Önizleme** (Mod+Shift+M), kişisel verileri maskelenmiş olarak gösterir. Kendi hassas terimlerinizi (müşteri adları, kod adları) **Araçlar › Özel Hassas Terimler…** ile ekleyebilirsiniz.

---

## 8. Sürümler

Bir veri setinin o anki hâlini numaralı bir sürüm olarak dondurabilirsiniz. Örneğin bir eğitimden önce sürüm almak, hangi veriyle eğittiğinizi kesin olarak saklar.

- **Dosya › Sürüm Oluştur…** ile sürüm alın.
- **Dosya › Sürümler…** penceresinde:
  - iki sürümü ya da bir sürümü bugünkü hâlle kelime kelime karşılaştırın;
  - veri setini bir sürüme geri döndürün (bu işlem de geri alınabilir);
  - sürüm silin.
- Dışa aktarma sihirbazında kaynak olarak bir sürüm seçerseniz, dosyalar o sürüm dondurulduğu hâliyle yazılır.

---

## 9. Dışa aktarma

**Dosya › Dosyaya Dışa Aktar…** (Mod+E) sihirbazını açar.

1. **KAYNAK:** veri setinin bugünkü hâli ya da bir sürüm.
2. **BİÇİM:** eğitim aracınızın beklediği biçim:
   - **OpenAI JSONL** (`messages`), **ShareGPT**, **Alpaca**, **Hugging Face DPO**;
   - metin şablonları: ChatML ve Llama 3.
3. **KAYITLAR:** hangi durumların yazılacağı ve şu seçenekler:
   - bağlam uzunluğunu aşan kayıtları dışarıda bırak;
   - birebir kopyaları dışarıda bırak (ilki kalır);
   - modelin düşük puan verdiği kayıtları (1–2) dışarıda bırak.
4. **BÖLME:**
   - **Tek dosya**;
   - **Eğitim / doğrulama / test** (80 / 10 / 10);
   - **Eğitim / doğrulama** (90 / 10);
   - **Özel oranlar**.

   Bölme tohuma göre yapılır: aynı tohum her seferinde aynı dosyaları verir, yeni eklenen kayıtlar eskilerin yerini değiştirmez. Benzer sorular ve bir kaydın varyantları hep aynı bölümde kalır, böylece test sorularının bir benzeri eğitim verisine sızmaz.
5. **EĞİTİM YAPILANDIRMASI:** isterseniz veri dosyalarının yanına çalıştırılmaya hazır bir **Axolotl** (`axolotl.yaml`) ya da **LLaMA-Factory** (`dataset_info.json`, `llamafactory.yaml`) LoRA yapılandırması yazılır. **QLoRA: model 4 bit yüklensin** kutusunu işaretlerseniz yapılandırma QLoRA olur: çok daha az GPU belleğiyle (7–8B bir model için yaklaşık 10 GB) eğitir; bitsandbytes kurulu bir NVIDIA ekran kartı gerekir.

Önizleme, kaç kaydın hangi dosyaya gideceğini ve hangi kayıtların neden dışarıda kaldığını gösterir. Seçtiğiniz ayarlar veri seti için hatırlanır.

---

## 10. Yapay zekâ özellikleri

Bu özellikler bir dil modeli bağlantısı gerektirir. Hiçbiri zorunlu değildir: bağlantı olmadan uygulamanın geri kalanı tam çalışır.

### Bağlantı kurma

**Araçlar › Yapay Zekâ Bağlantıları…** penceresinden bağlantı ekleyin:

- **Bu bilgisayarda ya da ağınızda çalışan modeller:** Ollama, LM Studio, vLLM, LocalAI ya da OpenAI uyumlu herhangi bir sunucu. Veriler dışarı çıkmaz.
- **Bulut servisleri:** OpenAI, Anthropic, Azure OpenAI, OpenRouter, Groq, DeepSeek.
  - Bilgisayarınızın ve yerel ağınızın dışındaki bir sunucuya, siz **Kayıtların bu sunucuya gönderilmesine izin ver** kutusunu işaretlemeden hiçbir kayıt gönderilmez.
  - API anahtarları işletim sisteminin anahtar deposunda saklanır (Windows Kimlik Bilgisi Yöneticisi, macOS Anahtar Zinciri, Linux Secret Service).
- Bir bağlantıya isteğe bağlı olarak bir **embedding modeli** de ekleyebilirsiniz; anlamca benzer soruları bulmak için kullanılır.
- **Dene** düğmesi bağlantının çalıştığını ve modelin ne kadar sürede yanıt verdiğini gösterir.

Her veri setine **yapay zekâ talimatları** (ör. "Resmî Türkçe yaz.") girebilirsiniz; model o veri setinde yazarken bunlara uyar.

### Yapay zekâyla yazma

**Kayıt › Yapay zekâyla yaz** (Mod+G): model, kaydın yanıt tarafını siz izlerken yazar. Yazmayı durdurmak için kısayola yeniden basın. Kaydın kökeninde hangi modelin yazdığı saklanır.

### Varyant üretme

**Kayıt › Varyant Üret…** (Mod+Shift+G): model, bir kaydın sorusunu 1–10 farklı biçimde yeniden sorar; isterseniz yanıtı da yeniden yazar. Varyantları inceleyip seçtiklerinizi eklersiniz. Varyantlar ana kaydın ailesine bağlanır ve dışa aktarmada hep onunla aynı bölümde kalır.

### Modelle değerlendirme

**Kayıt › Modelle Değerlendir** (Mod+J): model kayıtlara 1–5 arası puan ve kısa bir gerekçe verir.
- Belgeden çıkarılan kayıtlarda yanıtın kaynak paragrafa uygunluğunu da denetler.
- Aynı şeyi soran kayıtlar arasında çelişen yanıtları bulur.
- Puanlar listede ve düzenleyicide görünür. Kayıt sonradan değişirse eski puan "güncel değil" olarak işaretlenir.
- Birden çok kaydı toplu değerlendirmek için veri seti özetindeki değerlendirme kartını kullanın.

### Modeli değerlendirme (benchmark)

**Araçlar › Modeli Değerlendir…**, eğittiğiniz (ya da herhangi bir) modelin veri setinizdeki sorulara verdiği yanıtları ölçer:
- Sorular varsayılan olarak son dışa aktarımın **test** bölümünden alınır; bunlar, eğitilen modelin görmediği sorulardır.
- Bir hakem model, her yanıtı veri setindeki yanıtla karşılaştırıp 1–5 puan verir. Yanında modelden bağımsız bir kelime örtüşmesi (F1) puanı da gösterilir.
- Biten değerlendirmeler saklanır; farklı modelleri aynı sorular üzerinde karşılaştırabilirsiniz. **Raporu kaydet…** bütün yanıtları JSON Lines dosyasına yazar.

---

## 11. Veri setlerini birleştirme

**Araçlar › Veri Setlerini Birleştir…** (gezginde sağ tık menüsünden de açılır), aynı türden veri setlerini yeni bir veri setinde toplar. Seçtiğiniz veri setleri olduğu gibi kalır.

- Farklı projelerden en az iki veri seti seçin. Kayıtlar içerikleri, durumları, etiketleri ve tarihleriyle kopyalanır.
- Birebir kopyaları dışarıda bırakabilir, arşivlenmiş kayıtları da alabilir ya da her şeyi yeniden incelemek için taslak olarak alabilirsiniz.
- **Başka bir dosyadan…** düğmesi başka bir `.adstudio` dosyasının veri setlerini listeler. O dosyadan tek bir veri seti getirmek de mümkündür.
  - Diğer dosya hiç değiştirilmez: uygulama dosyanın bir kopyasından okur, dosya başka bir pencerede açıkken bile.
  - Etiketler adlarıyla eşleştirilir. Sizde olmayan bir etiket kendi rengiyle ve kısayolsuz eklenir.

---

## 12. Klavye kısayolları

| Kısayol | İşlem |
|---|---|
| Mod+N / Mod+O | Yeni çalışma alanı / Çalışma alanı aç |
| Mod+E | Dışa aktar |
| Mod+S | Değişiklikleri kaydet |
| Mod+Enter | Onayla ve sonrakine geç |
| Mod+Shift+Backspace | Arşivle ve sonrakine geç |
| F8 / Shift+F8 | Sonraki / önceki kayıt |
| Mod+Z / Mod+Shift+Z (Mod+Y) | Geri al / yinele |
| Mod+F | Kayıtlarda ara |
| Delete | Seçili kayıtları sil (listedeyken) |
| Mod+Shift+N | Elle yeni kayıt yaz |
| Mod+Shift+M | Maskelenmiş önizleme |
| Mod+1 … Mod+5 | Kısayolu atanmış etiketi ekle / kaldır |
| Mod+G | Yapay zekâyla yaz (tekrar basınca durur) |
| Mod+Shift+G | Varyant üret |
| Mod+J | Modelle değerlendir |

---

## 13. Ayarlar, güncellemeler ve sorun giderme

### Güncellemeler

**Yardım › Güncellemeleri Denetle…** yeni sürüm olup olmadığına bakar. **Yardım › Açılışta Güncellemeleri Denetle** işaretliyse uygulama her açılışta kendisi kontrol eder. Yeni sürüm bulunursa **Güncelle ve Yeniden Başlat** ile indirilir ve kurulur; çalışmanız önce kaydedilir. Güncellemeler yalnızca kurulu uygulamada yapılır, kaynaktan derlenip çalıştırılan kopyada yapılmaz.

### Yedekleme ve taşıma

Bütün veriniz tek bir `.adstudio` dosyasındadır. Yedeklemek ya da başka bilgisayara taşımak için uygulamayı kapatıp bu dosyayı kopyalamanız yeterlidir. Eski sürümlerde kaydedilmiş dosyalar yeni sürümde açılır.

### Sık karşılaşılan durumlar

| Durum | Ne yapmalı |
|---|---|
| "Daha yeni bir sürümüyle kaydedilmiş" uyarısı | Dosya uygulamanın daha yeni bir sürümüyle kaydedilmiş. Uygulamayı güncelleyin. |
| PDF'den hiç metin gelmiyor | PDF taranmış bir görüntüdür; içinde metin yoktur. Önce bir OCR aracıyla metne çevirin. |
| Yapay zekâ özellikleri kapalı görünüyor | **Araçlar › Yapay Zekâ Bağlantıları…** penceresinden bir bağlantı ekleyip **Dene** ile doğrulayın. Yerel modellerde Ollama ya da LM Studio'nun çalıştığından emin olun. |
| "Bu sunucu bilgisayarınızın dışında" uyarısı | Bağlantıdaki **Kayıtların bu sunucuya gönderilmesine izin ver** kutusunu işaretleyin; izin verilmeden hiçbir kayıt gönderilmez. |
| Kayıt "bağlam uzunluğunu aşıyor" | Veri seti özetinden doğru tokenizer'ı ve bağlam uzunluğunu seçin, ya da kaydı kısaltın. |
| "Beklenmeyen bir hata oluştu" | Mesaj, ayrıntıların yazıldığı hata kaydı dosyasının yerini gösterir. Sorun bildirirken bu dosyayı ekleyin. |

### Bilinen sınırlar

- Taranmış PDF'ler ve sayfalardaki görseller için metin tanıma (OCR) yoktur.
- Notion, Confluence ve yardım masası sistemleri (Zendesk, Intercom) için yalnızca dışa aktarılmış dosyalar okunur; doğrudan bağlantı yoktur.
- Telegram gibi diğer sohbet uygulamalarının biçimleri ve sesli mesajlar desteklenmez.
- Kayıt listesi aynı anda yalnızca bir etikete göre süzülebilir.
