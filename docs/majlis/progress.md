---
type: changelog
project: "[[projects/ideas/Amir_Lab/README|Amir Lap]]"
tags:
  - project/amir-lap
  - project/imp-majlis
  - document/progress
created: 2026-11-19
updated: 2026-11-19
status: active
aliases:
  - Imp Majlis Progress
related:
  - "[[projects/ideas/Amir_Lab/docs/majlis/Roadmap|Imp Majlis Roadmap]]"
  - "[[projects/ideas/Amir_Lab/docs/majlis/Status|Imp Majlis Status]]"
  - "[[projects/ideas/Amir_Lab/docs/DECISIONS|Amir Lap Decisions]]"
  - "[[projects/ideas/Amir_Lab/docs/LESSONS|Amir Lap Lessons]]"
---

# Imp Majlis Progress

**Last updated:** 2026-11-19

**Current version:** Version 1 (Image jobs you can trust)

This is Imp Majlis's past. The newest entry is at the top, and reading down goes back in time. It records what was done and how it was checked. Future work belongs only in [[projects/ideas/Amir_Lab/docs/majlis/Roadmap|Roadmap]], and the present in [[projects/ideas/Amir_Lab/docs/majlis/Status|Status]].

Imp Majlis was called **Pulse chat** until 2026-11-19. Entries before that date use the old name, because that is what the code and the commits say.

## Version 1

### 2026-11-19 — Look before you approve, built as 0.9.1 (Checkpoint 1.1, not committed, not installed)

