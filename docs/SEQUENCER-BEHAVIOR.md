# B1 Chat — Sequencer behavior contract (console side)

What the WPF console's Sequencer actually does at runtime: transport, stop
levels, scheduling, editing, persistence and Scene workflow. Moved out of
`CLAUDE.md` on 2026-08-12 to keep that always-loaded file focused on
architecture, protocol and pitfalls.

Related documents:

- [`../CLAUDE.md`](../CLAUDE.md) — project-wide rules. Keeps a short invariants
  summary that must stay consistent with this file.
- [`ARCHITECTURE.md`](ARCHITECTURE.md) — where the Sequencer's types and
  services live inside `console/`.
- [`KNOWN-PITFALLS.md`](KNOWN-PITFALLS.md) — WPF layout and input traps that
  apply to the timeline.
- [`SEQUENCER-HARDENING.md`](SEQUENCER-HARDENING.md) — the tracked backlog
  (`SEQ-*` items, decision log, evidence log). **That** file records what is
  still open; **this** file records what currently ships.
- [`FIRMWARE-CONTRACT.md`](FIRMWARE-CONTRACT.md) — protocol contract
  status.

This file describes shipped behavior. When behavior changes, update this file
in the same commit.

## Animation execution telemetry

Execution telemetry is observational and never gates the console-side timeline.
`WRITE` means the OS serial write completed, not that the master received it.
New masters then emit `animAccepted` after parsing and validating the command
(`MASTER` in the timeline), including whether ESP-NOW accepted the broadcast
frame and whether the master itself is a local target. The master maps the
console's `requestId` to the existing mesh-header sequence, so `AnimPayload`
remains byte-compatible with older slaves.

New droids report when the software animation engine starts, finishes, is
interrupted, or refuses the command because servos are disabled; broadcast
replies are deterministically jittered to avoid a response burst. The timeline
aggregates reports per online target (`ACK 2/3`, `DONE 3/3`, `REJ 1/3`). Local
refusal is immediate (`NO LINK`, `NOT READY`, `WRITE FAIL`); a failed required
ESP-NOW queue is `MESH FAIL`.

A missing start report expires after 1.5 s (`UNCONF`/`MISS n/N`); finite
gestures that start but do not send a terminal report expire after their
reported duration plus 1.5 s (`TIMEOUT`). Late reports recover the display, and
delayed duplicate `started` reports cannot regress a terminal state. These
warnings never delay or stop the show. Looping POWER_DOWN/TALK require only a
start report because completion requires a later interruption.

This proves firmware execution, not physical servo movement or mechanical
inter-droid skew.

## Infinite gestures: tracking, cleanup and lease

The WPF playback controller records the latest successfully written gesture per
concrete droid. Broadcast TALK or POWER_DOWN expands to the online roster; later
targeted finite/IDLE commands replace only their target. Stop, a non-looping
natural end, application disposal, and Play restart send tracked IDLE commands
only to droids whose latest state is still infinite. A whole-pass Loop boundary
and Pause deliberately do not clean up. Failed serial cleanup remains retryable.

Sequencer playback starts those two gestures with a 5 s firmware lease and
renews it every 2 s while the owning pass remains valid. Missing renewal returns
the droid to IDLE and reports `interrupted/leaseExpired`; renewals are
correlated to the originating mesh sequence so stale packets cannot extend a
replacement gesture. Pause and whole-pass Loop continue renewal, while
Stop/end/restart/disconnect/shutdown cancel it before targeted IDLE cleanup.
Manual Animation-card commands and autonomous animations remain unleased.

On firmware advertising `animLease`, that independent lease is the fail-closed
fallback if cleanup cannot cross a lost serial or mesh path.

## Three stop levels

Normal Sequencer **Stop** cancels the transport/audio and sends targeted IDLE
only for its remaining infinite gestures; finite gestures finish naturally.

