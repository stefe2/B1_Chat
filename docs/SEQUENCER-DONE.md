# Animation Sequencer — completed hardening items

Every `SEQ-*` item that shipped, with the acceptance criteria it was judged
against and the evidence log recorded while closing it. Split out of
[SEQUENCER-HARDENING.md](SEQUENCER-HARDENING.md) on 2026-08-13 so the working
backlog only carries actionable work.

Nothing here is a summary: the entries are the originals, kept whole because
SEQ-H08 (final regression and release gate) needs this traceability. The
dashboard counts in the working backlog remain the authoritative totals.

For what the shipped behavior actually does at runtime, read
[SEQUENCER-BEHAVIOR.md](SEQUENCER-BEHAVIOR.md) instead — this file records
*why each item was considered closed*, not how the feature behaves.

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
- **Implemented:** persistent document and Local Library mutations occur only
  in `Stopped`. Relay-command `CanExecute`, direct ViewModel guards,
  inspector/container disabling and pointer-drag rechecks all derive from
  `CanEditSequence`; the transport displays `EDIT LOCKED` and disabled controls
  explain that Stop is required. Document-replacement entry points remain
  discoverable during Play/Pause, but require explicit approval and transition
  to `Stopped` before mutating anything. A transport transition during a
  captured drag releases transient visuals without applying a late change.
- **Policy/validation matrix:**

  | Operation group | Stopped | Playing | Paused |
  |---|---|---|---|
  | Insert, drag, retarget, inspector, duplicate/delete, Loop | edit | locked | locked |
  | Audio lane/clip edits, Undo/Redo, Clear | edit | locked | locked |
  | New/Open/Import document replacement | replace | confirm Stop, then replace | confirm Stop, then replace |
  | Local Library Trash | edit | locked | locked |
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
- **Implemented:** interactive New, Import and Scene Open share an injected
  replacement workflow. Clean documents proceed directly; modified documents
  offer Save, continue without saving, or Cancel. Play/Pause replacements first
  ask permission to stop, and defer that Stop until all cancel-capable questions
  succeed. Startup last-file restore bypasses UI and remains silent.
- **Evidence:** clean/dirty/save/discard/cancel matrices cover New, Import and
  Open. Play/Pause cases cover stop refusal, accepted replacement and later
  unsaved-work cancellation without stopping the pass. Invalid confirmed Import
  preserves the editor and reports the parse error; startup invokes no dialog.

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
- **Implemented:** the Scene bar reports name, `NEW`/`LOCAL LIBRARY`/
  `IMPORTED / EXTERNAL FILE` origin and `CLEAN`/`SAVED`/`MODIFIED` state. Save,
  Save As, Import, Export, Open and Trash tooltips now describe their actual
  boundaries; in-app Help and storage documentation use the same Scene-first
  workflow and linked-audio warning. The later G18 browser revision removes raw
  Load/Trash rows without changing the stable-ID storage contract.
- **Validated:** badge/origin transitions and replacement/edit-lock behavior are
  automated; compiled XAML plus the Help/content review cover visible text.

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

### [x] SEQ-E06 — Cache derived duration and control UI update cost

- **Priority:** P2
- **Problem:** timecode can rescan every clip on each 30 ms playhead tick; ruler
  collections are wholesale rebuilt during zoom/layout changes.
- **Depends on:** SEQ-C06, SEQ-F01.
- **Acceptance:** document mutations update cached total once; playhead ticks do
  not recompute it. Ruler generation is bounded/debounced or virtualized enough
  for the documented maximum sequence size.
- **Validation:** performance test with the supported maximum event count and
  duration; no perceptible UI stall while playing or zooming.
- **Implemented:** cached total duration no longer rescans clips on
  playhead ticks. Unchanged 1.5 s droid telemetry is now projected into separate
  roster and online-target signatures, so it does not rebuild timeline tracks,
  duration metadata or ruler bindings. A regression test proves repeated
  identical heartbeats emit no extent/track refresh; real name/role and online
  membership changes still update only their affected projection. The operator
  confirmed that radar and scene-playhead animation are fluid again in build
  347. Ruler spacing now adapts across milliseconds, seconds, minutes and hours
  while enforcing a strict 600-tick ceiling shared by all ruler/gridline views.
  A maximum-size test commits 10,000 events over the supported 24-hour duration
  with one derived refresh, then verifies both density and count limits at the
  maximum zoom.