- **Task reference:** Phases 1.1.1 to 1.1.5 and Tasks 1.1.6.1 (in part) and 1.1.6.3; research Task 0.5.1.11.
- **Outcome:**
  - **The blank window:** `ChatWindow.Rebuild()` lets go of the chat list and chat area before building the new layout, and `ThemeResources` calls each window on its own, logging one that fails. Only the chat window had this pattern. Written up in `docs/LESSONS.md`.
  - **`look`:** `PictureCheck.Look` saves a picture whole (with a labelled A1–C2 grid) and in six overlapping parts at full resolution, in a temp folder of its own. `ftb chat look` and the MCP tool `pulse_look`; no Amir Lap needed.
  - **`diff`:** `PictureCheck.Diff` reports a size change, how much changed in all, inside and outside the boxes given, where the outside change is, and which parts changed most, with a heat map. `ftb chat diff` and `pulse_diff`.
  - **The skill:** a Reviewing section (hunt, open the whole and every part, the image checklist, measure every edit, "Checked / Found", never hand checking back), what to do when the owner finds a fault, not repeating the owner's requests, one "Hard rules" section, the master first, and the sweep when a job is picked up again. The CLI wait advice is now 540 s, or the MCP wait up to 900 s.
  - **Every agent's rules:** say "I can't" before using a different method, never save over an earlier version, change only the part asked for. A directed guest ends each delivery with Made from / Changed / Size / Known weaknesses.
  - **Replies:** `ChatRules.PictureList` collapses numbered sequences and says what a long list leaves out; replies flag pictures saved over an earlier version (`ChatMessage.Overwrote`); the window shows the first 24 pictures and counts the rest. A turn records up to 2,000 pictures, not 48.
  - **The installer** closes copies of `ftb.exe` from the install folder before copying (two were running as the agents' MCP servers), and says how to reconnect.
  - The version is 0.9.1.
- **Checked:**
  - New tests: `PictureCheckTests` (5: the parts cover every pixel at full size, an edit inside the box, change found outside the box and placed in C2, a new size, boxes), `PulseChatTests` (2: the work rules for every agent and the footer, picture lists), and `HomeWindowTests.CheckChatRebuild` (four theme and language switches with a chat open). The rebuild test was run once without the fix and failed, then passed with it.
  - All three test projects pass: 225, 144 and 160 tests.
  - On the real film frames: `look` on `kfA.png` shows the flat dusk picture plainly in part C1; `diff` from `kf02-doorway-v2` to v3 found the new one one pixel narrower and 13% changed outside the doorway.
  - `tools/install.ps1` parses.
- **Advisor:** Fable 5.2 reordered the plan for image jobs (see the next entry) and proposed `diff`, the guest's footer, the overwrite flag and the installer question. It did not write code.
- **Not done:** committing and installing (the owner's yes); running setup again so the agents get the new skill; the sweep of the other 17 frames; the reviewer trial by hand (Task 1.1.6.2); the owner's try.

### 2026-11-19 — Image jobs first: the plan reordered (Task 0.4.2.5)

- **Task reference:** Task 0.4.2.5. The owner: "we need to fix the claude and codex images issue first, as I need to work on it again", with the session's notes and anything else that helps.
- **Advisor:** Fable 5.2 (read only) split the image work into small releases: look before you approve; pick up where you left off; the viewer; mark and fix; fresh eyes and the master gate; pictures inside MCP results. It advised building them so the general system extends them (a picture manifest named like Version 2's items, the session on every message, "builds on" on a request), keeping today's names until after the image work, and adding `diff`, the guest's footer, the overwrite flag, a contact-sheet command and a carried-on budget fix, from the chat logs.
- **Outcome:** Version 1 is now "Image jobs you can trust" (Checkpoints 1.1 to 1.6). The general trust work became Version 2 (the new name lands at its start), its own home Version 3, pointing Version 4, artifacts Version 5, and the backlog Version 6. The record table now says which down payment each record gets in Version 1.

## Version 0

### 2026-11-19 — The plan: Roadmap, Status and Progress written (Tasks 0.4.2.2, 0.4.2.3)

- **Note:** checkpoint and task numbers in this entry are from before the reorder later the same day (next entry up). For example, the in-product replay "Task 1.7.4.1" is now Task 1.5.3.1.
- **Task reference:** Phase 0.4.2 (The plan). The owner asked to plan everything first, before any building, in the Version → Checkpoint → Phase → Task structure used by Imp Design.
- **Owner's answers recorded:**
  - The name is **Imp Majlis** (مجلس إمب).
  - The fresh-eyes reviewer is the owner's setting, not a fixed choice.
  - Any agent in the room can put a question to the owner with options to click, the way Claude Code and Codex do inside a session.
  - Build a full system, not a fix for one job: every feature must work for code, documents, images, research and video, not only key frames.
  - The trust fixes (Version 1, Checkpoint 1.1) come first, after the plan is agreed.
- **Advisor:** Fable 5.2 (read only) proposed the six core records (Event, Request, Item, Note, Verdict, Ask), the rules for asking the owner (never depend on a long call) and for gates (they fire when work builds on something unapproved, for any kind of output), the evidence an approval needs per kind, where the rename lands (names now, the code move later), and 10 research questions. It did not write the files.
- **What was changed from its proposal, on purpose:**
  - Version 0 keeps the history as four checkpoints and adds Checkpoint 0.5 for open questions, each marked "needed before", instead of a separate research version.
  - The reviewer and gates move into Version 1 (Checkpoints 1.6 and 1.7), because the owner's first note was the director's misses. The move into its own tool goes to Version 2.
  - The skill's review rules and a simple `look` tool come in Checkpoint 1.1, before the full evidence model in 1.6.
  - Keeping every owner request open until it is done comes with the request record (Checkpoint 1.3).
- **Fable's review of the draft, and the fixes made:**
  - The reviewer trial depended on the reviewer it was meant to test. It is now a trial by hand (Task 0.5.1.5), and the in-product replay is Task 1.7.4.1.
  - Checkpoint 1.1 was too big for one release. Thinking levels, the CLI wait and the health check moved to a new Checkpoint 1.2.
  - What the guest can and can't do (Review P0-3) moved from Version 2 into Checkpoint 1.2, as a trust item.
  - Checkpoint 1.5 now says how a director comes back after its chat carried on (an adoption by the same session), and when the summary of the time away is written.
  - The owner's live try is now its own last task in each checkpoint, so other tasks can be marked done on evidence.
  - Smaller fixes: exits that can be tested, a cap of 3 free fixes per request, one-click dismissal of owner requests, a word list, and review items placed (`follow`, the guest showing it has seen a request, a summary on closing).
- **Outcome:** `docs/majlis/Roadmap.md`, `docs/majlis/Status.md` and this file. `docs/ROADMAP.md` links to them.
- **Not done:** the owner has not approved the roadmap yet (Task 0.4.2.4). Nothing was built or committed.

### 2026-11-19 — Advice on the owner's 10 notes (Task 0.4.2.1)

- **Task reference:** Phase 0.4.2. The owner asked for suggestions, ideas and additions, and for Fable 5.2 as an advisor that thinks but never builds.
- **What was found in the code:**
  - The blank window after a switch to dark mode: `ChatWindow.Rebuild()` puts `_list` and `_content` into a new layout while they still belong to the old one. WPF refuses, and only the window's tint is left. `MixerWindow.Rebuild` does it right. `ThemeResources.Changed` has no guard per window, so one failure can stop the others.
  - The MCP server (`src/Toolbox.Cli/McpServer.cs`) answers with text only. The director got file paths and could approve a picture without opening it.
  - `claude --effort low|medium|high|xhigh|max` and Codex's `model_reasoning_effort` both work per turn. Pulse passes neither, so Codex ran at the owner's own setting (max).
  - Codex `exec -i` takes images. It has no mask option.
  - Parts to reuse: Preview (`Viewer/ViewerWindow.cs`: zoom, next and previous file) and Studio's drawing tools (`Studio/DrawOverlay.cs`: arrow, box, ellipse, pen, highlight, text, numbered steps).
- **Outcome:** a four-step order (trust, memory and seeing, pointing, artifacts) and the name idea Majlis, which the owner took as Imp Majlis.
- **Not done:** nothing was built.

### 2026-11-19 — First real use: key frames for a promo film, and the review (Phase 0.4.1)

- **Task reference:** Tasks 0.4.1.1 to 0.4.1.3.
- **Outcome:** Claude directed Codex (gpt-6.2-sol) in three chats and got 18 photoreal key frames for the «المخيم الرمضاني» film in about two hours, with the owner allowing each chat and writing notes. Codex said honestly that it has no video tool when asked for motion clips.
- **Review:** `projects/ideas/motion_design/PULSE_REVIEW.md`, by the director with a Fable audit: 6.5 out of 10. Good: the owner's control, the open → say → wait → read loop, clear errors, the long MCP wait, file paths in every reply. Bad: a silent "carried on" copy made about 10 files with no record, chats lost after a restart, no way to know what the guest can do, no request ids, the short CLI wait, noisy picture lists, no cost or time.
- **The owner's verdict: 70%.** The director missed very big problems and moved on. From the chat log (`296088576b82.json`): the director approved 13 of 13 first versions; all four big problems were found by the owner: a flat picture in the doorway with the wrong perspective, people far too big, a broken arm, and flat fake-looking windows in most interior frames. The final `kfA.png` still shows a flat dusk picture at the top right.
- **Not done:** fixing any of it. That is the plan's job.

### 2026-11-18 — Installed and set up for both agents (Task 0.3.3.2)

- **Outcome:** at the owner's request, 0.9.0 was installed and `ftb chat setup --mcp` was run. Both agents have the `pulse-group-chat` skill and an MCP server named `pulse` (Claude Code: user scope, connected; Codex: `config.toml`). Pulse answers `ftb chat ping`.

### 2026-11-18 — Chats directed from a session (Checkpoint 0.3, Amir Lap 0.9.0)

- **Task reference:** Phases 0.3.1 to 0.3.3. Commit `ed7eff2`, pushed.
- **Owner's answers:** the owner's messages go to both agents; Take over is allowed; the guest reads only by default and the consent card can let it edit; the guest's model can be picked.
- **Outcome:**
  - Core: `ChatDoor`, `ChatPipe` and `ChatPipeServer` (the pipe `AmirLap.<user>.chat`), `DirectorNotes`, `AgentModels`. `ChatHub` gained `CreateDirected`, `Direct`, `Close`, `TakeOver`, `ResumeDirector` and `SetModel`.
  - CLI: `ftb chat open/say/wait/read/list/close/models/setup`, and `ftb mcp`.
  - Skill: `skills/pulse-group-chat/SKILL.md`, embedded in ftb and installed for both agents by `ftb chat setup`.
  - Window: the consent card, the director's status with Resume director and Take over, and model pickers.
- **Found live:** the old Codex engine in `.sandbox-bin` can't run commands. `FindCodex` now prefers the Codex app's engine. Written up in `docs/LESSONS.md`.
- **Checked:** all three test projects passed (223, 139 and 160 tests; 522 in all). The opt-in `LiveDirectorTests` passed: the real ftb CLI and MCP, with the real Codex reading a file in the director's folder.

### 2026-11-18 — Design for directed chats (Phase 0.3.1)

- **Outcome:** with Fable agreeing: the director is an outside author that Pulse never runs, so there is never a second copy of it; context comes in layers (a required brief, a readable copy of the director's conversation); the verbs are open, say, wait, read, list and close; when the director's session is gone the owner can take over or resume the director from a copy. Recorded in `docs/DECISIONS.md` (18 Nov).

### 2026-11-15 — Pulse chat: Claude and Codex in one room (Checkpoint 0.2, Amir Lap 0.7.0)

- **Task reference:** Phases 0.2.1 to 0.2.3. Commit `ab8a7a1`, pushed at the owner's request, not installed.
- **Outcome:**
  - Core in `src/Toolbox.Core/Pulse/Chat/`: `ChatHub` (the room), `ChatRules` (@, the digest, room rules, the hop limit), the agents' command lines (`claude -p` stream-json, `codex exec --json`, with resume and fork), reading both agents' output, finding sessions already in a folder, and `ChatStore` (`%LOCALAPPDATA%\AmirLap\pulse\chats`).
  - Window in `src/Toolbox.Windows/Pulse/Chat/`: `ChatWindow`, `NewChatWindow`, `ChatViews`, and a Chats tile.
  - Approvals: Claude's refused commands come back as `permission_denials`, and a card runs the turn again with them allowed. Codex can't ask mid-turn, so "Ask first" makes it read only.
- **Checked:** 18 `PulseChatTests`; the opt-in `LiveChatTests` passed for real (Claude → @codex → "pong"); window snapshots in `HomeWindowTests`.

### 2026-11-15 — Designed on the canvas (Task 0.1.2.1)

- **Outcome:** three boards on the "Amir Lap redesign" canvas, under "Pulse chat: Claude and Codex in one room": the chat room (interactive), New chat, and nine states and problems.

### 2026-11-14 — Research (Phase 0.1.1)

- **Outcome:** how to drive each agent (Claude with `claude -p` stream-json; Codex with `codex exec --json` or its app-server), what the terms allow (Pulse only starts the user's own, unchanged `claude` and never reads its tokens), and facts about this PC (where the Codex engine lives; Codex image generation is on).
