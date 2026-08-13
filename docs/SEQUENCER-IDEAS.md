# Animation Sequencer — deferred design ideas

EPIC I (Scene & Show System) and EPIC K (Project workspace): 30 items that are
deliberately **not** backlog. They are gated behind the M1–M4 reliability
baseline and an approved shared design, and they must not expand or reorder
the current hardening work.

Split out of [SEQUENCER-HARDENING.md](SEQUENCER-HARDENING.md) on 2026-08-13.
They are kept in full for one reason: they record architectural choices to
avoid making harder later, so the reliability work stays compatible with them.
Read this file when designing something structural — not when picking up the
next task.

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

## EPIC K — Project workspace (entirely deferred)

This epic adds a **Project mode** that keeps the mutable authoring material for
one production in one explicit folder. A Project is not a published Show
package: the Project remains editable and may contain drafts, while publishing
produces a frozen, validated output through SEQ-I08 and SEQ-I21. Project work is
deferred and must not interrupt the current reliability baseline.

### [D] SEQ-K01 — Define the Project domain and versioned manifest

- **Priority:** P3
- **Problem:** Scenes currently live in the application Local Library while
  linked audio and future Shows can live anywhere on the PC, so one production
  has no clear working boundary.
- **Depends on:** DEC-003, SEQ-I01, SEQ-I03.
- **Acceptance:** define a versioned `.b1project.json` manifest with stable
  project ID/name, schema version, authoring metadata and relative references to
  Scenes, Shows and assets. Unknown future versions fail safely and migrations
  never partially mutate the source project.
- **Validation:** golden round-trip, migration, unknown-version, malformed and
  interrupted-write fixtures.

### [D] SEQ-K02 — Establish a predictable Project folder layout

- **Priority:** P3
- **Problem:** simply placing files in one directory is insufficient if generated
  files, source assets and recoverable drafts cannot be distinguished.
- **Depends on:** SEQ-K01.
- **Acceptance:** define a human-readable layout such as
  `Scenes/`, `Shows/`, `Assets/Audio/`, `Backups/` and a rebuildable local cache.
  Authored source files are never hidden in the cache; temporary and generated
  content cannot overwrite source material.
- **Validation:** create/open/move/copy fixtures on paths containing spaces,
  accents and long names; verify that deleting the cache loses no authored work.

### [D] SEQ-K03 — Add New/Open/Close/Save Project workflow

- **Priority:** P3
- **Problem:** a workspace needs an application-level lifecycle rather than an
  implicit collection of unrelated recent files.
- **Depends on:** SEQ-K01, SEQ-K02, SEQ-G18.
- **Acceptance:** provide conventional New Project, Open Project, Close Project
  and recent-project actions; show the active Project prominently; protect
  modified Scenes/Shows before switching; restore the last valid Project only
  when safe and offer a clear no-Project mode.
- **Validation:** clean/dirty/open/cancel/missing/recent/startup matrices plus
  keyboard and rendered-window checks.

### [D] SEQ-K04 — Scope Scene and Show libraries to the active Project

- **Priority:** P3
- **Problem:** a global Local Library becomes ambiguous when several productions
  contain similarly named Scenes or different revisions.
- **Depends on:** SEQ-K03, SEQ-I04.
- **Acceptance:** Scene Open/Save and future Show Open/Save default to the active
  Project. Import/copy from the global legacy library is explicit, preserves
  stable identities where safe, resolves conflicts without overwrite and leaves
  the existing library usable when no Project is open.
- **Validation:** two-project isolation, same-name/stable-ID conflict, legacy
  import, project switch and no-Project compatibility tests.

### [D] SEQ-K05 — Manage project assets with relative paths

- **Priority:** P3
- **Problem:** absolute audio paths break when a Project folder is moved or
  copied, while silently copying every chosen file can create confusing
  duplicates.
- **Depends on:** SEQ-F05, SEQ-K02.
- **Acceptance:** importing an asset defaults to a managed copy under
  `Assets/`, with an explicit advanced option to link externally. Project-owned
  references are relative and path traversal outside the root is rejected.
  Hashes identify duplicates and source changes without claiming that filenames
  alone are identities.
- **Validation:** copy/move/rename, duplicate-content, same-name collision,
  external-link, traversal and read-only-source cases.

### [D] SEQ-K06 — Detect, relink and audit missing or changed assets

- **Priority:** P3
- **Problem:** a Project can open successfully while audio has been deleted,
  moved or replaced behind its back.
- **Depends on:** SEQ-K05, SEQ-G02.
- **Acceptance:** opening and Preflight distinguish missing, changed and external
  assets; offer locate/relink with a preview of every affected Scene/Show; never
  silently accept a different file; record deliberate relinks in Project
  metadata/history.
- **Validation:** missing/changed/restored assets, bulk relink, wrong hash,
  cancel and one-asset-used-by-many-scenes cases.

### [D] SEQ-K07 — Make Project persistence atomic and recoverable

- **Priority:** P3
- **Problem:** a multi-file workspace can be left internally inconsistent by a
  crash, full disk or interrupted save.
- **Depends on:** SEQ-C09, SEQ-D04, SEQ-K01 through SEQ-K05.
- **Acceptance:** use atomic per-file writes plus a Project transaction/journal
  boundary, bounded backups and crash recovery that identifies exactly which
  documents were recovered. Autosave never overwrites the last explicit save,
  and cleanup cannot remove the only recoverable copy.
- **Validation:** injected failure at every transaction phase, crash/restart,
  full disk, access denial, stale recovery and backup-retention tests.

### [D] SEQ-K08 — Keep working Projects distinct from published packages

- **Priority:** P3
- **Problem:** an editable Project folder may contain drafts, unused assets,
  external links and caches that must not become performance input by accident.
- **Depends on:** SEQ-I08, SEQ-I21, SEQ-K01 through SEQ-K07.
- **Acceptance:** Publish consumes an explicit Project revision but outputs a
  separate immutable Show package containing only resolved Scenes/assets and
  hashes. The UI clearly labels Project/Draft versus Published; Show mode never
  arms a mutable Project directly. Publishing reports unused, missing, external
  and changed assets before creating output.
- **Validation:** publish then edit Project, transfer output to another PC,
  rollback published revision, unused-asset exclusion and tamper tests.
