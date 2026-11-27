# BetterCompact

![.NET 10](https://img.shields.io/badge/.NET-10-512BD4)
![C# 14](https://img.shields.io/badge/C%23-14-239120)
![Platform](https://img.shields.io/badge/Platform-Windows%20x64-0078D6)

BetterCompact exports AI agent conversations into clean, compact Markdown transcripts without summarizing or altering the actual conversation text. Tool calls, tool outputs, and internal runtime noise are filtered out while preserving user messages, assistant replies, and rich session metadata.

## Included Tools

| Tool | Target Harness | Aliases | Default Output Behavior |
| --- | --- | --- | --- |
| `BetterCompactAntigravity.exe` | Antigravity (Gemini) | `BetterCompactGemini` | Single Markdown file (`--split` for 44 KB / 700 lines) |
| `BetterCompactClaudeCode.exe` | Claude Code (Claude) | `BetterCompactClaude` | Single Markdown file (`--split` for 44 KB / 700 lines) |
| `BetterCompactCodex.exe` | Codex (ChatGPT / GPT) | `BetterCompactGPT`, `BetterCompactChatGPT` | Single Markdown file (`--split` for 44 KB / 700 lines) |

## Features

- **Standardized Metadata Header:** Session UUID, title, harness, primary model & effort, model family, multi-model shares, working directory, start/end duration, context fill, billing token totals (excluding cache hits), cache hit ratio, message/exclusion counts, top used tools, size reduction, and source paths.
- **Standardized File Naming:** `HarnessName_Model_SessionTitle.md` (with `_1.md`, `_2.md`, ... suffixes only when split).
- **Optional Table of Contents (`--toc`):** Adds an `İçindekiler` block at the top and labels messages sequentially (`k1`, `a1`, `k2`, `a2`, ...).
- **Collapsible Reasoning (`--reasoning` / `--thinking`):** Renders model thinking/reasoning blocks inside `<details>` blocks with line and size summaries.
- **Session Discovery:** `--list [count]` and `--find <query>` to list or search sessions by title across each harness.
- **Clean Single-File Executables:** No sidecar DLLs or config files; rotating diagnostic logs are stored under `%TEMP%\BetterCompact\<ToolName>\Logs`.

## Usage

```powershell
# Export by session UUID or file path (writes to a fresh NewTemp folder by default)
BetterCompactAntigravity.exe <conversation-uuid>
BetterCompactClaudeCode.exe <session-uuid-or-jsonl>
BetterCompactCodex.exe <session-uuid-or-jsonl>

# Optional flags
BetterCompactAntigravity.exe <uuid> --toc --reasoning --output "<folder>"
BetterCompactCodex.exe --list 10
BetterCompactClaudeCode.exe --find "MyProject"
```

## Installation

Download either the self-contained single-file executables or the framework-dependent variants (`-FrameworkDependent-RequiresNET10.exe`, requires .NET 10 Runtime) from the Releases page.

> The executables are digitally signed. To let Windows verify the signature, run `Install-Certificate.cmd` from `SignatureTrust.zip` once. The programs run without it; only the signature stays unverified.
