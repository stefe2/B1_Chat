# Animation Sequencer — Hardening backlog

Status: implementation underway — first Safe Playback foundation batch
Created: 2026-08-11
Scope: WPF console Sequencer, console-side audio, serial/mesh animation dispatch,
and the small firmware changes needed to give playback safe stop semantics.

This document is the persistent source of truth for making the Animation
Sequencer reliable. `PROGRESS-ARCHIVE.md` remains the historical record; this
file tracks unfinished work and must stay current as items are implemented.

## Tracking rules

Status markers:

- `[ ]` — not started;
- `[~]` — in progress;
- `[!]` — blocked, with the blocker written under the item;
- `[x]` — implemented and all stated acceptance checks passed;
- `[H]` — code complete but hardware validation still required;
- `[D]` — deliberately deferred enhancement, not required for the reliability
  baseline.

Priorities:

- **P0** — safety or playback-corruption risk; fix before show use;
- **P1** — correctness or data-loss risk;
- **P2** — robustness, maintainability, or significant usability issue;
- **P3** — optional enhancement.

An item is complete only when its code, automated checks, relevant manual check,
and user/help documentation all agree. A clean compile alone is not completion.
Do not mix unrelated items merely because they touch the same file.

## Baseline and global definition of done

Current baseline, established by the 2026-08-10 static review:

- the console builds with 0 errors and 0 warnings;
- playback is fully console-driven and uses absolute `StartMs` values;
- no Sequencer-specific automated test project was found;
- existing user changes in `README.md`, `platformio.ini`, `src/config.h`, and
  `docs/` must be preserved;
- runtime behavior has not yet been validated with a real multi-droid fleet.

Every implementation batch must:

1. keep the console build clean;
2. run the relevant new tests plus the existing repository checks;
3. leave the application in a usable state at the end of the batch;
4. update this document's marker and evidence notes;
5. update in-app Help/tooltips when externally visible behavior changes;
6. identify checks that still require real hardware.

## Milestones

| Milestone | Outcome | Included epics | Exit gate |
|---|---|---|---|
| M1 — Safe Playback | No stale or hidden playback; Stop is safe | A, B | All P0 items in A/B passed |
| M2 — Reliable Editing | Dirty, Undo/Redo and persistence are trustworthy | C, D | All P0/P1 items in C/D passed |
| M3 — Deterministic Playback | One testable scheduler with stable ordering | E | Scheduler timing suite passed |
| M4 — Show Ready | Durations, audio and preflight are robust | F, G | Preflight plus hardware protocol passed |
| M5 — Enhancements | Optional creative/productivity features | Deferred P3 items | Chosen individually |

## Dashboard

The dashboard is updated whenever an item changes state.

| Epic | Description | Required items complete | Deferred ideas complete |
|---|---|---:|---:|
| A | Playback isolation and cancellation | 7 / 8 | — |
| B | Infinite gestures and Stop/Pause semantics | 6 / 6 | 0 / 1 |
| C | Dirty, Undo/Redo and editing transactions | 8 / 8 | 0 / 1 |
| D | Import, export and local library | 8 / 8 | 0 / 1 |
| E | Deterministic scheduler and performance | 4 / 6 | 0 / 1 |
| F | Duration and audio robustness | 1 / 8 | — |
| G | Preflight and ergonomics | 0 / 13 | 0 / 4 |
| H | Automated and hardware validation | 1 / 8 | — |
| I | Scene & Show System (future) | — | 0 / 22 |
| J | Commissioning and servo configuration safety | 0 / 2 | — |

## EPIC A — Playback isolation and cancellation

### [x] SEQ-A01 — Build an immutable playback plan

- **Priority:** P0
- **Problem:** timer callbacks retain mutable `SequenceStep` and `AudioClip`
  objects. An edit after scheduling can change what a previously armed callback
  sends or plays.
- **Depends on:** SEQ-H01.
- **Acceptance:** Play copies all runnable events into immutable records containing
  resolved start time, target, gesture/seed or audio path/loop state. Later editor
  mutations cannot alter the active pass.
- **Validation:** unit test edits, deletes, and replaces source objects after Play;
  the captured plan remains unchanged.

### [x] SEQ-A02 — Add playback generation cancellation

- **Priority:** P0
- **Problem:** disposing `System.Threading.Timer` does not guarantee that an
  already queued callback cannot execute during a later pass.
- **Depends on:** SEQ-A01.
- **Acceptance:** every pass has a unique generation/cancellation token; all
  callbacks verify it immediately before side effects; Stop, restart, disconnect
  and shutdown invalidate the generation. Under resolved DEC-001, Import, Load
  and Clear cannot execute during Play/Pause and therefore cannot replace an
  active generation.
- **Validation:** stress test rapid Play/Stop/Play and Loop transitions; no event
  from an earlier generation reaches the protocol or audio fake.

### [x] SEQ-A03 — Define and enforce the editing policy during playback

- **Priority:** P0
- **Problem:** Clear, Import, library Load, deletion, and inspector edits can
  occur while a different timeline is still playing.
- **Depends on:** SEQ-A01, SEQ-A02.
- **Acceptance:** choose one consistent rule: lock sequence-changing controls
  during Play/Pause, or stop the pass before an accepted edit. The UI indicates
  why a control is unavailable. Zoom, horizontal scroll, and harmless inspection
  may remain available.
- **Validation:** manual UI matrix covers every editing command in stopped,
  playing, and paused states.
- **Implemented:** persistent document and Local Library mutations are enabled
  only in `Stopped`. Relay-command `CanExecute`, direct ViewModel guards,
  inspector/container disabling and pointer-drag rechecks all derive from
  `CanEditSequence`; the transport displays `EDIT LOCKED` and disabled controls
  explain that Stop is required. A transport transition during a captured drag
  now releases transient visuals without applying a late placement change.
- **Policy/validation matrix:**

  | Operation group | Stopped | Playing | Paused |
  |---|---|---|---|
  | Insert, drag, retarget, inspector, duplicate/delete, Loop | edit | locked | locked |
  | Audio lane/clip edits, Undo/Redo, Import, Clear | edit | locked | locked |
  | Local Library Load/Delete | edit | locked | locked |
  | Select/inspect, arm track, dynamic track mute | allowed | allowed | allowed |
  | Zoom, Snap, Fit, scroll and Export snapshot | allowed | allowed | allowed |

  The compiled XAML bindings/tooltips and context-menu command paths were
  reviewed against this matrix. The automated three-state command/guard matrix,
  including separately available Undo and Redo histories, passes within the
  complete 76/76 WPF suite.

### [x] SEQ-A04 — Evaluate track mute at dispatch time

- **Priority:** P1
- **Problem:** mute is currently sampled only when timers are created. Toggling
  it during a pass does not affect already scheduled events.
- **Depends on:** SEQ-A01.
- **Acceptance:** a track muted before an event's dispatch suppresses that event;
  unmuting allows later events. The chosen behavior is documented and tested.
- **Validation:** fake clock test toggles mute on both sides of a due time.

### [x] SEQ-A05 — Stop cleanly on link loss and application shutdown

- **Priority:** P1
- **Problem:** serial disconnect clears the live droid list but does not end the
  Sequencer pass; local audio and timers can continue. Shutdown has no explicit
  Sequencer cleanup contract.
- **Depends on:** SEQ-A02.
- **Acceptance:** unexpected disconnect and orderly app shutdown invalidate the
  pass, dispose scheduling resources, close active players, and update transport
  state. The policy for optionally continuing audio-only rehearsal is explicit.
- **Validation:** simulated `LinkClosed` and window shutdown leave no live timer
  or player.

### [x] SEQ-A06 — Make transport state transitions single-source and consistent

- **Priority:** P1
- **Problem:** `IsPlaying`, `IsPaused`, `IsLiveTracking`, playhead position,
  elapsed-at-pause, timers, and players are updated independently in several
  branches.
- **Depends on:** SEQ-A02.
- **Acceptance:** a small state machine or equivalent centralized transition
  methods define Stopped, Playing, and Paused. Commands and badges derive from
  that state and impossible combinations cannot occur.
- **Validation:** transition table test covers Play, restart, Pause, Resume,
  Stop, natural end, Loop, disconnect, and failed start.
- **Implemented:** `SequencerTransportState` is the sole writable transport
  state. `IsPlaying`, `IsPaused`, `IsLiveTracking`, editing availability and
  Pause command availability are derived from it; a private guarded transition
  method rejects illegal state changes. Start/Resume/Loop share one pass-start
  path, and a partial scheduler failure performs full stopped-state cleanup.
- **Evidence:** the nine-row transition-table theory covers every required path
  and asserts all derived UI flags and notifications; the complete WPF suite
  passes 75/75.

### [x] SEQ-A07 — Use monotonic elapsed time for the playhead

- **Priority:** P1
- **Problem:** `DateTime.UtcNow` can jump when Windows adjusts its clock.
- **Depends on:** SEQ-E01 or a minimal injectable monotonic clock.
- **Acceptance:** elapsed playback and pause position come from `Stopwatch` or an
  injected monotonic clock; wall-clock changes cannot move the playhead.
- **Validation:** fake clock test simulates wall-clock jumps while monotonic time
  advances normally.

### [ ] SEQ-A08 — Add explicit Edit, Ready, Armed and Playing lifecycle states

- **Priority:** P1
- **Problem:** a user can validate one document state, modify it, and still have
  transport controls that appear ready; there is no explicit arming boundary
  between authoring and performance.
- **Depends on:** SEQ-A03, SEQ-A06, SEQ-C05, SEQ-G01.
- **Acceptance:** define `EDIT → READY → ARMED → PLAYING` transitions. Successful
  Preflight can enter Ready; arming locks the exact document/casting/environment
  snapshot; Play requires Armed in performance mode. Any persistent edit or
  relevant connection/asset change invalidates Ready/Armed and returns to Edit.
  Rehearsal may use a clearly distinct, documented shortcut.
- **Validation:** state-table tests cover edit after validation, asset removal,
  casting/roster change, disconnect, arm/disarm, Play, Stop and failed Preflight.

## EPIC B — Infinite gestures and Stop/Pause semantics

### [x] SEQ-B01 — Track infinite gestures activated by the pass

- **Priority:** P0
- **Problem:** `POWER_DOWN` and `TALK` loop on a droid until another animation is
  received, but the console does not track which targets need cleanup.
- **Depends on:** SEQ-A01.
- **Acceptance:** playback records every affected concrete/broadcast target and
  whether its latest dispatched gesture is infinite. Later finite or IDLE
  gestures update that state correctly.
- **Validation:** tests cover broadcast plus per-droid overrides and repeated
  infinite gestures.
- **Implemented:** the latest successfully written gesture is tracked per
  concrete droid and request. Broadcast uses the online roster, later targeted
  gestures replace only their droid, stale terminal reports cannot clear a
  newer command, and a failed mesh queue restores the prior known state.

### [x] SEQ-B02 — Stop infinite gestures on Stop and natural end

- **Priority:** P0
- **Problem:** Stop currently cancels console work only; TALK/POWER_DOWN can keep
  moving hardware indefinitely after the UI returns to zero.
- **Depends on:** SEQ-B01, SEQ-A02.
- **Acceptance:** Stop and non-looping natural end send a safe termination
  (`IDLE` or a dedicated firmware command) to every target still running an
  infinite gesture, without disturbing unrelated targets. Behavior on link loss
  is documented because delivery cannot be guaranteed.
- **Validation:** protocol fake asserts exact target cleanup; real droid confirms
  TALK and POWER_DOWN end safely.
- **Implemented:** Stop, non-looping natural end, application disposal and Play
  restart send targeted tracked IDLE only to remaining infinite targets. Pause
  and whole-pass Loop boundaries intentionally retain them. Successful cleanup
  is idempotent; a local write failure leaves cleanup retryable. Link-loss
  delivery remains retryable, while SEQ-B06 provides the independent firmware
  fallback when the console or link disappears.

### [x] SEQ-B03 — Make Pause semantics explicit and honest

- **Priority:** P1
- **Problem:** Pause freezes audio/timeline scheduling but cannot freeze a gesture
  already running on a droid.
- **Depends on:** SEQ-A06.
- **Acceptance:** UI and Help call this out unambiguously. Decide whether Pause
  leaves hardware gestures running, terminates only infinite gestures, or gains
  a new firmware pause capability. Resume semantics match that decision.
- **Validation:** manual test with a long finite gesture and TALK.
- **Implemented:** Pause is explicitly console-transport-only. It freezes the
  playhead, future scheduled sends and local audio, but sends no hardware
  command: already dispatched finite gestures complete normally and
  TALK/POWER_DOWN continue with lease renewal. Execution reports continue to
  update while paused. Play resumes only undispatched timeline events and local
  audio; it does not resend a gesture that continued. The transport shows a
  persistent `PAUSED · DROID MOTION CONTINUES` warning and its tooltip/Help use
  the same wording.

