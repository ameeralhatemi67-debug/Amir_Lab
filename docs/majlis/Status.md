---
type: status
project: "[[projects/ideas/Amir_Lab/README|Amir Lap]]"
tags:
  - project/amir-lap
  - project/imp-majlis
  - document/status
  - status/in-progress
created: 2026-11-19
updated: 2026-11-19
status: active
current_version: "1"
current_checkpoint: "1.1"
aliases:
  - Imp Majlis Status
related:
  - "[[projects/ideas/Amir_Lab/docs/majlis/Roadmap|Imp Majlis Roadmap]]"
  - "[[projects/ideas/Amir_Lab/docs/majlis/progress|Imp Majlis Progress]]"
  - "[[projects/ideas/Amir_Lab/docs/majlis/RESEARCH|Imp Majlis Research]]"
  - "[[projects/ideas/Amir_Lab/docs/LESSONS|Amir Lap Lessons]]"
---

# Imp Majlis Status

**Last updated:** 2026-11-19

**Current version:** Version 1 (Image jobs you can trust)

**Current checkpoint:** Checkpoint 1.1 (Look before you approve), **installed as Amir Lap 0.9.1** on 2026-11-19 (commit `c7ba1e3`, not pushed). The owner's try and the sweep of the other 17 film frames remain. On 2026-11-19 the owner put image jobs first, because he is going back to the promo film's pictures; the plan was reordered for it (see [[projects/ideas/Amir_Lab/docs/majlis/progress|Progress]]).

What 0.9.1 has:
- **The blank window is fixed.** Switching theme or language with a chat open keeps the window working. A test switches four times with a chat selected; it fails without the fix.
- **`look` and `diff`** (`ftb chat look|diff`, MCP `pulse_look|pulse_diff`), working without Amir Lap running. On the real frames, `look` shows the flat dusk picture in `kfA` plainly (part C1), and `diff` found that `kf02-doorway-v3` is one pixel narrower than v2.
- **The skill's review rules:** hunt for faults with an image checklist, open every part, measure every edit, write "Checked / Found" before approving, never hand checking back to the owner, re-check every earlier picture when the owner finds a fault, keep one "Hard rules" section, don't repeat the owner's requests, and sweep approved pictures first when a job is picked up again.
- **The guest's rules:** say "I can't" before using another method, never save over a version, change only the part asked for, and end each delivery with Made from / Changed / Size / Known weaknesses.
- **Replies:** a sequence of pictures is one line with its count; long lists say what they leave out; a picture saved over an earlier version is flagged; the window shows the first 24 pictures and counts the rest.
- **The installer** closes the copies of `ftb.exe` that Claude Code and Codex keep running, which would otherwise make the update fail.
- **Checked:** all three test projects pass (225, 144 and 160 tests; 529 in all).

**System state:** Amir Lap 0.9.1 is installed and running; Pulse answers. Setup ran again: both agents have the new `pulse-group-chat` skill (with the Reviewing section), Codex's `pulse` MCP entry was re-added, and Claude Code's entry (unchanged, same `ftb.exe`) reports connected. The installed MCP server lists `pulse_look` and `pulse_diff`, and both answered on the real frames. Sessions that were open during the install lost their chat tools until reconnected (`/mcp`) or restarted.

This file reports the present only. Planned work lives in [[projects/ideas/Amir_Lab/docs/majlis/Roadmap|Roadmap]], and finished work in [[projects/ideas/Amir_Lab/docs/majlis/progress|Progress]].

> [!note] Waiting on the owner
> - **The owner's try** (Task 1.1.6.5): switch theme with a chat open, and run the next image job from a **new** session (sessions open before the install need `/mcp` to get the chat tools back).
> - Pushing `c7ba1e3` to GitHub, if wanted.
> - Task 0.5.2.1: how release numbers fit with Imp Studio. 0.9.1 was used for this checkpoint, as proposed.
> - The roadmap as a whole (Task 0.4.2.4).

## Current Objective

> [!important] Current Objective
> The owner goes back to the film's pictures with 0.9.1. The next image session's first job is the sweep of the 17 other approved frames with `look`. Then Checkpoint 1.2 (Pick up where you left off): finding the folder's chats from a new session, adopting one with the owner's click, and the picture manifest.

## Current State

| Area | State | Note |
|---|---|---|
| The room | Working | Claude and Codex in one chat, @ hand-offs, hop limit, approval cards (0.7.0). The owner hasn't tried a chat run from the Pulse window yet. |
| Directed chats | Working | A Claude or Codex session directs through the CLI, MCP or skill; the owner allows each chat (0.9.0). |
| Window after a theme or language switch | Fixed in 0.9.1 (installed; the owner's try pending) | `ChatWindow.Rebuild()` lets go of the parts it reuses; the theme event calls each window on its own. |
| Looking at pictures | Installed in 0.9.1 | `look` (whole with a grid, six parts at full size) and `diff` (size, change outside a box, heat map). Paths only; pictures inside MCP results wait for research (Checkpoint 1.6). |
| Review of the guest's work | Rules written, not enforced | The skill's review rules and checklist (0.9.1). Enforcement comes with verdicts and evidence (Checkpoint 2.6); the fresh-eyes reviewer and the master gate in Checkpoint 1.5. |
| The guest's honesty | Rules written | Every agent is told to say "I can't" first, never save over a version, and add the delivery footer (0.9.1). |
| Picture lists in replies | Installed in 0.9.1 | Sequences collapsed, nothing cut silently, saved-over versions flagged. |
| Picking a chat up from a new session | Missing | A new session can't direct a chat another session opened; the three film chats are tied to the old session. Planned in Checkpoint 1.2. Until then, a new chat that points at `keyframes/BRIEF.md` works. |
| A record of time away | Weak | A copy that carries on leaves no record, apart from the chat itself. Planned in Checkpoint 1.2. |
| Asking the owner | Missing | Only fixed cards. Planned in Checkpoint 2.4. |
| Thinking level | Not set | Each agent runs at the owner's own setting (Codex at max). Planned in Phase 2.2.1. |
| The board | Not useful | Shows setup, not status. Planned in Checkpoint 3.3. |
| Image preview | Basic | A click opens the file outside the app. Planned in Checkpoint 1.3. |
| Marks and fixes | Missing | Planned in Checkpoint 1.4. |
| Artifacts | Missing | Planned in Version 5. |
| Name and place | Pulse chat, inside Pulse | Renamed Imp Majlis by the owner; the code keeps the old names until Checkpoint 2.1. |
| Tests | Pass | 529 tests in the three projects, plus opt-in live tests with real agents. |

## Known Gaps

- 17 of the 18 approved film frames haven't been swept with `look` yet; `kfA` still shows the flat dusk picture at the top right. That belongs to the film, and is the next image session's first job.
- An MCP tool call that fails with an unexpected kind of error gets no answer at all (the server only turns a few kinds of error into an answer). Not seen in use; worth hardening in Checkpoint 1.2.
- Measured afterwards: Codex's kf08 "fix the arm only" edit (v2 → v3) changed 11% of the picture outside the arm, mostly on the right. That edit leak went unnoticed in the session.
- `diff` compares a picture of another size after scaling it, so a one-pixel change in width shows up as change everywhere. It says so in its report.
- The MCP server answers with text and paths, not pictures, so a director still has to open the files it is given.
- Codex's session id is guessed from environment variables.
- The whole chat file is rewritten on every save.

## Next Step

The owner picks the film work up in a new session with 0.9.1: first the sweep of the 17 other approved frames with `look`, then new work. Meanwhile Checkpoint 1.2 (Pick up where you left off) can start.
