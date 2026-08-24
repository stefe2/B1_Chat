# Animation Sequencer — Hardening backlog

Status: implementation underway — first Safe Playback foundation batch
Created: 2026-08-11
Scope: WPF console Sequencer, console-side audio, serial/mesh animation dispatch,
and the small firmware changes needed to give playback safe stop semantics.

This document is the persistent source of truth for making the Animation
Sequencer reliable. It carries **only actionable work** — the 22 items that are
open, in progress or awaiting hardware validation.

The product direction changed on 2026-08-15: development may now break old
gesture IDs, Scene schemas and runtime protocol generations, and the Sequencer
plus a predefined gesture catalog becomes the primary application workflow.
[GESTURE-SEQUENCER-V2.md](GESTURE-SEQUENCER-V2.md) owns that approved target and
its stage gates. This backlog still owns unfinished reliability work in the
current implementation; do not assume its compatibility/migration items are V2
requirements unless the V2 plan explicitly retains them.

Three companion documents hold the rest, so this one stays cheap to read:

- [SEQUENCER-BEHAVIOR.md](SEQUENCER-BEHAVIOR.md) — what currently *ships*, at
  runtime. Read it before changing Sequencer behavior.
- [SEQUENCER-DONE.md](SEQUENCER-DONE.md) — the 55 closed items with their
  acceptance criteria and the completion evidence log.
- [SEQUENCER-IDEAS.md](SEQUENCER-IDEAS.md) — EPIC I and EPIC K, 30 deferred
  design ideas gated behind the M1–M4 baseline.

`PROGRESS-ARCHIVE.md` remains the chronological project record. This file must
stay current as items are implemented: when an item closes, move its entry to
`SEQUENCER-DONE.md` and update the dashboard here.

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

The dashboard is updated whenever an item changes state. It stays complete —
counting items that now live in [SEQUENCER-DONE.md](SEQUENCER-DONE.md) and
[SEQUENCER-IDEAS.md](SEQUENCER-IDEAS.md) — so this table alone answers "how far
along is the Sequencer".

| Epic | Description | Required items complete | Deferred ideas complete |
|---|---|---:|---:|
| A | Playback isolation and cancellation | 7 / 7 | 0 / 1 |
| B | Infinite gestures and Stop/Pause semantics | 6 / 6 | 0 / 1 |
| C | Dirty, Undo/Redo and editing transactions | 8 / 8 | 0 / 1 |
| D | Import, export and local library | 8 / 8 | 0 / 1 |
| E | Deterministic scheduler and performance | 6 / 6 | 0 / 1 |
| F | Duration and audio robustness | 7 / 8 | — |
| G | Preflight and ergonomics | 11 / 12 | 0 / 7 |
| H | Automated and hardware validation | 5 / 8 | — |
| I | Scene & Show System (future) | — | 0 / 22 |
| J | Commissioning and servo configuration safety | 0 / 2 | — |
| K | Project workspace (future) | — | 0 / 8 |

## EPIC A — Playback isolation and cancellation

7 completed items moved to [SEQUENCER-DONE.md](SEQUENCER-DONE.md): SEQ-A01, SEQ-A02, SEQ-A03, SEQ-A04, SEQ-A05, SEQ-A06, SEQ-A07.

### [D] SEQ-A08 — Add explicit Edit, Ready, Armed and Playing lifecycle states

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
- **Deferred direction (2026-08-14):** the current editor deliberately keeps
  Play direct and Preflight advisory. Reconsider explicit arming only with a
  separately approved future performance/Show mode, not as editor ceremony.

## EPIC B — Infinite gestures and Stop/Pause semantics

6 completed items moved to [SEQUENCER-DONE.md](SEQUENCER-DONE.md): SEQ-B01, SEQ-B02, SEQ-B03, SEQ-B04, SEQ-B06, SEQ-B07.

### [D] SEQ-B05 — Link TALK duration to an audio clip