### [x] SEQ-B04 — Give infinite gesture clips explicit end semantics

- **Priority:** P1
- **Problem:** their displayed two-second width is only indicative and is not an
  actual endpoint.
- **Depends on:** SEQ-B02, SEQ-F01.
- **Acceptance:** TALK/POWER_DOWN clips have a real user-visible duration or an
  explicit "until next gesture/end" mode. Playback sends termination at the
  represented endpoint, and timeline width matches behavior.
- **Validation:** edit, export/import, Play, Pause/Resume, Stop, and Loop tests.
- **Implemented:** POWER_DOWN/TALK persist a user-visible fixed endpoint
  (`endAfterMs`, default/migration 2 s) in schema v5. Their clip width and
  transport endpoint are identical. Playback sends targeted IDLE only while the
  originating infinite request still owns that droid, so a later gesture at the
  same timestamp cannot be stopped by stale cleanup. The inspector edits the
  endpoint in 100 ms increments; Undo/Redo, Pause/Resume, Loop and export/import
  are covered.

### [D] SEQ-B05 — Link TALK duration to an audio clip

- **Priority:** P3
- **Problem:** synchronizing TALK to audio is presently manual.
- **Depends on:** SEQ-B04, SEQ-F08.
- **Acceptance:** a user can link TALK to a chosen audio clip so it starts and
  terminates with that clip, with a visible relationship and deterministic
  behavior when the audio loops or is missing.
- **Validation:** UI and playback integration test.

### [x] SEQ-B06 — Add a firmware fail-safe lease for infinite gestures

- **Priority:** P0
- **Problem:** console cleanup cannot stop TALK/POWER_DOWN if the PC crashes, the
  serial cable disconnects, or the master becomes unreachable before IDLE is
  delivered.
- **Depends on:** SEQ-B01, firmware protocol design.
- **Acceptance:** infinite gestures started by Sequencer playback carry or create
  a bounded lease/TTL. The console renews the lease while the owning pass remains
  valid; missing renewal causes the droid to return automatically to a defined
  safe state. Manual Animation-card behavior and autonomous animations have an
  explicit, non-ambiguous policy. Stale generations cannot renew a new pass.
- **Validation:** real and simulated tests cover normal renewal, console crash,
  cable removal, master loss/restart, delayed packets, renewal from a stale pass,
  and clean IDLE before expiry.
- **Implemented:** Sequencer-started `TALK`/`POWER_DOWN` use a 5 s firmware
  lease renewed every 2 s after correlated master acceptance. Pause and
  whole-pass Loop retain renewal; Stop, natural end, restart, disconnect and
  disposal cancel it before targeted IDLE cleanup. The firmware fails closed to
  IDLE with an `interrupted/leaseExpired` report. Renewals carry the originating
  mesh sequence, so an old callback or delayed packet cannot extend a newer
  gesture. The manual Animation card remains intentionally unleased and
  autonomous animations are unaffected. Older firmware falls back to the B02
  cleanup behavior through the additive `animLease` capability.

### [x] SEQ-B07 — Define Stop, Safe Stop and Emergency Stop levels

- **Priority:** P0
- **Problem:** one Stop action cannot express both orderly scene termination and
  immediate physical intervention; blindly disabling servos may itself allow a
  mechanism to fall.
- **Depends on:** SEQ-B02, SEQ-B06, servo/mechanical safety review.
- **Acceptance:** define at least normal Stop (cancel schedule/audio and orderly
  IDLE), Safe Stop (immediate animation interruption and configured safe pose),
  and Emergency Stop (hardware-specific servo policy). Destructive/high-risk
  actions require unmistakable UI and cannot be confused with Pause. Default
  behavior is justified for the actual head mechanics and remains operable from
  Show mode.
- **Implemented:** the original Stop remains the orderly transport action and
  only cleans Sequencer-owned infinite gestures. Safe Stop cancels every local
  schedule/audio/lease, broadcasts a transient firmware safety hold, interrupts
  current motion, moves each reachable droid to calibrated center while keeping
  servo torque, and suppresses spontaneous animation until a later explicit
  gesture. Emergency Stop has no confirmation delay: it cancels the show and
  broadcasts the existing persistent Servo OFF command. The owner explicitly
  accepted that an unsupported head may fall when servo power is removed. Older
  firmware falls back from Safe Stop to broadcast IDLE without the automatic-
  motion hold.
- **Validation:** protocol/state tests plus a documented real-hardware safety
  procedure covering loaded/unloaded mechanisms, disconnect and repeated use.

## EPIC C — Dirty, Undo/Redo and editing transactions

### [x] SEQ-C01 — Centralize sequence edit transactions

- **Priority:** P1
- **Problem:** commands manually call `PushHistory()` and `Dirty = true`, while
  direct bindings and code-behind edits bypass one or both.
- **Depends on:** SEQ-H01.
- **Acceptance:** all persistent mutations use one begin/commit edit mechanism.
  Commit compares before/after, creates exactly one history entry for a real
  change, clears Redo, marks Dirty, and refreshes derived timeline state.
- **Validation:** table-driven tests run every edit type through the same rules.
- **Implemented:** command and pointer-drag mutations now enter one structural
  snapshot transaction. Commit compares persistent fields, records exactly one
  pre-edit snapshot only for a real change, clears Redo, marks Dirty and performs
  one derived tracks/ruler/timecode refresh. Long-lived gesture/audio drags use
  the same begin/commit boundary; transient selection, execution, waveform and
  drag-visual fields are excluded. Load/Import remain intentional document
  replacement boundaries, while direct property bindings are the scoped work of
  SEQ-C03.
- **Evidence:** a 13-family edit matrix verifies Dirty, one Undo entry, cleared
  Redo, exactly one derived refresh, and exact Undo/Redo round trips. Dedicated
  checks prove click/no-move, zero-clamped nudge and absent-clip move are no-ops,
  and that only a subsequent real edit invalidates Redo. Full WPF suite passes
  78/78.

### [x] SEQ-C02 — Do not create history for selection or no-op drags

- **Priority:** P1
- **Problem:** mouse-down immediately pushes history even when the user only
  selects a clip or releases without moving it.
- **Depends on:** SEQ-C01.
- **Acceptance:** selection is transient; drag history begins only after movement
  exceeds a defined threshold, and a drag ending at its original state is a
  no-op.
- **Validation:** click, sub-threshold motion, moved-and-returned, and real-drag
  interaction tests.
- **Implemented:** gesture and audio clips remain selection candidates until
  pointer movement reaches a shared 5 px threshold. Only then does the edit
  transaction and drag visual begin; structural commit still rejects a drag
  returned to its exact original document state.
- **Evidence:** threshold boundary, click/no-change, moved-and-returned and real
  movement cases pass in the WPF suite.

### [x] SEQ-C03 — Cover every persistent property edit

- **Priority:** P1
- **Problem:** gesture/target inspector changes, lane rename, audio Loop, and
  whole-sequence Loop currently lack dependable history and Dirty behavior.
- **Depends on:** SEQ-C01.
- **Acceptance:** gesture, target, start, audio path/duration/start/lane/Loop,
  lane label/order, sequence Loop/name, insert/delete/duplicate, and Clear all
  participate in transactions. Mute, armed track, selection, zoom, scroll, Snap,
  peaks, and drag visual state remain transient.
- **Validation:** one Undo and one Redo test per property category.
- **Implemented:** transaction-backed editor properties/operations now cover
  sequence name/Loop, gesture animation/target/start, audio path/duration/start/
  lane/Loop, lane label/order, insert/delete/duplicate and Clear. The lane-name
  TextBox spans one focus transaction; inspector and Loop controls no longer
  bind directly to untracked persistent fields. Mute, armed track, selection,
  viewport/Snap, waveform, execution and drag state remain transient.
- **Evidence:** the expanded 20-family matrix performs exact Undo and Redo round
  trips, while a dedicated transient-state test proves no Dirty/history entry.

### [x] SEQ-C04 — Enforce the history capacity

- **Priority:** P2
- **Problem:** `HistoryMax = 50` is declared but old snapshots are never removed.
- **Depends on:** SEQ-C01.
- **Acceptance:** history retains exactly the newest configured number of edits,
  without unbounded memory growth, and Redo follows the same bounded policy.
- **Validation:** perform more than 50 edits and verify boundaries/order.
- **Implemented:** Undo and Redo use bounded newest-first lists. Each push evicts
  the oldest snapshot beyond the configured 50-entry capacity, for both history
  directions.
- **Evidence:** 55 ordered edits permit exactly 50 Undo operations down to edit
  5, then exactly 50 Redo operations back to edit 55.

### [x] SEQ-C05 — Base Dirty on a saved checkpoint

- **Priority:** P1
- **Problem:** Export does not clear Dirty reliably, and manual Boolean updates
  can disagree with the actual document.
- **Depends on:** SEQ-C01, SEQ-D04.
- **Acceptance:** successful Load/Import/Export establishes a saved checkpoint;
  Dirty reflects document equality with that checkpoint. Undoing back to it
  clears Dirty, and redoing away sets it again.
- **Validation:** save/edit/undo/redo/export matrix.
- **Implemented:** the editor owns one saved `SequenceSnapshot`; `Dirty` has no
  public/manual setter and is recomputed exclusively by structural equality.
  Successful Export, Import and Local Library Load establish the checkpoint.
  Edits, cancellation, Undo and Redo compare their resulting document to it
  without clearing history.
- **Evidence:** the save/edit/Undo/Redo/re-export matrix proves equality in both
  directions across two checkpoints. Load and Import establish clean baselines;
  cancellation restores equality without retaining the former Dirty Boolean in
  transaction history.

### [x] SEQ-C06 — Refresh duration and extent after property changes

- **Priority:** P1
- **Problem:** collection changes rebuild ruler extent, but nudging `StartMs` or
  replacing `AnimId` can alter the total without notifying timeline width.
- **Depends on:** SEQ-C01, SEQ-F01.
- **Acceptance:** all changes affecting clip end or sequence total refresh cached
  duration, ruler, timecode, and scroll extent exactly once at commit.
- **Validation:** move the last clip, select longer/shorter gestures, replace
  audio, and Undo/Redo each operation.
- **Implemented:** each committed document transaction resolves gesture tails,
  updates one cached total, then refreshes tracks/ruler/timecode/scroll extent
  once. Metadata/config changes use the same path; playhead ticks read the cache.
  The edit matrix covers gesture animation/start/end, audio replacement and
  Undo/Redo with exactly one derived-width notification per commit.

### [x] SEQ-C07 — Handle lost mouse capture and cancellation

- **Priority:** P2
- **Problem:** losing capture/window focus can leave drag flags, offsets, or the
  library ghost active because completion relies on MouseUp.
- **Depends on:** SEQ-C01, SEQ-C02.
- **Acceptance:** lost capture, Escape, view unload, and window deactivation
  either cancel and restore the original edit or commit by a documented rule;
  all transient visual state is cleared.
- **Validation:** manual and UI-level capture-loss scenarios for gesture, audio,
  ruler scrub, and library-chip drag.
- **Implemented:** Escape, lost mouse capture, view unload and host-window
  deactivation route through one cancellation path. Active gesture/audio/lane
  edits restore their pre-edit document and prior Dirty state without touching
  history; all drag offsets/flags and the library ghost are cleared. Cancelled
  ruler scrubbing restores its starting playhead. Normal MouseUp/Enter commit.
- **Evidence:** cancellation restoration/idempotence tests cover clean and
  already-dirty documents; compiled WPF routed-event wiring covers gesture,
  audio, ruler and library-chip capture paths. Full suite passes 82/82.

### [x] SEQ-C08 — Separate document state from transient UI state

- **Priority:** P2
- **Problem:** the large ViewModel mixes persistence, editor state, playback,
  geometry, dialogs, and scheduling, making omissions likely.
- **Depends on:** SEQ-C01, SEQ-E01.
- **Acceptance:** establish explicit document/editor/playback responsibilities
  without a risky all-at-once rewrite. Snapshots serialize document state only.
- **Validation:** architecture tests or type-level boundaries plus unchanged UI
  behavior.
- **Implemented:** `SequenceSnapshot` is now the explicit persistent-document
  boundary and owns structural comparison of only name/Loop, gesture DTOs and
  audio lane/clip DTOs. `SequencerEditHistory` independently owns begin/commit/
  cancel plus bounded Undo/Redo, while immutable `SequencerPlaybackPlan` remains
  the runtime boundary. The ViewModel coordinates those three responsibilities
  and continues to own transient selection, viewport, drag and telemetry state.
- **Evidence:** six type/architecture tests lock the snapshot surface, every
  persistent comparison field, transaction/cancellation semantics, both bounded
  history directions and read-only playback-plan state. The pre-existing editor
  and transport integration suite remains unchanged; full WPF suite passes
  88/88.

### [D] SEQ-C09 — Add recoverable draft autosave

