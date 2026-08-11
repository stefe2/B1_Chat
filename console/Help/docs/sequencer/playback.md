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
- **Stop** cancels future sends, stops local audio, sends targeted `IDLE` cleanup
  to droids whose latest Sequencer gesture is `TALK` or `POWER_DOWN`, and resets
  the playhead to 0.
- **Safe Stop** cancels the same console work, then tells every reachable droid
  to interrupt its current gesture, move to calibrated center, retain servo
  holding torque, and suppress automatic animation until a later explicit
  gesture is sent.
- **E-STOP** cancels the show and persistently disables servo outputs on every
  reachable droid immediately. It has no confirmation dialog. With no holding
  torque, unsupported mechanics may fall; re-enable Servos deliberately from
  the Droids card after inspecting the hardware.
- **Loop** starts a new pass when the calculated sequence duration ends.

Persistent timeline editing is locked during both Play and Pause. You can still
inspect the timeline and change track mute switches, but press **Stop** before
inserting, moving, deleting, importing, or otherwise changing sequence content.
This keeps the visible document identical to the immutable pass being performed.

An unexpected serial disconnect stops the active pass and its local audio rather
than silently continuing a partial audio-only performance. Closing the console
does the same cleanup. A deliberate audio-only Dry Run mode is a future feature,
not an automatic fallback for a lost master.

> **Important:** Pause cannot freeze or retract a gesture already sent to a
> droid, and Stop does not interrupt finite one-shot gestures. A one-shot gesture
> finishes naturally. For a tracked looping `POWER_DOWN` or `TALK`, Stop,
> non-looping natural end, application shutdown, or restarting Play sends a
> targeted `IDLE` to each affected droid without disturbing other targets.

The console remembers the latest Sequencer gesture written for each concrete
droid. Broadcast looping gestures are expanded to the droids online at dispatch;
a later per-droid finite gesture removes only that droid from cleanup. Repeated
Stop is idempotent after a successful serial write. If the link is unavailable,
cleanup remains retryable. Current firmware also gives Sequencer-started
`TALK`/`POWER_DOWN` a five-second safety lease, renewed every two seconds while
the pass still owns the gesture. If the console crashes, the cable is removed,
the master becomes unreachable, or renewal otherwise stops, the target returns
to `IDLE` automatically. Pause and a whole-sequence Loop boundary keep renewing;
explicit Stop cancels renewal before sending IDLE. Delayed renewal from an older
pass cannot extend a newer gesture.

This lease applies only to Sequencer playback. A looping gesture started from
the Animation card remains a direct operator command and runs until another
gesture is sent. Autonomous idle animations are also outside the lease policy.
With older firmware that does not advertise `animLease`, the Sequencer retains
targeted Stop cleanup but cannot provide the crash/link-loss fallback.

Safe Stop requires firmware advertising `safeStop`. With older firmware the
console sends broadcast `IDLE` as a best-effort fallback, but automatic movement
may resume afterward. Emergency Stop uses the older fleet Servo OFF command and
therefore remains available across that compatibility boundary.

Each gesture clip shows non-blocking delivery and execution feedback. `WRITE`
means Windows accepted the write to the serial port. `MASTER` means the master
parsed, validated, and dispatched that correlated command. `START`/`ACK n/N`
means one or more software animation engines started it, and `DONE`, `STOP`, or
`REJECT` reports the final result. `NO LINK`, `NOT READY`, or `WRITE FAIL`
appears immediately when the command cannot leave the console; no execution
timeout is armed for such a command. `MESH FAIL` means the master accepted the
command but its radio stack could not queue a frame needed by the selected
remote/broadcast target. If no start report arrives within 1.5 seconds, the
clip changes to
`UNCONF` (or `MISS n/N` for a broadcast). A finite gesture that started but did
not report completion by its expected duration plus 1.5 seconds changes to
`TIMEOUT`. These are warnings: they never pause the timeline. A delayed valid
report updates the clip again. Hover over the clip for per-droid details.

Older firmware without the additive `animAccepted` event can move directly from
`WRITE` to a target execution state. ESP-NOW's `meshQueued` indication only says
the radio stack accepted the outgoing frame; it is not proof that a slave
received it. Target execution remains the success signal.

`POWER_DOWN` and `TALK` loop while their Sequencer lease is renewed, so their
healthy state remains `START`; they become terminal when another gesture
interrupts them, the firmware rejects the command, or the lease expires
(`STOP`, reason `leaseExpired`). Execution feedback confirms the firmware
animation engine, not physical servo motion.

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