**Safe Stop** cancels all console work and broadcasts `safeStop`: each current
droid interrupts motion, moves to calibrated center over the normal IDLE
transition, retains servo holding torque, and transiently suppresses its
automatic animations until a later explicit animation command.

**Emergency Stop** has no confirmation dialog and broadcasts persistent
`servo enabled:false` to the whole fleet. It removes holding torque and may let
unsupported mechanics fall; that behavior is explicitly accepted for this
project.

Older firmware without the additive `safeStop` cap receives broadcast IDLE as a
best-effort fallback but cannot suppress subsequent automatic motion.

## Pause is not a hardware stop

Pause freezes the PC playhead, future scheduler dispatches and local audio only.
Already received finite gestures continue to completion, TALK/POWER_DOWN keep
running under their renewed lease, and their execution reports may update while
paused. Play resumes undispatched events and audio without resending gestures
that continued. The transport shows `PAUSED · DROID MOTION CONTINUES` so this
behavior is never implied to be a physical freeze; use Stop, SAFE or E-STOP for
their distinct policies above. Clicking the unchanged paused playhead preserves
this resumable state. Moving the playhead during Pause instead abandons the old
pass through normal Stop cleanup, changes transport to Stopped and leaves the
cursor at the requested time; the next Play creates a fresh play-from-cursor
pass there.

## Transport state and controls

Transport state is single-source: the WPF controller owns one guarded
`Stopped`/`Playing`/`Paused` value. Play/Pause badges, LIVE tracking, editing
locks and command availability are derived from it, so contradictory UI states
cannot be constructed. Play, Resume and Loop share one pass-start path; a
scheduler startup failure invalidates the generation and returns to Stopped
after disposing the partial scheduler/audio state.

Controls are conventional, and **Stop is not a rewind**: the primary button (and
`Space`) is one Play/Pause/Resume toggle — a second Play press pauses instead of
restarting and resending choreography. `Restart` (`Ctrl+Enter`) is the only
explicit clean from-zero performance path. Normal Stop, Safe Stop and E-STOP all
**retain** the measured playhead for inspection; a non-looping natural end
retains the calculated end. `Return to start` (`Ctrl+Home`, enabled only while
stopped) is the separate navigation action. Play while stopped resumes from the
retained cursor: earlier gesture events remain skipped, while audio clips that
overlap the cursor seek to their matching source offsets (modulo their duration
when looping). At the natural end Play starts a fresh pass from zero.

## Preflight and Play gate

The toolbar's **Preflight** control performs a side-effect-free readiness scan.
Its expandable panel reports `ERROR`, `WARNING` and `INFO` findings with the
affected target, lane, filename, gesture and timestamp. **Go to** moves a stopped
playhead to the finding and selects its gesture when applicable. The scan never
sends protocol traffic, probes media, changes Windows settings or edits the Scene.

Play and Restart refresh the scan before replacing or starting a pass. Errors
intercept that start and open the panel; warnings remain playable. Resume also
rechecks the live environment without rebuilding the immutable paused plan. A
Scene with active gestures requires an open port, completed handshake and online
master. Targeted clips require that target online, and broadcasts require at
least one online recipient. Muted gesture tracks are excluded because their
commands will not be dispatched. An audio-only Scene does not require a droid
connection. Muting an offline target is the explicit rehearsal authorization to
run without that droid; missed commands are never queued or replayed if it later
reconnects.

Every audio path is checked again when the panel or transport runs. Missing or
decoder-rejected files are errors; validation still in progress and a validated
zero/unknown duration are warnings. The latter may play but cannot establish an
automatic content tail. Every effective `TALK`/`POWER_DOWN` must retain its
authored endpoint of at least 100 ms inside the effective Scene boundary; an
invalid endpoint blocks Play because targeted IDLE cleanup would not be
represented. Existing import/edit validation normally prevents that state, so
this is a final fail-closed check.

