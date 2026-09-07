# BetterCompact

[![.NET 10](https://img.shields.io/badge/.NET-10-512BD4)](https://dotnet.microsoft.com/)
[![C# 14](https://img.shields.io/badge/C%23-14-239120)](https://learn.microsoft.com/dotnet/csharp/)
[![Platform](https://img.shields.io/badge/platform-Windows-0078D6)](#building-it)
[![License](https://img.shields.io/badge/license-MIT-blue)](LICENSE)

Turns a Claude Code session file into the conversation it contains.

A `.jsonl` session file is mostly tool traffic: the contents of every file that was read, every command
that ran and everything it printed. What the two sides said to each other is a small fraction of it.
BetterCompact keeps that fraction — whole, never shortened — and counts the rest.

On one real session: **24 MB in, 188 KB out.** 66 user messages, 5 slash commands and 460 replies kept;
1,854 tool calls, 1,854 tool results and 2,763 attachments left behind as a one-line count.

## Why not just let the model summarise it

Because a summary is a rewrite. Ask for one and the messages come back paraphrased, quoted with an
ellipsis through the middle, or turned into third-person narration — and the parts that mattered are
gone with no way to tell that they went.

BetterCompact does not read the conversation, it filters it. Nothing that survives the filter is
touched: a kept message is byte-for-byte what was in the file. Everything dropped is counted and
printed, so a small output can always be explained.

## What it keeps

| | |
|---|---|
| **User messages** | Every message the person typed, at full length. |
| **Slash commands** | Flattened from their markup to the line that was typed: `/mycommand` and its arguments. |
| **Replies** | Every block of assistant text. |
| **Tool activity** | One line between messages: `> 14 tool calls - Read x6, Edit x5, PowerShell x3`. |

Dropped by default and available behind a switch: injected text (task notifications, skill bodies,
resumption prompts), the assistant's thinking blocks, the summaries a session writes when it runs out
of context, and `<system-reminder>` blocks inside messages.

## How it decides what a message is

Not from its wording. Every entry in the user turn carries its own markers, and those are what get read:

- `origin.kind == "human"` — the person typed it.
- `origin.kind` set to anything else, or `isMeta == true` — the tool put it there.
- `isCompactSummary == true` — a context-limit summary.
- **No marker at all — counted as the person's own.** Session files written before those fields existed
  have nothing else to go on, and the cost of guessing wrong in that direction is a little noise,
  against losing somebody's message in the other.

Two details worth stating, because the obvious implementation of each is wrong:

**Duplicate entries are matched by `uuid`, never by their text.** A prefix comparison drops the second
of two messages that happen to start alike — `devam et` sent twice is one message afterwards. The 1,997
repeated entries in the test session were verified to be byte-identical to their first appearance
before this was relied on.

**A slash command is recognised only when the markup *opens* the entry.** Recognising it anywhere in
the text means that pasting an old transcript into the prompt — which carries those tags along with it —
collapses the whole paste to the command name. That bug destroyed a 3 MB message during development.

## Usage

```
BetterCompact <session.jsonl> [-o <output>] [options]
BetterCompact <folder>        [-o <folder>] [options]
BetterCompact                                           (interactive)
```

| Option | |
|---|---|
| `-o`, `--output <path>` | Output file, or a folder to put the output in. Default: beside the input. |
| `--injected` | Also keep text nobody typed. |
| `--thinking` | Also keep the assistant's reasoning blocks. |
| `--summaries` | Also keep context-limit summaries. |
| `--reminders` | Leave `<system-reminder>` blocks inside messages. |
| `--no-tools` | Drop the tool-count lines as well. |
| `--everything` | All of the above except `--no-tools`. |
| `--max-message <n>` | Replace any single message over `n` kilobytes with a line saying how big it was. Off by default. |
| `-h`, `--help` | Usage. |

`--max-message` exists for one situation: somebody pasted a whole transcript into the prompt, so a
single message is now most of the output. That message is real and the default keeps it, but a session
like that compacts to 2.94 MB instead of the 160 KB everything else in it amounts to. The message is
replaced outright rather than cut short — `[2.71 MB message left out: over the 200 KB limit]` — so a
shortened message can never be mistaken for the real one, and the count at the end still reports it.

Session files live in `%USERPROFILE%\.claude\projects\<slugified-project-path>\`, one per session,
named by session id. Passing that folder compacts every session in it, oldest first.

### Output

````markdown
# Session title

> Session `747f2ed1-3ad0-4621-9d27-a25d4368b008`
> 2026-09-03 14:56 - 2026-09-05 20:33
> 66 user messages, 5 slash commands, 460 replies, kept from 12446 entries
> Dropped: 1854 tool calls, 1854 tool results, 29 injected, 381 thinking, 3 compact summaries

---

## User - 2026-09-03 14:56:25

the message, exactly as it was sent

> 3 tool calls - PowerShell x1, Read x1, Skill x1

## Claude - 2026-09-03 14:56:56

the reply, exactly as it was written
````

### What it prints

```
[*] Reading C:\Users\...\747f2ed1-....jsonl

  kept
    user messages     66
    slash commands    5
    replies           460
  dropped
    tool calls        1854
    tool results      1854
    injected          29
    thinking          381
    compact summaries 3
    attachments       2763
    repeated entries  1997

[+] 24 MB -> 188 KB  (0.8% of the original)
```

## Building it

Needs the .NET 10 SDK.

```bash
dotnet build BetterCompact.csproj -t:Compile
dotnet run --project BetterCompact.csproj -- session.jsonl
```

Two single-file executables are published under `publish/`: a self-contained one that runs on its own,
and a framework-dependent one that needs .NET 10 installed.

The executables are digitally signed. To let Windows verify the signature, run `Guven-Kur.cmd` from
`SignatureTrust.zip` once. The programs run without it; only the signature stays unverified.

## Layout

```
Program.cs             Entry point, interactive mode, output paths
CompactOptions.cs      The options and the command line that produces them
TranscriptMessage.cs   What a kept message is, and the counts behind a run
TranscriptReader.cs    Reads the .jsonl and decides what each entry is
MarkdownComposer.cs    Lays the result out as Markdown
ConsoleReport.cs       Everything the program prints
ByteSize.cs            Byte counts written the way a person reads them
```

## Licence

MIT. See [LICENSE](LICENSE).
