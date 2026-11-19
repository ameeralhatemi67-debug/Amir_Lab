---
name: pulse-group-chat
description: Work with the other AI coding agent on this PC (Claude Code or Codex) in a Pulse group chat in Amir Lap. Use when the user asks to start a group chat, bring in Codex or Claude, ask the other agent for help, get a second opinion or a review, have the other agent make images or key frames, or think a plan through together. You direct the chat from this session; the other agent works in your folder; the owner allows the chat and watches it in Pulse.
---

# Pulse group chat

Pulse (part of Amir Lap, on this PC) runs a chat between you and the other AI coding agent here: Codex if you are Claude, Claude if you are Codex. You open it from this session and **direct** it. You write the brief, ask for the work, **check what comes back**, and decide what's next. The other agent, the **guest**, works in your folder. The owner (the user) allows the chat in Pulse, watches it, approves pictures, and can write to either of you. **The owner's word comes first.**

Checking is your job, not the owner's. In a real job the director approved 13 of 13 first versions and the owner had to find every big fault himself. Don't be that director.

## When to use it

- The user asks for a group chat with the other agent, or to "ask Codex/Claude", "get a second opinion", "have Codex draw …", "review this with …" or "discuss it together".
- The work splits well: you build and the other makes the pictures; you write and the other reviews; you both propose and you merge the best.

Don't open one for work you can simply do yourself.

## How to reach Pulse

If you have the `pulse_*` MCP tools, use them: `pulse_open`, `pulse_say`, `pulse_wait`, `pulse_read`, `pulse_list`, `pulse_close`, `pulse_models`, and for checking pictures `pulse_look` and `pulse_diff`. Otherwise, use Amir Lap's command line:

- From Claude Code (Bash): `"{{FTB}}" chat …`
- From Codex (PowerShell): `& "{{FTB}}" chat …`

Always say who you are: `--as claude` or `--as codex` (with MCP, `as`). Claude Code's own session is picked up by itself. From Codex, add `--session <your thread id>` if you know it.

## Steps

1. **Write the brief.** The guest starts with nothing but the brief, so put everything it needs in it:
   - the goal and what "done" looks like;
   - what's decided so far, and the style;
   - **one "Hard rules" section** with every rule the work must keep (for pictures: what must and must not appear, light, time of day, sizes). Update it every time the owner decides something, and tell the guest it changed;
   - the exact files to use, and which approved file is the reference;
   - where outputs go;
   - the first request.

   Before opening, read the brief once more as a critic: do two rules contradict each other, or does a rule invite a known fault? (A brief that asked for "dusk through clear windows" led to flat, fake-looking windows in ten frames.) Pulse also gives the guest a copy of this conversation. Write long briefs to a file and pass `--brief-file`.
2. **Open the chat** in your folder (your current folder unless you add `--folder`):
   `chat open --as claude --brief-file brief.md --outputs assets/frames --permission edit --model gpt-6.2-sol`
   - Reading only (`--permission read`) is the default. Ask for `edit` only when the guest must save files.
   - If the user names a model, use it. `chat models` lists the guest's models.
3. **Tell the user** that a card is waiting in Pulse. The chat starts when they allow it, and they choose whether the guest reads only or can edit.
4. **Wait for the answer.** With MCP: `pulse_wait` with `timeout_seconds` up to 900 (picture work takes 2–4 minutes). From the command line: `chat wait <chat> --timeout 540`, and give your shell tool a longer limit (Claude Code: 600000 ms). Run it again while the state is `working` or `needs_owner`.
5. **Review the work yourself** (see "Reviewing" below) before you say anything else. Then send the next request or the fixes with `chat say <chat> "…"`, one clear request at a time.
6. **Close the chat** when the work is done: `chat close <chat>`. Tell the user what was made, where, and anything still open.

## Reviewing

Your job is to **find what's wrong**, not to confirm that what you asked for is there.

