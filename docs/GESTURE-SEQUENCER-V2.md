# B1 Chat — Gesture Catalog and Sequencer V2

Status: stages 1–6 complete; Stage 7 (new catalog content) first batch built, bench audition pending
Approved: 2026-08-15

This document owns the target product direction, breaking-development policy and
stage gates for rebuilding B1 Chat around the Sequencer and a predefined gesture
catalog. It describes the approved destination and work order, not behavior that
already ships. [SEQUENCER-BEHAVIOR.md](SEQUENCER-BEHAVIOR.md) and
[PROTOCOL-REFERENCE.md](PROTOCOL-REFERENCE.md) remain authoritative for the
current runtime until an implemented stage updates them.

Every stage is discussed and approved before implementation. A stage may be
split, reordered, deferred or removed, but its stated dependencies and safety
gates remain in force.

## Product direction

The Sequencer is the heart of B1 Chat. The primary authoring workflow is:

1. choose a predefined gesture from a rich catalog;
2. place it on an explicitly targeted timeline track;
3. adjust a small set of clip-local properties;
4. audition safely or play the Scene;
5. see dispatch and execution results.

The design favors ease of use and predictable choreography over a general
end-user keyframe editor. Calibration, mesh topology and firmware management are
supporting tools, not peers competing with the Sequencer for the main workspace.

All physical motion must have an explicit owner: Emergency Stop, Safe Stop,
Sequencer/audition, or neutral rest. The first V2 generation has no hidden
autonomous gesture scheduler and no implicit broadcast target.

### Current scope decision (2026-08-16)

The current Sequencer already responds well to the product's needs. It is not
being redesigned during the current work: its layout, workflow and controls are
out of scope until the animation work is complete.

The active work is exclusively the built-in gesture system shared by the
console and firmware: its catalog, trajectories, tempo definitions, limits,
execution and physical audition. There is no end-user movement editor in this
scope.

When a future Sequencer workspace review is appropriate, retain the visual
direction **"per droid"** as a discussion reference: small robot identities at
the left of individual timeline rows, making it immediately clear who moves and
when. This is a preference only, not an approved UI implementation.

## Approved breaking-development policy

The repository has no production users and is under intensive development.
Therefore V2 may deliberately break or remove:

- the 18 historical gesture definitions and their numeric IDs;
- existing `b1-sequence` files and v1-v6 import migrations;
- the Animation card and its ViewModel;
- Auto anims in the console, serial protocol, mesh protocol, heartbeat and NVS;
- global animation frequency, amplitude and speed configuration;
- current animation-specific serial/mesh commands and events;
- permanent support for mixed firmware protocol generations.

Breaking permission is not permission to weaken safety. OTA anti-brick, bounded
callback-to-loop processing, mechanical calibration, Stop semantics and
fail-closed validation remain mandatory.

Persisted fields are never reused under a new meaning. Obsolete animation keys
may be ignored, explicitly deleted, or abandoned behind a new NVS namespace.
No data migration is required unless a later stage explicitly approves one.

## Fleet coherence and transition

Normal V2 operation assumes one coordinated release generation across the
console, USB master and all slaves. The final handshake/preflight should verify:

- protocol generation;
- gesture-catalog identity/hash;
- required motion-engine capabilities;
- the expected role-specific firmware release identity.

An incompatible fleet must fail closed for Scene playback with a precise update
instruction. It must not silently approximate unsupported clip behavior.

OTA still updates boards sequentially, which creates a temporary mixed fleet
even when mixed operation is not a product feature. Before the breaking cutover,
choose one bounded transition strategy:

1. a bridge build that retains only the old OTA/identity path long enough to
   update every slave and then the master;
2. a complete USB reflash of every board;
3. another explicitly tested coordinated procedure.

Temporary transition code is removed after the fleet is aligned. It must not
become an open-ended compatibility layer.

## Target domain model

### Gesture identity

Scenes should persist a stable textual `gestureKey`, such as
`communicate.nod`. Firmware may use a compact generated `wireId`. Numeric wire
ordering is an implementation detail and is not the Scene identity.