- **Priority:** P3
- **Problem:** unexported edits are lost on crash or restart; the last exported
  file is reloaded instead.
- **Depends on:** SEQ-C05, SEQ-D04.
- **Acceptance:** a separate atomic draft does not overwrite the user's export;
  startup offers recovery only when the draft is newer and Dirty.
- **Validation:** crash/restart and stale-draft tests.

## EPIC D — Scene import, external copies and local library

### [x] SEQ-D01 — Parse and validate import before mutating the editor

- **Priority:** P1
- **Problem:** `ImportFrom` changes Name, Loop, tracks, audio, and steps
  progressively; a later exception can leave a partially imported document.
- **Depends on:** SEQ-H01.
- **Acceptance:** parse into a temporary DTO, validate fully, then apply once.
  Failure leaves the current document, history, selection, and saved checkpoint
  untouched and reports an actionable error.
- **Validation:** malformed input at every major section produces no partial
  mutation.
- **Implemented:** file reading now produces a temporary
  `ImportedSequenceDocument` through a side-effect-free parser. The ViewModel
  applies that complete document only after parsing, migration and validation
  all succeed. Failures include the JSON field path and leave document content,
  file roster, history, Dirty, selection, armed track and playhead untouched.
- **Evidence:** malformed metadata, tracks, audio and steps integration fixtures
  all fail before replacement while preserving persistent and transient editor
  fingerprints, Undo/Redo availability and object selection.

### [x] SEQ-D02 — Validate schema identity, version and numeric bounds

- **Priority:** P1
- **Problem:** imports do not enforce `type`, supported version, gesture range,
  nonnegative times/durations, safe integer arithmetic, or reasonable counts.
- **Depends on:** SEQ-D01.
- **Acceptance:** validate type/version; AnimId 0..17; legal target IDs;
  nonnegative bounded start/duration; nonempty bounded lane labels; maximum
  tracks/steps/lanes/clips; and checked end-time calculations. Errors identify
  the offending field.
- **Validation:** boundary, overflow, negative, wrong-type, huge-count, and
  unknown-version fixtures.
- **Implemented:** strict field readers enforce `b1-sequence` versions 1–4,
  gesture IDs 0–17, nonzero physical/broadcast target rules, 24-hour checked
  timing, nonempty bounded lane labels and bounded names/paths. Documents are
  limited to 256 tracks, 10,000 steps, 64 lanes and 10,000 total audio clips;
  audio end arithmetic is checked before conversion to editor objects.
- **Evidence:** sixteen invalid-schema scenarios cover type/version, numeric
  range, reserved IDs, negative values, end overflow, duplicates, missing fields
  and every count limit. A separate exact-boundary fixture passes.

### [x] SEQ-D03 — Implement explicit schema migrations

- **Priority:** P1
- **Problem:** legacy `delayMs` is relative but is currently treated as an
  absolute start, changing choreography.
- **Depends on:** SEQ-D01, SEQ-D02.
- **Acceptance:** each supported version has a named migration. Relative delays
  are cumulatively converted according to the historical format; unsupported
  ambiguous files fail clearly instead of silently changing timing.
- **Validation:** golden fixtures for every supported schema version.
- **Implemented:** named readers cover v1 relative gesture delays, v2 absolute
  gesture timing, v3 console audio lanes and v4 offline droid rosters. V1 starts
  are reconstructed as zero followed by cumulative prior delays. Retired
  DFPlayer `audioTrack` metadata is validated but discarded because no reliable
  mapping to a PC audio path exists. Timing fields that contradict their stated
  schema fail as ambiguous rather than silently changing choreography.
- **Evidence:** four copied golden JSON fixtures verify versions 1–4, including
  v1 starts `0/100/350`; dedicated ambiguity and cumulative-overflow checks pass.

### [x] SEQ-D04 — Make export atomic and establish the saved checkpoint

- **Priority:** P1
- **Problem:** direct `File.WriteAllText` can leave a truncated file, exceptions
  are not surfaced consistently, and export does not mark the current snapshot
  as saved.
- **Depends on:** SEQ-C05, SEQ-D02.
- **Acceptance:** write a temporary file in the destination directory, flush and
  atomically replace/move it; report failure without changing save state; on
  success update last path, document name policy, checkpoint, and Dirty.
- **Validation:** successful export, denied path, interrupted/failed replacement,
  and re-import round-trip tests.
- **Implemented:** Export captures one document snapshot, serializes schema v4,
  writes and flushes a uniquely named sibling temporary file, then renames it
  over the destination. The old file, last path and checkpoint remain untouched
  on any write/replace failure; temporary files are cleaned best-effort. Success
  updates the last path and checkpoint while preserving Undo history. Filename
  choice never silently changes the explicit sequence Name.
- **Evidence:** real create/replace and second-ViewModel round trips pass. Injected
  access denial and replacement interruption preserve the prior file/checkpoint,
  clean the temp and surface an actionable UI error.

### [x] SEQ-D05 — Protect unsaved work before replacement

- **Priority:** P1
- **Problem:** Import and library Load replace the current sequence without the
  Clear command's unsaved-work confirmation.
- **Depends on:** SEQ-C05, SEQ-D01.
- **Acceptance:** replacement prompts when Dirty, supports Cancel, and follows
  the playback policy from SEQ-A03. Startup recovery remains silent/safe.
- **Validation:** clean/dirty plus stopped/playing/paused matrix.
- **Implemented:** interactive Import and Local Library Load share an injected
  replacement confirmation. Clean documents proceed directly; Dirty documents
  identify the selected file/item and cancel without changing content, history
  or last path. Both remain inert during Play/Pause under the existing edit lock.
  Startup last-file restore bypasses UI and remains silent.
- **Evidence:** clean/dirty/confirm/cancel matrices cover Import and Load; four
  Play/Pause cases cover clean and Dirty documents. Invalid confirmed Import
  preserves unsaved work and reports the parse error; startup recovery invokes
  no dialog.

### [x] SEQ-D06 — Resolve the local-library dead end

- **Priority:** P2
- **Problem:** the UI can Load/Delete historical library entries but cannot call
  the existing Save method; Import's tooltip incorrectly says it adds an item.
- **Depends on:** SEQ-D01, SEQ-D04.
- **Decision:** retained. The current Sequencer document is a Scene; the Local
  Library is its normal working store. Save updates the selected scene and Save
  As creates a new stable scene identity. A future Show combines scene
  instances without replacing the scene editor.
- **Acceptance:** implement Library Save/Save As with explicit overwrite/name
  conflict rules, stable IDs, versioned/atomic storage and legacy migration.
  Import never silently adds a scene. Export remains an explicit external copy
  for transfer, support, version control or backup—not the everyday Save path.
  README, Help and tooltips match.
- **Validation:** end-to-end save/load/delete or migration/removal test.
- **Implemented:** the Scene name is editable; Save updates the active GUID and
  Save As creates another. Case-insensitive conflicts are refused without
  overwriting. `b1-scene-library-item` v1 envelopes contain the strictly
  validated sequence document and are written atomically. Flat legacy JSON is
  assigned a deterministic GUID, migrated, and retained in the trash folder.
  Invalid entries remain untouched and visible through a detailed issue status.
- **Validated:** atomic round-trip/replacement failure, legacy migration,
  invalid-entry isolation, stable identity, Save/Save As, conflicts, startup
  restore and library-backed Export semantics are automated.

### [x] SEQ-D07 — Confirm destructive library deletion

- **Priority:** P2
- **Problem:** deleting a library entry is immediate and unrecoverable from the
  UI.
- **Depends on:** SEQ-D06 if the library is retained.
- **Acceptance:** identify the exact item, request confirmation, report file
  errors, and refresh only after success. Prefer a recoverable move if practical.
- **Validation:** confirm, cancel, missing file, and access-error cases.
- **Implemented:** Trash names the exact Scene, asks first, moves the versioned
  file to `library\trash` with a UTC suffix, refreshes only after success and
  reports failures. Trashing the open Scene preserves its editor content as a
  modified new document that can be saved again.
- **Validated:** confirm/cancel, recoverable file movement, missing entry,
  simulated access failure and current-Scene behavior are automated.

### [x] SEQ-D08 — Align naming, badges, tooltips and Help with reality

- **Priority:** P2
- **Problem:** new sequences cannot be named naturally; the badge ignores Dirty;
  Export says portable despite absolute audio paths; Import claims a library
  action it does not perform.
- **Depends on:** SEQ-C05, SEQ-D04, SEQ-D06.
- **Acceptance:** define editable/filename-derived naming; display clean/dirty
  state; accurately describe linked audio and startup restore; remove every
  contradictory tooltip/help passage.
- **Validation:** content review plus UI smoke test for new/imported/exported and
  edited sequences.
- **Implemented:** the header reports Scene name, `NEW`/`LOCAL LIBRARY`/
  `IMPORTED / EXTERNAL FILE` origin and `CLEAN`/`SAVED`/`MODIFIED` state. Save,
  Save As, Import, Export, Load and Trash tooltips now describe their actual
  boundaries; in-app Help and storage documentation use the same Scene-first
  workflow and linked-audio warning.
- **Validated:** badge/origin transitions and Play/Pause command locking are
  automated; compiled XAML plus the Help/content review cover the visible text.

### [D] SEQ-D09 — Export a portable show package

- **Priority:** P3
- **Problem:** `.b1seq.json` stores absolute paths and does not carry audio.
- **Depends on:** SEQ-D04, SEQ-F05.
- **Acceptance:** optional package export copies audio into a stable folder,
  writes relative paths and hashes, handles filename collisions, and reopens on
  another PC without manual repair.
- **Validation:** round trip in a different base directory and missing/tampered
  asset tests.

## EPIC E — Deterministic scheduler and performance

### [x] SEQ-E01 — Introduce testable clock, scheduler, protocol and audio seams

- **Priority:** P1
- **Problem:** concrete timers, `DateTime`, `ProtocolClient`, static dialogs, and
  concrete audio service make temporal behavior difficult to test.
- **Depends on:** SEQ-H01 may be developed in the same batch.
- **Acceptance:** narrow interfaces permit fake monotonic time, captured protocol
  sends, captured audio actions, and deterministic scheduler advancement without
  launching WPF or touching hardware.
- **Validation:** a headless test constructs and runs a simple playback plan.

### [x] SEQ-E02 — Replace one timer per event with one scheduler

- **Priority:** P1
- **Problem:** independent thread-pool timers scale poorly and create callback
  races and nondeterministic ordering.
- **Depends on:** SEQ-A01, SEQ-A02, SEQ-E01.
- **Acceptance:** one scheduler owns a priority queue of the immutable pass and
  dispatches due events from monotonic elapsed time. Stop/cancel releases it
  completely; event count does not equal OS timer count.
- **Validation:** large synthetic timeline, cancellation, drift, and resource
  count tests.
- **Implemented:** one rearmable wake timer owns the immutable pass batches and
  a monotonic forward-only cursor. Each wake drains every batch now due, catches
  up late host scheduling without accumulating drift, then rearms the same OS
  timer for the next batch or end boundary. Pause, Stop, restart, Loop and stale
  generations dispose or replace the complete scheduler session.
- **Validated:** a 10,000-event plan creates one active wake timer; late-wake
  catch-up, next-deadline compensation, Stop disposal and all existing
  Pause/Resume/restart/Loop cancellation cases are automated.

### [x] SEQ-E03 — Define stable same-time ordering and batching

- **Priority:** P1
- **Problem:** broadcast and targeted gestures at the same timestamp, or multiple
  gestures for one target, currently race through independent timers.
- **Depends on:** SEQ-E02, SEQ-G03.
- **Acceptance:** define an explicit stable order and conflict policy. All events
  due in one scheduler tick are collected and dispatched predictably; warnings
  cover combinations whose hardware outcome remains ambiguous.
- **Validation:** repeated runs produce identical send order.
- **Decision:** same-time events keep immutable source order: gesture clips in
  editor order, then audio clips in lane/clip order. Multiple gestures for one
  target are sent in that order and the last command received wins. Broadcast
  plus targeted gestures are sent in source order but remain physically
  ambiguous because mesh arrival order is not guaranteed.
- **Implemented:** explicit timestamp batches are drained atomically by one
  scheduler wake. Same-target and broadcast/target overlaps produce timestamped
  plan warnings exposed by the hoverable `SCHEDULE` badge.
- **Validated:** batch shape/order, conflict classification, atomic gesture plus
  audio dispatch and 20 repeated same-time passes are automated.

### [x] SEQ-E04 — Make Pause/Resume boundary behavior exact

- **Priority:** P1
- **Problem:** integer truncation and `StartMs >= fromMs` can replay an event at
  the pause boundary or skip one depending on timing.
- **Depends on:** SEQ-E02, SEQ-A06.
- **Acceptance:** each event has a dispatched/not-dispatched identity independent
  of rounded elapsed milliseconds. Resume never duplicates a fired event and
  never loses an unfired one.