- **Priority:** P3
- **Problem:** synchronizing TALK to audio is presently manual.
- **Depends on:** SEQ-B04, SEQ-F08.
- **Acceptance:** a user can link TALK to a chosen audio clip so it starts and
  terminates with that clip, with a visible relationship and deterministic
  behavior when the audio loops or is missing.
- **Validation:** UI and playback integration test.

## EPIC C — Dirty, Undo/Redo and editing transactions

8 completed items moved to [SEQUENCER-DONE.md](SEQUENCER-DONE.md): SEQ-C01, SEQ-C02, SEQ-C03, SEQ-C04, SEQ-C05, SEQ-C06, SEQ-C07, SEQ-C08.

### [D] SEQ-C09 — Add recoverable draft autosave

- **Priority:** P3
- **Problem:** unexported edits are lost on crash or restart; the last exported
  file is reloaded instead.
- **Depends on:** SEQ-C05, SEQ-D04.
- **Acceptance:** a separate atomic draft does not overwrite the user's export;
  startup offers recovery only when the draft is newer and Dirty.
- **Validation:** crash/restart and stale-draft tests.

## EPIC D — Scene import, external copies and local library

8 completed items moved to [SEQUENCER-DONE.md](SEQUENCER-DONE.md): SEQ-D01, SEQ-D02, SEQ-D03, SEQ-D04, SEQ-D05, SEQ-D06, SEQ-D07, SEQ-D08.

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

6 completed items moved to [SEQUENCER-DONE.md](SEQUENCER-DONE.md): SEQ-E01, SEQ-E02, SEQ-E03, SEQ-E04, SEQ-E05, SEQ-E06.

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

7 completed items moved to [SEQUENCER-DONE.md](SEQUENCER-DONE.md): SEQ-F02, SEQ-F03, SEQ-F04, SEQ-F05, SEQ-F06, SEQ-F07, SEQ-F08.

### [H] SEQ-F01 — Model finite, immediate and infinite gesture duration correctly

> Superseded by Gesture Sequencer V2 stage 2C on 2026-08-15: global speed
> configuration and timing jitter were removed. Finite gestures now use their
> fixed catalog nominal duration. The historical implementation notes below are
> retained only to explain prior tests and measurements.

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

## EPIC G — Preflight and ergonomics

11 completed items moved to [SEQUENCER-DONE.md](SEQUENCER-DONE.md): SEQ-G01,
SEQ-G02, SEQ-G03, SEQ-G04, SEQ-G05, SEQ-G06, SEQ-G14, SEQ-G15, SEQ-G16,
SEQ-G17, SEQ-G18.

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

### [D] SEQ-G12 — Analyze mechanical workload before playback

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
- **Deferred direction (2026-08-14):** do not expand the current manual Scene
  checker until real production use demonstrates a need and usable thresholds.

### [D] SEQ-G13 — Preflight the host PC and audio environment

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
- **Deferred direction (2026-08-14):** live host readiness is outside the
  current Scene-content checker. Revisit only for a future performance mode.

### [D] SEQ-G19 — Add a temporary In/Out playback range

- **Priority:** P3
- **Problem:** rehearsing a short excerpt requires moving the playhead manually
  and stopping at the desired boundary. The authored Scene endpoint and the
  future persistent marker/loop-region model in SEQ-G07 should not be changed
  merely to audition part of a Scene.
- **Depends on:** SEQ-E05, SEQ-G14, SEQ-G15.
- **Acceptance:** two clearly labelled, draggable vertical handles define `IN`
  and `OUT`, with the selected playback range shaded without hiding clips. The
  operator can set either boundary at the playhead, drag it with existing snap
  rules, clear the range and operate it by keyboard. Play and Restart begin at
  `IN`; natural range completion stops at `OUT`, or repeats `IN → OUT` when the
  existing whole-pass Loop mode is active. Clearing the range restores normal
  full-Scene playback. The range is editor-session state only: it does not alter
  clips or the authoritative Scene endpoint, is excluded from Dirty, Undo/Redo,
  Save and Export, and resets when the document is replaced. Playback from `IN`
  follows the existing play-from-cursor rule: prior gestures are skipped, while
  an audio clip overlapping `IN` seeks to the matching source offset. No past
  mechanical state is reconstructed implicitly.