## EPIC F — Duration and audio robustness

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

### [x] SEQ-F03 — Notify the UI when an audio filename changes

- **Priority:** P1
- **Problem:** `FileName` is derived from `FilePath` but receives no property
  notification after Replace file.
- **Depends on:** none.
- **Acceptance:** replacing a path immediately updates the displayed basename and
  related missing/error state.
- **Validation:** model binding test plus manual replace check.
- **Implemented:** `FilePath` carries `[NotifyPropertyChangedFor(nameof(FileName))]`,
  so Replace file… updates the displayed basename immediately. `StatusTooltip`
  is notified from the same source change, while probe status/message changes
  notify the warning flag and error tooltip.

### [x] SEQ-F04 — Keep zero/unknown-duration audio clips visible and editable

- **Priority:** P1
- **Problem:** probe failure returns duration 0, producing a zero-width clip with
  no useful error affordance.
- **Depends on:** SEQ-F05.
- **Acceptance:** unknown-duration clips have a minimum selectable width, a clear
  warning badge/tooltip, and do not silently define an invalid sequence end.
- **Validation:** missing, corrupt, unsupported, and valid zero-length files.
- **Implemented:** a failed probe still inserts the clip. The new `AudioWidth`
  converter mode floors its rendered width at 26 px and it shows a ⚠ badge, an
  orange border and the reason in its tooltip. The floor is presentation only —
  effective duration stays 0, so an unreadable file never contributes a stale tail
  to the sequence end. The last serialized duration may be retained for recovery
  but is ignored until the asset is readable. Scene and Undo/Redo restoration
  revalidate distinct present assets; a valid empty file remains a success at 0 ms
  and carries no warning.

### [x] SEQ-F05 — Add bounded audio probing and actionable errors

- **Priority:** P1
- **Problem:** duration probing has no timeout/cancellation and collapses every
  failure to 0; media decoding depends on installed Windows codecs.
- **Depends on:** SEQ-E01 for a testable service boundary.
- **Acceptance:** probe returns a typed success/failure result, closes resources
  on every path, supports timeout/cancellation, and surfaces codec/file errors
  without blocking the UI.
- **Validation:** success, failure event, thrown URI/open error, timeout, cancel,
  and file removed during probe.
- **Implemented:** `AudioProbe` returns a typed `AudioProbeResult`
  (`Ok`/`FileMissing`/`DecodeFailed`/`Timeout`/`Cancelled`), bounded by a 10 s
  default timeout and a cancellation token. The media handle is disposed on every
  exit path, including timeout and a throwing Open; teardown is marshalled to the
  WPF handle's owning dispatcher after a worker-thread continuation. A source that
  opens but reports no timespan is a decode failure naming a possibly missing codec.

### [x] SEQ-F06 — Prevent stale waveform assignment and cache invalidation bugs

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
- **Implemented:** the cache key is path plus file size and last-write time, so
  replacing a file's contents under the same name invalidates it. Failed and
  cancelled decodes are not cached and can retry; the cache is bounded at 64
  entries with least-recently-used eviction. Each clip carries a waveform token
  bumped on every source change, and a decode that lands after the clip moved on is
  discarded instead of overwriting the current envelope.

### [x] SEQ-F07 — Manage MediaPlayer lifecycle and playback failures

- **Priority:** P1
- **Problem:** completed non-looping players remain open until global Stop and
  playback failures are silent.
- **Depends on:** SEQ-E01.
- **Acceptance:** ended/failed players detach handlers, close, and leave the
  active set; failures identify the clip; Pause/Resume touches only genuinely
  active players; StopAll remains idempotent.
- **Validation:** concurrent clips, natural end, loop, failure, pause after one
  clip ended, and repeated Stop tests.
- **Implemented:** one media handle per clip, behind an `IMediaHandle` seam. A
  non-looping clip that ends, or any clip that fails, detaches its handlers, closes
  and leaves the active set; a looping clip rewinds and stays. Pause/Resume touch
  only active clips, so Resume can no longer restart something that already
  finished. `StopAll` is idempotent, and a failure is reported once with the clip
  that caused it. The ViewModel exposes it through a visible `⚠ AUDIO` badge whose
  tooltip names the file and reason; duplicate notifications are suppressed per
  clip identity, not merely per error string.

