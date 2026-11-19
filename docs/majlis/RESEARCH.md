---
type: research
project: "[[projects/ideas/Amir_Lab/README|Amir Lap]]"
tags:
  - project/amir-lap
  - project/imp-majlis
  - document/research
created: 2026-11-19
updated: 2026-11-19
related:
  - "[[projects/ideas/Amir_Lab/docs/majlis/Roadmap|Imp Majlis Roadmap]]"
---

# Imp Majlis Research

Answers to the research questions in [[projects/ideas/Amir_Lab/docs/majlis/Roadmap|Roadmap]] Checkpoint 0.5, newest first. Each answer names its task, the date, how it was found, and what it changed.

## Task 0.5.1.11 — Can the installer replace `ftb.exe` while `ftb mcp` runs? (2026-11-19)

**Answer: no.**
- Once `ftb chat setup --mcp` has run, Claude Code and Codex start `ftb.exe mcp` from the install folder and keep it running for as long as their session lives. On 2026-11-19 two copies were running from `%LOCALAPPDATA%\Programs\AmirLap\ftb.exe` (started 08:57 and 09:45).
- `ftb` is not a single file: a running copy holds `ftb.exe`, `ftb.dll`, the `Toolbox.*.dll` files and the native engines it loaded. Windows refuses to overwrite a file in use, so the installer's `robocopy /MIR` would fail ("Copying failed … is a file open?").
- 0.9.0 installed cleanly only because the MCP server was set up after it.

**What changed:** `tools/install.ps1` now closes copies of `ftb.exe` from the install folder (and only those), right after Amir Lap itself, and tells the owner to reconnect the chat tools in open sessions (Claude Code: `/mcp`) or start a new session (Roadmap Task 1.1.6.3).

**Checked (2026-11-19):** the real install of 0.9.1 closed the two running copies, copied the app, and Amir Lap 0.9.1 started. Claude Code reported its `pulse` server connected again afterwards.

## Task 0.5.1.8 — Thinking-level flags (2026-11-19, partly answered)

- Claude Code 2.1.321: `claude --effort <level>`, with low, medium, high, xhigh and max (from `claude --help`).
- Codex: `model_reasoning_effort` (set with `-c model_reasoning_effort="…"`), with each model's levels listed in `~/.codex/models_cache.json` (low to ultra for gpt-6.2-sol). Seen by the advisor; not yet run.