The same scan compares every unmuted gesture's represented half-open duration
span and effective target intersection. Same-target overlaps, duplicate
timestamps and broadcast/target intersections are warnings, not errors. Each
finding links to the later clip where replacement or ambiguous delivery begins;
touching endpoints do not overlap. Offline-target conflicts remain visible for
repair before reconnection, while a muted track is excluded completely.

## Scene endpoint and loop boundaries

Every Scene has one authoritative endpoint, drawn as a cyan dashed `END` line
and shown by the total timecode. `END AUTO` follows the latest effective gesture
or audio tail. **Set End** changes it to `END SET` at the stopped playhead; it may
extend the Scene but never truncate existing content. Moving content later in the
same edit advances a fixed endpoint with it. **Auto** returns to the calculated
tail. Endpoint mode/value are persistent document state, participate in
Dirty/Undo/Redo, and are locked during Play/Pause.

A looping audio clip repeats its natural media cycle until this Scene endpoint;
it no longer needs an unrelated later clip merely to keep the pass alive. At the
endpoint, non-Loop playback stops every audio handle and retains the endpoint
cursor. Whole-pass Loop first stops the old audio generation and completes any
scheduled infinite-gesture termination at that boundary, then starts exactly one
fresh pass from t = 0. Generation checks prevent stale boundary callbacks from
rearming an older pass. An automatic empty Scene remains a Play no-op; a Scene
with a fixed non-zero endpoint may intentionally run as a silent timed pass.

## Timeline navigation (`SequenceTimelineView.xaml.cs`)

A visible `Follow` toggle keeps the playhead inside a 15–72 % viewport comfort
corridor, changing horizontal offset only. New Play/Restart re-enables it. A
horizontal scrollbar drag suspends Follow while the pointer is held and restores
it after the final deferred `ScrollChanged`; Fit, slider zoom, pointer zoom and
Shift-wheel pan suspend it until the operator opts back in. Pause freezes it and
Resume preserves its state. Automatic scroll destinations stay tagged until WPF
observes them, otherwise Follow mistakes its own movement for manual navigation
and turns itself off at the corridor boundary.

`Ctrl+wheel` zooms multiplicatively (1.15 per notch, clamped 20–300 px/s) around
the pointer's content time, `Shift+wheel` pans horizontally, plain wheel stays
native — and `MainWindow`'s tunneling wheel handler must keep yielding modified
wheel events that originate in the timeline viewport, or the nested handler
never runs.

## Explicit gesture insertion target

Fresh startup has no armed gesture track. Clicking a library chip inserts only
when a track was explicitly armed; it never falls back to the first `All droids`
row. The library header continuously shows `NO TRACK ARMED` or
`ARMED · <target>`, including an unmistakable `All droids` broadcast label.
Direct drag-and-drop remains available without arming because the drop lane is
itself the explicit target. The command and direct mutation guard enforce the
same rule for mouse, keyboard and programmatic paths. Track rebuilds preserve an
armed ID only while that row still exists.

## Scheduler

Playback uses one rearmable OS timer per active pass, not one timer per event.
`SequencerPlaybackPlan` groups its immutable ordered events into timestamp
batches. A monotonic cursor drains every due batch in source order, catches up
late host wakes without drift accumulation, then rearms the same timer for the
next batch/end boundary. Pause/Stop/generation replacement disposes the timer
completely.

The final wake is always armed against the immutable pass endpoint, independently
of the last event timestamp. This makes empty timed passes, extended audio loops
and whole-pass Loop share the same deterministic boundary.

Same-target gestures retain editor order with last-received-wins semantics;
broadcast plus targeted overlap is serialized but flagged because mesh arrival
remains ambiguous. Preflight covers complete represented duration overlaps and
links them to their source clips. The transport's hoverable SCHEDULE badge
remains a runtime reminder for same-timestamp conflicts captured in the active
plan.

## Audio robustness

Everything below runs console-side; the droids have no audio hardware.