- **Validation:** pause immediately before, exactly at, and immediately after
  several simultaneous events.
- **Implemented:** immutable event `SourceOrder` is retained as the per-pass
  dispatch identity. Consumed identities survive Pause/Resume, reset on fresh
  Play/Stop/Loop, and overdue-but-unfired events resume immediately.

### [ ] SEQ-E05 — Make sequence end and whole-pass Loop explicit

- **Priority:** P1
- **Problem:** the end is inferred from natural clip tails; a looping audio clip
  alone ends after one natural duration, and infinite gestures use fake tails.
- **Depends on:** SEQ-B04, SEQ-F01, SEQ-F08.
- **Acceptance:** define an explicit calculated/user end marker and loop region
  semantics. At a loop boundary, players and hardware state transition once,
  without stacking or gaps caused by stale callbacks.
- **Validation:** empty, audio-only loop, infinite-gesture, mixed, zero-duration,
  and whole-pass Loop cases.

### [ ] SEQ-E06 — Cache derived duration and control UI update cost

- **Priority:** P2
- **Problem:** timecode can rescan every clip on each 30 ms playhead tick; ruler
  collections are wholesale rebuilt during zoom/layout changes.
- **Depends on:** SEQ-C06, SEQ-F01.
- **Acceptance:** document mutations update cached total once; playhead ticks do
  not recompute it. Ruler generation is bounded/debounced or virtualized enough
  for the documented maximum sequence size.
- **Validation:** performance test with the supported maximum event count and
  duration; no perceptible UI stall while playing or zooming.
- **Implemented so far:** cached total duration no longer rescans clips on
  playhead ticks. Unchanged 1.5 s droid telemetry is now projected into separate
  roster and online-target signatures, so it does not rebuild timeline tracks,
  duration metadata or ruler bindings. A regression test proves repeated
  identical heartbeats emit no extent/track refresh; real name/role and online
  membership changes still update only their affected projection. The operator
  confirmed that radar and scene-playhead animation are fluid again in build
  347. Maximum-duration ruler generation remains before this item can close.

### [D] SEQ-E07 — Add master-side scheduled execution

- **Priority:** P3
- **Problem:** PC timers, serial writes and mesh relays cannot provide a shared
  hardware clock edge for simultaneous choreography.
- **Depends on:** M1–M4, firmware protocol design.
- **Acceptance:** optional future protocol sends look-ahead events with execution
  timestamps, clock synchronization and acknowledgements while preserving a
  safe console fallback.
- **Validation:** measured multi-droid skew and packet-loss recovery tests.

## EPIC F — Duration and audio robustness

### [H] SEQ-F01 — Model finite, immediate and infinite gesture duration correctly

- **Priority:** P1
- **Problem:** firmware reports a nominal static sum; target speed scaling and
  jitter alter reality, while IDLE/TALK/POWER_DOWN receive an arbitrary 2000 ms.
- **Depends on:** SEQ-D02 for persisted model changes.
- **Acceptance:** duration metadata distinguishes immediate/finite/infinite,
  nominal duration, and estimated target-specific range. Broadcast behavior with
  mixed speed settings is explicitly represented or conservatively warned.
- **Validation:** compare calculated estimates with firmware rules and measured
  samples at representative speed settings.
- **Implemented:** additive firmware metadata reports `immediate`/`finite`/
  `infinite`, nominal milliseconds, frame count and IDLE settle time. Finite
  estimates reproduce the firmware's 10–100 % speed clamp and ±60 ms movement
  jitter per keyframe. Targeted clips use that droid's config; broadcast clips
  aggregate every online target and visibly call out mixed speeds while using
  the conservative upper bound.
- **Hardware remaining:** capture actual completion times at representative
  Speed values on the bench and confirm they remain inside the calculated
  target-specific ranges. The implementation and formula-level tests are
  complete; this measurement is the only open acceptance check.

### [x] SEQ-F02 — Use one coherent fallback duration policy

- **Priority:** P2
- **Problem:** gesture geometry/active highlighting default to 800 ms while total
  duration defaults to 1500 ms before firmware metadata arrives.
- **Depends on:** SEQ-F01.
- **Acceptance:** one shared metadata provider supplies geometry, active state,
  total duration and inspector text; the pre-handshake fallback is consistent and
  visibly provisional.
- **Validation:** disconnected, handshaking, metadata-received, and invalid-ID
  snapshots agree.
- **Implemented:** `AnimationDurationProvider` exclusively supplies each
  gesture's kind, effective tail, range, provisional state and inspector text.
  Geometry, active highlighting, cached total and playback plan consume the
  resulting per-step projection. The disconnected fallback is consistently
  1500 ms and explicitly labeled provisional; the former independent 800 ms
  converter fallbacks were removed.

### [ ] SEQ-F03 — Notify the UI when an audio filename changes

- **Priority:** P1
- **Problem:** `FileName` is derived from `FilePath` but receives no property
  notification after Replace file.
- **Depends on:** none.
- **Acceptance:** replacing a path immediately updates the displayed basename and
  related missing/error state.
- **Validation:** model binding test plus manual replace check.

### [ ] SEQ-F04 — Keep zero/unknown-duration audio clips visible and editable

- **Priority:** P1
- **Problem:** probe failure returns duration 0, producing a zero-width clip with
  no useful error affordance.
- **Depends on:** SEQ-F05.
- **Acceptance:** unknown-duration clips have a minimum selectable width, a clear
  warning badge/tooltip, and do not silently define an invalid sequence end.
- **Validation:** missing, corrupt, unsupported, and valid zero-length files.

### [ ] SEQ-F05 — Add bounded audio probing and actionable errors

- **Priority:** P1
- **Problem:** duration probing has no timeout/cancellation and collapses every
  failure to 0; media decoding depends on installed Windows codecs.
- **Depends on:** SEQ-E01 for a testable service boundary.
- **Acceptance:** probe returns a typed success/failure result, closes resources
  on every path, supports timeout/cancellation, and surfaces codec/file errors
  without blocking the UI.
- **Validation:** success, failure event, thrown URI/open error, timeout, cancel,
  and file removed during probe.

### [ ] SEQ-F06 — Prevent stale waveform assignment and cache invalidation bugs

- **Priority:** P1
- **Problem:** an old decode can finish after Replace and overwrite the new
  waveform; the permanent path-only cache survives file replacement and caches
  failures indefinitely.
- **Depends on:** SEQ-E01.
- **Acceptance:** assignments verify clip generation/path; cache keys include
  stable file metadata or support invalidation; failed/cancelled tasks can retry;
  cache growth is bounded.
- **Validation:** rapid replace, same-path content change, missing-then-created
  file, cancellation, and repeated-file cases.

### [ ] SEQ-F07 — Manage MediaPlayer lifecycle and playback failures

- **Priority:** P1
- **Problem:** completed non-looping players remain open until global Stop and
  playback failures are silent.
- **Depends on:** SEQ-E01.
- **Acceptance:** ended/failed players detach handlers, close, and leave the
  active set; failures identify the clip; Pause/Resume touches only genuinely
  active players; StopAll remains idempotent.
- **Validation:** concurrent clips, natural end, loop, failure, pause after one
  clip ended, and repeated Stop tests.

### [ ] SEQ-F08 — Define audio-loop duration against the sequence endpoint

- **Priority:** P1
- **Problem:** a looping clip does not extend the pass beyond its natural end, so
  it needs unrelated later content to repeat.
- **Depends on:** SEQ-E05.
- **Acceptance:** looping audio runs until an explicit sequence/clip endpoint;
  timeline representation and export schema make that endpoint clear.
- **Validation:** loop-only, loop-with-end-marker, whole-pass Loop, Pause/Resume,
  and Stop cases.

## EPIC G — Preflight and ergonomics

### [ ] SEQ-G01 — Add a preflight result model and Play gate

- **Priority:** P1
- **Problem:** Play silently starts audio even if no ready master can receive
  gestures, and there is no consolidated readiness check.
- **Depends on:** SEQ-A06, SEQ-D02, SEQ-F05.
- **Acceptance:** preflight produces errors, warnings and information; blocking
  errors disable or intercept Play; the user can inspect exact affected clips,
  tracks and files.
- **Validation:** headless rule tests and manual UI presentation.

### [ ] SEQ-G02 — Detect connection, offline-target and missing-audio issues

- **Priority:** P1
- **Problem:** offline rows are preserved correctly but missed commands are not
  queued, while missing audio is silently skipped.
- **Depends on:** SEQ-G01.
- **Acceptance:** preflight reports no port/session, no master, offline targeted
  droids, broadcast with no recipients, missing/unreadable audio, and unknown
  duration. The user sees which findings block Play.
- **Validation:** rule matrix with mixed live/offline roster and file states.

### [ ] SEQ-G03 — Detect overlapping and ambiguous gesture commands

- **Priority:** P1
- **Problem:** a later gesture interrupts the previous one, and simultaneous
  broadcast plus per-target events have order-dependent outcomes.
- **Depends on:** SEQ-F01, SEQ-G01.
- **Acceptance:** analyze effective target intersections and duration spans;
  flag same-target overlaps, same-time duplicates, and broadcast/target conflicts;
  link warnings to clips.
- **Validation:** finite, infinite, broadcast, muted, and offline combinations.

### [ ] SEQ-G04 — Detect unterminated infinite gestures

- **Priority:** P0
- **Problem:** TALK/POWER_DOWN without a represented terminator can outlive the
  intended scene.
- **Depends on:** SEQ-B04, SEQ-G01.
- **Acceptance:** Play is blocked or requires an explicit safety confirmation
  until every effective infinite gesture has a defined end/cleanup path.
- **Validation:** per-target and broadcast termination graph tests.

### [ ] SEQ-G05 — Remove implicit broadcast insertion risk

- **Priority:** P1
- **Problem:** clicking a gesture with no armed track falls back to the first
  `All droids` row.
- **Depends on:** none.
- **Acceptance:** insertion requires an explicitly armed track, or presents an
  unmistakable confirmation for broadcast. The armed target is always prominent
  and keyboard insertion follows the same rule.
- **Validation:** fresh startup, lost/rebuilt armed track, offline track, and
  explicit broadcast cases.

### [ ] SEQ-G06 — Keep UI text and control availability synchronized

- **Priority:** P2
- **Problem:** current tooltips and Help contain known caveats that can drift from
  the fixes, and some controls remain available when their result is unsafe or a
  no-op.
- **Depends on:** all behavior-changing P0/P1 items.
- **Acceptance:** final content audit covers transport, mute, Pause, Stop, loops,
  Import/Export, library, audio portability, preflight, and offline behavior.
- **Validation:** checklist review of XAML tooltips and all Sequencer Help pages.

### [D] SEQ-G07 — Add markers, explicit end marker and loop region editing

- **Priority:** P3
- **Depends on:** SEQ-E05.
- **Acceptance:** named markers and loop/end handles are editable, snap-aware,
  serialized, and keyboard accessible.
- **Validation:** editor and persistence round trip.

### [D] SEQ-G08 — Add deterministic per-clip animation seeds

- **Priority:** P3
- **Problem:** `Random.Shared` makes each performance render differently.
- **Depends on:** SEQ-A01, SEQ-D03.
- **Acceptance:** choose random-each-pass or stored deterministic seed per clip;
  exports preserve deterministic mode and broadcast recipients share intended
  variation.
- **Validation:** repeated-pass protocol captures.

### [D] SEQ-G09 — Add multi-select, copy/paste and grouped movement

- **Priority:** P3
- **Depends on:** SEQ-C01.
- **Acceptance:** selections and clipboard operations create one coherent Undo,
  preserve relative timing/targets, and remain safe around broadcast tracks.
- **Validation:** interaction and history tests.

### [D] SEQ-G10 — Add per-track latency compensation

- **Priority:** P3
- **Depends on:** SEQ-E02; measured hardware need.
- **Acceptance:** optional offsets affect dispatch but not destructively rewrite
  authored times; UI shows compensated timing and export preserves settings.
- **Validation:** measured fleet synchronization comparison.

### [~] SEQ-G11 — Monitor command delivery and runtime execution health

- **Priority:** P1
- **Problem:** writing an `anim` command to serial is not proof that the target
  received or started it; current UI feedback can imply success even when the
  link rejected or lost the command.
- **Depends on:** SEQ-A01, SEQ-E03, firmware acknowledgement/telemetry design.
- **Acceptance:** distinguish queued, written to master, acknowledged/relayed,
  confirmed by target, failed and timed-out states to the degree supported by the
  selected protocol. Runtime health identifies target/event correlation and
  never fabricates successful delivery when disconnected. Define whether late or
  missing acknowledgements warn, pause or stop a performance.
- **Validation:** protocol fake plus hardware tests for success, target offline,
  mesh loss, duplicate/delayed acknowledgement, disconnect and timeout.
