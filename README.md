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
- 📦 **Portable `.aids` Projects:** Workspaces and datasets are saved into self-contained, portable SQLite database files.
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

- **Platform:** .NET 9 / C# 13
- **UI Framework:** [Avalonia UI](https://avaloniaui.net/) 11+ (Cross-platform XAML)
- **Architecture:** Clean Architecture + CQRS + MVVM
- **Storage:** SQLite (WAL Mode, FTS5 Full-Text Search, JSON1) via Entity Framework Core 9
- **Tokenization:** `Microsoft.ML.Tokenizers` & `Tiktoken-CSharp`
- **Editor:** AvaloniaEdit (Monaco/TextMate syntax highlighting)

---

## 🏗️ Architecture

```text
AIDatasetStudio/
├── src/
│   ├── AIDatasetStudio.Domain/          # Core entities, Enums, Value Objects (Zero dependencies)
│   ├── AIDatasetStudio.Application/     # CQRS Handlers, Services, Interfaces, DTOs
│   ├── AIDatasetStudio.Infrastructure/  # EF Core SQLite, File I/O, Tiktoken Tokenizer, Import/Export
│   └── AIDatasetStudio.Desktop/         # Avalonia UI (Views, ViewModels, Themes, Controls)
├── tests/
│   ├── AIDatasetStudio.Domain.Tests/
│   ├── AIDatasetStudio.Application.Tests/
│   └── AIDatasetStudio.Infrastructure.Tests/
├── docs/                                # Software Design Document (SDD) & ADRs
└── .gitignore
```

---

## 📋 Documentation

For detailed architectural specifications, data schemas, and the product roadmap, refer to [Plan.txt](Plan.txt).

---

## 📄 License

Proprietary / Private Repository - All rights reserved.
