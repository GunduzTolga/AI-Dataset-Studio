# AI Dataset Studio 🧠📊

> **The Modern, Offline-First Desktop Studio for AI Dataset Engineering & Curation.**  
> Curate, clean, tokenize, version, and export LLM datasets (Instruction, Chat, DPO/RLHF preference) locally on Windows, macOS, and Linux.

> 🇹🇷 **Türkçe tanıtım hemen aşağıda.** · 🇬🇧 English description: [Overview](#-overview)

---

## 🇹🇷 Türkçe Tanıtım

### Bu proje nedir?

**AI Dataset Studio**, yapay zekâ dil modellerini (LLM) eğitmek için gereken veriyi hazırlayan bir masaüstü uygulamasıdır. Windows, macOS ve Linux'ta çalışır.

Bir modeli kendi işinize göre eğitmek (ince ayar, *fine-tuning*) için yüzlerce ya da binlerce örnek gerekir. "Müşteri şunu sorarsa şöyle cevap ver" gibi soru-cevap çiftleri, örnek sohbetler, iyi ve kötü cevap karşılaştırmaları bunlara örnektir. Bu örnekler genellikle dağınık Excel dosyalarında, PDF'lerde, WhatsApp yazışmalarında ya da yarım kalmış betiklerde durur. AI Dataset Studio bu veriyi tek bir yerde toplar. İçeri alır, tek tek incelemenizi sağlar, hatalı, tekrar eden ya da kişisel bilgi içeren kayıtları bulur ve sonunda eğitim araçlarının doğrudan okuyacağı dosyalara dönüştürür.

Her şey **kendi bilgisayarınızda** çalışır. İnternete bağlanması gerekmez, verileriniz hiçbir yere gönderilmez. Bir bulut yapay zekâ servisi bağlamayı siz seçmedikçe ve açıkça izin vermedikçe hiçbir kayıt bilgisayarınızdan çıkmaz.

### Ne işe yarar? Örnek kullanım senaryoları

| Senaryo | AI Dataset Studio ile |
|---|---|
| **Müşteri destek asistanı** | Sık sorulan sorular Excel'ini ve WhatsApp destek yazışmalarını içe aktarın. Kişisel verileri maskeleyin, tekrar eden soruları ayıklayın ve modeli şirketinizin üslubuyla cevap verecek şekilde eğitmek için dışa aktarın. |
| **Şirket içi bilgi asistanı** | Prosedür PDF'lerinden, Word belgelerinden, Notion ya da Confluence sayfalarından yapay zekâya soru-cevap çiftleri çıkartın. Her cevabın hangi paragraftan geldiği kayıtta saklanır. |
| **Hukuk, sağlık, finans gibi hassas alanlar** | Veri bilgisayardan çıkmadan çalışın. T.C. kimlik numarası, IBAN, kart numarası, telefon ve e-posta otomatik bulunur ve maskelenebilir. |
| **Model kalitesini iyileştirme** | Kötü cevapları yapay zekâya puanlatıp ayıklayın, aynı soruya çelişen cevapları bulun, iyi ve kötü cevap çiftleriyle tercih (DPO) verisi hazırlayın. |
| **Eğitilen modeli ölçme** | Eğittiğiniz modele hiç görmediği test sorularını sorun ve cevaplarını veri setindeki doğru cevaplarla karşılaştırıp puanlayın. Farklı modelleri yan yana kıyaslayın. |
| **Hazır bir veri setinden başlamak** | Hugging Face'teki bir veri setini adıyla bulup içe alın, düzenleyin, istemediğiniz kayıtları ayıklayın ve kendi kayıtlarınızı elle ya da yapay zekâyla ekleyin. |
| **Ekip çalışması** | Herkesin hazırladığı `.adstudio` dosyalarındaki veri setlerini tek bir veri setinde birleştirin. Etiketler isimleriyle eşleşir, tekrar eden kayıtlar ayıklanır. |

### Tipik iş akışı

```
 İçe aktar  →  İncele ve düzenle  →  Temizle  →  Sürüm al  →  Dışa aktar  →  Modeli eğit  →  Modeli değerlendir
 (Excel, PDF,   (onayla, arşivle,     (kopyalar,   (v1.0.0)     (eğitim/test   (Axolotl /       (test soruları,
  WhatsApp…)     etiketle)            kişisel veri)              dosyaları)      LLaMA-Factory)   puan ve kıyas)
```

1. **İçe aktarın:** Excel, CSV, JSON, PDF, Word, Markdown, web sayfaları, Notion/Confluence dışa aktarımları ya da WhatsApp sohbetleri. Sihirbaz dosyayı kendisi tanır, sütunları eşler ve canlı önizleme gösterir.
2. **İnceleyin:** her kaydı ekranda görün, düzenleyin, onaylayın ya da arşivleyin. Klavye kısayollarıyla (Ctrl+Enter onayla ve sonrakine geç) binlerce kayıt hızla gözden geçirilir. Her işlem geri alınabilir.
3. **Temizleyin:** uygulama boş, çok kısa, tekrar eden, birbirine çok benzeyen, çelişen, kişisel veri içeren ya da modelin bağlam sınırını aşan kayıtları kendisi bulur.
4. **Sürüm alın:** eğitimden önce veri setini dondurun. Hangi veriyle eğittiğiniz hep bilinir; iki sürümü kelime kelime karşılaştırabilir ya da eskisine dönebilirsiniz.
5. **Dışa aktarın:** OpenAI, ShareGPT, Alpaca, Hugging Face DPO, ChatML ya da Llama 3 biçiminde, eğitim/doğrulama/test olarak bölünmüş dosyalar alın. İsterseniz yanına hazır bir Axolotl ya da LLaMA-Factory eğitim yapılandırması da yazılır.
6. **Değerlendirin:** eğittiğiniz modele test sorularını sorun, cevaplarını puanlatın ve önceki modellerle karşılaştırın.

### Özellikler

**Veri türleri**
- **Talimat (SFT):** talimat, isteğe bağlı girdi ve yanıt. Klasik soru-cevap ince ayarı (LoRA, QLoRA) için.
- **Sohbet:** sistem mesajı ve sırayla kullanıcı/asistan mesajları. Çok turlu sohbet asistanları için.
- **Tercih (DPO):** bir istem, tercih edilen ve reddedilen yanıt. DPO ve ORPO gibi tercih eğitimleri için.

**İçe aktarma**
- JSON Lines / JSON: OpenAI, ShareGPT, Alpaca, Hugging Face DPO biçimleri kendiliğinden tanınır.
- **Hugging Face:** Dosya › Hugging Face'ten İçe Aktar… ile bir veri setinin adını (`tatsu-lab/alpaca`) yazın, dosyalarından birini seçin; indirilip içe aktarma sihirbazında açılır. Hugging Face'in kullandığı Parquet dosyaları da doğrudan okunur.
- CSV / TSV ve Excel: sütunlar Türkçe adlarıyla da eşlenir. Excel'in Türkçe karakterli (Windows-1254) dosyaları sorunsuz okunur; çok sayfalı çalışma kitaplarında tek sayfa ya da hepsi seçilebilir.
- Markdown, düz metin, PDF ve Word: başlıklar, listeler ve tablolar tanınır; sayfa numaraları, üst bilgiler ve içindekiler atlanır.
- Web sayfaları ve Notion / Confluence dışa aktarımları (ZIP): internete bağlanmadan okunur.
- WhatsApp sohbet dışa aktarımları (Android ve iOS) ve konuşma dökümleri: her konuşma bir sohbet kaydı olur, müşteri ve temsilci rolleri eşlenir.

**İnceleme ve düzenleme**
- **Elle kayıt yazma** (Ctrl+Shift+N): veri setine boş bir taslak ekler, düzenleyicide yazarsınız.
- Kayıt listesi, renklendirilmiş düzenleyici (Markdown, kod blokları, JSON), toplu seçim ve toplu işlemler.
- Durumlar: Taslak → İncelemede → Onaylandı / Arşivlendi.
- Tam geri alma ve yineleme (Ctrl+Z / Ctrl+Shift+Z).
- Metin araması; duruma, kalite sorununa ve etikete göre süzme.
- **Etiketler:** renkli etiketler, Ctrl+1…5 ile tek tuşla etiketleme, `kategori:hukuk` gibi etiket grupları.

**Kalite ve güvenlik**
- 12 otomatik kalite kuralı:
  - boş alanlar;
  - geçiştirici yanıtlar;
  - soruyu tekrarlayan yanıtlar;
  - dengesiz uzunluk;
  - aynı yanıtlar;
  - kapanmamış kod blokları;
  - geçersiz JSON;
  - kişisel veri;
  - kopyalar;
  - benzer sorular;
  - çelişen yanıtlar;
  - bağlam uzunluğunu aşanlar.
- **Token sayımı:** her kaydın token sayısı gösterilir, modelin bağlam sınırını aşanlar işaretlenir.
- **Kişisel veri tarama ve maskeleme:** T.C. kimlik no (doğrulama haneli), IBAN, kart numarası, telefon, e-posta, iç IP/URL, API anahtarları ve parolalar. Kendi hassas terimlerinizi (müşteri adları, proje kod adları) de ekleyebilirsiniz.
- **Benzer sorular:** "Şifremi unuttum" ile "Parolamı nasıl sıfırlarım?" gibi aynı şeyi soran kayıtları bulur. Harf benzerliğiyle her zaman, bir embedding modeli bağlarsanız anlam benzerliğiyle de çalışır.

**Sürümler ve dışa aktarma**
- Numaralı sürümler, iki sürümün kelime kelime karşılaştırılması, bir sürüme geri dönme.
- **Bölme:** tohuma bağlı, tekrarlanabilir eğitim/doğrulama/test bölmesi.
  - İsterseniz oranlar her etiket içinde ayrı ayrı tutulur.
  - Birbirine benzeyen sorular aynı bölümde kalır, test soruları eğitim verisine sızmaz.
- Bağlam sınırını aşan, kopya ya da yapay zekânın düşük puan verdiği kayıtları dışarıda bırakma.
- Axolotl ve LLaMA-Factory için çalışmaya hazır LoRA ya da **QLoRA** (model 4 bit yüklenir, çok daha az GPU belleği) eğitim yapılandırması.
- **Ekran kartına göre ayar:** NVIDIA ya da **AMD (ROCm)** kartınızı ve belleğini seçin; uygulama modelin karta sığıp sığmadığını tahmin eder, sığmıyorsa ne yapacağınızı söyler. **Bellek tasarrufu** (Liger kernel, ara sonuçlar RAM'de) kaliteyi değiştirmeden belleği düşürür.

**Yapay zekâ özellikleri** (isteğe bağlı; bağlantı olmadan uygulamanın geri kalanı tam çalışır)
- **Yerel modeller:** Ollama, LM Studio, vLLM, LocalAI ya da OpenAI uyumlu herhangi bir sunucu. Veri bilgisayarınızdan çıkmaz.
- **Bulut servisleri:** OpenAI, Anthropic, Azure OpenAI, OpenRouter, Groq, DeepSeek.
  - Kayıtlar ancak siz açıkça izin verirseniz gönderilir.
  - API anahtarları işletim sisteminin şifre deposunda saklanır (Windows Kimlik Bilgisi Yöneticisi, macOS Anahtar Zinciri, Linux Secret Service).
- **Yapay zekâyla yazma** (Ctrl+G): modelin bir kaydın cevabını yazmasını izleyin, beğenmezseniz düzeltin.
- **Belgeden soru-cevap çıkarma:** model bir PDF ya da Word belgesinin her bölümü için sorular yazar; siz onayladıklarınızı eklersiniz.
- **Varyant üretme** (Ctrl+Shift+G): bir soruyu farklı biçimlerde (isterseniz günlük dille ve yazım hatalarıyla) yeniden yazdırın, veri setini zenginleştirin.
- **Modelle değerlendirme** (Ctrl+J): model her kayda 1–5 arası puan ve gerekçe verir, çelişen cevapları bulur.
- **Model değerlendirme (benchmark):** eğittiğiniz modelin test sorularına verdiği cevapları bir hakem model puanlar; sonuçlar saklanır, modeller karşılaştırılır.

**Diğer**
- **Veri setlerini birleştirme:** farklı projelerden ya da başka bir `.adstudio` dosyasından. Diğer dosya hiç değiştirilmez.
- **Arayüz:** Türkçe ve İngilizce, uygulama yeniden başlatılmadan değiştirilebilir. Açık ve koyu tema var.
- **Taşınabilir dosya:** bütün iş tek bir `.adstudio` dosyasında durur; kopyalayıp başka bilgisayarda açabilirsiniz.
- **Otomatik güncelleme ve hata kaydı.**

### Gizlilik

- Uygulama telemetri göndermez. İnternete yalnızca güncelleme denetlediğinizde bağlanır.
- Yerel bir model kullanırsanız hiçbir veri bilgisayarınızdan ya da yerel ağınızdan çıkmaz.
- Bulut servisine ya da ağ dışındaki bir sunucuya, siz izin kutusunu işaretlemedikçe hiçbir kayıt gönderilmez.

### Kurulum ve çalıştırma

- **Hazır paket:** GitHub **Releases** sayfasından işletim sisteminize uygun paketi indirin. Paketler bir sürüm etiketi (ör. `v1.0.0`) atıldığında yayınlanır.
- **Kaynaktan:** [.NET 10 SDK](https://dotnet.microsoft.com/download) kurulu olmalı.

  ```bash
  dotnet run --project src/AIDatasetStudio.Desktop
  ```

İlk açılışta **Yeni çalışma alanı** oluşturun, bir proje ve veri seti ekleyin. Hemen denemek için **Araçlar › Örnek Kayıt Üret…** seçili veri setine örnek kayıtlar ekler.

### Bilinen sınırlar

- Taranmış (görüntü olarak kaydedilmiş) PDF'ler için metin tanıma (OCR) yoktur.
- Notion, Confluence ve Zendesk gibi servislere doğrudan bağlanılmaz; bu servislerden dışa aktarılmış dosyalar okunur.
- Telegram gibi diğer sohbet uygulamalarının biçimleri ve sesli mesajlar desteklenmez.
- Kayıt listesi aynı anda yalnızca bir etikete göre süzülebilir.
- Uygulama eğitimin kendisini yapmaz. Veriyi ve eğitim yapılandırmasını hazırlar; eğitimi Axolotl, LLaMA-Factory ya da bir bulut servisinin ince ayar özelliğiyle siz başlatırsınız.

### Daha fazlası

- 📖 **Adım adım kullanım kılavuzu:** [docs/kullanim-kilavuzu.md](docs/kullanim-kilavuzu.md)
- 🗺️ **Ayrıntılı tasarım ve yol haritası:** [Plan.txt](Plan.txt)
- 📝 **Sürüm notları:** [CHANGELOG.md](CHANGELOG.md)

---

## 🚀 Overview

Training state-of-the-art AI models requires clean, consistent, and strictly validated datasets. Today, datasets are often scattered across messy spreadsheets, unformatted JSONL files, and random scripts. 

**AI Dataset Studio** is a privacy-first, cross-platform desktop application designed to treat dataset curation as a first-class engineering discipline. It stores data in a **model-agnostic format** and transforms it into target training formats (OpenAI, ShareGPT, ChatML, Alpaca, Llama-3, etc.) only at export time.

---

## ✨ Key Features (v0.3 Roadmap & MVP)

- 🔒 **100% Offline & Private:** Built with an offline-first architecture. Your proprietary data never touches third-party clouds unless you connect a cloud model service yourself and allow it.
- 📦 **Portable `.adstudio` Projects:** Workspaces and datasets are saved into self-contained, portable SQLite database files; Tools › Merge Datasets brings datasets over from another `.adstudio` file without changing it.
- 🌐 **Bilingual UI:** English and Turkish, switchable at runtime (View › Language).
- 🎨 **Light & Dark Themes:** Token-based design system with Fluent UI icons (View › Theme).
- 🗂️ **Universal Schema Engine:** Support for Instruction (SFT), Multi-turn Chat and Preference (DPO/ORPO/RLHF) datasets.
- ⚡ **High-Performance Virtualization:** Seamlessly navigate and search 100,000+ records with zero UI lag.
- 📐 **Token & Context Engine:** Real-time token counting (Tiktoken / BPE) to prevent Out-Of-Memory (OOM) training failures.
- 🛡️ **PII & Secret Scrubber:** Automatically detect and mask emails, phone numbers, API keys, and sensitive tokens before training.
- 🧹 **Dataset Health Engine:** Detect exact copies and near-duplicate questions (MinHash, and by meaning with an embedding model), empty prompts, syntax errors, and formatting anomalies.
- 🎲 **Train / Validation / Test Split:** Deterministic, seed-based dataset splitting with stratified sampling.
- 🕰️ **Dataset Versions:** Freeze a dataset as v1.0.0, compare any two versions word by word, export a version exactly as it was frozen, and go back to it (undoable).
- 🏷️ **Tags:** Colored tags shared by all datasets, toggled with Mod+1…5, on one record or many; filter the list by tag and split exports within each tag or tag group (`kategori:hukuk`).
- ✨ **Write with a local or cloud model:** Connect Ollama, LM Studio, vLLM or LocalAI, or OpenAI, Anthropic, Azure OpenAI, OpenRouter, Groq or DeepSeek (Tools › AI Connections), and let the model write an answer, a chat reply, or the chosen and a worse rejected answer of a DPO pair (Mod+G). You review the text before saving, and the model is recorded in the record's lineage. Nothing leaves your network unless you allow a remote server or a cloud service; API keys are kept in Windows Credential Manager, the macOS keychain or the Secret Service on Linux.
- 📄 **Questions from documents:** File › Extract Q&A from Document reads a PDF, Word, Markdown, text or HTML document, or a Notion or Confluence export, splits it into passages along its headings, and has the model write the questions each passage answers. Review and edit them next to the passage, then add them as draft records that remember their source passage (Edit › Undo takes them back).
- 🌿 **Variations:** Record › Generate Variations (Mod+Shift+G) has the model ask a record's question in other words — casual and with typos if you like, with the answer reworded too if you choose. Review the variants, then add them as draft records of the record's family: a family stays in one split part and is not flagged as near duplicates of itself.
- ⚖️ **AI judge:** Record › Judge with AI (Mod+J) has the model rate a record from 1 to 5 with a reason — checking an answer written from a document against its passage and the order of a preference pair — and compare it with records that ask the same thing to find contradicting answers. Scores show in the list, filters list the low ones, and the overview shows how they spread.
- 🧭 **Questions alike in meaning:** Choose an embedding model on your AI connection (e.g. `bge-m3` in Ollama) and the dataset overview's **Compare by meaning** finds questions asked in other words ("Parolamı unuttum" / "Şifremi nasıl sıfırlarım?"). They join the near-duplicate groups, so the filter, the clean-up and the split treat them alike. Vectors are made only when you start it and are kept in the workspace.
- 🏆 **Model evaluation:** Tools › Evaluate a Model asks a model — the base model, or the one you trained — the questions of the dataset's test part (the records the last export put in `test.jsonl`, so questions it was not trained on), and a grader model scores each answer against the dataset's answer from 1 to 5, with word overlap (F1) alongside. Finished evaluations are kept with the dataset to compare models; the answers can be saved as a JSON Lines report.
- 🔄 **Universal Import & Export:**
  - **Import:** JSONL, JSON, Parquet, CSV (with a column mapping wizard), Excel, Markdown, plain text, PDF, Word, HTML pages, Notion or Confluence exports (ZIP), and customer conversations (WhatsApp chat exports and transcripts) as chat records.
  - **Export:** OpenAI JSONL, ShareGPT, ChatML, Alpaca, Llama-3, HuggingFace DPO; optionally leaving out records the AI judge scored low, and with a ready-to-run Axolotl or LLaMA-Factory LoRA or QLoRA configuration next to the files, set for your GPU (NVIDIA or AMD ROCm, its memory, optional memory saving) with an estimate of whether the model fits.
  - **Hugging Face:** File › Import from Hugging Face lists a Hub dataset's files from its id (`tatsu-lab/alpaca`), downloads the one you choose and opens it in the import wizard; gated and private datasets with a token in `HF_TOKEN`.
- ✍️ **Records written by hand:** Record › New Record (Mod+Shift+N) adds an empty draft of the dataset's type and puts the keyboard in it.

---

## 🛠️ Tech Stack

- **Platform:** .NET 10 (LTS) / C# 14
- **UI Framework:** [Avalonia UI](https://avaloniaui.net/) 12 (Cross-platform XAML) + CommunityToolkit.Mvvm
- **Design:** Fluent theme + own design tokens ([docs/design-system.md](docs/design-system.md)), [Fluent UI System Icons](https://github.com/davidxuang/FluentIcons)
- **Architecture:** Clean Architecture + lightweight CQRS (separate command/query services, no MediatR) + MVVM
- **Storage:** SQLite (WAL Mode, FTS5 Full-Text Search, JSON1) via Entity Framework Core 10
- **Tokenization:** `Microsoft.ML.Tokenizers` (offline `cl100k_base` / `o200k_base` vocabularies)
- **Editor:** AvaloniaEdit, with built-in highlighting for Markdown, fenced code blocks and JSON
- **Documents:** [PdfPig](https://github.com/UglyToad/PdfPig) for PDF text and layout; an own streaming reader for Word and Excel files
- **Tests:** xUnit v3 on Microsoft Testing Platform

---

## 🏗️ Architecture

```text
AIDatasetStudio/
├── src/
│   ├── AIDatasetStudio.Domain/          # Entities, typed record payloads, lifecycle rules (zero dependencies)
│   ├── AIDatasetStudio.Application/     # Use-case interfaces, DTOs, import conversion, quality rules and PII scanner
│   ├── AIDatasetStudio.Infrastructure/  # EF Core SQLite, .adstudio workspace files, migrations, file readers
│   └── AIDatasetStudio.Desktop/         # Avalonia UI (Views, ViewModels, Themes, Localization)
├── tests/
│   ├── AIDatasetStudio.Domain.Tests/
│   ├── AIDatasetStudio.Application.Tests/    # Import conversion, quality rules, PII detection
│   ├── AIDatasetStudio.Infrastructure.Tests/
│   └── AIDatasetStudio.Desktop.Tests/   # Localization + headless UI tests
├── docs/                                # Plan review, design system
├── Plan.txt                             # Software Design Document (SDD)
└── AIDatasetStudio.slnx
```

---

## 📥 Install

Download the installer for your system from the repository's **Releases** page:

| System | File |
|---|---|
| Windows 10/11 (x64) | `AIDatasetStudio-win-x64-Setup.exe` (or the portable `.zip`) |
| macOS (Apple silicon / Intel) | `AIDatasetStudio-osx-arm64-Setup.pkg` / `AIDatasetStudio-osx-x64-Setup.pkg` |
| Linux (x64) | `AIDatasetStudio-linux-x64.AppImage` (`chmod +x`, then run) |

No .NET installation is needed. Until signing certificates are configured, Windows shows a SmartScreen warning (*More info › Run anyway*) and macOS asks you to open the app with right-click › *Open*. The app never goes online by itself: **Help › Check for Updates** looks for a new version, and **Check for Updates at Startup** (off by default) does so when the app starts. Errors are written to a local log (**Help › About › Open Log Folder**).

Publishing a release and setting up signing: [docs/release.md](docs/release.md) (Turkish). Changes per version: [CHANGELOG.md](CHANGELOG.md).

---

## 🚀 Getting Started

Prerequisite: [.NET 10 SDK](https://dotnet.microsoft.com/download) (pinned in `global.json`). Set `AVALONIA_TELEMETRY_OPTOUT=1` to stop Avalonia's build task from sending usage statistics (CI does).

```bash
dotnet build AIDatasetStudio.slnx                      # build everything
dotnet run --project src/AIDatasetStudio.Desktop       # start the desktop app
dotnet test --solution AIDatasetStudio.slnx            # run all tests
```

Adding or changing UI text: put every user-visible string in `src/AIDatasetStudio.Desktop/Localization/Strings.resx` (English) **and** `Strings.tr.resx` (Turkish), then use `{l:Tr Key}` in XAML or `Localizer` in code. Tests fail if a translation or key is missing.

Importing data: select a dataset, then **File › Import from File…** (or the Import button above the record list). The wizard detects the format and layout, lets you adjust CSV settings and column mapping against a live preview, and reports every skipped row:

| Source | Recognized layouts | Target dataset types |
|---|---|---|
| JSON Lines (`.jsonl`, `.ndjson`) and JSON arrays (`.json`) | OpenAI messages, ShareGPT, Alpaca, prompt/completion, Hugging Face DPO, OpenAI preference, any flat object | Instruction, Chat, Preference |
| CSV / TSV (`.csv`, `.tsv`, `.txt`) | Any columns, mapped by name (English and Turkish names are suggested automatically); UTF-8, UTF-16 and Turkish Windows-1254 files from Excel | Instruction, Chat, Preference |
| Excel workbooks (`.xlsx`, `.xlsm`) | One sheet or all sheets at once, each with its own header row; columns mapped by name as for CSV; dates read as ISO text | Instruction, Chat, Preference |
| Markdown (`.md`, `.markdown`) | One record per heading with text under it: the heading asks, the text answers; the headings above it can fill the input | Instruction, Chat, Preference |
| PDF (`.pdf`) and Word (`.docx`, `.docm`) | Read like Markdown: headings (by font size in PDFs, by style in Word), lists and tables are recognised; running headers, page numbers and tables of contents are left out. Scanned PDFs (no text) and Word 97–2003 files are explained | Instruction, Chat, Preference |
| Plain text (`.txt`) | One record per paragraph: the first line asks, the lines after it answer. A `.txt` file is read as a table or as text, whichever it looks like, and the wizard can switch | Instruction, Chat, Preference |
| HTML pages (`.html`, `.htm`) and ZIPs of pages (Notion, Confluence exports) | Read like Markdown; scripts, navigation and a site's header and footer are left out. A ZIP is one document of pages, a page before the pages under it, each under its title | Instruction, Chat, Preference |
| Conversation logs: WhatsApp chat exports (Android and iOS) and transcripts (`Customer: …` / `**Agent:** …`, `.txt` or `.md`) | One record per conversation; speakers are mapped like columns (the customer asks, the agent answers). A chat starts a new conversation after a 6-hour pause, a transcript at `---` or headings; media and deleted messages are left out | Chat; Instruction for single exchanges |

Reviewing records: edit fields directly in the right-hand panel; changes are saved with **Mod+S** or automatically when you move on. Fields colour Markdown, fenced code blocks (```` ```json ````, `python`, `javascript`, `csharp`, `sql`, `bash`, `xml`) and JSON documents. Shift/Ctrl-click selects several records for batch actions. (**Mod** is Ctrl on Windows/Linux and Cmd on macOS.)

| Keys | Action |
|---|---|
| Mod+Enter | Approve and go to the next record |
| Mod+Shift+Backspace | Archive and go to the next record |
| F8 / Shift+F8 | Next / previous record (↑/↓ also work in the list) |
| Mod+S | Save changes |
| Mod+Z / Mod+Shift+Z (or Mod+Y) | Undo / redo edits, status changes and deletes, including batch actions |
| Mod+F | Search records |
| Delete | Delete the selected records (in the list) |
| Mod+Shift+M | Masked preview of personal data |
| Mod+E | Export the dataset |
| Mod+1 … Mod+5 | Add or remove the tag with that shortcut (on all selected records) |
| Mod+G | Write the answer side of the record with the AI model (again to stop) |

Tokens and quality: every record is analysed in the background with an offline tokenizer (`o200k_base` or `cl100k_base`, chosen per dataset together with its context length). With no record selected, the right-hand panel shows the **dataset overview**: quality score, token totals, a length histogram and the issues found, each with **Show** (filters the list) and, where it makes sense, **Archive** (undoable). The list shows tokens and a score per record and can be filtered by quality; the editor updates both while you type.

| Severity | Rules |
|---|---|
| Error (−40) | Empty field or turn, chosen = rejected answer, longer than the context length |
| Warning (−10) | Throwaway answer, answer 50× longer than the question, answer repeats the question, unclosed code block, invalid JSON, personal data, exact duplicate, same question with a different answer, near-duplicate question (85%+ of character 5-grams shared) |

Exporting: **File › Export to File…** (Mod+E) or the Export button above the list opens a wizard with a live preview of how many records go out and the first lines exactly as written.

| Dataset type | Formats |
|---|---|
| Instruction | OpenAI JSONL, Alpaca, ShareGPT, ChatML text, Llama 3 text |
| Chat | OpenAI JSONL, ShareGPT, ChatML text, Llama 3 text, Alpaca (single-turn chats only) |
| Preference | Hugging Face DPO, OpenAI JSONL (OpenAI's DPO shape) |

By default only approved records are exported, without records longer than the context length or exact copies, and with personal data masked. A split writes `train.jsonl`, `val.jsonl` and `test.jsonl` (80/10/10, 90/10 or custom shares) from a hash of the seed and each record's question: the same seed always gives the same files, new records never move existing ones, and records asking the same or nearly the same question stay in one file. Files appear only when complete, and the settings (including the seed) are remembered per dataset.

Near-duplicate questions: after the background analysis, records whose questions share at least 85% of their character 5-grams ("Kargo ücreti ne kadar?" / "Kargo ücreti ne kadardır?") are flagged; the editor names the first similar record, and the overview's **Archive** keeps the first of each group. With an embedding model on the AI connection, the overview's **Similar in meaning** card also compares questions by meaning (cosine 0.90 by default, 0.80–0.98): their pairs join the same groups. For preference pairs, the editor's **Compare the answers** button marks the words only the chosen or only the rejected answer has, live while you type.

Tags: **Add tag** in the editor finds a tag as you type or creates it (a prefix such as `kategori:` puts it in a group); the × on a chip removes it. With several records selected, the panel adds a tag to all of them or removes one from all. **Tools › Tags…** renames, recolors and deletes tags and gives them Mod+1…5; **Record › Tag** lists them. The list shows each record's tags and filters by tag (or *Untagged*). In the export wizard, **Split within** gives every tag, or every tag of a group, exactly the chosen shares — unlike the plain split, new records can then move a few records at the cut.

Personal data: e-mail, phone, Turkish national ID, IBAN, card numbers, internal IPs and URLs, API keys, passwords and your own terms (**Tools › Custom Sensitive Terms…**) are detected locally. **Record › Masked Preview** shows the record with consistent placeholders (`[EMAIL_1]`); **Apply Masking** puts the masked text into the editor, to be saved (and undone) like any edit.

Adding a database migration (after changing the EF Core model):

```bash
dotnet tool restore
dotnet ef migrations add <Name> --project src/AIDatasetStudio.Infrastructure --output-dir Persistence/Migrations
```

---

## 🧭 Status

| Milestone | Scope | State |
|---|---|---|
| M0 | Solution skeleton, `.adstudio` files, bilingual shell | ✅ |
| M1 | Project/dataset tree, record services, full-text search, paging, settings | ✅ |
| M2 | Import (JSONL, JSON, CSV wizard) | ✅ |
| M3 | Record editor, review shortcuts, batch actions, undo/redo | ✅ |
| M4 | Tokens, health rules, PII masking | ✅ |
| M5 | Export + train/val/test split | ✅ |
| M6 | Installers (Velopack), optional signing, updates, first release 1.0.0 | ✅ |
| M7 | Design system, light/dark themes, welcome screen | ✅ |
| M8 | Dataset versions: freeze, compare with a word-level diff, go back, export a version | ✅ |
| M9 | Tags: editor chips, Mod+1…5, batch tagging, tag filter, tag manager, stratified split | ✅ |
| M10 | Near-duplicate questions (MinHash + exact Jaccard), clean-up and one split part per group; chosen vs rejected diff for preference pairs | ✅ |
| M11 | Excel (multi-sheet), Markdown and plain-text import; Markdown, code and JSON highlighting in the editor | ✅ |
| M12 | Local model connections (Ollama, LM Studio, vLLM, LocalAI) and writing records with AI | ✅ |
| M13 | PDF and Word import; question–answer extraction from documents with a model, reviewed before adding | ✅ |
| M14 | Synthetic variations of records, reviewed before adding; a record and its variations stay in one split part | ✅ |
| M15 | LLM-as-a-judge scores with reasons, grounding and preference checks, and contradicting answers | ✅ |
| M16 | Questions alike in meaning: an embedding model on the AI connection, vectors kept in the workspace, pairs joined to the near-duplicate groups | ✅ |
| M17 | HTML pages and Notion or Confluence exports (ZIP of pages) as documents, for import and Q&A extraction | ✅ |
| M18 | Customer conversations (WhatsApp chat exports, transcripts) as chat records, speakers mapped to roles | ✅ |
| M19 | Cloud model services (OpenAI, Anthropic, Azure OpenAI, OpenRouter, Groq, DeepSeek) with consent, API keys in the OS key store | ✅ |
| M20 | Axolotl and LLaMA-Factory training configurations written next to exported files | ✅ |
| M21 | Merging datasets of one type from several projects into a new dataset | ✅ |
| M22 | Model evaluation: a model answers the test part's questions, a grader scores them against the dataset's answers; earlier evaluations kept to compare | ✅ |
| M23 | Merging datasets from another `.adstudio` file, read from a copy and never changed; tags matched by name | ✅ |
| M24 | Writing a new record by hand (Record › New Record, Mod+Shift+N) | ✅ |
| M25 | Parquet files and importing a dataset straight from the Hugging Face Hub | ✅ |
| M26 | QLoRA training configurations (model loaded in 4 bits) | ✅ |
| M27 | Training settings for your GPU: memory estimate, AMD (ROCm), memory saving with Liger kernels | ✅ |

**Tools › Generate Sample Records** fills the selected dataset with synthetic Turkish/English records.

---

## 📋 Documentation

**User guide (Turkish):** [docs/kullanim-kilavuzu.md](docs/kullanim-kilavuzu.md) explains every feature step by step, with the menu names as the app shows them.

For detailed architectural specifications, data schemas, and the product roadmap, refer to [Plan.txt](Plan.txt).
Review notes, open design decisions and the milestone plan are in [docs/plan-review.md](docs/plan-review.md).
Packaging, signing and publishing are described in [docs/release.md](docs/release.md); open-source components in [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).

---

## 📄 License

Proprietary / Private Repository - All rights reserved.