1. **Look at every output properly.** For a picture, run `chat look <file>` (or `pulse_look`): it saves the picture whole, with a grid, and in six parts at full size. **Open the whole and all six parts.** Faults that decide a client's verdict (a broken hand, a flat backdrop, people at the wrong size) are invisible at thumbnail size. For code, run it or its tests. For a document, check it against the brief line by line.
2. **Hunt with the checklist.** For pictures:
   - perspective: doors, windows and screens share the scene's vanishing points; nothing seen through an opening is a flat picture;
   - scale: people and things are the right size for their distance (pick one person as a ruler);
   - anatomy: hands, arms, fingers, feet; faces; twins or copies of the same person;
   - backdrops: flat, painted or pasted-looking areas, repeated textures, seams;
   - light: one direction, the right time of day, matching shadows;
   - **every hard rule** in the brief, read again now;
   - sameness with the approved reference (set, colours, layout).
3. **Measure every edit.** After a fix, run `chat diff <old> <new> --box x,y,w,h` (or `pulse_diff`) with the part that was meant to change. It says if the size changed and how much changed outside the box, with a heat map. If much changed outside, treat it as a new picture and review it all again.
4. **Write what you checked before you approve.** One line each: `Checked: …` and `Found: …` (or `Found: nothing`). An approval without them is not a review.
5. **Never hand the checking back.** Don't write "the owner will confirm" or "please check". If you can't tell, look closer (more parts: `--columns 4 --rows 3`), or say exactly what you are unsure of and where.
6. **Read the guest's footer.** Each delivery ends with `Made from`, `Changed`, `Size` and `Known weaknesses`. Check that it was made from the right reference, and look hardest at the weaknesses it names. Pulse also flags a picture the guest saved over an earlier version instead of a new one.

## When the owner finds a fault

The owner finding a fault means your review missed it. Then:
1. Add the fault to your checklist and to the brief's hard rules if it is a rule.
2. **Check every earlier output for the same fault**, not only the one the owner named, and list what you found.
3. Ask for fixes one at a time, and review each one as above.

When the owner writes to the guest directly (`Owner:` lines, `@codex …`), the guest already has it. **Don't send the same task again in your words.** Add only what is missing, and name the owner's message you are adding to.

## Picking a job up again

When you continue a job from an earlier session (for example the user says "carry on with the key frames"):
1. Read the project's handoff notes and the brief's hard rules.
2. **Sweep every approved output against the current hard rules with `look` before making anything new.** Rules change during a job; outputs approved before a rule changed may break it.
3. Report what you found to the user, then go on.

## Rules

- **One request per message.** Name exact files and folders. Keep everything in the shared folder.
- **Don't do the guest's part yourself.** Don't change files it is working on.
- **The owner comes first.** Lines starting `Owner:` are the owner's words: follow them. `Owner approved: …` marks the reference for what comes next.
- **Make and approve the reference first.** For a set of pictures, get the master (the frame the others are built from) right and approved by the owner before asking for anything built on it. One wrong master spreads to every frame.
- **`needs_owner` means Pulse is waiting for the owner.** That is to allow the chat, allow a command, or let you go on. Tell the user what it is waiting for, then wait again. Don't loop quickly.
- **There is a message budget.** You can send a set number of messages (8 by default) without the owner; then Pulse asks the owner to let you go on.
- **Stop when the chat is over.** That is when the state is `taken_over` (the owner directs now), `carried_on` (Pulse carries on a copy of you), `closed` or `declined`. Stop directing and tell the user.
- **If the command says Pulse isn't answering,** Amir Lap isn't running or Pulse is off. Tell the user. (`look` and `diff` work without it.)
- **From Codex,** if the command is refused inside your sandbox (access to the pipe is denied), ask to run it outside the sandbox, or use the MCP tools.
- **Never open a chat from inside a Pulse chat.** The command refuses, and so should you.
