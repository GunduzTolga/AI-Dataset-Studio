# Changelog

All notable changes to AI Dataset Studio. Versions follow [Semantic Versioning](https://semver.org);
from 1.0.0 on, every version opens `.adstudio` files written by earlier versions.

## [1.0.0] - 2026-10-08

The first release: the complete MVP of the design document (SDD §20), offline on Windows, macOS
and Linux, in English and Turkish.

### Workspaces and records
- Portable `.adstudio` workspace files (SQLite) with projects and datasets of three types:
  instruction, chat and preference (DPO).
- A virtualized record list for 100,000+ records with full-text search (Turkish-aware), status
  filters and quality filters.
- A type-aware editor with automatic saving, review shortcuts (Mod+Enter, Mod+Shift+Backspace,
  F8), multi-select batch actions and undo / redo.

### Import and export
- Import from JSON Lines, JSON arrays and CSV (column mapping, delimiter and encoding detection,
  Turkish Excel files) with a live preview and a report of skipped rows.
- Export to OpenAI JSONL, ShareGPT, Alpaca, Hugging Face DPO, ChatML and Llama 3 text, with a
  hash-based train / validation / test split that keeps questions in one file and stays stable
  as the dataset grows.

### Quality and privacy
- Offline token counts (o200k_base, cl100k_base) against each dataset's context length.
- Eleven quality rules and a 0–100 score, with a dataset overview and one-click clean-up.
- Detection and masking of personal data and secrets (e-mail, phone, Turkish ID, IBAN, cards,
  internal addresses, API keys, passwords and your own terms), as a preview and at export.

### Installation
- Installers for Windows (x64), macOS (Apple silicon and Intel) and Linux (x64 AppImage).
- Optional update check (off by default; Help › Check for Updates). No telemetry.
- Unexpected errors are written to a local log (Help › About › Open Log Folder).
