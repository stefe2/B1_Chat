# Animation Sequencer — Hardening backlog

Status: implementation underway — first Safe Playback foundation batch
Created: 2026-08-11
Scope: WPF console Sequencer, console-side audio, serial/mesh animation dispatch,
and the small firmware changes needed to give playback safe stop semantics.

This document is the persistent source of truth for making the Animation
Sequencer reliable. It carries **only actionable work** — the 35 items that are
open, in progress or awaiting hardware validation.

Three companion documents hold the rest, so this one stays cheap to read:

- [SEQUENCER-BEHAVIOR.md](SEQUENCER-BEHAVIOR.md) — what currently *ships*, at
  runtime. Read it before changing Sequencer behavior.
- [SEQUENCER-DONE.md](SEQUENCER-DONE.md) — the 41 closed items with their
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
| A | Playback isolation and cancellation | 7 / 8 | — |
| B | Infinite gestures and Stop/Pause semantics | 6 / 6 | 0 / 1 |
| C | Dirty, Undo/Redo and editing transactions | 8 / 8 | 0 / 1 |
| D | Import, export and local library | 8 / 8 | 0 / 1 |
| E | Deterministic scheduler and performance | 5 / 6 | 0 / 1 |
| F | Duration and audio robustness | 6 / 8 | — |
| G | Preflight and ergonomics | 0 / 14 | 0 / 4 |
| H | Automated and hardware validation | 1 / 8 | — |
| I | Scene & Show System (future) | — | 0 / 22 |
| J | Commissioning and servo configuration safety | 0 / 2 | — |
| K | Project workspace (future) | — | 0 / 8 |

## EPIC A — Playback isolation and cancellation

7 completed items moved to [SEQUENCER-DONE.md](SEQUENCER-DONE.md): SEQ-A01, SEQ-A02, SEQ-A03, SEQ-A04, SEQ-A05, SEQ-A06, SEQ-A07.

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

5 completed items moved to [SEQUENCER-DONE.md](SEQUENCER-DONE.md): SEQ-E01, SEQ-E02, SEQ-E03, SEQ-E04, SEQ-E06.

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

6 completed items moved to [SEQUENCER-DONE.md](SEQUENCER-DONE.md): SEQ-F02, SEQ-F03, SEQ-F04, SEQ-F05, SEQ-F06, SEQ-F07.

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

### [~] SEQ-G14 — Redesign Play as an unambiguous Play/Pause control

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
- **Implemented:** the primary button and `Space` now expose Play/Pause/Resume
  through a state-dependent glyph and tooltip. A second Play pauses rather than
  resending choreography; `Restart`/`Ctrl+Enter` owns the explicit clean restart
  path. Existing generation and Loop safety tests now use that separate action.
  The safety hierarchy is visually explicit: E-STOP uses a permanent filled-red
  treatment, while Loop is a neutral editing mode that turns orange when active.
- **Remaining validation:** rendered toolbar/glyph/keyboard interaction check in
  the Release console.

### [~] SEQ-G15 — Separate Stop from playhead navigation and rewind

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
- **Implemented:** normal, Safe and Emergency Stop retain the measured cursor;
  non-looping natural completion retains the calculated end. A distinct
  return-to-start button/`Ctrl+Home` is enabled only while stopped. Play starts
  from a retained cursor and skips older events; at the natural end it starts a
  new pass from zero. Restart is the always-explicit performance-from-zero path.
- **Remaining validation:** rendered control ordering and rehearsal workflow
  check in the Release console.

### [~] SEQ-G16 — Add an operator-controlled Follow Playhead mode

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
- **Implemented:** a visible transient Follow toggle uses a 15–72% viewport
  comfort corridor and changes horizontal offset only. New Play/Restart enables
  it; manual scroll/zoom/pan suspends it, re-enabling catches up, Pause freezes
  it and Resume preserves a suspension. Automatic scroll destinations remain
  tagged until WPF's deferred `ScrollChanged` observes them; this prevents
  Follow from mistaking its own movement for manual navigation and turning
  itself off at the corridor boundary. Pure navigation and scroll-correlation
  math is unit-tested.
- **Remaining validation:** manual long-timeline smoothness and scrollbar test
  in the Release console.

### [~] SEQ-G17 — Add pointer-centered timeline wheel zoom

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
- **Implemented:** `Ctrl+wheel` applies a continuous multiplicative 1.15-per-notch
  zoom clamped to 20–300 px/s and restores the pointer's content time after WPF
  layout. `Shift+wheel` pans horizontally; plain wheel remains native. Slider,
  Fit and deliberate wheel navigation suspend Follow. The main page's tunneling
  wheel handler now yields modified wheel events originating in the timeline
  viewport; previously it consumed them before the nested timeline handler could
  run. Boundary, fractional wheel, pointer-anchor, routing and corridor
  calculations are unit-tested.
- **Remaining validation:** physical mouse/trackpad interaction check in the
  Release console.

### [~] SEQ-G18 — Replace the exposed library list with a Scene document workflow

- **Priority:** P1
- **Problem:** the Local Library is rendered below the entire timeline as raw
  Load/Trash rows. Finding and opening a Scene requires unexplained page
  scrolling, destructive actions are visually overexposed, and the workflow
  does not resemble familiar document or editing applications.
- **Depends on:** SEQ-D05 through SEQ-D08, DEC-003.
- **Acceptance:** the primary Scene bar exposes New, Open and Save; less common
  Save As, Rename, Import, Export and Trash actions move to a secondary menu.
  Open launches a searchable Scene browser with selection, double-click,
  current-Scene indication, last-save/content metadata and an empty state.
  Conventional Ctrl+N/O/S, Ctrl+Shift+S and F2 shortcuts work. Replacing a
  modified Scene offers save/continue, continue without saving, or cancel;
  active playback offers an explicit stop-and-continue decision rather than an
  unexplained disabled Open action. The same browser can later serve Add Scene
  in the Show editor.