- **Implemented:** target-execution correlation now reports per-droid
  started/completed/interrupted/rejected states. Missing starts expire after
  1.5 s; missing finite-gesture completion expires after the reported duration
  plus 1.5 s. Late reports recover warning states and delayed START duplicates
  cannot regress a terminal report. Warnings remain observational and never
  pause/stop playback. Local serial refusal/write and correlated master
  acceptance are now separate stages (`WRITE` then `MASTER`); disconnected,
  pre-handshake and failed writes produce an immediate local failure without
  arming timeouts. The master also exposes mesh-queue and local-target facts,
  explicitly without calling the former a slave receipt. Remaining work:
  exercise offline/weak-link/disconnect timeouts on hardware and decide whether
  a true per-hop relay acknowledgement is worth the added mesh traffic.

### [ ] SEQ-G12 — Analyze mechanical workload before playback

- **Priority:** P1
- **Problem:** a syntactically valid timeline can still demand rapid reversals,
  continuous high-amplitude motion, excessive command density or too little rest
  for the servos and mechanism.
- **Depends on:** SEQ-F01, SEQ-G01, calibration/servo safety limits.
- **Acceptance:** Preflight estimates per-target duty, reversal density, command
  interruption rate, time near configured limits and infinite-motion exposure.
  Thresholds are conservative, documented and configurable only within safe
  bounds; warnings never claim to replace physical validation.
- **Validation:** benign and intentionally aggressive synthetic scenes plus
  measured hardware review at representative calibration/amplitude/speed values.

### [ ] SEQ-G13 — Preflight the host PC and audio environment

- **Priority:** P1
- **Problem:** a valid Show can fail because Windows sleeps, the audio device or
  codec is unavailable, volume is muted, assets changed, power is low or logging
  storage is exhausted.
- **Depends on:** SEQ-F05, SEQ-G01, SEQ-G02.
- **Acceptance:** Preflight checks the selected/default audio output, test tone,
  mute/usable volume where observable, required codecs/files, power/sleep policy,
  available log/package storage and relevant environment changes since arming.
  Checks clearly separate detectable facts from operator confirmations and avoid
  silently changing system-wide settings.
- **Validation:** device removal/change, mute, unsupported media, battery/power,
  sleep-policy warning, low-space and successful show-PC checklist.

### [ ] SEQ-G14 — Redesign Play as an unambiguous Play/Pause control

- **Priority:** P1
- **Problem:** pressing Play while already playing currently restarts the Scene
  from zero and resends its first commands. This is surprising for a conventional
  transport and can cause an accidental motion restart.
- **Depends on:** SEQ-A06, SEQ-E04.
- **Acceptance:** the primary control always exposes the action that will happen
  next. Decide whether it is a two-state Play/Pause button or two adjacent
  controls, but a second ordinary Play press must not silently restart. Restart
  from zero remains available through a separate, unmistakable action. Tooltips,
  keyboard shortcuts, disabled states and Help all match Stopped/Playing/Paused.
- **Recommendation:** use one large Play/Pause toggle: Play becomes Pause while
  running and becomes Resume while paused. Add a separate `Restart`/`From start`
  control rather than hiding restart behind a second Play press.
- **Validation:** state/command/UI tests cover stopped Play, playing Pause,
  paused Resume, explicit Restart, rapid double-click, failure to start and Loop.

### [ ] SEQ-G15 — Separate Stop from playhead navigation and rewind

- **Priority:** P2
- **Problem:** normal Stop currently ends playback and always returns the
  playhead to zero. Transport safety cleanup and timeline navigation are two
  different intentions, and forcing both makes inspection/rehearsal awkward.
- **Depends on:** SEQ-A06, SEQ-B07, SEQ-G14.
- **Acceptance:** define and expose the post-Stop playhead policy separately from
  hardware/audio cleanup. Specify normal Stop, Safe Stop, Emergency Stop,
  natural end and Loop boundaries, plus whether Play while stopped begins at
  zero or at the retained cursor. A dedicated return-to-start action is always
  available and cannot be confused with a safety stop.
- **Recommendation:** normal Stop should cancel/clean up immediately but retain
  the current playhead for inspection. Add a distinct `Return to start` button.
  Keep performance-mode GO-from-zero and rehearsal Play-from-cursor as explicit
  choices instead of inferring them from the cursor position. Safe/Emergency
  Stop should prioritize safety and may retain the last position diagnostically.
- **Validation:** tests cover Stop from Play/Pause, repeated Stop, return to
  start, Play after retained Stop, natural end, Safe/Emergency Stop and Loop.

### [ ] SEQ-G16 — Add an operator-controlled Follow Playhead mode

- **Priority:** P2
- **Problem:** on a timeline longer than the viewport, the playhead leaves the
  visible window while playback continues. A forced center-on-every-tick design,
  however, would fight manual inspection and can make the whole timeline feel
  constantly in motion.
- **Depends on:** SEQ-E06, SEQ-G14.
- **Acceptance:** a visible `Follow` mode keeps the active playhead in view
  without changing vertical scroll or stealing manual control. Behavior is
  defined for Play/Pause/Resume, zoom/Fit, manual horizontal scrolling, Loop,
  Restart and natural end. Scrolling is smooth and bounded; it does not rebuild
  timeline content or create a per-tick layout stall.
- **Recommendation:** default Follow on when playback begins, but use a comfort
  corridor rather than permanent centering: let the playhead move across roughly
  the first 65–75% of the viewport, then smoothly advance the window. Manual
  horizontal scroll disables/suspends Follow; a visible button re-enables it and
  catches up. Pause freezes auto-scroll while leaving inspection free.
- **Validation:** long-timeline UI tests and manual checks cover every zoom,
  manual-scroll, Pause/Resume, Loop/restart and end-boundary combination.

### [ ] SEQ-G17 — Add pointer-centered timeline wheel zoom

- **Priority:** P2
- **Problem:** changing the zoom currently requires reaching for the toolbar
  control. Repeated zoom-and-pan work is slow when editing precise choreography,
  and a naïve wheel zoom can make the point of interest jump out of view.
- **Depends on:** SEQ-E06, SEQ-G16.
- **Acceptance:** `Ctrl + mouse wheel` zooms in/out within the existing supported
  limits while preserving the timeline time beneath the pointer at the same
  viewport position. Plain wheel retains normal scrolling behavior;
  `Shift + wheel` provides horizontal navigation where WPF/trackpad behavior is
  otherwise ambiguous. Zoom works while stopped, playing or paused without
  modifying the Scene, playhead or selection. Trackpad/high-resolution wheel
  deltas are accumulated smoothly, and Fit remains an explicit deterministic
  action.
- **Recommendation:** use multiplicative zoom steps rather than fixed pixel
  increments, anchor the scroll offset around the pointer's time coordinate,
  and temporarily suspend Follow on deliberate manual zoom/pan. A visible
  Follow action can then catch up without fighting the editor.
- **Validation:** tests cover pointer anchoring near the start/middle/end,
  min/max clamping, rapid/high-resolution wheel input, horizontal scroll,
  Play/Pause, Follow suspension/re-enable, Fit and timelines shorter than the
  viewport.

## EPIC H — Automated and hardware validation

### [x] SEQ-H01 — Create a Sequencer-focused test project and fixtures

- **Priority:** P1
- **Problem:** no automated Sequencer coverage was found, and concrete WPF/service
  dependencies impede headless tests.
- **Depends on:** none.
- **Acceptance:** a test project runs from the command line without hardware;
  includes document builders, fake protocol/audio/clock, JSON fixture helpers,
  and does not increment the release build number unexpectedly.
- **Validation:** clean test run from a fresh build environment.

### [ ] SEQ-H02 — Cover edit transactions, Dirty and history

- **Priority:** P1
- **Depends on:** SEQ-C01 through SEQ-C06, SEQ-H01.
- **Acceptance:** automated matrix covers every persistent/transient property,
  no-op edits, capacity, saved checkpoints, Undo/Redo, and derived extent.
- **Validation:** all matrix rows pass and fail if their corresponding behavior is
  intentionally broken.

### [~] SEQ-H03 — Cover scheduler, transport and safety timing

- **Priority:** P0
- **Depends on:** SEQ-A01 through SEQ-A08, SEQ-B01 through SEQ-B04, SEQ-B06,
  SEQ-B07, SEQ-E01 through SEQ-E05, SEQ-H01.
- **Acceptance:** deterministic tests cover cancellation generation, rapid
  restart, simultaneous events, dynamic mute, Pause boundaries, natural end,
  whole-pass Loop, disconnect, infinite-gesture lease renewal/expiry, Stop level
  transitions, arming invalidation, and cleanup.
- **Validation:** repeated stress run produces no intermittent result.
- **Current coverage:** immutable empty/audio/gesture/mixed plans, same-time
  source order, duration fallback/zero/clamping/overflow, TALK/POWER_DOWN tails,
  generation cancellation, rapid restart, stale Loop end, dynamic broadcast and
  per-droid mute, edit-lock release, Pause boundaries and repeated Resume,
  natural end, repeated Stop, disconnect in Play/Pause, disposal and cleanup;
  one-timer 10,000-event resource bounds, monotonic late-wake catch-up,
  timestamp batching, repeated stable ordering, infinite leases, three Stop
  levels and arming invalidation are also covered. Exact lifecycle/duration end
  semantics remain open with SEQ-A08, SEQ-B04 and SEQ-E05.

### [ ] SEQ-H04 — Cover persistence validation and migrations

- **Priority:** P1
- **Depends on:** SEQ-D01 through SEQ-D05, SEQ-H01.
- **Acceptance:** golden round trips for every schema plus malformed, partial,
  overflow, unsupported and failed-write cases; current document is preserved on
  every failure.
- **Validation:** fixture suite passes on a clean machine path.

### [ ] SEQ-H05 — Cover audio and waveform services

- **Priority:** P1
- **Depends on:** SEQ-F03 through SEQ-F08, SEQ-H01.
- **Acceptance:** tests cover probe result types, timeout, lifecycle, concurrent
  players, stale waveform prevention, cache invalidation, missing files, and
  audio loop endpoint behavior.
- **Validation:** service suite plus a Windows Media Foundation smoke test.

### [ ] SEQ-H06 — Add UI interaction smoke tests/checklists

- **Priority:** P2
- **Depends on:** SEQ-C07, SEQ-G01 through SEQ-G06.
- **Acceptance:** repeatable checks cover drag/capture loss, Snap, Fit, arming,
  broadcast confirmation, disabled controls, preflight navigation, inspector,
  context menus, and accessibility via keyboard where supported.
- **Validation:** automated UI tests where stable; otherwise versioned manual
  checklist with evidence.

### [~] SEQ-H07 — Execute real-hardware timing and safety protocol

- **Priority:** P0
- **Depends on:** M1–M4 code complete.
- **Acceptance:** test at least master-only, master plus one slave, and multiple
  slaves; cover finite/infinite gestures, broadcast/target conflict, weak/offline
  link, disconnect, Pause/Resume, Stop, Loop, and simultaneous audio. Record
  observed skew and every firmware/console version.
- **Validation:** signed results appended to `TEST-PROTOCOL.md` or a linked report.
- **Current status:** COM3 bench confirmed master 43140 plus slaves 4216/34880.
  The stale master binary was replaced over USB; both complete same-version
  slave OTA transfers rebooted and remained stable beyond the anti-brick window.
  Strict serial regression passed 20/20 and the dedicated active script passed
  15/15: calibrated preview, each target, broadcast, deterministic seed, rapid
  restart/IDLE, TALK/POWER_DOWN interruption and five Loop cycles without
  observed inbox overflow. Cleanup restored configs/calibrations and left all
  servo/auto-animation states off. A content-derived Build ID now distinguishes
  same-version images: rolling compatibility with both legacy heartbeat formats
  was verified, and repeat OTA updates of both slaves returned `ok=true` with
  master `4DAD66EF` and slaves `72349AFE`. The Build ID regression passed 22/22
  and the read-only Sequencer preflight passed 6/6. Non-blocking application
  execution reports now correlate the console request with the existing mesh
  sequence and expose started/completed/interrupted/rejected per droid without
  changing `MSG_ANIM`. On firmware builds `00FD6D8C`/`65440D15`, the fully
  automated no-slave-servo bench passed 5/5 targeted, broadcast and TALK→IDLE
  lifecycle scenarios; the WPF suite passed 35/35 and strict hardware
  regression passed 24/24. Remaining H07 work: operator-confirmed motion,
  measured inter-droid skew, actual WPF Pause/Resume and simultaneous PC audio,
  intentional disconnect/offline target and weak-link cases.

### [ ] SEQ-H08 — Final regression, documentation and release gate

- **Priority:** P1
- **Depends on:** all required P0/P1 items.
- **Acceptance:** console build and all tests clean; existing firmware self-test
  unaffected; Help/README/contract agree; no stale TODO or dead library path;
  known limitations are explicit; dashboard is accurate.
- **Validation:** recorded commands/results and final review of the git diff.

