# AI Dataset Studio 🧠📊

> **The Modern, Offline-First Desktop Studio for AI Dataset Engineering & Curation.**  
> Curate, clean, tokenize, version, and export LLM datasets (Instruction, Chat, DPO/RLHF, Tool Calling) locally on Windows, macOS, and Linux.

---

## 🚀 Overview

Training state-of-the-art AI models requires clean, consistent, and strictly validated datasets. Today, datasets are often scattered across messy spreadsheets, unformatted JSONL files, and random scripts. 

**AI Dataset Studio** is a privacy-first, cross-platform desktop application designed to treat dataset curation as a first-class engineering discipline. It stores data in a **model-agnostic format** and transforms it into target training formats (OpenAI, ShareGPT, ChatML, Alpaca, Llama-3, etc.) only at export time.

---

## ✨ Key Features (v0.3 Roadmap & MVP)

- 🔒 **100% Offline & Private:** Built with an offline-first architecture. Your proprietary data never touches third-party clouds unless you connect a cloud model service yourself and allow it.
- 📦 **Portable `.adstudio` Projects:** Workspaces and datasets are saved into self-contained, portable SQLite database files.
- 🌐 **Bilingual UI:** English and Turkish, switchable at runtime (View › Language).
- 🎨 **Light & Dark Themes:** Token-based design system with Fluent UI icons (View › Theme).
- 🗂️ **Universal Schema Engine:** Support for Instruction (SFT), Multi-turn Chat, Preference (DPO/ORPO/RLHF), and Tool Calling formats.
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
- 🔄 **Universal Import & Export:**
  - **Import:** JSONL, JSON, CSV (with a column mapping wizard), Excel, Markdown, plain text, PDF, Word, HTML pages, Notion or Confluence exports (ZIP), and customer conversations (WhatsApp chat exports and transcripts) as chat records.
  - **Export:** OpenAI JSONL, ShareGPT, ChatML, Alpaca, Llama-3, HuggingFace DPO; optionally with a ready-to-run Axolotl or LLaMA-Factory LoRA configuration next to the files.

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

**Tools › Generate Sample Records** fills the selected dataset with synthetic Turkish/English records.

---

## 📋 Documentation

For detailed architectural specifications, data schemas, and the product roadmap, refer to [Plan.txt](Plan.txt).
Review notes, open design decisions and the milestone plan are in [docs/plan-review.md](docs/plan-review.md).
Packaging, signing and publishing are described in [docs/release.md](docs/release.md); open-source components in [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).

---

## 📄 License

Proprietary / Private Repository - All rights reserved.