- **Validation:** drag and keyboard operation, snapping/minimum width, boundaries
  near the start/middle/end, Play/Restart/Pause/Stop/Loop, clear/document replace,
  and gesture/audio clips that overlap `IN` or `OUT`.

## EPIC H — Automated and hardware validation

2 completed items moved to [SEQUENCER-DONE.md](SEQUENCER-DONE.md): SEQ-H01, SEQ-H05.

### [x] SEQ-H02 — Cover edit transactions, Dirty and history

- **Priority:** P1
- **Depends on:** SEQ-C01 through SEQ-C06, SEQ-H01.
- **Acceptance:** automated matrix covers every persistent/transient property,
  no-op edits, capacity, saved checkpoints, Undo/Redo, and derived extent.
- **Validation:** all matrix rows pass and fail if their corresponding behavior is
  intentionally broken.
- **Implemented (2026-08-24):** `SequencerPlaybackIntegrationTests.EditTransactionMatrix_CommitsOneUndoableChangeAndOneDerivedRefresh`
  already covered every currently-editable persistent property (name, loop,
  end, gesture insert/anim/target/nudge/infinite-end/duplicate/delete/drag,
  audio lane/clip add/replace/loop/move/delete/drag, clear timeline) plus
  no-op edits, Undo/Redo, saved checkpoints and the derived timeline extent.
  `SequencerGridSnapTests` closes the one real gap found: `RoundToGrid` (the
  snap-to-grid pure function) had no direct coverage. Undo/Redo history
  capacity (50 entries) is covered by `UndoAndRedoHistory_RetainExactlyTheNewestFiftyEditsInOrder`;
  interactive document size (steps/tracks/clips) is deliberately unbounded
  from the ViewModel, unlike import, since it reflects trusted live operator
  action rather than an untrusted file — `SequenceImportService`'s
  `MaxSteps`/`MaxTracks`/`MaxAudioLanes`/`MaxAudioClips` guard the untrusted
  path and are already covered under SEQ-H04.
- **Known scope boundary, not a gap:** `SequenceStep.GestureKey`/`Intensity`/
  `Tempo`/`Variant`/`Seed` are persistent V2 fields with no editing command in
  the ViewModel yet (only `AnimId`/`Target`/timing are editable today) — they
  round-trip through DTO code but cannot be edited interactively. Wiring them
  is Stage 6 of `GESTURE-SEQUENCER-V2.md`, not a Sequencer-hardening gap; add
  their edit/Undo/Dirty coverage alongside that stage's editor work instead of
  here.

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
  levels and arming invalidation are also covered. Endpoint and B04 duration-end
  semantics are complete; the explicit Ready/Armed lifecycle remains open with
  SEQ-A08.

### [x] SEQ-H04 — Cover persistence validation and migrations

- **Priority:** P1
- **Depends on:** SEQ-D01 through SEQ-D05, SEQ-H01.
- **Acceptance:** golden round trips for every schema plus malformed, partial,
  overflow, unsupported and failed-write cases; current document is preserved on
  every failure.
- **Validation:** fixture suite passes on a clean machine path.
- **Implemented (2026-08-24):** golden round trips, malformed/overflow/unsupported
  rejection and preserve-on-failure coverage already existed for the V2 Scene
  schema (`GestureSceneV2SchemaTests.cs`), the local Scene Library
  (`SceneLibraryTests.cs`) and Export (`SequencerPersistenceTests.cs`), all via
  an injectable failing writer. `SequencerPersistenceFilesystemTests.cs` closes
  the remaining gap by exercising the real `AtomicTextFileWriter` against real
  filesystem conditions instead of an injected exception: a missing destination
  directory, a successful round trip leaving no leftover temp file, a genuinely
  invalid path (illegal filename character), and a stray `.tmp` left behind by
  an earlier interrupted write, which the current implementation intentionally
  neither treats as an obstacle nor sweeps up (documented, not changed, since
  no code currently reconciles orphaned temp files). `GestureSceneV2SchemaTests.cs`
  gained `Catalog_RejectsAnUnsupportedFutureVersion` and
  `Scene_RejectsAnUnsupportedFutureVersion` for the "unsupported" case at the
  current schema generation, mirroring the legacy importer's existing
  future-version case.