### Gesture definition

A catalog entry is expected to define:

- key, display name, description, family and tags;
- finite, hold or loop execution kind;
- pan/tilt trajectory and segment easing, including a declared base or overlay
  composition layer and its controlled axis;
- nominal duration and end policy;
- allowed intensity, tempo, variant and deterministic-seed behavior;
- calibrated motion envelope and mechanical constraints;
- audition/broadcast safety metadata;
- catalog and minimum engine capability metadata.

### Scene clip

A gesture clip is expected to persist:

- `gestureKey`;
- explicit target/track;
- start time;
- intensity and tempo preset;
- optional variant;
- deterministic seed;
- explicit hold/end duration for continuous gestures.

The proposed clean document boundary is `{ "type": "b1-scene", "version": 1 }`.
Old `b1-sequence` documents are unsupported rather than migrated.

### Approved Stage 3A rules (2026-08-16)

The strict V2 schema is specified in [GESTURE-CATALOG-SCHEMA-V1.md](GESTURE-CATALOG-SCHEMA-V1.md).
The source catalog is `catalog/gesture-catalog-v1.json`; it generates the
runtime firmware table and is loaded by the console for Scene validation.

- `gestureKey` is the sole persisted gesture identity. `wireId` is generated
  later and is rejected in catalog and Scene source data.
- A Scene binds to the exact `{ id, revision, hash }` of its catalog. A mismatch
  is a validation error, never a best-effort remap.
- Every clip persists `intensity`, `tempo`, `variant` and a `uint32` seed. The
  default values are explicit, so replay has no hidden defaults.
- A tempo is a named, catalog-declared duration, not a speed multiplier. Every
  gesture initially offers only `normal`; `slow` and `fast` are added only when
  their individual trajectory is designed and safety-tested.
- Intensity changes movement envelope, never planned duration. A continuous
  clip has an explicit `holdMs`; finite and immediate clips must not carry one.
- Duplicating a future editor clip preserves its seed. A separate future
  variation action may deliberately generate a new one.

## Target runtime architecture

```text
declarative gesture catalog
    +-- generated compact firmware tables
    +-- generated console catalog/models
    +-- validation fixtures and documentation metadata

Sequencer / Audition
    -> protocol command
    -> current safety arbitration in main.cpp (future DroidController)
    -> MotionEngine trajectory and limits
    -> ServoEngine PWM
    -> correlated execution telemetry
```

The V2 target is for `DroidController` to own this boundary. Until that small
extraction is completed, the same priority is explicitly enforced in `main.cpp`:

```text
Emergency Stop > Safe Stop > Sequencer/Audition > neutral rest
```

IDLE means calibrated center and no hidden movement. A visible, explicit future
gesture may provide subtle presence; the current unconditional idle noise is not
part of the target baseline.

## Staged implementation plan

### Stage 1 — Policy and target architecture

Status: approved and recorded by this document.

- Record the breaking-development policy.
- Make the distinction between current behavior and target behavior explicit.
- Establish the whole-fleet coherence rule.
- Require discussion/approval before each later stage.

Exit: project bootstrap, documentation index, Sequencer decision log and
chronological archive all point to this plan.

### Stage 2 — Controlled removal of the legacy Animation surface

Status: complete, delivered in three independently validated sub-stages.

- **2A complete (2026-08-15):** removed the standalone Animation card,
  `AnimationViewModel`, its Help page and card-specific test. The Sequencer now
  reads the unchanged 18 names from a deliberately transitional
  `LegacyGestureCatalog`; no firmware/runtime behavior changed.
- **2B complete (2026-08-15):** removed Auto anims end to end, both
  autonomous schedulers and unconditional idle noise. The heartbeat's former
  Auto bit is reserved at zero for the bounded OTA transition; the temporary
  Safe Stop latch remains until `DroidController` replaces it.