## EPIC I — Scene & Show System (entirely deferred)

This epic records the possible evolution of the current Sequencer into a scene
editor, with a higher-level Show/Movie builder arranging several scenes. Every
item below is deliberately deferred: it must not expand or reorder the current
hardening work. The reliability baseline should, however, avoid architectural
choices that would make these items unnecessarily difficult later.

### [D] SEQ-I01 — Define the Scene and Show domain model

- **Priority:** P3
- **Problem:** the current sequence document represents one flat timeline and has
  no higher-level composition concept.
- **Depends on:** M1–M4 complete, SEQ-C08.
- **Acceptance:** define a Scene as one independently editable/playable timeline
  and a Show as an ordered collection of scene instances. Specify stable IDs,
  names, duration/end behavior, metadata, and the boundary between authored
  scene data and live performance state.
- **Validation:** architecture review demonstrates that one-scene playback still
  uses the same document and scheduler primitives rather than a parallel engine.

### [D] SEQ-I02 — Add scene entry and exit contracts

- **Priority:** P3
- **Problem:** reliable scene transitions require a known hardware state before
  and after each scene, especially for TALK and POWER_DOWN.
- **Depends on:** SEQ-B02, SEQ-B04, SEQ-E05.
- **Acceptance:** each scene supports explicit entry requirements and one of at
  least three exit policies: Safe (terminate active gestures/IDLE), Preserve
  (carry the last state into the next scene), or Custom (authored exit actions).
  Safe is the default unless a deliberate product decision says otherwise.
- **Validation:** transition tests cover finite/infinite gestures, broadcast and
  individual targets, Stop, Skip, restart, and failed scene start.

### [D] SEQ-I03 — Design versioned `.b1scene` and `.b1show` schemas

- **Priority:** P3
- **Problem:** scene composition, transitions and reusable identities should not
  be forced into the current flat `.b1seq.json` schema ad hoc.
- **Depends on:** SEQ-D01 through SEQ-D04, SEQ-I01, SEQ-I02.
- **Acceptance:** define versioned, validated schemas and migrations; decide how
  existing `.b1seq.json` files become/import as scenes; preserve forward-safe
  unknown-version behavior and atomic persistence.
- **Validation:** golden fixtures cover current sequence import, scene round trip,
  show round trip, schema migration and rejected future versions.

### [D] SEQ-I04 — Build the linear Show editor

- **Priority:** P3
- **Problem:** authors need to arrange manageable scenes instead of maintaining
  one very long timeline.
- **Depends on:** SEQ-I01 through SEQ-I03.
- **Acceptance:** create, add, remove, duplicate, rename, enable/disable and
  reorder scene instances. Each instance can define Auto, delayed, Manual GO,
  Hold or repeat-count transition behavior without modifying the source scene.
  Editing operations participate in Show-level Undo/Redo and Dirty state.
- **Validation:** UI interaction, persistence and transition-order tests.

### [D] SEQ-I05 — Add semantic roles and show-level casting

- **Priority:** P3
- **Problem:** scenes tied only to physical `ushort` droid IDs are difficult to
  reuse with another fleet or casting.
- **Depends on:** SEQ-I01, SEQ-I03, SEQ-G02, SEQ-G03.
- **Acceptance:** a scene can target roles such as HERO, SIDEKICK or CROWD; the
  Show maps roles to physical droids/broadcast groups. Preflight detects missing,
  duplicate or conflicting assignments. Existing ID-bound scenes remain usable.
- **Validation:** replay one scene with two different castings, including group
  intersection and offline-role cases.

### [D] SEQ-I06 — Compile scenes into the existing playback scheduler

- **Priority:** P3
- **Problem:** starting an unrelated timer engine per scene would reintroduce
  gaps, stale callbacks and inconsistent transport behavior.
- **Depends on:** SEQ-E02 through SEQ-E05, SEQ-I01, SEQ-I02, SEQ-I04.
- **Acceptance:** Show playback resolves casting and compiles scene instances into
  immutable plans handled by the same scheduler/state machine. Manual transitions
  pause at a defined boundary; automatic transitions and Skip invalidate stale
  work; the next scene may be prepared ahead without dispatching early.
- **Validation:** fake-clock tests cover Auto, Delay, Manual GO, Hold, Skip,
  repeat count, Pause/Resume, Stop and restart from a selected scene.

### [D] SEQ-I07 — Create an operator-focused Show mode

- **Priority:** P3
- **Problem:** the detailed timeline editor is not the safest interface for an
  operator during a performance.
- **Depends on:** SEQ-G01 through SEQ-G06, SEQ-I04, SEQ-I06.
- **Acceptance:** provide a simplified performance view with current/next scene,
  large GO/Stop/Previous/Next controls, elapsed/remaining time, fleet/audio
  readiness and prominent faults. Editing is unavailable while Show mode is
  armed or playing according to an explicit policy.
- **Validation:** keyboard/mouse accessibility and operator rehearsal checklist,
  including accidental double-GO and emergency Stop.

### [D] SEQ-I08 — Build a portable Show package

- **Priority:** P3
- **Problem:** a show depends on several scene files and audio assets that can be
  moved, renamed or changed independently.
- **Depends on:** SEQ-D09, SEQ-I03, SEQ-I04.
- **Acceptance:** a publish/build action creates a self-contained package with
  the Show, immutable scene snapshots, relative audio assets, hashes and a
  manifest. It reports collisions/missing assets and reopens on another PC.
- **Validation:** build, copy to a different base directory, preflight and play;
  detect tampered or missing assets.

### [D] SEQ-I09 — Define scene reuse and linked-versus-embedded behavior

- **Priority:** P3
- **Problem:** linked scenes are reusable but may change unexpectedly; embedded
  snapshots are reliable but can drift from their source templates.
- **Depends on:** SEQ-I03, SEQ-I04, SEQ-I08.
- **Acceptance:** define an explicit authoring model, recommended initially as
  linked templates while editing and embedded/versioned snapshots when
  publishing. The UI shows source, revision, local overrides and stale-link
  status without silently updating a production Show.
- **Validation:** source update, missing source, override, republish and rollback
  scenarios.

### [D] SEQ-I10 — Reserve a path for triggers and branching

- **Priority:** P3
- **Problem:** future interactive shows may need to wait for an operator, sensor
  or external event, or choose a different next scene.
- **Depends on:** SEQ-I04, SEQ-I06, SEQ-I07; separate safety/protocol design.
- **Acceptance:** the Show model can later add named triggers and conditional
  transitions without changing basic linear-scene identity or scheduler safety.
  The first implementation may remain strictly linear; no speculative runtime
  trigger system is required now.
- **Validation:** schema/architecture review plus future-version compatibility
  fixture; runtime tests are deferred until triggers are selected for delivery.

### [D] SEQ-I11 — Add scene rehearsal and navigation controls

- **Priority:** P3
- **Problem:** a long Show must not require replaying every earlier scene to
  rehearse or diagnose one section.
- **Depends on:** SEQ-I04, SEQ-I06, SEQ-I07.
- **Acceptance:** an author/operator can play only the selected scene, start the
  Show from that scene, repeat a selected scene range, move Previous/Next, and
  create a temporary rehearsal loop without changing the published Show. Entry
  contracts are applied when starting in the middle.
- **Validation:** navigation tests from first/middle/last scenes plus range-loop,
  manual transition, Dirty-state and safe-entry checks.

### [D] SEQ-I12 — Add safe incident recovery and checkpoints

- **Priority:** P3 now; safety-critical if Show mode is implemented.
- **Problem:** after disconnect, emergency Stop, audio failure or an operator
  error, blindly resuming elapsed events can leave hardware in an unknown state.
- **Depends on:** SEQ-A05, SEQ-B02, SEQ-I02, SEQ-I06, SEQ-I07.
- **Acceptance:** define safe checkpoints and offer explicit Restart scene, Skip
  to next, Restore safe state, Resume from checkpoint, or Abort Show actions.
  Missed events are never replayed implicitly. Recovery explains which hardware
  cleanup commands were delivered or could not be delivered.
- **Validation:** simulated disconnect/failure at entry, middle, transition and
  exit; real-hardware emergency recovery protocol.

### [D] SEQ-I13 — Declare role capability requirements

- **Priority:** P3
- **Problem:** semantic casting is unsafe if a chosen droid lacks the firmware,
  actuators, gestures or configuration expected by its role.
- **Depends on:** SEQ-I05, SEQ-G01, SEQ-G02.
- **Acceptance:** roles can declare required capabilities, minimum firmware,
  actuator/gesture needs and optional recommended settings. Casting Preflight
  reports incompatible, degraded, duplicate or unavailable assignments before
  the Show is armed.
- **Validation:** compatible, incompatible, partially capable, outdated firmware
  and offline casting fixtures.

### [D] SEQ-I14 — Support Show-level audio and scene transitions

- **Priority:** P3
- **Problem:** ambience, narration or music may need to span scene boundaries;
  treating every audio clip as scene-local can cause abrupt cuts or accidental
  overlap.
- **Depends on:** SEQ-F07, SEQ-F08, SEQ-I02, SEQ-I04, SEQ-I06.
- **Acceptance:** distinguish scene-local from Show-level audio and define Stop,
  Continue, Fade out, Fade in and Crossfade transition policies. Pause, Skip,
  incident recovery and whole-Show Stop manage both scopes predictably.
- **Validation:** automatic/manual transitions, crossfade timing, Skip, Pause,
  restart, loop and audio-failure tests.

### [D] SEQ-I15 — Add a simulation / Dry Run mode

- **Priority:** P3
- **Problem:** authors need to validate casting, events and transitions without
  moving physical hardware.
- **Depends on:** SEQ-E01, SEQ-G01 through SEQ-G04, SEQ-I05, SEQ-I06.
- **Acceptance:** Dry Run uses the real compiled plan and scheduler but replaces
  protocol output with a visible simulated event stream. Audio can be real,
  muted or simulated by choice. The UI shows resolved targets, scene transitions,
  warnings and cleanup actions without emitting a droid command.
- **Validation:** compare simulated and captured real-plan event order from the
  same Show; assert the physical protocol receives nothing.

### [D] SEQ-I16 — Record a performance journal

- **Priority:** P3
- **Problem:** after a Show, there is no concise record of what ran, what failed
  or how the operator intervened.
- **Depends on:** SEQ-I06, SEQ-I07, SEQ-I12; logging/privacy design.
- **Acceptance:** record Show/package/version, console/firmware versions, casting,
  scene start/end/skip/restart, dispatched commands, offline/failure events,
  measured lateness and operator actions. Logs are bounded, timestamped,
  exportable and avoid unnecessary sensitive local paths.
- **Validation:** normal, warning, failure and recovery sessions produce a
  readable report with deterministic event correlation.

### [D] SEQ-I17 — Add scene notes and operator cues

- **Priority:** P3
- **Problem:** scene timing alone does not carry dialogue, staging instructions,
  prop requirements or rehearsal notes.
- **Depends on:** SEQ-I03, SEQ-I04, SEQ-I07.
- **Acceptance:** scenes can contain a summary, formatted operator notes, dialogue
  references, prop/setup checklist and optional pre-GO countdown/cue. Notes do
  not affect playback timing unless represented by an explicit transition.
- **Validation:** edit, persistence, package, operator-display and long-text
  layout checks.

### [D] SEQ-I18 — Add an optional musical time grid

- **Priority:** P3
- **Problem:** millisecond-only editing is awkward for dance and music-driven
  choreography.
- **Depends on:** SEQ-C01, SEQ-E05, SEQ-G07, SEQ-I03.
- **Acceptance:** a scene may define BPM, time signature, beat/bar origin and
  musical Snap while preserving canonical millisecond times. Tempo changes are
  either explicitly unsupported at first or represented by a documented tempo
  map. Markers can label musical sections.
- **Validation:** beat-to-time conversion, rounding boundaries, zoom, export
  round trip and playback-equivalence tests.

### [D] SEQ-I19 — Add reusable scene variants and parameters

- **Priority:** P3
- **Problem:** language, energy, duration or cast-size variants would otherwise
  require duplicated scenes that drift independently.
- **Depends on:** SEQ-I03, SEQ-I05, SEQ-I09.
- **Acceptance:** define typed, bounded scene parameters and explicit variant
  overrides without permitting arbitrary runtime code. The compiled scene is a
  deterministic snapshot, Preflight validates each selected variant, and package
  publishing records resolved values.
- **Validation:** language/energy/cast-size examples, invalid override, source
  update, package and deterministic compilation tests.

### [D] SEQ-I20 — Integrate external cues, control and timecode

- **Priority:** P3
- **Problem:** a future Show may need to exchange cues with lighting, sound,
  physical controls or another playback system.
- **Depends on:** SEQ-I10, SEQ-I12, SEQ-I16; separate threat/safety model for
  every selected protocol.
