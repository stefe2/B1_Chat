# Sequencer — Playback & Saving

![Sequencer transport and file controls](../images/sequencer-transport.png)

*Figure: Playback is controlled from the left; Snap, Fit, Undo/Redo,
Export/Import, Clear, and audio-lane creation follow in the same toolbar.*

## What Play actually does

Play schedules one console timer per gesture and audio clip. At each start time,
the console sends the real gesture command through the master and starts local
audio on the PC. Keep the console running, the master connected, and the PC awake
for the entire performance.

This is practical choreography, not sample-accurate show control. Windows timer
scheduling, serial delivery, ESP-NOW relays, weak links, and commands sharing the
same start time can introduce small offsets. Commands at the same timestamp are
sent in sequence, not latched by every droid on one hardware clock edge.

![Audio and per-droid gesture tracks](../images/sequencer-tracks.png)

*Figure: The orange playhead crosses linked audio and per-droid gesture lanes.
Muted or offline rows do not create a queue for missed commands.*

## Play, Pause, Stop, and Loop

- **Play** while stopped starts from t = 0. Pressing Play during playback restarts
  cleanly from t = 0.
- **Pause** freezes the console playhead, pauses active PC audio, and cancels
  future scheduled sends.
- **Play** while paused resumes local audio and schedules items that have not yet
  reached their start time.
- **Stop** cancels future sends, stops local audio, and resets the playhead to 0.
- **Loop** starts a new pass when the calculated sequence duration ends.

Persistent timeline editing is locked during both Play and Pause. You can still
inspect the timeline and change track mute switches, but press **Stop** before
inserting, moving, deleting, importing, or otherwise changing sequence content.
This keeps the visible document identical to the immutable pass being performed.

An unexpected serial disconnect stops the active pass and its local audio rather
than silently continuing a partial audio-only performance. Closing the console
does the same cleanup. A deliberate audio-only Dry Run mode is a future feature,
not an automatic fallback for a lost master.

> **Important:** gestures are fire-and-forget commands. Pause and Stop cannot
> freeze or retract a gesture already sent to a droid. A one-shot gesture finishes
> naturally. A looping `POWER_DOWN` or `TALK` gesture continues until another
> gesture replaces it or Servos is disabled.

A muted droid track is skipped when its scheduled start arrives. If the command
was already sent before you mute or pause, the target continues it.

## Offline droids and missed events

An offline track is retained for layout, but it does not create an offline work
queue. A droid that reconnects halfway through a performance does not receive
earlier clips retroactively. Stop, restore connectivity, and restart the pass if
those events matter.

## No standalone onboard playback

Firmware versions before 1.7.0 had eight master sequence slots and could run
without a PC. Those slots and that player no longer exist. Current playback
always needs the console and active serial connection.

## Export and Import

**Export** writes a `.b1seq.json` snapshot containing:

- sequence name and whole-sequence Loop setting;
- gesture clips and target IDs;
- saved droid track names/order for offline layout;
- audio lane names, clip timing, Loop flags, and local file paths.

The sound bytes are not embedded. Copy the audio files separately and expect to
repair paths when moving a sequence to another PC.

**Import** loads the snapshot and records that file as the last sequence path.
The console tries to reload that same file on its next launch. Later edits are
still in memory only: they do not update the file until you Export again.

Export currently does not clear every internal dirty/edit warning, so Clear may
still ask for confirmation after an export. Treat the exported file as the
authoritative snapshot, not the warning state.

## Current Local Library behavior

The Local Library panel can **Load** and **Delete** entries already present in
the console's local library directory. The current interface no longer includes
a command to save a new or edited sequence into that library. For all new work,
use Export/Import. See [Data & Backups](../reference/data-and-backups.md) for the
library and settings locations.

## Clear and recovery

**Clear** removes every gesture and audio clip but keeps the audio lanes and
current sequence name. It asks before clearing when the editor reports changes,
and the clear itself can be undone immediately. Undo history is memory-only; an
application restart cannot recover an unexported timeline.