- **2C complete (2026-08-15):** removed global frequency/amplitude/speed,
  their NVS/protocol/mesh/UI paths, and Droids Backup/Restore. Gesture pose
  variation is still deterministic, but duration is now fixed at the catalog's
  nominal value. Obsolete NVS keys are ignored rather than repurposed.

- Remove the Animation card and its ViewModel.
- Remove Auto anims end to end.
- Remove global frequency/amplitude/speed end to end.
- Remove autonomous master/slave schedulers and unconditional idle noise.
- Simplify safety state that existed only to suppress automatic motion.
- Update Help, backup/restore, tooltips and tests.

Exit: the current Sequencer still plays its existing gesture path, with no
standalone Animation/Auto-animation workflow or global gesture tuning.

### Stage 3 — Clean gesture and Scene models

Status: complete, delivered in two reviewable sub-stages.

- **3A complete (2026-08-16):** strict in-memory catalog/Scene V2 parsers,
  schema fixtures and catalog-vs-Scene validation exist outside the current
  runtime. The contract starts with `idle.center`, `communicate.nod` and
  `dialogue.talk`, each offering only `normal` tempo.
- **3B complete (2026-08-16):** Export, Import and Local Library now use only
  `b1-scene` V1. Old `b1-sequence` files are rejected without migration. Each
  stored clip retains its named gesture, intensity, tempo, variant, seed and
  hold. Numeric wire IDs are never persisted.

- Finalize the catalog schema and validation rules.
- Finalize `gestureKey` versus generated `wireId` ownership.
- Create the new `b1-scene` schema without legacy import.
- Define clip-local intensity, tempo, variant, seed and hold semantics.

Exit: V2 Scenes round-trip through Export, Import and Local Library with no
legacy schema path; strict fixtures pass before Stage 4 runtime adoption.

### Stage 4 — Minimal vertical slice

Status: implementation complete (2026-08-16); hardware audition pending.

The source catalog now generates the compact firmware catalog. The console,
master and mesh execute `idle.center`, `communicate.nod` and `dialogue.talk`
by key, with catalog identity checked in the handshake before playback.

Prove catalog -> generation -> firmware -> protocol -> WPF catalog -> audition
-> timeline -> telemetry with only:

- `idle.center`;
- `communicate.nod`;
- `dialogue.talk`.

Exit: targeted finite and leased continuous gestures work end to end without an
old Animation-card dependency.

### Stage 5 — Motion ownership and engine V2

Status: complete (2026-08-23). Composition foundation implemented
(2026-08-16); pure trajectory tests complete (2026-08-23); reduced-amplitude
bench validation on real hardware confirmed (2026-08-23). `DroidController`
extraction remains explicitly deferred, tracked as a standing item rather
than a Stage 5 blocker.

- **Pure trajectory tests (2026-08-23):** a host-native `env:native` Unity
  suite (`pio test -e native`, wired into `tools/self-test.ps1`) exercises
  `ServoEngine` and `MotionEngine` with no ESP32 board: easing shape, the
  velocity ceiling, mechanical/normalized clamping and clipping reporting,
  calibration semantics, axis reversal, per-axis base/overlay composition,
  deterministic end policy, same-channel interruption reporting, axis/layer
  independence, and the intentional infinite loop of a `continuous` gesture
  until explicitly stopped. See `test/test_servo_engine/`,
  `test/test_motion_engine/` and docs/TEST-PROTOCOL.md.
- **Reduced-amplitude bench validation (2026-08-23):** confirmed on real
  hardware — `idle.center`, each `attention.look-*` base pose, and the
  `communicate.nod`/`dialogue.talk` TILT overlays composing correctly over a
  held PAN base pose, with clean same-channel interruption.