- **Acceptance:** design an adapter boundary for selected inputs/outputs such as
  MIDI, OSC, DMX, physical buttons/remotes or external timecode. External input
  cannot bypass transport safety, Preflight, generation cancellation or operator
  arming; unsupported protocols are not speculatively implemented.
- **Validation:** deterministic adapter contract tests, duplicate/lost cue,
  disconnect, unauthorized/unarmed input and emergency Stop scenarios for each
  integration actually delivered.

### [D] SEQ-I21 — Publish immutable Show revisions and support rollback

- **Priority:** P3
- **Problem:** an operator should not perform from a mutable working document or
  discover that linked scenes/assets changed after the final rehearsal.
- **Depends on:** SEQ-A08, SEQ-I08, SEQ-I09, SEQ-I13, SEQ-I14.
- **Acceptance:** distinguish Draft from Published revisions. Publishing freezes
  resolved scenes, casting, parameters, audio hashes, expected console/firmware
  capabilities, Preflight-relevant metadata, author/date and revision identity.
  Show mode arms only an explicit published revision; previous valid revisions
  remain available for deliberate rollback without overwriting the draft.
- **Validation:** publish, modify draft, asset/source change, arm, rollback,
  package transfer and tamper-detection tests.

### [D] SEQ-I22 — Add instrumented rehearsal and latency reporting

- **Priority:** P3
- **Problem:** per-droid/audio timing compensation should be based on observed
  measurements rather than subjective guessing.
- **Depends on:** SEQ-G10, SEQ-G11, SEQ-I11, SEQ-I16.
- **Acceptance:** rehearsal correlates requested due time, scheduler dispatch,
  serial write, available acknowledgements/target start telemetry and audio start
  observations. Reports distributions and confidence, not false precision, and
  may propose—but never silently apply—per-track compensation.
- **Validation:** repeatable master-only and multi-droid measurements, missing
  telemetry, outliers, audio-device change and before/after compensation review.

## EPIC J — Commissioning and servo configuration safety

These requirements were identified while resolving the Scene Library model.
They are cross-cutting firmware/console safeguards rather than Scene features,
but remain in this tracked plan so they cannot be lost between backlog batches.

### [H] SEQ-J01 — Make a virgin ESP32 inert by default

- **Priority:** P0
- **Problem:** an erased/new slave previously defaulted Servos and Auto anims to
  ON, and the servo engine emitted a center PWM pulse before those preferences
  loaded. Locate already began OFF but had no explicit regression contract.
- **Acceptance:** on missing NVS keys, Servos, Auto anims and Locate are OFF for
  both roles; PWM stays detached throughout startup. Normal updates/reboots
  preserve choices that an operator already stored.
- **Validation:** master/slave builds and source invariants, then full-erase one
  bench ESP32 and observe no servo PWM/motion or Locate override before enabling
  them explicitly.
- **Implemented:** missing NVS state now resolves Servos and Auto anims to OFF
  for both roles; the servo engine starts detached and never emits its former
  pre-preference center pulse. Locate starts OFF and is now included in live
  heartbeat/inventory state so the console corrects stale optimistic display
  after a reboot.
- **Remaining hardware gate:** full-erase one bench ESP32 and directly observe
  inert boot plus explicit-enable behavior.

### [H] SEQ-J02 — Add independent PAN/TILT Reverse calibration

- **Priority:** P1
- **Problem:** mechanically mirrored servo installations currently require
  rewiring, custom firmware or inverted authored motion.
- **Acceptance:** Calibration exposes PAN Reverse and TILT Reverse per droid;
  flags persist locally, traverse serial and mesh, apply only at PWM output and
  leave Scene/preview coordinates unchanged. Old six-byte NVS limits remain
  rollback-readable; current masters send both legacy limits and additive V2
  direction data. Each droid reports the additive capability independently, so
  the controls stay disabled for an older master or slave in a mixed fleet.
- **Validation:** master/slave/WPF builds, offline invariants, serial read and
  invalid-type rejection, reboot persistence, and visible direction checks on
  each axis of one equipped droid.
- **Implemented:** per-axis controls, debounced serial fields, strict Boolean
  validation, rollback-compatible NVS sidecar, legacy-plus-V2 mesh delivery,
  per-droid capability reports and center-preserving asymmetric-range mapping
  are complete. Logical Scene and preview coordinates are unchanged.
- **Remaining hardware gate:** deploy current firmware, verify read/reboot
  persistence, invalid-type rejection and visibly reversed PAN/TILT on the
  master bench servos.

## Recommended execution order

The backlog is intentionally exhaustive; implementation should remain small and
sequential. Unless a test seam must be introduced first, follow this order:

1. **Foundation:** SEQ-H01 and the minimum of SEQ-E01 needed for fakes.
2. **First safe-playback batch:** SEQ-A01, SEQ-A02, SEQ-A03.
3. **Hardware safety:** SEQ-B01, SEQ-B02, SEQ-B06, SEQ-B07, then SEQ-G04.
4. **Transport consistency:** SEQ-A04 through SEQ-A08 and SEQ-B03.
5. **Editing correctness:** SEQ-C01 through SEQ-C08.
6. **Persistence correctness:** SEQ-D01 through SEQ-D08.
7. **Scheduler replacement:** SEQ-E02 through SEQ-E06.
8. **Duration/audio:** SEQ-F01 through SEQ-F08 and SEQ-B04.
9. **Preflight/ergonomics:** required SEQ-G01 through SEQ-G06 and SEQ-G11
   through SEQ-G17.
10. **Validation gate:** SEQ-H02 through SEQ-H08.
11. **Optional enhancements:** only selected `[D]` items. EPIC I remains deferred
    until the M1–M4 reliability baseline is complete and a separate Scene/Show
    design is approved.

### Immediate implementation batches

Completed persistence and scheduler work is grouped by shared responsibility
and dependency. Each batch remains independently testable and receives one
detailed commit after its full regression passes.

| Batch | Items | Scope and commit boundary |
|---|---|---|
| P1 — Safe import pipeline | SEQ-D01, SEQ-D02, SEQ-D03 | Parse into a temporary document, validate schema/content/bounds, migrate every supported legacy version, then apply once. One fixture-driven import commit. |
| P2 — Saved-state integrity | SEQ-C05, SEQ-D04, SEQ-D05 | Implement the saved checkpoint and atomic export together, then use that authoritative Dirty state to guard Import and library Load. C05 and D04 are intentionally one batch because their stated dependencies are circular. |
| Decision gate | DEC-003 | Resolved: retain the Local Library as the normal Scene store; keep Export only as an explicit external-copy escape hatch. |
| P3 — Scene Library and wording | SEQ-D06, SEQ-D07, SEQ-D08 | **Complete.** Scene Save/Save As uses stable IDs and atomic/versioned storage; legacy entries migrate, deletion is recoverable, and naming/source/Dirty badges plus Help agree. External Export remains clearly secondary. |
| S1 — Single deterministic scheduler | SEQ-E02, SEQ-E03 | **Complete.** One rearmable timer drains monotonic timestamp batches in immutable source order, compensates late wakes, warns about same-target/broadcast overlap and releases completely on cancellation. |
| T1 — Coherent duration and infinite ends | SEQ-F01, SEQ-F02, SEQ-C06, SEQ-B04 | **Code complete; F01 hardware measurement pending.** Structured firmware timing metadata feeds one target-aware provider and cached extent; schema v5 promotes looping-gesture width into a persisted endpoint with ownership-safe IDLE termination. |

SEQ-D09 remains deferred. The next dependency-complete batch is sequence/audio
end semantics: SEQ-F08 then SEQ-E05, followed by the remaining ruler-performance
portion of SEQ-E06.

## Decision log

Record decisions here before implementing behavior with multiple reasonable
options.

| ID | Status | Decision |
|---|---|---|
| DEC-001 | Resolved 2026-08-11 | Lock persistent editing during Play/Pause; allow transient inspection, zoom/scroll and dynamic track mute. |
| DEC-002 | Resolved 2026-08-11 | Normal Stop uses targeted tracked IDLE only for Sequencer-owned infinite gestures. Safe Stop broadcasts a transient centered/servo-powered hold. Emergency Stop immediately broadcasts persistent Servo OFF without confirmation; the owner accepts loss of holding torque. |
| DEC-003 | Resolved 2026-08-11 | Treat the current Sequencer document as a Scene and retain Local Library as the normal local working catalog. Save updates the current stable scene ID; Save As creates a new one. Import never auto-adds. Export is not required for normal work, but remains an explicit external snapshot for backup, transfer, support and version control. Future Show authoring combines scenes and published Shows embed immutable scene snapshots. |
| DEC-004 | Open | Unknown/offline targets: warning or blocking preflight error? |
| DEC-005 | Resolved 2026-08-11 | Same-time events form one batch in immutable source order: gesture clips in editor order, then audio clips by lane/clip order. Multiple gestures for one target are last-received-wins. Broadcast plus targeted overlap is serialized but warned because mesh arrival can differ from console order. |
| DEC-006 | Open | Audio-only rehearsal when no master is connected? |
| DEC-007 | Deferred | Scene identity and migration from `.b1seq.json` to `.b1scene.json`? |
| DEC-008 | Deferred | Show authoring uses linked scenes, embedded snapshots, or a publish-time hybrid? |
| DEC-009 | Deferred | Scene targets remain physical IDs, become semantic roles, or support both? |
| DEC-010 | Deferred | Default scene exit policy: Safe, Preserve, or author-selected per scene? |
| DEC-011 | Deferred | Incident recovery checkpoints are automatic, author-defined, or both? |
| DEC-012 | Deferred | Show-level audio transition support begins with hard cuts/fades or full crossfade? |
| DEC-013 | Deferred | Performance journals default on or require explicit operator opt-in? |
| DEC-014 | Deferred | First external integration target: MIDI, OSC, DMX, physical remote, or timecode? |
| DEC-015 | Resolved 2026-08-11 | Sequencer-only infinite gestures receive a 5 s lease, renewed every 2 s after master acceptance; expiry reports `leaseExpired` and commands IDLE. Manual Animation-card and autonomous gestures remain unleased. |
| DEC-016 | Open | Mechanical policy for Safe Stop versus Emergency Stop and servo power? |
| DEC-017 | Resolved 2026-08-11 | Target execution is the required success signal. Missing reports warn but do not gate playback; serial-write/master-relay stages may be added diagnostically. |
| DEC-018 | Deferred | Published Show revision naming, retention and rollback policy? |
| DEC-019 | Open | Transport UX: Play/Pause toggle versus separate controls; explicit Restart; normal Stop cursor retention; and Play-from-zero versus Play-from-cursor policy? |
| DEC-020 | Open | Timeline following: comfort-corridor behavior, default Follow state, and how manual scroll suspends/re-enables it? |
| DEC-021 | Open | Timeline pointer navigation: exact Ctrl/Shift-wheel bindings, trackpad behavior and whether manual zoom/pan suspends Follow until explicitly re-enabled? |

## Completion evidence log

Append concise evidence when closing items; do not paste full build logs.