**Duration probing.** `AudioProbe` returns a typed `AudioProbeResult`
(`Ok`, `FileMissing`, `DecodeFailed`, `Timeout`, `Cancelled`), is bounded by a
10 s default timeout, accepts a cancellation token, and disposes its media handle
on every exit path including timeout and exception. Disposal is marshalled to the
handle's owning WPF dispatcher even when the bounded wait resumes on a worker
thread. A file that opens but reports no timespan is a `DecodeFailed`; a valid but
empty file is an `Ok` at 0 ms.

**Unreadable clips stay visible.** A failed probe still inserts the clip. It
renders at a minimum width, shows a ⚠ badge and an orange border, and its tooltip
carries the reason. Its effective duration is 0, so it never contributes a stale
tail to the sequence end — the width floor is presentation only. A Scene retains
the last serialized duration for recovery, but playback and geometry ignore it
while the asset is missing or unreadable. Scene load and Undo/Redo re-probe each
distinct present asset; those clips remain warning-badged with zero effective tail
until validation completes, while missing files are flagged immediately by
existence check.

**Playback lifecycle.** Each clip owns one media handle. A non-looping clip that
ends, or any clip that fails, detaches its handlers, closes and leaves the active
set immediately; a looping clip rewinds and stays. `PauseAll`/`ResumeAll`
therefore touch only genuinely active clips, and Resume cannot restart something
that already finished. `StopAll` is idempotent. A playback failure is reported
once per clip, naming the file in a visible `⚠ AUDIO` transport badge and tooltip,
and the rest of the pass continues.

**Audio-loop endpoint.** A clip's natural duration remains its seek/modulo cycle;
its `Loop` flag never fabricates an unbounded document tail. Once started, the
player repeats until the immutable Scene endpoint and is stopped exactly once at
that pass boundary. Pause/Resume retains its media position, while whole-pass Loop
closes the old handle before starting the next pass.

**Play from cursor.** Starting a stopped pass inside an audio clip opens that
clip and seeks to the elapsed source position before playback. Looping clips use
the matching modulo phase. Earlier gesture clips are not reconstructed because
their mechanical state cannot be inferred safely from timeline position alone.

**Waveforms.** The decode cache is keyed on path plus file size and last-write
time, so replacing a file's contents under the same name invalidates it. Failed
and cancelled decodes are not cached, so they can retry. The cache is bounded
(64 entries, least-recently-used evicted). Each clip carries a waveform token
bumped whenever its source changes; a decode that finishes after the clip moved on
is discarded rather than overwriting the current envelope.

## Editing policy during playback

Persistent editing is permitted only in `Stopped`: timeline content, Loop,
Scene endpoint, inspector fields, audio lanes/clips, Undo/Redo, Clear, and Scene
Save/Save As/Trash all lock during Play and Pause. Selection/inspection, track
arming, runtime track mute, zoom/Snap/Fit/scroll/Follow, and Export remain
available because they do not change the immutable pass being performed.

Document replacement (New/Open/Import) stays discoverable during Play/Pause but
first asks permission to stop — that Stop is deferred until every cancel-capable
question has succeeded, so a refused save never leaves the pass half-stopped.

## Edit transactions, drags and history

Command and pointer-drag mutations use one structural snapshot edit transaction.
A real commit records one pre-edit Undo snapshot, clears Redo, marks Dirty and
refreshes tracks/ruler/timecode once; a no-op commit records nothing. Persistent
DTO fields participate in comparison, while selection, execution telemetry,
waveform peaks and drag visuals remain transient. Direct property bindings join
this transaction boundary (SEQ-C03).

Clip drags use a 5 px threshold before opening a transaction. Escape, lost mouse
capture, view unload and window deactivation restore the pre-edit snapshot and
clear gesture/audio drag state, library ghosts and ruler capture; cancelled
ruler scrubbing restores its starting playhead. Inspector properties,
sequence/audio Loop, Scene endpoint, lane labels/order and sequence name share
the same transaction path. Undo and Redo are newest-first bounded lists retaining exactly
50 snapshots; document snapshots exclude every transient editor/telemetry field.