- **Motion smoothness follow-up (2026-08-24):** bench feedback found several
  gestures rode close enough to the 180°/s velocity ceiling that the existing
  ease-in-out curve was compressed into an imperceptibly short window and
  read as a snap to target. `communicate.nod`, `dialogue.talk` and the four
  `attention.look-*` gestures had their frame timing and
  `tempos.normal.durationMs` slowed down in `catalog/gesture-catalog-v1.json`
  (see `docs/KNOWN-PITFALLS.md` for the moveMs-vs-ceiling constraint this
  respects). `idle.center` has no trajectory frames and is handled separately
  by `MotionEngine::stop()`'s fixed glide duration, also used by Safe Stop;
  that duration moved from 180ms to 550ms after bench feedback, chosen to stay
  well under expressive-gesture durations so Safe Stop remains visibly faster.
  `SERVO_PAN_MIN/MAX` in `config.h` was also narrowed to match the existing
  ±30°-from-center tilt default, for a safer virgin/uncalibrated-board
  fallback; this does not affect an already-calibrated droid.

`MotionEngine` composes a persistent base pose and expression overlays per
axis. A PAN orientation such as `attention.look-right` therefore remains in
place while a TILT overlay such as `dialogue.talk` runs. The resulting one
normalized pan/tilt target remains bounded by calibrated asymmetric ranges,
smootherstep easing and the velocity ceiling. The existing main-loop safety
priority remains the controller boundary until it is extracted as a class.

- **Deferred:** extract the current priority logic into `DroidController`.
- **Implemented:** normalized poses to calibrated asymmetric ranges and smooth
  segment easing.
- **Implemented:** velocity ceiling, deterministic interruption/end policy and
  clipping telemetry. `idle.center` clears every layer; `communicate.nod` and
  `dialogue.talk` clear only their TILT overlay; `attention.look-left` and
  `attention.look-right` hold PAN base poses; `attention.look-up` and
  `attention.look-down` hold TILT base poses. Positive TILT is explicitly up.
  The initial catalog deliberately declares no variation.

Exit: pure trajectory tests plus reduced-amplitude hardware checks pass. Met
2026-08-23.

### Stage 6 — Deterministic clip properties

Status: complete for what the current catalog actually offers (2026-08-24).

- Wire the approved intensity and tempo presets into the editor and runtime.
- Wire the approved variant, seed controls and continuous-gesture hold duration
  into the editor and runtime.
- Integrate Dirty, Undo/Redo, duplication, persistence, duration and Preflight.
- Refuse unsupported firmware/catalog combinations rather than degrade silently.

Exit: saved Scenes replay the same planned gestures independent of hidden
per-droid animation configuration.

**What was already done before this stage was picked up:** continuous-gesture
hold duration (`EndAfterMs`, the ±0.1s buttons in the "SELECTED CLIP"
inspector), Dirty/Undo/Redo/duplication/persistence for every currently-
editable property, and refuse-not-degrade (catalog-hash compatibility fails
closed; `SceneV2Parser.ValidateAgainstCatalog` rejects an unsupported
tempo/intensity/variant combination) were all already implemented.

**Implemented this stage (2026-08-24):** the one real gap — every newly
inserted gesture previously kept `Seed = 0` forever, with no way to change
it. `InsertGestureAt` now assigns a fresh random seed on insertion, and a
"Regenerate" button appears in the inspector's new VARIATION section for
gestures whose catalog entry declares `seedPolicy:"required"`
(`communicate.nod`, `dialogue.talk` today — driven by
`SequenceStep.RequiresSeed`, not a hardcoded UI list), wired through the same
Dirty/Undo transaction as every other clip edit. Duplicate still preserves
the original seed, per `GESTURE-CATALOG-SCHEMA-V1.md`'s existing intent — this
is the separate explicit variation action that document anticipated.

**Deliberately not wired: intensity, tempo and variant pickers.** The
current catalog declares exactly one option for each of these on every
gesture (`normal`/`normal`/`default`) — see
`catalog/gesture-catalog-v1.json`. A dropdown offering a single choice has no
functional value. Building this UI is deferred until Stage 7 gives at least
one gesture a real second option to choose between; wiring it now would be
speculative work against a UI surface nothing yet needs.

### Stage 7 — New catalog content

