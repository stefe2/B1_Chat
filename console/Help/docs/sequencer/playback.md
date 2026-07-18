# Sequencer — Playback

## Play / Pause / Resume / Stop

**Play** is console-driven: pressing it fires the real per-step gesture
commands to the right droids at the right times (and starts any audio clips),
directly from the timeline you're currently editing — there's no separate "save
first" step.

- **Play** while stopped starts from t = 0. Pressed again while **paused**, it
  **resumes** from where you left off instead of restarting.
- **Pause** freezes the playhead and the audio in place.
- **Stop** resets back to t = 0.
- **Loop** repeats the whole pass automatically at the end.

A muted track (see [Timeline](timeline.md)) is skipped during Play — useful for
auditioning one droid's part in isolation without touching the timeline itself.

## What's *not* possible anymore

Earlier firmware versions could save a sequence into one of the master's 8
on-board slots and replay it standalone, without a PC connected. That on-board
player and its slots were **removed from the firmware** — sequences are now
100% console-driven, and Play always requires the console to be connected and
actively running the timeline. If you need "press a button, no PC" playback,
that capability no longer exists in this app.

## Saving and sharing sequences

- **Local Library**: keeps a named sequence on this PC for quick reuse across
  sessions.
- **Export / Import (`.b1seq.json`)**: a sequence file also carries the droid
  roster it was built for, so opening it on another PC (or with some droids
  offline) still lays out one row per saved droid instead of collapsing
  everything onto a single track — a droid coming online later simply takes its
  row over.

## Undo/redo and clearing

Undo/Redo cover every timeline edit, including audio. **Clear** empties the
whole timeline (steps and audio) while keeping the sequence's name — it asks
for confirmation if there are unsaved changes.