- **Follow-up worth a separate decision, not done here:** `SequenceImportService.Parse`
  (the legacy `b1-sequence` v1-v6 parser, with its ~15-test golden/migration
  suite in `SequenceImportServiceTests.cs`) is no longer called from
  `SequencerViewModel.ImportFrom` — the shipped Import command only ever calls
  `GestureSceneV2Persistence.ParseFile`. That legacy coverage is exercising a
  parser the UI can no longer reach; per DEC-007 (`.b1seq.json` has no
  migration path), this may be safe to delete rather than maintain, but that is
  a separate cleanup decision, not a SEQ-H04 gap.

### [x] SEQ-H06 — Add UI interaction smoke tests/checklists

- **Priority:** P2
- **Depends on:** SEQ-C07, SEQ-G01 through SEQ-G06.
- **Acceptance:** repeatable checks cover drag/capture loss, Snap, Fit, arming,
  broadcast confirmation, disabled controls, preflight navigation, inspector,
  context menus, and accessibility via keyboard where supported.
- **Validation:** automated UI tests where stable; otherwise versioned manual
  checklist with evidence.
- **Implemented (2026-08-24, two passes):** the project's first real
  UI-automation infrastructure, via FlaUI/UIA3 driving the compiled
  `b1-chat-console.exe` (`console.tests/UiAutomationFixture.cs`,
  `UiAutomationSmokeTests.cs`, `UiAutomationSmokeTests2.cs`) rather than the
  prior static-XAML-inspection style. One shared launched instance per test
  run, collection-level parallelization disabled, and (added in the second
  pass) `DisableTestParallelization` at the assembly level after cross-
  collection CPU contention was found to make synthetic clicks silently miss
  under full-suite load (see `docs/KNOWN-PITFALLS.md`). 29 tests.
  **Reliability update (2026-08-24):** the "verified reliable, no residual
  flakiness" claim below did not hold up under further full-suite repetition.
  `OverlappingClips_TheShorterTopmostClipReceivesTheClick` had a real,
  reproducible flake (~4 of 5 full-suite runs) traced to this environment
  rendering the same launched window at a real physical display scale that
  is not constant between launches — not the DPI-scale-constant fix
  originally credited here. Rewritten to walk to the click target
  empirically instead of predicting it from a DPI constant (see
  `docs/KNOWN-PITFALLS.md`'s "UI test automation" section for the full
  investigation); this cut the failure rate to roughly 1 in 14 full-suite
  runs. That residual is a known, accepted limitation, not confirmed fully
  eliminated — investigation was deliberately stopped there as disproportionate
  effort for a P2 item.
  Real, passing coverage now spans every item in the acceptance list except
  Calibration's own panel (see below): app launch/window identity; disabled
  controls (Undo/Redo, and the real — not assumed — Visibility-gated, not
  disabled, contract for Delete/Duplicate/Regenerate); Snap and Fit (zoom
  state changes, not a pinned formula); right-click context menus (clip
  Duplicate/Delete, Scene "Save As…"); Delete/Copy/Paste/Undo/Redo via real
  keyboard shortcuts; a real synthetic mouse drag moving a clip's `StartMs`;
  Preflight open/close (re-examined and confirmed safe — DEC-024 establishes
  it never dispatches anything); the Firmware panel's port combo and every
  local-only control (Rescan, role toggle, Advanced options — never Flash);
  the read-only Mesh Topology panel; the Scene Library browser and Scene name
  dialog (both exercised without touching the real Local Library on disk);
  the Help window; a rich tooltip rendering real text, not a stringified type
  name (regression guard for the `ContentPresenter` tooltip fix); and the
  shorter-clip-wins-the-click overlap/z-order behavior (regression guard for
  the `ItemContainerStyle`/`Panel.ZIndex` fix), including the geometric sanity
  check that it's real position-based hit-testing and not merely
  "last-inserted always wins." Two `x:Name` additions in
  `SequenceTimelineView.xaml` (mirrored to `AutomationId`, this project's
  existing convention) were the only production changes needed.