## EPIC G — Preflight and ergonomics

### [x] SEQ-G14 — Redesign Play as an unambiguous Play/Pause control

- **Priority:** P1
- **Problem:** pressing Play while already playing restarted the Scene from zero
  and resent its first commands. This was surprising for a conventional
  transport and could cause an accidental motion restart.
- **Depends on:** SEQ-A06, SEQ-E04.
- **Acceptance:** the primary control always exposes the action that will happen
  next. A second ordinary Play press must not silently restart; restart from zero
  remains available through a separate, unmistakable action. Tooltips, keyboard
  shortcuts, disabled states and Help all match Stopped/Playing/Paused.
- **Validation:** state/command/UI tests cover stopped Play, playing Pause,
  paused Resume, explicit Restart, rapid double-click, failure to start and Loop.
- **Implemented:** the primary button and `Space` expose Play/Pause/Resume
  through a state-dependent glyph and tooltip. A second Play pauses rather than
  resending choreography; `Restart`/`Ctrl+Enter` owns the explicit clean restart
  path. Existing generation and Loop safety tests use that separate action. The
  safety hierarchy is visually explicit: E-STOP uses a permanent filled-red
  treatment, while Loop is a neutral editing mode that turns orange when active.
- **Evidence:** automated transport coverage passed in the 172/172 WPF suite,
  followed by the current 224/224 full suite. In Release build 359, the operator
  validated the rendered glyphs/tooltips, click and `Space` Play/Pause/Resume,
  confirmed that a second Play does not restart, that explicit Restart returns
  to zero, and that the complete workflow behaves as expected.

### [x] SEQ-G15 — Separate Stop from playhead navigation and rewind

- **Priority:** P2
- **Problem:** normal Stop ended playback and always returned the playhead to
  zero. Transport safety cleanup and timeline navigation are different
  intentions, and forcing both made inspection and rehearsal awkward.
- **Depends on:** SEQ-A06, SEQ-B07, SEQ-G14.
- **Acceptance:** define and expose the post-Stop playhead policy separately from
  hardware/audio cleanup. Specify normal Stop, Safe Stop, Emergency Stop,
  natural end and Loop boundaries, plus whether Play while stopped begins at
  zero or at the retained cursor. A dedicated return-to-start action is always
  available and cannot be confused with a safety stop.
- **Validation:** tests cover Stop from Play/Pause, repeated Stop, return to
  start, Play after retained Stop, natural end, Safe/Emergency Stop and Loop.
- **Implemented:** normal, Safe and Emergency Stop retain the measured cursor;
  non-looping natural completion retains the calculated end. A distinct
  return-to-start button/`Ctrl+Home` is enabled only while stopped. Play starts
  from a retained cursor and skips older events; at the natural end it starts a
  new pass from zero. Restart is the always-explicit performance-from-zero path.
- **Evidence:** automated transport coverage passed in the 172/172 WPF suite,
  followed by the current 224/224 full suite. In Release build 359, the operator
  confirmed that Stop retains the cursor, Play resumes from that retained
  position, `Return to start` moves it to zero and explicit Restart begins from
  zero. The rendered control ordering and rehearsal workflow behaved correctly.

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

## Completion evidence log

Append concise evidence when closing items; do not paste full build logs.