Build and bench coherent batches: rest/transitions, attention, communication,
emotion, dialogue, reaction and mechanical effects. Each entry must have a
clear intent and pass simulation, trajectory validation and physical audition.
Redundant or weak gestures are removed rather than preserved for compatibility.

**First batch built, not yet bench-audited (2026-08-24):** 46 candidate
gestures added to `catalog/gesture-catalog-v1.json` across all seven listed
families (catalog now 53 entries; hash and `RequiredGestureCatalogHash` in
`console/Services/ProtocolClient.cs` regenerated together, console and
firmware `env:b1` both build clean). Every new entry is single-axis
(`base`/`holdPose` for a held orientation, `overlay`/`clearLayer` for a
transient expression that clears back to the persistent base) because the
schema's composition rule forbids a two-axis `base`/`overlay` gesture; a
diagonal or combined pose is a Scene-authoring composition of two existing
single-axis gestures, not a new catalog entry. `seedPolicy` is `ignored` for
all 46 — none has seed-driven variation implemented, so marking them
`required` would show a non-functional Regenerate button. Every frame's
`moveMs` was chosen so no segment moves faster than `attention.look-right`'s
already bench-validated ~13 ms per percent-point of amplitude, deliberately
slower than `communicate.nod`'s fastest segment, since none of these 46 has
had a physical audition yet. **Not done:** simulation/trajectory-validation
pass, physical audition on hardware, and pruning weak or redundant entries —
the next step is bench testing this batch to decide what survives. Intensity,
tempo and variant pickers remain deliberately unwired per Stage 6's note:
every new entry, like the existing seven, declares exactly one option for
each.

