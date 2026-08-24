# B1 Chat — Gesture Catalog and Sequencer V2

Status: stages 1–5 implemented; reduced-amplitude bench validation pending
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

Status: composition foundation implemented (2026-08-16); pure trajectory
tests complete (2026-08-23); `DroidController` extraction deferred and
reduced-amplitude bench validation still pending before this stage can close.

- **Pure trajectory tests (2026-08-23):** a host-native `env:native` Unity
  suite (`pio test -e native`, wired into `tools/self-test.ps1`) exercises
  `ServoEngine` and `MotionEngine` with no ESP32 board: easing shape, the
  velocity ceiling, mechanical/normalized clamping and clipping reporting,
  calibration semantics, axis reversal, per-axis base/overlay composition,
  deterministic end policy, same-channel interruption reporting, axis/layer
  independence, and the intentional infinite loop of a `continuous` gesture
  until explicitly stopped. See `test/test_servo_engine/`,
  `test/test_motion_engine/` and docs/TEST-PROTOCOL.md.

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

Exit: pure trajectory tests plus reduced-amplitude hardware checks pass.

### Stage 6 — Deterministic clip properties

- Wire the approved intensity and tempo presets into the editor and runtime.
- Wire the approved variant, seed controls and continuous-gesture hold duration
  into the editor and runtime.
- Integrate Dirty, Undo/Redo, duplication, persistence, duration and Preflight.
- Refuse unsupported firmware/catalog combinations rather than degrade silently.

Exit: saved Scenes replay the same planned gestures independent of hidden
per-droid animation configuration.

### Stage 7 — New catalog content

Build and bench coherent batches: rest/transitions, attention, communication,
emotion, dialogue, reaction and mechanical effects. Each entry must have a
clear intent and pass simulation, trajectory validation and physical audition.
Redundant or weak gestures are removed rather than preserved for compatibility.

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