- **Validation:** command tests cover browser selection/cancel/new, every dirty
  choice, save failure/cancel, active Play/Pause accept/cancel, current Scene
  deletion and startup restore; rendered keyboard, search, double-click,
  metadata, focus, empty state and narrow-window layout are manually checked.
- **Implemented:** the permanent Local Library list has been removed from below
  the timeline. A conventional document bar now provides New/Open/Save and a
  secondary Scene menu; the modal browser sorts by recent save, searches by
  name, marks the current Scene, summarizes gesture/audio content and supports
  double-click. Protected replacement can save, discard or cancel, and can
  explicitly stop an active/paused pass. Ctrl+N/O/S, Ctrl+Shift+S and F2 are
  wired at the Sequencer card level. Automated Scene/persistence coverage now
  includes the new browser and replacement paths.
- **Visual polish:** colored status pills now use crisp semantic borders without
  glow/blur, avoiding color bleed between dense droid rows and controls. Every
  application-owned WPF window now uses the common dark native title bar and
  themed content; the former programmatic Save As prompt was replaced by a
  dedicated themed Scene-name window. Native Windows file pickers remain native.
- **Remaining validation:** rendered browser, menu, shortcuts, double-click,
  search/empty state and Play/Pause replacement check in the Release console.

## EPIC H — Automated and hardware validation

1 completed item moved to [SEQUENCER-DONE.md](SEQUENCER-DONE.md): SEQ-H01.

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

### [~] SEQ-H05 — Cover audio and waveform services

- **Priority:** P1
- **Depends on:** SEQ-F03 through SEQ-F08, SEQ-H01.
- **Acceptance:** tests cover probe result types, timeout, lifecycle, concurrent
  players, stale waveform prevention, cache invalidation, missing files, and
  audio loop endpoint behavior.
- **Validation:** service suite plus a Windows Media Foundation smoke test.
- **Implemented:** 37 tests in `console.tests/AudioServiceTests.cs` covering every
  probe outcome (success, missing, empty path, decode failure, no timespan, valid
  zero length, timeout, cancellation before and during, throwing Open, file removed
  after the check), the playback lifecycle (natural end, loop, failure reported
  once, missing file, throwing start, concurrent clips, resume after one clip
  ended, resume without pause, idempotent Stop, pause/stop/resume) and the waveform
  cache (single decode, same-path content change, missing file, retry after
  failure, retry after the file appears, bounded capacity, empty path), plus the
  stale-assignment race driven through the view model. A committed MP3 fixture is
  decoded by NAudio for real, asserting a rising envelope so a broken bucket
  mapping fails the suite. `tools/self-test.ps1` gained an audio invariant check
  and a Media Foundation presence check.
- **Remaining:** audio loop endpoint coverage, which cannot exist before SEQ-F08
  defines that endpoint. The MediaPlayer half of the smoke test needs a dispatcher
  and stays in `self-test.ps1` rather than the headless suite.

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
11. **Optional enhancements:** only selected `[D]` items. EPIC I (Scene/Show)
    and EPIC K (Project workspace), in
    [SEQUENCER-IDEAS.md](SEQUENCER-IDEAS.md), remain deferred until the M1–M4
    reliability baseline is complete and their shared design is approved.

Steps 1 through 7 are complete and step 8 is most of the way there — only SEQ-F08
(blocked on SEQ-E05) and SEQ-F01's bench measurement remain. Closed items are in
[SEQUENCER-DONE.md](SEQUENCER-DONE.md); live work is now step 9, preflight and
ergonomics.

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

SEQ-D09 remains deferred. Transport/navigation UX (SEQ-G14 through SEQ-G17) and
the Scene document workflow (SEQ-G18) are implemented with rendered validation
pending, followed by sequence/audio end semantics (SEQ-F08 then SEQ-E05).

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
| DEC-019 | Resolved 2026-08-12 | Primary Play is a Play/Pause/Resume toggle (`Space`), while Restart (`Ctrl+Enter`) is the explicit clean from-zero action. Stop/Safe/Emergency retain the playhead; Return to start (`Ctrl+Home`) is separate. Stopped Play begins at the retained cursor and skips prior events, except Play at natural end begins from zero. |
| DEC-020 | Resolved 2026-08-12 | Follow defaults on for each new Play/Restart and uses a 15–72% viewport comfort corridor. Pause freezes it; Resume preserves its state. Manual horizontal scroll, Fit or zoom/pan suspends it, and the visible Follow toggle re-enables it with immediate catch-up. |
| DEC-021 | Resolved 2026-08-12 | Ctrl+wheel zooms continuously and multiplicatively around the pointer within 20–300 px/s; Shift+wheel pans horizontally and plain wheel remains native. Slider/Fit/wheel navigation suspends Follow until the operator re-enables it. |
| DEC-022 | Resolved 2026-08-12 | Treat Scenes like conventional editor documents: New/Open/Save are primary, Local Library storage stays behind a searchable Open browser, Save As/rename/import/export/trash are secondary, and replacement explicitly handles active playback plus save/discard/cancel. The browser is reusable by the future Show editor. |
| DEC-023 | Direction recorded 2026-08-12; details deferred | Add a Project mode as the mutable working boundary for one production: manifest, Scenes, Shows and managed assets live under one movable folder with relative references. A Project remains distinct from the immutable portable package created by Publish; Show mode must arm the published package, not mutable Project drafts. |