| Date | Items | Evidence |
|---|---|---|
| 2026-08-13 | SEQ-G15 | Closed after rendered Release build 359 validation: Stop retained the playhead, Play resumed from the retained position without replaying earlier content, Return to start moved to zero and explicit Restart began from zero. Automated Stop/Pause/Safe/Emergency/natural-end/Loop coverage had already passed in the 172/172 suite; the current full WPF suite remains 224/224. |
| 2026-08-13 | SEQ-G14 | Closed after rendered Release build 359 validation: state-dependent Play/Pause/Resume glyph and tooltip, click and `Space` interaction, no implicit restart on a second Play, and explicit Restart from zero all behaved as expected. Automated transport coverage had already passed in the 172/172 suite; the current full WPF suite remains 224/224. |
| 2026-08-13 | SEQ-F03, SEQ-F04, SEQ-F05, SEQ-F07 follow-up | Audit corrections: `MediaPlayer` teardown now returns to its owner dispatcher; the transport binds a visible per-clip `⚠ AUDIO` failure badge; unavailable or pending assets have zero effective duration while retaining serialized recovery metadata; Scene and Undo/Redo restoration revalidate present assets. Six focused tests add binding notification, restored corrupt/missing/pending state, effective playback-plan duration and a real WPF dispatcher/MediaPlayer open-close smoke path. Full suite: 224/224. Offline self-test: 21/21, clean WPF/master/slave builds, build number preserved at 359, report `b1-self-test-20260813-225837.json`. Operator rendered validation passed with a text payload renamed `.mp3`: narrow warning clip, orange border, reason tooltip and visible playback `⚠ AUDIO` report all behaved correctly. |
| 2026-08-13 | SEQ-F03, SEQ-F04, SEQ-F05, SEQ-F06, SEQ-F07 | Audio robustness batch. Probe, waveform decoding and media lifecycle moved behind `IAudioProbe`, `IWaveformDecoder` and `IMediaHandle`; typed probe results with timeout and cancellation; unreadable clips stay visible and badged without affecting sequence length; metadata-keyed bounded waveform cache with stale-assignment rejection; per-clip media handles retired on end or failure with the failure reported. Console build clean, 0 warnings.
| 2026-08-13 | SEQ-H05 (in progress) | Sequencer suite 181 to 218 tests, all passing (`dotnet test`, 304 ms). New `AudioServiceTests.cs` drives probe, lifecycle and cache policy through fakes, plus a real NAudio decode of the committed `probe-tone-1500ms.mp3` fixture asserting a rising envelope. `tools/self-test.ps1 -SkipSerial`: 21 passed, 0 failed (19 before), report `b1-self-test-20260813-011856.json`. Audio loop endpoint coverage still blocked on SEQ-F08.
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
| 2026-08-12 | SEQ-E06 | Removed the visible 1.5 s UI hitch introduced by duration refreshes on every unchanged `droids` heartbeat. Sequencer roster and broadcast-duration target signatures now invalidate independently, avoiding track/ruler reconstruction for age/RSSI-only telemetry while preserving real fleet/name/online changes. Ruler intervals now expand from milliseconds through hours and enforce a 600-tick ceiling across all three WPF consumers. Maximum-size coverage commits 10,000 events over 24 hours with one derived refresh and verifies spacing/count at maximum zoom. The WPF suite passed 168/168; Release build 348 and offline regression passed 19/19 (`b1-self-test-20260812-002905.json`). The operator previously confirmed smooth radar and playhead animation in build 347; no firmware deployment or hardware run was required. |
| 2026-08-12 | SEQ-G14, SEQ-G15, SEQ-G16, SEQ-G17 (validation pending) | Implemented explicit Play/Pause/Resume, Restart, retained Stop cursor and Return-to-start semantics; added operator-controlled comfort-corridor Follow plus pointer-anchored Ctrl-wheel zoom and Shift-wheel pan. Four focused transport/navigation tests cover double-click safety, play-from-cursor filtering, Stop/Pause/Safe/E-STOP retention, Follow state and navigation math within the 172/172 WPF suite. Release build 350 succeeded and offline regression passed 19/19 (`b1-self-test-20260812-003817.json`). Rendered toolbar, long-timeline Follow and physical mouse/trackpad checks remain before closure; no firmware deployment or hardware run is required. |
| 2026-08-12 | DEC-022, SEQ-G18 (validation pending) | Replaced raw Local Library Load/Trash rows with a conventional New/Open/Save Scene bar, secondary menu and searchable modal browser with current/recent/content context. Replacement now offers explicitly labelled save/discard/cancel actions and negotiates stopping Play/Pause while deferring Stop until every cancellation point succeeds. Nine focused browser/new/replacement/trash cases expanded the WPF suite to 181/181; Release build 352 succeeded and offline regression passed 19/19 (`b1-self-test-20260812-010808.json`). Browser layout, menu, search, double-click and shortcut checks remain for operator confirmation; no firmware deployment or hardware run is required. |