- **Two real bugs found and fixed while building this coverage, beyond the
  GESTURE-combo `ToString()` bug already logged under SEQ-H02/KNOWN-PITFALLS:**
  a `ResetToCleanNewScene` test-fixture bug (searched for a discard dialog via
  `GetAllTopLevelWindows`, which never lists this app's owned windows in this
  environment — see `docs/KNOWN-PITFALLS.md`), and a DPI-scale unit mismatch
  in one geometry test's own click-point math (logical WPF pixels added
  directly to a physical FlaUI screen coordinate) — both fixed, both
  documented in `docs/KNOWN-PITFALLS.md` under "UI test automation" so future
  test authors don't repeat either.
- **Deliberately not covered, with reasons (see the test files' trailing
  remarks for detail):** the Calibration panel/window — unlike Firmware and
  Help, it has no unconditional entry point (only opens pre-targeted at a
  live Droid row) and every one of its controls sends a real Preview/SetCalib
  movement or `RequestCalib` command to hardware the instant it's touched, so
  there is no safe, hardware-state-independent path to it through the real
  UI; arming (SEQ-A08's Edit/Ready/Armed/Playing lifecycle is deferred and
  doesn't exist to test — arming a *track*, a different, already-implemented
  concept, is exercised via `ArmBroadcastTrack`); broadcast confirmation,
  Play/Restart/Stop, SAFE/E-STOP (this dev machine's console auto-reconnects
  to a real droid fleet over USB serial on launch, so exercising anything
  that dispatches or risks dispatching a real anim/audio/servo command
  belongs to the hardware-gated SEQ-H07 protocol, not this local smoke
  suite); a dedicated "inspector" panel distinct from the Sequencer's own
  "SELECTED CLIP" panel (which the Category A/E tests above already exercise
  directly) was not otherwise found.

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

## EPIC J — Commissioning and servo configuration safety

These requirements were identified while resolving the Scene Library model.
They are cross-cutting firmware/console safeguards rather than Scene features,
but remain in this tracked plan so they cannot be lost between backlog batches.

### [H] SEQ-J01 — Make a virgin ESP32 inert by default

- **Priority:** P0
- **Problem:** an erased/new slave previously defaulted Servos and Auto anims to
  ON, and the servo engine emitted a center PWM pulse before the servo preference
  loaded. Locate already began OFF but had no explicit regression contract.
- **Acceptance:** on missing NVS keys, Servos and Locate are OFF for both roles;
  PWM stays detached throughout startup. Normal updates/reboots
  preserve choices that an operator already stored.
- **Validation:** master/slave builds and source invariants, then full-erase one
  bench ESP32 and observe no servo PWM/motion or Locate override before enabling
  them explicitly.
- **Implemented:** Auto anims were removed in V2 stage 2B. Missing NVS state
  resolves Servos to OFF for both roles; the servo engine starts detached and
  never emits its former pre-preference center pulse. Locate starts OFF and is now included in live
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
9. **Preflight/ergonomics:** required SEQ-G01 through SEQ-G06, SEQ-G11 and
   SEQ-G14 through SEQ-G18. SEQ-G12/G13 are deferred with the strict lifecycle.
10. **Validation gate:** SEQ-H02 through SEQ-H08.
11. **Optional enhancements:** only selected `[D]` items. EPIC I (Scene/Show)
    and EPIC K (Project workspace), in
    [SEQUENCER-IDEAS.md](SEQUENCER-IDEAS.md), remain deferred until the M1–M4
    reliability baseline is complete and their shared design is approved.

