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
│   ├── AIDatasetStudio.Application/     # Use-case interfaces and DTOs (e.g. IWorkspaceService)
│   ├── AIDatasetStudio.Infrastructure/  # EF Core SQLite, .adstudio workspace files, migrations
│   └── AIDatasetStudio.Desktop/         # Avalonia UI (Views, ViewModels, Services, Localization)
├── tests/
│   ├── AIDatasetStudio.Domain.Tests/
│   ├── AIDatasetStudio.Infrastructure.Tests/
│   └── AIDatasetStudio.Desktop.Tests/   # Localization + headless UI tests
├── docs/                                # Plan review & decision notes
├── Plan.txt                             # Software Design Document (SDD)
└── AIDatasetStudio.slnx
```

---

## 🚀 Getting Started

Prerequisite: [.NET 10 SDK](https://dotnet.microsoft.com/download) (pinned in `global.json`).

```bash
dotnet build AIDatasetStudio.slnx                      # build everything
dotnet run --project src/AIDatasetStudio.Desktop       # start the desktop app
dotnet test --solution AIDatasetStudio.slnx            # run all tests
```

Adding or changing UI text: put every user-visible string in `src/AIDatasetStudio.Desktop/Localization/Strings.resx` (English) **and** `Strings.tr.resx` (Turkish), then use `{l:Tr Key}` in XAML or `Localizer` in code. Tests fail if a translation or key is missing.

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
| M2 | Import (JSONL, JSON, CSV wizard) | ⏳ |
| M3 | Record editor, review shortcuts, undo/redo | ⏳ |
| M4 | Tokens, health rules, PII masking | ⏳ |
| M5 | Export + train/val/test split | ⏳ |
| M6–M7 | Packaging, visual design pass | ⏳ |

Until import lands, **Tools › Generate Sample Records** fills the selected dataset with synthetic Turkish/English records.

---

## 📋 Documentation

For detailed architectural specifications, data schemas, and the product roadmap, refer to [Plan.txt](Plan.txt).
Review notes, open design decisions and the milestone plan are in [docs/plan-review.md](docs/plan-review.md).

---

## 📄 License

Proprietary / Private Repository - All rights reserved.
