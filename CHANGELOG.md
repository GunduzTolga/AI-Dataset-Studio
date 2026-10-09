# Changelog

All notable changes to AI Dataset Studio. Versions follow [Semantic Versioning](https://semver.org);
from 1.0.0 on, every version opens `.adstudio` files written by earlier versions.

## [Unreleased]

### Training configurations
- The export wizard's new **Training configuration** writes a ready-to-run LoRA fine-tuning setup
  next to the exported files: `axolotl.yaml` for Axolotl, or `dataset_info.json` and
  `llamafactory.yaml` for LLaMA-Factory. Choose the base model by its Hugging Face id; the
  suggestion follows the format's chat template.
- The configuration names the files by name, so the folder can be copied to a training machine as
  it is. It reads each format the way the tool expects (chat templates, Alpaca, completion text,
  DPO pairs), uses the dataset's context length and the split's seed, evaluates on the validation
  file and keeps the test file out of training.
- Formats a tool cannot read are explained instead of exported, and the choice is remembered
  with the dataset's other export settings.

### Cloud model services
- **AI Connections** can now connect OpenAI, Anthropic, Azure OpenAI, OpenRouter, Groq and
  DeepSeek, besides servers on your computer or network. Anthropic is spoken to through its own
  Messages API; the others through the OpenAI-compatible one.
- A cloud service is never asked anything before you allow it: the warning names the service and
  says its terms of use and privacy policy apply to the records you write with it.
- **API keys are kept in the operating system's key store:** Windows Credential Manager, the
  macOS keychain, or the Secret Service on Linux (GNOME Keyring, KWallet, KeePassXC). A saved key
  is not shown again; it can be replaced or deleted, and goes when its connection is deleted.
  Keys kept in the settings file by earlier versions move to the key store.
- Without a key store, keys stay in the settings file, which only you can read, and the window
  says so. A connection without a key uses the service's environment variable, such as
  `OPENAI_API_KEY` or `ANTHROPIC_API_KEY`.
- OpenAI's reasoning models are asked for an answer length the way they accept it, and a model
  that takes only its own temperature is asked again without one.
- Services without embeddings (Anthropic, Groq, DeepSeek) hide the embedding model.

### Customer conversations
- WhatsApp chat exports (Android and iOS, in any date format) and transcripts that name the
  speaker before each message ("Customer: …", "**Agent:** …", in `.txt` or `.md` files) are
  recognised in **Import from File** and imported as chat records, one per conversation.
- The wizard shows the log's **speakers** in place of columns: choose the customer (user turns)
  and, optionally, the agent (assistant turns). Both are suggested from their names ("Müşteri",
  "Mağaza Destek") or from who opens the conversations; without an agent, everyone else answers.
- A chat starts a new conversation after a 6-hour pause, a transcript at lines such as `---` or at
  headings (or else at blank lines). Messages in a row from one side become one turn; a
  conversation starts with the customer and ends with an answer. System lines, media and deleted
  messages are left out, and "<This message was edited>" is cut.
- Instruction datasets take conversations of one question and its answer, so a Markdown FAQ
  written as "**Soru:** … / **Cevap:** …" under headings gives one record per section.
- Records point to the conversation's first line: `WhatsApp Chat.txt#line=3`.

### HTML pages and Notion or Confluence exports
- HTML pages (`.html`, `.htm`) and ZIPs of pages can be imported and used for **Extract Q&A from
  Document**: headings, paragraphs, nested lists, tables and preformatted text are read, while
  scripts, styles, navigation, forms, hidden parts and a site's header and footer are left out.
  The page's declared charset is honoured.
- **Confluence** space exports (HTML) leave out breadcrumbs, "Created by …" lines, attachments,
  comments, the footer and the space's index page, and page titles lose their "Space : " prefix.
- **Notion** exports (Markdown or HTML, also in parts) lose the 32-digit ids in page names and the
  property tables of database pages.
- A ZIP is read as one document of pages, a page before the pages under it. Each page starts with
  its title, so passages and sections carry the page they come from, and records point to it:
  `Export.zip#page=2`. A page always starts a new passage, so short pages are not mixed.
  Archives without pages, damaged or oversized ones are explained.

### Questions alike in meaning
- An AI connection can name an **embedding model** (e.g. `bge-m3` or `nomic-embed-text`);
  **Test** then also turns a text into a vector and says how long it is.
- The dataset overview's new **Similar in meaning** card compares the questions of an
  instruction or chat dataset by meaning: "Parolamı unuttum, ne yapmalıyım?" and "Şifremi nasıl
  sıfırlarım?" share few words but mean the same. Questions are sent to the embedding model only
  when you start it, each distinct question once, with progress and stop; a chat's system prompt
  is left out.
- Pairs whose vectors are close (cosine 0.90 by default, adjustable from 0.80 to 0.98) join the
  near-duplicate groups: the *Near-duplicate questions* rule, filter and clean-up, the split and
  versions treat them like questions that share their wording. Variations of a record still do
  not count.
- Vectors are kept in the workspace, so they are made once. New and edited questions are compared
  by their wording until **Compare N more questions**; another embedding model on the connection
  offers to start over with it, and **Turn off** goes back to wording only.
- The near-duplicate issue now reads "Nearly the same question as #1, in wording or in meaning."

### AI judge
- **Record › Judge with AI** (Mod+J, also in the editor's new AI JUDGMENT section) has the model
  rate the shown record from 1 to 5 as a training example, with a short reason in the app's
  language. A chat's replies, the chosen answer of a preference pair (and whether the rejected one
  is better) and an answer written from a document (against its passage) are judged as such. The
  dataset's instructions count, and judgments are asked at temperature 0 so they stay the same.
- Each answer is also compared with up to three records that ask the same or a nearly same
  question, or are its variations, to find answers that contradict each other: "Contradicts #5:
  one says 14 days, the other 30".
- Several selected records, or all records not judged yet (from the dataset overview), are judged
  in a window with a score per record, stop and judge again. Judgments are saved as they come.
- Scores show on list rows; the quality filter lists low scores, records not judged, claims not
  in the source passage, doubtful preference pairs and contradicting answers. The overview shows
  the average score, how scores spread and what the judgments found.
- A judgment is kept with the content it judged: after an edit it is shown as out of date and no
  longer counts.

### Fixed
- Mod+G now stops writing with AI, as the menu says; the shortcut was disabled while the model wrote.

### Variations
- **Record › Generate Variations** (Mod+Shift+G, also in the editor's header and in the panel for
  several selected records) has the model ask each record's question in other words: 1–10
  variations per record, 3 by default. **Reword** the question only, so every variation keeps the
  record's answer, or the question and the answer. **Casual, with typos** writes some questions
  the way people type in a chat. An instruction's input, a chat's system prompt and earlier turns
  and a preference pair's rejected answer stay as they are.
- Variations appear as they are written; writing can be stopped and any record written again.
  A variation that asks exactly the original question is dropped, and one that changes little is
  flagged *Close to the original* and left out unless you check it. Every variation can be
  edited, left out or removed.
- **Add** turns the kept variations into draft records with *Synthetic* lineage: the editor says
  "Synthetic (variation of #12)", and a document passage the record came from comes along.
  Edit › Undo takes the whole addition back.
- A record and its variations are a family: exports put them in the same split part (also
  stratified, and from a version), and they are not near-duplicate questions of each other —
  archiving near duplicates keeps the first record together with its variations. Splits of
  datasets without variations do not change.

### Questions from documents
- **File › Extract Q&A from Document** (also on the dataset overview's AI card, and offered by the
  import wizard for documents) reads a PDF, Word, Markdown or text document and splits it into
  passages along its headings (800 tokens by default). The model writes the questions each
  passage answers, at most three by default, in the passage's language and following the
  dataset's instructions. Pairs appear as they are written; writing can be stopped, and any
  passage written again.
- Every pair can be edited, left out or removed next to its passage. **Add** turns the kept
  pairs into draft records of an instruction, chat or preference dataset, with the model in
  their lineage and the passage kept with them: the editor shows the section and page and opens
  the **Source passage**, so answers can be checked against the document. Edit › Undo takes the
  whole addition back.
- Answers that are not clean JSON are still read: code fences, wrapped lists, Turkish keys and
  "Q: … A: …" lines. An answer with no pairs is shown, so the passage can be tried again.

### Writing with AI
- **Tools › AI Connections** connects a model server on this computer or network: Ollama,
  LM Studio, vLLM, LocalAI or any other OpenAI-compatible server. The server's models are listed,
  **Test** shows that a model answers, and one connection is the default. A server outside the
  local network is used only after you allow sending records to it; moving the connection to
  another server asks again.
- The ✨ button on an answer field (Output, Chosen, Rejected), **Write the reply** under a chat,
  or **Record › Write with AI** (Mod+G) has the model write that part of the record. The answer
  streams in and can be stopped. Rejected answers are written to be plausible but worse.
- Written text is an ordinary edit: review and save it, or discard it. Saving records the model in
  the record's lineage (*Generated by*), and Edit › Undo takes both back.
- The dataset overview shows the model in use and keeps **instructions for this dataset** (tone,
  length, language) that are sent with every request.

### Import
- PDF (`.pdf`) and Word (`.docx`, `.docm`) documents are read like Markdown, one record per
  heading. PDF headings come from their size (or short bold lines, as in FAQs), Word headings
  from their style; numbered and bulleted lists and tables keep their shape. Running headers,
  page numbers, tables of contents, text boxes and deleted (tracked) text are left out; a word
  broken at the end of a line is joined again, and a paragraph that goes on to the next page or
  column stays whole. Imported records point at their page or paragraph
  (`kılavuz.pdf#page=3`). Scanned PDFs without text, password-protected PDFs and Word 97–2003
  files are explained, not misread.
- Excel workbooks (`.xlsx`, `.xlsm`): the sheet that was open is read, or any other sheet, or
  all sheets at once — each with its own header row, columns with the same name mapped
  together. Dates come in as `2024-03-01` / `2024-03-01 14:30`, and imported records point at
  their sheet and row. Old `.xls` and password-protected workbooks are explained, not misread.
- Markdown (`.md`): every heading with text under it becomes a record — the heading asks, the
  text answers, and the headings above it can fill the input. Code blocks stay intact.
- Plain text (`.txt`): every paragraph becomes a record (first line → question, the rest →
  answer). A `.txt` file is read as a table or as text, whichever it looks like; **Read as** in
  the wizard switches.

### Editor
- Record fields colour Markdown (headings, bold, italic, `code`, links, lists), fenced code
  blocks in JSON, Python, JavaScript/TypeScript, C#, SQL, shell and XML/HTML, and fields that hold
  a JSON document. Code is set in a monospace font; colours follow the light or dark theme.
- Fields keep working like text boxes: Tab moves on, right-click cuts/copies/pastes, and the
  review shortcuts work while typing.

### Dataset versions
- Freeze a dataset as a numbered version (v1.0.0, v1.1.0, …) with a note. A version keeps every
  record's content, status, order, tags and token count, and the tokenizer and context length.
  Contents are stored once however many versions keep them.
- File › Versions… (or the *Versions* button above the list) compares any two versions, or a
  version with the current records: added, removed, changed and status changes, and for a
  record its fields side by side with the changed words in colour.
- Go back to a version: contents and statuses return, deleted records come back and records
  added since are archived, never deleted. Edit › Undo takes it back.
- Export a version exactly as it was frozen, even after the dataset has changed; the export
  history remembers which version was written.
- The latest version is shown under the dataset name.

### Near-duplicate questions
- A new quality rule flags records whose question nearly matches another one (85% or more of
  their character 5-grams, e.g. "Kargo ücreti ne kadar?" / "Kargo ücreti ne kadardır?"). The
  editor names the first similar record; the list can be filtered by it; the overview archives
  all but the first of each group (undoable).
- Records asking the same or nearly the same question now always go to the same split file, also
  when exporting a version. Exports with a split may therefore place a few records differently
  than before.
- Every record is analysed once more after updating, to compute its question signature.

### Preference pairs
- **Compare the answers** in the editor shows the rejected and the chosen answer with the words
  only one of them has marked in red and green, updated while you type.

### Tags
- Colored tags, shared by all datasets of a workspace. **Add tag** in the editor finds a tag as
  you type or creates one; the × on a chip removes it. A prefix makes a group
  (`kategori:hukuk` belongs to `kategori`).
- Mod+1 … Mod+5 add or remove the tag with that shortcut, also while typing. New tags take the
  next free shortcut; **Tools › Tags…** renames, recolors, deletes and reassigns them, and
  **Record › Tag** lists them.
- Tag or untag many records at once from the batch panel; every tagging is one Edit › Undo step.
- The list shows each record's tags and filters by tag, or by records without tags.
- Export: **Split within** a tag or a tag group gives every one of them exactly the chosen
  shares, keeping records that ask the same question together.
- Versions keep tags: comparisons show tag changes and going back to a version restores the
  tags of existing records too.

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