| Date | Items | Evidence |
|---|---|---|
| 2026-08-11 | Planning baseline | Static review complete; console build previously clean; implementation not started. |
| 2026-08-11 | SEQ-A01, SEQ-A02, SEQ-A04, SEQ-A05, SEQ-A07, SEQ-E01 | Immutable pass records, generation cancellation, dispatch-time mute, disconnect/shutdown cleanup, monotonic clock and injected protocol/audio/timer/clock seams; 12 tests passed. |
| 2026-08-11 | SEQ-H01 | Headless xUnit project, reusable document/JSON fixtures and fake protocol/audio/timer/clock added; default self-test runs the suite without changing the product build number. |
| 2026-08-11 | SEQ-A03 (in progress) | Play/Pause edit lock, direct mutation guards, visible EDIT LOCKED badge and automated command matrix implemented; final rendered UI interaction pass remains. |
| 2026-08-11 | Offline regression | `tools/self-test.ps1 -SkipSerial`: 15 passed, 0 failed; master/slave firmware and WPF console clean; build number remained 343. |
| 2026-08-11 | SEQ-E04, SEQ-H03 (in progress) | Transport suite expanded from 12 to 33 tests; reproduced and fixed duplicate/lost events at Pause boundaries; 20/20 stress runs passed. Coverage now includes empty/audio/gesture plans, same-time ordering, restart/Loop cancellation, mute, repeated Pause/Resume/Stop, disconnect, cleanup and numeric limits. |
| 2026-08-11 | Offline regression | `tools/self-test.ps1 -SkipSerial`: 15 passed, 0 failed; 33 Sequencer tests, master/slave firmware and WPF console clean; build number remained 343; report `b1-self-test-20260811-085028.json`. |
| 2026-08-11 | SEQ-H07 (in progress) | COM3 preflight found master 43140 plus slaves 4216/34880 with stable inventory, but exposed a stale/mismatched 1.9.0 binary: targetless config responses and absent runtime validation. Active movement was refused. Bench state restored to configs `50/60/50`, master calibration `0/90/180`, servos/auto-animation off. Added guarded `sequencer-bench-test.ps1`; hardened default self-test so mutating rejection checks are suppressed unless a read-only validation probe passes. Reports `b1-self-test-20260811-085949.json`, `b1-sequencer-bench-20260811-090523.json`, `b1-self-test-20260811-090558.json`. |
| 2026-08-11 | SEQ-H07 (in progress) | Master USB flash succeeded; two slave OTA payloads completed at 5128/5128 chunks and stayed healthy beyond 20 s. Same-version 1.9.0 falsely yields `rolledBack`, exposing a version-only verdict limitation. Active serial/mesh bench passed 15/15, then strict serial regression passed 20/20. Final state: master `59/60/50`, slaves `50/60/50`, original calibrations, servos/auto-animation off. Reports `b1-sequencer-bench-20260811-092352.json` and `b1-self-test-20260811-092501.json`. Physical movement/skew, WPF+audio and disconnect/weak-link observations remain. |
| 2026-08-11 | Build ID / SEQ-H07 | Added deterministic 8-hex firmware identities throughout PlatformIO, heartbeat/registry, serial protocol, WPF inventory/status, OTA verdicts and release manifests. The new master accepted both legacy slaves during the rolling upgrade; same-version OTA then returned `ok=true` for 4216 and 34880 with master build `4DAD66EF` and slave build `72349AFE`. Read-only bench passed 6/6 and strict serial regression passed 22/22, including 15 s stable mesh observation. Reports `b1-sequencer-bench-20260811-101337.json` and `b1-self-test-20260811-101406.json`. |
| 2026-08-11 | Execution telemetry / SEQ-H07 | Added backward-compatible, non-blocking `MSG_ANIM_EXEC` lifecycle reports correlated through the existing mesh sequence and console `requestId`; WPF clips aggregate started/completed/interrupted/rejected per droid. Master `00FD6D8C` plus slaves `65440D15` passed headless targeted/broadcast/TALK→IDLE lifecycle 5/5, WPF tests 35/35, offline regression 17/17 and strict hardware regression 24/24. Final servo/auto-animation state is off on all three. Reports `b1-anim-exec-20260811-133214.json`, `b1-self-test-20260811-131949.json`, `b1-sequencer-bench-20260811-133234.json`, `b1-self-test-20260811-133306.json`. Physical motion/skew, WPF+audio, disconnect and weak-link tests remain deferred until operator/hardware availability. |
| 2026-08-11 | SEQ-G11 (in progress) | Added separate non-blocking start/completion deadlines, `UNCONF`/`MISS`/`TIMEOUT` clip states, late-report recovery, terminal-state duplicate protection and looping-gesture semantics. Headless WPF suite passed 39/39; offline regression passed 17/17 with report `b1-self-test-20260811-135225.json`. Serial-write/master-receipt staging and hardware loss scenarios remain. |
| 2026-08-11 | SEQ-G11 (in progress) | Split delivery into immediate local dispatch (`NO LINK`/`NOT READY`/`WRITE FAIL` or `WRITE`), correlated master acceptance (`MASTER`) and target execution. Added additive `animAccepted` with mesh-queue/local-routing facts, preserved compatibility with older firmware, and strengthened the headless bench to require master acceptance before lifecycle reports. WPF tests passed 43/43; offline regression 17/17 (`b1-self-test-20260811-140233.json`); master `9A228A09` and slaves `1D787B84` passed hardware execution 5/5 (`b1-anim-exec-20260811-141734.json`), strict regression 29/29 (`b1-self-test-20260811-141828.json`) and preflight 6/6 (`b1-sequencer-bench-20260811-141836.json`). Final servo/auto-animation state is off on all three. Offline/weak-link/disconnect timeout observations and any true relay acknowledgement remain. |
| 2026-08-11 | SEQ-B01, SEQ-B02 | Added per-droid/request latest-gesture tracking and targeted IDLE cleanup on Stop, non-looping natural end, application disposal and Play restart. Broadcast overrides, repeated infinite commands, failed dispatch/cleanup retry, mesh-failure rollback, disconnect retry and Loop behavior are covered; WPF suite passed 55/55 and offline regression 17/17 (`b1-self-test-20260811-143504.json`). Existing hardware benches already prove tracked TALK/POWER_DOWN interruption by IDLE; no firmware change or reflash was required. |
| 2026-08-11 | SEQ-B06 | Added additive Sequencer-only infinite-animation leases: 5 s initial TTL, 2 s correlated renewal, fail-closed IDLE with `leaseExpired`, stale-mesh-sequence rejection, Pause/Loop retention and Stop/end/restart/disconnect cleanup. WPF suite passed 61/61. Master `7A38B49A` and slaves `673F513F` passed USB/OTA deployment, headless expiry/renewal/stale protection 8/8 (`b1-anim-exec-20260811-145900.json`), strict regression 31/31 with 15 s stable mesh (`b1-self-test-20260811-150021.json`) and read-only preflight 6/6 (`b1-sequencer-bench-20260811-150026.json`). Final servos and automatic animations are off on all three droids. |
| 2026-08-11 | SEQ-B07 | Defined and implemented Normal Stop, transient centered/servo-powered Safe Stop, and immediate persistent fleet Servo OFF Emergency Stop. Added transport buttons, old-firmware Safe Stop fallback, stale-callback cancellation, mesh visualization and operator documentation. WPF suite passed 64/64. Master `8460B615` and slaves `D6BF5A99` passed USB/OTA deployment, headless Safe/Emergency validation 10/10 (`b1-anim-exec-20260811-153345.json`), strict regression 32/32 with 15 s stable mesh (`b1-self-test-20260811-153458.json`) and read-only preflight 6/6 (`b1-sequencer-bench-20260811-153504.json`). Final servos and automatic animations are off on all three droids. |
| 2026-08-11 | SEQ-B03 | Formalized Pause as PC-transport-only: future dispatch/audio/playhead pause, dispatched finite motion continues, infinite leases remain renewed, reports keep updating, and Resume does not replay consumed events. Added the persistent `PAUSED · DROID MOTION CONTINUES` transport warning and aligned Help/tooltips. WPF suite passed 65/65, including finite completion during Pause plus existing boundary, repeated Resume and infinite-lease cases; offline regression passed 17/17 (`b1-self-test-20260811-154128.json`). No firmware change or reflash was required. |
| 2026-08-11 | SEQ-A06 | Replaced independently writable transport booleans with the guarded `Stopped`/`Playing`/`Paused` state machine and one shared pass-start path. All command/badge/edit-lock flags derive from that state; partial scheduler startup failure now rolls back cleanly. The nine-path transition table and UI notification check passed within the full WPF suite at 75/75; offline regression passed 17/17 (`b1-self-test-20260811-174442.json`). No firmware change or hardware run was required. |
| 2026-08-11 | SEQ-A03 | Closed the Play/Pause editing policy across the complete UI surface: document and Local Library mutations lock, while inspection, arm, runtime mute, viewport tools and Export remain available. Added late-drag transition guards, disabled-control guidance and a three-state command/direct-guard matrix including Undo/Redo; WPF suite passed 76/76 and offline regression passed 17/17 (`b1-self-test-20260811-175250.json`). No firmware change or hardware run was required. |
| 2026-08-11 | SEQ-C01 | Centralized command and drag mutations behind structural begin/commit transactions. Real edits now create one pre-edit snapshot, clear Redo, set Dirty and refresh derived timeline state once; no-op edits create nothing. Thirteen edit families plus no-op/Redo invalidation passed within the 78/78 WPF suite; offline regression passed 17/17 (`b1-self-test-20260811-201830.json`). No firmware change or hardware run was required. |
| 2026-08-11 | SEQ-C02, SEQ-C03, SEQ-C04, SEQ-C07 | Added the shared 5 px drag threshold, complete persistent-property transaction coverage, exact 50-entry Undo/Redo bounds, and fail-safe interaction cancellation/restoration for Escape/capture/focus/unload. Twenty edit families, transient-state isolation, threshold/no-op/return, cancellation and 55-edit history ordering pass within the 82/82 WPF suite; offline regression passed 17/17 (`b1-self-test-20260811-203025.json`). No firmware change or hardware run was required. |
| 2026-08-11 | SEQ-C08 | Established explicit persistent-document (`SequenceSnapshot`), editor-history (`SequencerEditHistory`) and immutable runtime (`SequencerPlaybackPlan`) boundaries without changing UI behavior. Six architecture tests cover the exact document surface, structural comparison, transactions/cancellation, bounded Undo/Redo and read-only playback state; full WPF suite passed 88/88 and offline regression passed 17/17 (`b1-self-test-20260811-203938.json`). No firmware change or hardware run was required. |
| 2026-08-11 | SEQ-D01, SEQ-D02, SEQ-D03 | Added validate-then-apply sequence import, strict field/count/timing limits and explicit v1–v4 migrations. Twenty-nine new golden, invalid, boundary, ambiguity and no-partial-mutation cases pass within the 117/117 WPF suite; offline regression passed 17/17 (`b1-self-test-20260811-215453.json`). No firmware change or hardware run was required. |
| 2026-08-11 | SEQ-C05, SEQ-D04, SEQ-D05 | Replaced manual Dirty toggles with structural saved-checkpoint equality; added schema-self-validating sibling-temp/flush/atomic-rename export and injected unsaved-work confirmation for Import/Library Load. Twenty new checkpoint, Undo/Redo, create/replace/failure, round-trip, naming, clean/dirty/cancel, Play/Pause and startup cases pass within the 137/137 WPF suite; offline regression passed 17/17 (`b1-self-test-20260811-220731.json`). No firmware change or hardware run was required. |
| 2026-08-11 | DEC-003, SEQ-J01, SEQ-J02 (hardware pending) | Resolved the product model as Scene Library first and future Show composition, with Export retained only as an explicit external copy. Implemented inert virgin-board Servos/Auto anims/Locate defaults with boot PWM detached, live Locate reconciliation, and independent center-preserving PAN/TILT Reverse through WPF, strict serial validation, rollback-compatible NVS, additive mesh V2 and per-droid capability gating. Master/slave/WPF builds and 137/137 WPF tests passed; offline regression passed 19/19 (`b1-self-test-20260811-224902.json`). Full-erase boot and physical Reverse observations remain. |
| 2026-08-11 | SEQ-D06, SEQ-D07, SEQ-D08 | Completed the Scene-first Local Library: editable names, Save/Save As with stable GUIDs and conflict refusal, validated versioned envelopes, atomic writes, deterministic legacy migration, visible corrupt-file issues, recoverable confirmed Trash, discriminated startup restore, truthful origin/Dirty badges and aligned Help/tooltips. Export remains an external copy and cannot falsely clear modified library content. Sixteen focused cases expanded the WPF suite to 153/153; Release build completed with zero warnings/errors and offline regression passed 19/19 (`b1-self-test-20260811-231018.json`). No firmware deployment or hardware run was required. |
| 2026-08-11 | SEQ-E02, SEQ-E03, SEQ-H03 (in progress) | Replaced per-event timers with one rearmable pass timer and a monotonic forward-only batch cursor. Late wakes drain all overdue timestamps in immutable source order and compensate the next delay; Pause/Stop/restart/Loop dispose or replace the whole session. Same-target and broadcast/target overlaps now produce timestamped SCHEDULE warnings with explicit last-received/ambiguous-mesh policy. Added 10,000-event resource, drift catch-up, batch shape/conflict, atomic gesture+audio and 20-pass repeatability coverage; WPF suite passed 157/157, Release build had zero warnings/errors and offline regression passed 19/19 (`b1-self-test-20260811-233354.json`). No firmware change, deployment or hardware run was required. |
| 2026-08-12 | SEQ-F01, SEQ-F02, SEQ-C06, SEQ-B04 | Added structured immediate/finite/infinite firmware timing metadata and one target-speed-aware console provider with conservative mixed-speed broadcast ranges and visible provisional fallback. Cached duration/extent now refreshes exactly once per edit commit. Schema v5 persists real POWER_DOWN/TALK endpoints; playback issues ownership-safe targeted IDLE at that width across Pause/Loop without stopping a replacement gesture. WPF suite passed 165/165; Release build and all three PlatformIO environments passed; offline regression passed 19/19 (`b1-self-test-20260812-000226.json`). Firmware deployment and measured physical-duration comparison remain hardware checks. |
| 2026-08-12 | SEQ-E06 (in progress) | Removed the visible 1.5 s UI hitch introduced by duration refreshes on every unchanged `droids` heartbeat. Sequencer roster and broadcast-duration target signatures now invalidate independently, avoiding track/ruler reconstruction for age/RSSI-only telemetry while preserving real fleet/name/online changes. A focused invalidation test expanded the WPF suite to 166/166; Debug suite and Release build 347 passed with zero errors/warnings, and the operator confirmed smooth radar and playhead animation. Maximum-duration ruler generation remains. |
