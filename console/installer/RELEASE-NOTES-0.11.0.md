# B1 Chat Console 0.11.0

Pair this console with **firmware 1.10.0** (`fw-v1.10.0`). Older firmware keeps
working: every new firmware feature below is additive and capability-gated, and
the console silently falls back when a droid does not advertise it.

## Sequencer — safety

- **Three distinct stop levels.** Normal *Stop* cancels the schedule and audio
  and only ends the infinite gestures the Sequencer itself started. *Safe Stop*
  interrupts motion on every droid, returns each head to its calibrated center
  and keeps holding torque while suppressing spontaneous animation.
  *Emergency Stop* immediately cuts servo power fleet-wide — holding torque is
  lost and an unsupported head may fall.
- **Fail-closed lease for looping gestures.** TALK and POWER_DOWN started by
  playback carry a 5 s firmware lease renewed every 2 s. If the PC crashes, the
  USB cable is pulled or the master becomes unreachable, the droid returns to
  IDLE on its own instead of moving indefinitely.
- **Pause is now honest.** It freezes the playhead, upcoming commands and local
  audio only; gestures already sent keep running. The transport says so
  explicitly (`PAUSED · DROID MOTION CONTINUES`).
- **Per-droid execution feedback.** Clips show whether each target actually
  started, completed, was interrupted or refused the gesture, and distinguish a
  local serial failure from a master rejection or a missing report.

## Sequencer — transport and navigation

- The main button is a real **Play/Pause/Resume toggle** (`Space`). Pressing
  Play twice no longer restarts the scene and re-sends its first commands.
- **Restart** (`Ctrl+Enter`) is the explicit from-zero action; **Return to
  start** (`Ctrl+Home`) is separate. Stop now keeps the playhead where it is.
- **Follow** keeps the playhead in view on long timelines without fighting
  manual inspection, and **Ctrl+mouse wheel** zooms around the pointer
  (`Shift+wheel` scrolls horizontally).

## Scenes

- The Sequencer document is now a **Scene**, edited like a normal document:
  New / Open / Save in a Scene bar, with Save As, Rename, Import, Export and
  Trash in a secondary menu, plus `Ctrl+N`/`O`/`S`, `Ctrl+Shift+S` and `F2`.
- **Open** shows a searchable browser sorted by last save, with the current
  Scene marked and a summary of each Scene's content.
- Replacing a modified Scene always offers **save / continue without saving /
  cancel**, and asks before stopping a running pass.
- Scene files are written atomically under stable identities; deletion moves the
  file to a recoverable trash folder instead of erasing it.
- Export to `.b1seq.json` remains available as an explicit external copy for
  backup, transfer or version control.

## Editing

- One edit-transaction model: bounded 50-step Undo/Redo, no history entry for a
  simple selection or a drag that ends where it started, and a *modified* state
  computed against the last save rather than a manual flag.
- Interrupted drags (Escape, lost focus, window deactivation) restore the
  document instead of leaving a half-applied edit.
- Persistent editing is locked during Play and Pause; inspection, zoom, track
  arming and runtime mute stay available.
- Import validates the whole file before touching the editor, supports schema
  versions 1 to 5 with explicit migrations, and refuses ambiguous timing rather
  than silently changing choreography.

## Timing and audio

- Gesture durations are now target-aware: the console reproduces the firmware's
  speed scaling and jitter, and broadcast clips warn when targets have mixed
  speed settings.
- Looping TALK/POWER_DOWN clips have a real, editable endpoint instead of a
  purely indicative width.
- Playback runs on a single deterministic scheduler, and conflicting
  same-timestamp commands are flagged before they surprise you.

## Fixes and performance

- Removed a periodic UI hitch caused by rebuilding the timeline on every fleet
  telemetry update; the radar and playhead animate smoothly again.
- The timeline ruler now adapts from milliseconds to hours with a bounded tick
  count at any zoom level.
- All application windows use the dark title bar, and status badges no longer
  bleed color into neighbouring controls.

## Requires firmware 1.10.0 for

Safe Stop, infinite-gesture leases, per-axis PAN/TILT Reverse calibration,
structured gesture-duration metadata, and inert-by-default servos on a newly
erased board.

## Download verification

SHA-256 for `b1-chat-console-setup-0.11.0.exe`:

`184a4bd4b48501fe049eea9a335c236f7ddf25171880fcf627dbad191b8740eb`

Matching firmware release `fw-v1.10.0`, Build IDs `0798EBB8` (master) and
`8C9045AE` (slave).
