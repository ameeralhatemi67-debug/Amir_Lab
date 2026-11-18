---
name: pulse-group-chat
description: Work with the other AI coding agent on this PC (Claude Code or Codex) in a Pulse group chat in Amir Lap. Use when the user asks to start a group chat, bring in Codex or Claude, ask the other agent for help, get a second opinion or a review, have the other agent make images or key frames, or think a plan through together. You direct the chat from this session; the other agent works in your folder; the owner allows the chat and watches it in Pulse.
---

# Pulse group chat

Pulse (part of Amir Lap, on this PC) runs a chat between you and the other AI coding agent here: Codex if you are Claude, Claude if you are Codex. You open it from this session and **direct** it. You write the brief, ask for the work, check what comes back, and decide what's next. The other agent, the **guest**, works in your folder. The owner (the user) allows the chat in Pulse, watches it, approves pictures, and can write to either of you. **The owner's word comes first.**

## When to use it

- The user asks for a group chat with the other agent, or to "ask Codex/Claude", "get a second opinion", "have Codex draw …", "review this with …" or "discuss it together".
- The work splits well: you build and the other makes the pictures; you write and the other reviews; you both propose and you merge the best.

Don't open one for work you can simply do yourself.

## How to reach Pulse

If you have the `pulse_*` MCP tools, use them: `pulse_open`, `pulse_say`, `pulse_wait`, `pulse_read`, `pulse_list`, `pulse_close` and `pulse_models`. Otherwise, use Amir Lap's command line:

- From Claude Code (Bash): `"{{FTB}}" chat …`
- From Codex (PowerShell): `& "{{FTB}}" chat …`

Always say who you are: `--as claude` or `--as codex` (with MCP, `as`). Claude Code's own session is picked up by itself. From Codex, add `--session <your thread id>` if you know it.

## Steps

1. **Write the brief.** The guest starts with nothing but the brief, so put everything it needs in it:
   - the goal and what "done" looks like;
   - what's decided so far, and the rules and style;
   - the exact files to use;
   - where outputs go;
   - the first request.

   Pulse also gives the guest a copy of this conversation to read if the brief isn't enough. Write long briefs to a file and pass `--brief-file`.
2. **Open the chat** in your folder (your current folder unless you add `--folder`):
   `chat open --as claude --brief-file brief.md --outputs assets/frames --permission edit --model gpt-6.2-sol`
   - Reading only (`--permission read`) is the default. Ask for `edit` only when the guest must save files.
   - If the user names a model, use it. `chat models` lists the guest's models.
3. **Tell the user** that a card is waiting in Pulse. The chat starts when they allow it, and they choose whether the guest reads only or can edit.
4. **Wait for the answer:** `chat wait <chat> --timeout 45`. Run it again while the state is `working` or `needs_owner`. Give your shell tool a time limit longer than `--timeout`.
5. **Check the work yourself.** Open the files and look at the pictures. Then send the next request or the fixes with `chat say <chat> "…"`, one clear request at a time.
6. **Close the chat** when the work is done: `chat close <chat>`. Tell the user what was made and where.

## Rules

- **One request per message.** Name exact files and folders. Keep everything in the shared folder.
- **Don't do the guest's part yourself.** Don't change files it is working on.
- **The owner comes first.** Lines starting `Owner:` are the owner's words: follow them. `Owner approved: …` marks the reference for what comes next.
- **`needs_owner` means Pulse is waiting for the owner.** That is to allow the chat, allow a command, or let you go on. Tell the user what it is waiting for, then wait again. Don't loop quickly.
- **There is a message budget.** You can send a set number of messages (8 by default) without the owner; then Pulse asks the owner to let you go on.
- **Stop when the chat is over.** That is when the state is `taken_over` (the owner directs now), `carried_on` (Pulse carries on a copy of you), `closed` or `declined`. Stop directing and tell the user.
- **If the command says Pulse isn't answering,** Amir Lap isn't running or Pulse is off. Tell the user.
- **From Codex,** if the command is refused inside your sandbox (access to the pipe is denied), ask to run it outside the sandbox, or use the MCP tools.
- **Never open a chat from inside a Pulse chat.** The command refuses, and so should you.
