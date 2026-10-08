# AI Dataset Studio 🧠📊

> **The Modern, Offline-First Desktop Studio for AI Dataset Engineering & Curation.**  
> Curate, clean, tokenize, version, and export LLM datasets (Instruction, Chat, DPO/RLHF, Tool Calling) locally on Windows, macOS, and Linux.

---

## 🚀 Overview

Training state-of-the-art AI models requires clean, consistent, and strictly validated datasets. Today, datasets are often scattered across messy spreadsheets, unformatted JSONL files, and random scripts. 

**AI Dataset Studio** is a privacy-first, cross-platform desktop application designed to treat dataset curation as a first-class engineering discipline. It stores data in a **model-agnostic format** and transforms it into target training formats (OpenAI, ShareGPT, ChatML, Alpaca, Llama-3, etc.) only at export time.

---

## ✨ Key Features (v0.3 Roadmap & MVP)

- 🔒 **100% Offline & Private:** Built with an offline-first architecture. Your proprietary data never touches third-party clouds.
- 📦 **Portable `.adstudio` Projects:** Workspaces and datasets are saved into self-contained, portable SQLite database files.
- 🌐 **Bilingual UI:** English and Turkish, switchable at runtime (View › Language).
- 🎨 **Light & Dark Themes:** Token-based design system with Fluent UI icons (View › Theme).
- 🗂️ **Universal Schema Engine:** Support for Instruction (SFT), Multi-turn Chat, Preference (DPO/ORPO/RLHF), and Tool Calling formats.
- ⚡ **High-Performance Virtualization:** Seamlessly navigate and search 100,000+ records with zero UI lag.
- 📐 **Token & Context Engine:** Real-time token counting (Tiktoken / BPE) to prevent Out-Of-Memory (OOM) training failures.
- 🛡️ **PII & Secret Scrubber:** Automatically detect and mask emails, phone numbers, API keys, and sensitive tokens before training.
- 🧹 **Dataset Health Engine:** Detect exact and near-duplicates, empty prompts, syntax errors, and formatting anomalies.
- 🎲 **Train / Validation / Test Split:** Deterministic, seed-based dataset splitting with stratified sampling.
- 🔄 **Universal Import & Export:**
  - **Import:** CSV (with column mapping wizard), JSONL, and standard JSON.
  - **Export:** OpenAI JSONL, ShareGPT, ChatML, Alpaca, Llama-3, HuggingFace DPO.

---

## 🛠️ Tech Stack

- **Platform:** .NET 10 (LTS) / C# 14
- **UI Framework:** [Avalonia UI](https://avaloniaui.net/) 12 (Cross-platform XAML) + CommunityToolkit.Mvvm
- **Design:** Fluent theme + own design tokens ([docs/design-system.md](docs/design-system.md)), [Fluent UI System Icons](https://github.com/davidxuang/FluentIcons)
- **Architecture:** Clean Architecture + lightweight CQRS (separate command/query services, no MediatR) + MVVM
- **Storage:** SQLite (WAL Mode, FTS5 Full-Text Search, JSON1) via Entity Framework Core 10
- **Tokenization:** `Microsoft.ML.Tokenizers` (offline `cl100k_base` / `o200k_base` vocabularies)
- **Editor:** AvaloniaEdit (TextMate syntax highlighting)
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

Reviewing records: edit fields directly in the right-hand panel; changes are saved with **Mod+S** or automatically when you move on. Shift/Ctrl-click selects several records for batch actions. (**Mod** is Ctrl on Windows/Linux and Cmd on macOS.)

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

Tokens and quality: every record is analysed in the background with an offline tokenizer (`o200k_base` or `cl100k_base`, chosen per dataset together with its context length). With no record selected, the right-hand panel shows the **dataset overview**: quality score, token totals, a length histogram and the issues found, each with **Show** (filters the list) and, where it makes sense, **Archive** (undoable). The list shows tokens and a score per record and can be filtered by quality; the editor updates both while you type.

| Severity | Rules |
|---|---|
| Error (−40) | Empty field or turn, chosen = rejected answer, longer than the context length |
| Warning (−10) | Throwaway answer, answer 50× longer than the question, answer repeats the question, unclosed code block, invalid JSON, personal data, exact duplicate, same question with a different answer |

Exporting: **File › Export to File…** (Mod+E) or the Export button above the list opens a wizard with a live preview of how many records go out and the first lines exactly as written.

| Dataset type | Formats |
|---|---|
| Instruction | OpenAI JSONL, Alpaca, ShareGPT, ChatML text, Llama 3 text |
| Chat | OpenAI JSONL, ShareGPT, ChatML text, Llama 3 text, Alpaca (single-turn chats only) |
| Preference | Hugging Face DPO, OpenAI JSONL (OpenAI's DPO shape) |

By default only approved records are exported, without records longer than the context length or exact copies, and with personal data masked. A split writes `train.jsonl`, `val.jsonl` and `test.jsonl` (80/10/10, 90/10 or custom shares) from a hash of the seed and each record's question: the same seed always gives the same files, new records never move existing ones, and records asking the same question stay in one file. Files appear only when complete, and the settings (including the seed) are remembered per dataset.

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

**Tools › Generate Sample Records** fills the selected dataset with synthetic Turkish/English records.

---

## 📋 Documentation

For detailed architectural specifications, data schemas, and the product roadmap, refer to [Plan.txt](Plan.txt).
Review notes, open design decisions and the milestone plan are in [docs/plan-review.md](docs/plan-review.md).
Packaging, signing and publishing are described in [docs/release.md](docs/release.md); open-source components in [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).

---

## 📄 License

Proprietary / Private Repository - All rights reserved.