**Console UI wiring fixed, same day:** the new batch was initially invisible
in the running console — five separate places hardcoded an `animId`↔`key`
table sized for the original 7 gestures (`SequencerViewModel.GestureLibrary`/
`GestureFamilies`/`InsertGestureAt`, `ProtocolClient.GestureKeyFor`, and
`GestureSceneV2Persistence.ResolveAnimId`/`ResolveGestureKey`, the last of
which would throw on Save/Export for any of the 46). All five now derive from
`GestureCatalogV2.Ordered` — the parsed catalog's own array order, which
already matches the firmware generator's `GestureWireId` order — so the
catalog file is the only place that needs to change to add, remove or
reorder a gesture. The temporary legacy `animId 17` alias for
`dialogue.talk` was dropped along with the hardcoded tables (no compatibility
need per DEC-025's development-reset stance); one persistence test that used
that alias was updated to reference `dialogue.talk` by its real, unchanged
index (2). `AnimFamilyToBrushConverter`'s per-family palette gained `emotion`
(rose) and `mechanical` (reusing brass) alongside the original `rest`/
`attention`/`communication`/`dialogue`/`reaction`, and is now computed from
the catalog's `family` field instead of a hardcoded animId table. Console
build clean, full `console.tests` suite green (352/352).

Each Gestures-palette chip's tooltip now shows that gesture's catalog
`description` (`GestureLibraryEntry.Description`, new field) instead of the
generic click/drag interaction hint — with 53 gestures, several with
non-obvious names (`mechanical.lock-on`, `communicate.beat`,
`attention.quizzical`...), knowing what a chip actually does before placing
it matters more than before. The interaction hint itself was not lost; it
just isn't repeated per chip (see
[SEQUENCER-BEHAVIOR.md](SEQUENCER-BEHAVIOR.md)'s "Explicit gesture insertion
target" section for that mechanic).

**Bench audition round 1 (2026-08-24, fw 1.12.1/console 0.13.1):** live testing
against a real master+slave fleet found and fixed four bugs, none in the new
trajectory data itself:

- The clip label converter (`AnimIdToNameConverter`) was a sixth hardcoded
  animId table missed in the earlier sweep, showing `?` for every gesture past
  the original 7.
- Delete/Undo/Redo/Restart went dead after clicking a transport button until
  the operator clicked a clip again (see KNOWN-PITFALLS.md, "WPF layout and
  input").
- `communicate.yes` (generated ID 17) silently never executed: a leftover
  pre-V2 legacy check (`AnimId is 16 or 17`) misidentified it as the old
  numeric alias for `dialogue.talk`'s continuous/leased behavior, and firmware
  rejected the resulting lease request. Fixed by deriving execution kind from
  the catalog everywhere instead (see KNOWN-PITFALLS.md, "Protocol and
  compatibility").
- The same landmine existed one layer down: the firmware's lease mechanism
  itself (`main.cpp`, `serial_console.cpp`) only accepted `dialogue.talk` by
  hardcoded ID, so all nine other new continuous gestures (`rest.idle-sway`,
  `attention.scan`, `attention.follow-slow`, `dialogue.listen`,
  `emotion.excited`, `emotion.confused`, `emotion.affection`, `emotion.bored`,
  `mechanical.self-check`, `mechanical.scan-vertical`) got rejected the same
  way once the console started correctly requesting a lease for them. Both
  checks now call the already-existing `MotionEngine::isContinuous(gestureId)`.
  Confirmed fixed on the master; the slave still needs the same firmware
  update before its continuous gestures will work.
- The clip's hover tooltip no longer appends live per-droid delivery detail
  (`Request N: serial: written; master: accepted...`) under the static
  interaction hint — mixing a live diagnostic trace into a tooltip whose first
  line is generic instructions read as broken/confusing rather than useful;
  that detail remains available via the clip's compact execution badge
  (`WRITE`/`MASTER`/`DONE`/`UNCONF`/...).

Trajectory/timing content itself has not yet needed a correction. Audition
continues on the remaining gestures.

### Stage 8 — Simulation and observability

- Add a no-motion Dry Run using the real compiled plan.
- Visualize pan/tilt trajectory and calibrated limits.
- Correlate planned time, serial write, master acceptance, droid start and end.
- Surface clipping, rejection, timeout and measured latency honestly.

### Stage 9 — Multi-droid synchronization

- Define a new uniform-generation protocol.
- Add catalog identity negotiation and planned start time.
- Synchronize clocks or measure offsets explicitly.
- Measure and report inter-droid skew.

### Stage 10 — Ambient behavior decision gate

After real use of the explicit workflow, choose one:

- no autonomous behavior;
- a deterministic, seeded Ambient clip on the timeline;
- an explicit Standby mode outside Scenes.

No Ambient implementation is approved yet.

### Stage 11 — Coordinated cutover and full bench gate

- Tag the pre-V2 baseline.
- Execute the approved bridge/USB transition.
- Align console, master and every slave.
- Reject incompatible generations after cutover.
- Run software, migration/reset and physical safety validation.
- Remove temporary transition code.

### Stage 12 — Sequencer workspace review (deferred to the end)

This stage is deliberately postponed until the built-in gesture catalog and its
real-world behavior are established. The present Sequencer remains the active
authoring surface; no Sequencer UI replacement is implied by the V2 motion
work.

- Reassess the existing Sequencer only after the catalog, observability,
  synchronization and coordinated cutover work are complete.
- If a redesign is then useful, validate a wireframe before any major XAML
  implementation.
- Start from the retained **"per droid"** visual direction, but treat it as a
  starting point for discussion rather than a committed design.

Exit: either keep the existing Sequencer, or approve and implement a separately
validated workspace improvement.

## Reordering rules

Stages may change order, but:

- approve a schema before persisting it;
- do not enrich the full catalog before a single vertical slice works;
- do not refactor motion content and the engine in one unmeasured batch;
- do not remove a safety control without its replacement path;
- do not begin a breaking OTA cutover without a complete-fleet recovery plan;
- do not add Ambient behavior before the explicit Sequencer workflow is proven.

## Explicitly out of scope

The first V2 generation does not include an end-user keyframe editor, arbitrary
runtime gesture upload, Show/Project workspace, branching, MIDI/OSC/DMX,
marketplace or other external automation. Those ideas may be reconsidered after
the gesture catalog, motion engine and Sequencer workflow are proven.