## Type boundaries

`SequenceSnapshot` contains and structurally compares only persistent document
DTOs; `SequencerEditHistory` owns begin/commit/cancel and bounded Undo/Redo
without any WPF or playback dependency; `SequencerPlaybackPlan` captures
immutable runtime events. `SequencerViewModel` coordinates them while retaining
transient selection, viewport, drag visuals, waveform and execution telemetry.

## Import and schema versions

File import is validate-then-apply. `SequenceImportService` strictly parses
`b1-sequence` schemas 1–6 into a temporary `ImportedSequenceDocument`, checks
identities, bounded counts/strings/timing and target/gesture ranges, and runs
named migrations before the ViewModel mutates. V1 `delayMs` values are
cumulative waits after the current gesture, producing absolute starts from the
sum of prior delays. Retired numeric DFPlayer `audioTrack` metadata is validated
but intentionally discarded; it cannot identify a console-side audio file.

**Schema v5** adds `endAfterMs`: POWER_DOWN/TALK persist a real user-visible endpoint
(default/migration 2 s, edited in 100 ms steps in the inspector) instead of a
purely indicative clip width. A v5 document containing an infinite gesture
without that field is rejected rather than silently re-guessed; older schemas
migrate to the default.

**Schema v6** (`SequenceImportService.CurrentVersion`, also the version Export
writes) adds root `endMs`: JSON `null` means automatic content-tail mode; a
bounded integer is the user-set endpoint. Versions 1–5 migrate to automatic mode.

## Dirty state and atomic persistence

`Dirty` is structural equality against one saved `SequenceSnapshot`, never a
manually toggled edit flag. Local Library Save/Load and Import establish that
checkpoint; Export also establishes it for new/external documents but stays an
external copy for a library-backed Scene, where it cannot falsely clear unsaved
library edits. Normal edits and Undo/Redo recompute equality, so returning
exactly to the checkpoint clears Dirty without deleting history.

Export and Local Library writes flush sibling temporary files and atomically
rename them; failure preserves the old file and checkpoint. Interactive
Import/Load ask before replacing a Dirty document and all library mutations
remain locked throughout Play/Pause; startup restore stays silent.

## Scene library and document workflow

The current Sequencer document is a Scene. `LibraryService` stores versioned
`b1-scene-library-item` envelopes under stable GUID filenames, with the
validated `b1-sequence` document nested inside. Save updates the active
identity; Save As creates another and case-insensitive name conflicts never
overwrite. Valid flat legacy entries migrate atomically and their originals move
to `library\trash`; confirmed removal uses the same recoverable trash directory.
Corrupt entries remain untouched and are counted in the UI. `settings.json`
discriminates the last library Scene identity from the last external file path.

**The Scene is edited like a conventional document** (2026-08-12): the permanent
Local Library list below the timeline is gone. A Scene bar exposes New / Open /
Save with name, origin (`NEW` / `LOCAL LIBRARY` / `IMPORTED / EXTERNAL FILE`)
and state (`CLEAN` / `SAVED` / `MODIFIED`) badges; Save As, Rename, Import,
Export and Trash live in a secondary menu. Open launches a modal
`SceneBrowserWindow` (sorted by recent save, name search, current-Scene marker,
gesture/audio content summary, double-click, empty state). Replacing a modified
Scene offers save / continue without saving / cancel through
`SceneDecisionWindow`, and `SceneNameWindow` replaces the former programmatic
Save As prompt (native Windows file pickers remain native, for Import/Export
only). Shortcuts `Ctrl+N`/`Ctrl+O`/`Ctrl+S`/`Ctrl+Shift+S`/`F2` are wired at the
Sequencer card level. The underlying stable-GUID storage contract is unchanged.