Steps 1 through 7 are complete. Step 8 is code-complete; only SEQ-F01's bench
measurement remains. Step 10 (validation gate): SEQ-H02, SEQ-H04 and SEQ-H06
are complete (2026-08-24, see SEQ-H06's entry for what's covered vs.
deliberately out of automated reach — Calibration's hardware-only panel);
SEQ-H03/H07 remain partially complete pending hardware; SEQ-H08 (final
regression/release gate) is not started. Closed items are in
[SEQUENCER-DONE.md](SEQUENCER-DONE.md). Further Sequencer feature work is
deliberately paused; remaining work is validation or individually approved need.

### Immediate implementation batches

Completed persistence and scheduler work is grouped by shared responsibility
and dependency. Each batch remains independently testable and receives one
detailed commit after its full regression passes.

| Batch | Items | Scope and commit boundary |
|---|---|---|
| P1 — Safe import pipeline | SEQ-D01, SEQ-D02, SEQ-D03 | Parse into a temporary document, validate schema/content/bounds, migrate every supported legacy version, then apply once. One fixture-driven import commit. |
| P2 — Saved-state integrity | SEQ-C05, SEQ-D04, SEQ-D05 | Implement the saved checkpoint and atomic export together, then use that authoritative Dirty state to guard Import and library Load. C05 and D04 are intentionally one batch because their stated dependencies are circular. |
| Decision gate | DEC-003 | Resolved: retain the Local Library as the normal Scene store; keep Export only as an explicit external-copy escape hatch. |
| P3 — Scene Library and wording | SEQ-D06, SEQ-D07, SEQ-D08 | **Complete.** Scene Save/Save As uses stable IDs and atomic/versioned V2 storage; legacy entries are reported as incompatible, deletion is recoverable, and naming/source/Dirty badges plus Help agree. External Export remains clearly secondary. |
| S1 — Single deterministic scheduler | SEQ-E02, SEQ-E03 | **Complete.** One rearmable timer drains monotonic timestamp batches in immutable source order, compensates late wakes, warns about same-target/broadcast overlap and releases completely on cancellation. |
| T1 — Coherent duration and infinite ends | SEQ-F01, SEQ-F02, SEQ-C06, SEQ-B04 | **Code complete; F01 hardware measurement pending.** Structured firmware timing metadata feeds one target-aware provider and cached extent; schema v5 promotes looping-gesture width into a persisted endpoint with ownership-safe IDLE termination. |

SEQ-D09 remains deferred. The Play/Pause control and the complete transport,
Follow, wheel-navigation and Scene-document workflow batch (SEQ-G14 through
SEQ-G18) are closed. Sequence/audio end semantics (SEQ-E05 and SEQ-F08) and their
audio-service coverage (SEQ-H05) are also closed after rendered Release validation.
The first preflight batch (SEQ-G01, SEQ-G02 and SEQ-G04) is closed after its
rendered operator pass. The conflict, explicit-target and final content/control
batch (SEQ-G03, SEQ-G05 and SEQ-G06) is also closed after full regression and
rendered Release inspection.

## Decision log

Record decisions here before implementing behavior with multiple reasonable
options.

| ID | Status | Decision |
|---|---|---|
| DEC-001 | Resolved 2026-08-11 | Lock persistent editing during Play/Pause; allow transient inspection, zoom/scroll and dynamic track mute. |
| DEC-002 | Resolved 2026-08-11 | Normal Stop uses targeted tracked IDLE only for Sequencer-owned infinite gestures. Safe Stop broadcasts a transient centered/servo-powered hold. Emergency Stop immediately broadcasts persistent Servo OFF without confirmation; the owner accepts loss of holding torque. |
| DEC-003 | Resolved 2026-08-11 | Treat the current Sequencer document as a Scene and retain Local Library as the normal local working catalog. Save updates the current stable scene ID; Save As creates a new one. Import never auto-adds. Export is not required for normal work, but remains an explicit external snapshot for backup, transfer, support and version control. Future Show authoring combines scenes and published Shows embed immutable scene snapshots. |
| DEC-004 | Superseded by DEC-024 | An active targeted gesture whose droid is unknown/offline was initially a blocking preflight error. The later advisory-only policy removes live connectivity from Preflight entirely. |
| DEC-005 | Resolved 2026-08-11 | Same-time events form one batch in immutable source order: gesture clips in editor order, then audio clips by lane/clip order. Multiple gestures for one target are last-received-wins. Broadcast plus targeted overlap is serialized but warned because mesh arrival can differ from console order. |
| DEC-006 | Superseded by DEC-024 | Audio-only Scenes were the first disconnected exception. Play is now independent of Preflight for every Scene; actual sends report their runtime result on each gesture clip. |
| DEC-007 | Resolved 2026-08-16 | Scene persistence is `b1-scene` V1 with stable library GUIDs and exact catalog identity. `.b1seq.json` is incompatible; no migration path exists. |
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
| DEC-019 | Resolved 2026-08-12; refined 2026-08-14 | Primary Play is a Play/Pause/Resume toggle (`Space`), while Restart (`Ctrl+Enter`) is the explicit clean from-zero action. Stop/Safe/Emergency retain the playhead; Return to start (`Ctrl+Home`) is separate. Stopped Play begins at the retained cursor: prior gestures are skipped, overlapping audio seeks to its matching offset, and Play at natural end begins from zero. Moving the playhead during Pause performs normal Stop cleanup and leaves transport Stopped at the new time; an unchanged click preserves Resume. |
| DEC-020 | Resolved 2026-08-12; refined 2026-08-13 | Follow defaults on for each new Play/Restart and uses a 15–72% viewport comfort corridor. Pause freezes it; Resume preserves its state. A horizontal scrollbar drag suspends Follow only while held and catches up on release; Fit or deliberate zoom/pan suspends it until the visible toggle re-enables it. |
| DEC-021 | Resolved 2026-08-12 | Ctrl+wheel zooms continuously and multiplicatively around the pointer within 20–300 px/s; Shift+wheel pans horizontally and plain wheel remains native. Slider/Fit/wheel navigation suspends Follow until the operator re-enables it. |
| DEC-022 | Resolved 2026-08-12 | Treat Scenes like conventional editor documents: New/Open/Save are primary, Local Library storage stays behind a searchable Open browser, Save As/rename/import/export/trash are secondary, and replacement explicitly handles active playback plus save/discard/cancel. The browser is reusable by the future Show editor. |
| DEC-023 | Direction recorded 2026-08-12; details deferred | Add a Project mode as the mutable working boundary for one production: manifest, Scenes, Shows and managed assets live under one movable folder with relative references. A Project remains distinct from the immutable portable package created by Publish; Show mode must arm the published package, not mutable Project drafts. |
| DEC-024 | Resolved 2026-08-14 | Preflight is an operator-requested, advisory Scene-content check. While its panel is open, relevant timeline edits refresh the findings in place and only a second click on the toolbar's Preflight button hides it; while closed, no scan runs. It has no persistent toolbar badge, never gates Play/Restart/Resume, and deliberately ignores port, handshake, master and online-droid state. Live dispatch/execution feedback owns connection failures. Strict readiness/arming is deferred to a future explicitly approved performance mode. |
| DEC-025 | Resolved 2026-08-15 | Treat the next Sequencer/gesture generation as a coordinated development reset: no compatibility obligation for old movement definitions/IDs, `b1-sequence` documents or permanent mixed firmware generations. The new product centers on the Sequencer and a predefined catalog; the Animation card, Auto anims and global freq/amp/speed settings are removal candidates. Persisted keys are never reinterpreted, and only a bounded transition mechanism needed to update the complete fleet may temporarily bridge generations. Detailed stage gates live in `GESTURE-SEQUENCER-V2.md`. |
