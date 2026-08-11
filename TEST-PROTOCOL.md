# B1 Chat — autonomous test protocol

The default self-test is intentionally non-destructive. It can be launched by
Codex or from PowerShell without answering prompts:

```powershell
.\tools\self-test.ps1
```

It never flashes a board, starts an OTA, changes a valid configuration,
enables/disables a servo, previews a position, or starts an animation.

## What it checks automatically

### Offline checks

- builds `b1_master` and `b1_slave`;
- builds the WPF console without changing `console/build.number`;
- restores and runs the headless Sequencer unit/integration tests without
  changing `console/build.number`; the suite covers immutable playback plans,
  restart/cancellation, Loop boundaries, Pause/Resume edges, mute, disconnect,
  natural end, repeated Stop, cleanup, numeric duration limits and aggregation
  of per-droid animation execution reports, including missing-start and
  missing-completion timeouts, late recovery, delayed duplicate reports, local
  dispatch failures, correlated master acceptance, and targeted cleanup of
  infinite gestures across broadcast overrides, Stop, restart, natural end and
  Loop boundaries;
- runs `git diff --check`;
- verifies that the callback-to-loop mesh inbox is present;
- verifies the per-droid animation-parameter store and targeted protocol;
- verifies the serial, mesh, and OTA validation guards;
- verifies that the content-derived firmware Build ID is generated and
  propagated through heartbeats, serial inventory and OTA verdicts;
- verifies the non-blocking animation execution-report path from mesh sequence
  correlation through the serial protocol and WPF timeline;
- verifies randomized mesh sequence initialization;
- verifies fail-closed SHA-256 handling for downloaded firmware assets;
- verifies that Help files are forced into the published payload, checked before
  installer creation, and handled safely at runtime if an image is absent;
- verifies that the installer checks Windows/CPU compatibility, warns when the
  optional Windows media stack is absent, and executes both installed binaries;
- verifies that animation/calibration debounces snapshot their target and that
  calibration loads suppress write-back callbacks.

### Safe serial integration checks

If an available B1 master is detected, the script opens it automatically and:

- validates the JSON handshake, firmware/protocol metadata and master Build ID;
- reads the droid inventory and confirms every node publishes a Build ID;
- reads targeted animation parameters and calibration;
- checks the 18-entry animation-duration catalog;
- proves strict runtime validation with a read-only invalid-target probe before
  sending any invalid setter or animation command;
- sends invalid animation, configuration, and calibration commands and requires
  an `evt:"err"` response;
- rereads configuration/calibration to prove rejected commands changed nothing;
- observes the inventory briefly and fails if it changes unexpectedly or a
  `mesh inbox full` event appears.

Opening serial can reset some ESP32 USB boards. The console must not already own
the COM port; an unavailable/busy port is reported as `SKIP`, not as a failure.
If the read-only validation probe fails, all invalid commands capable of changing
state are suppressed. This protects benches running a stale binary whose version
or advertised capabilities do not match its actual validation behavior.

## Options

```powershell
# Offline-only run
.\tools\self-test.ps1 -SkipSerial

# Serial-only run against an explicitly named port
.\tools\self-test.ps1 -SkipBuild -ComPort COM3 -RequireHardware

# Require automatic hardware discovery to succeed
.\tools\self-test.ps1 -RequireHardware

# Extend the short mesh observation
.\tools\self-test.ps1 -ObserveSeconds 15
```

Each run writes a JSON report under the current user's temporary directory and
prints its exact path. The process exits with code `1` if any required test
fails, making it usable from CI or another automation.

## Active Sequencer bench test

`tools/sequencer-bench-test.ps1` is deliberately separate from the default
self-test. Without a flag it performs only a strict read-only preflight:
firmware/protocol consistency, expected fleet, targeted configuration and
calibration responses, durations, runtime validation and mesh topology.

```powershell
# Read-only preflight for one master plus two slaves
.\tools\sequencer-bench-test.ps1 -ComPort COM3

# Explicitly permit calibrated preview and animation movement
.\tools\sequencer-bench-test.ps1 -ComPort COM3 -AllowMotion -LoopCycles 5
```

The active run snapshots servo/automatic-animation states, pauses automatic
motion, enables the three targets, exercises calibrated master preview, targeted
and broadcast finite gestures, deterministic seeds, rapid restart, explicit
IDLE interruption of TALK/POWER_DOWN, and a short broadcast-loop stress. A
`finally` cleanup always attempts broadcast IDLE and restores every captured
servo/automatic-animation state. It never changes configuration/calibration,
commits, flashes, starts OTA, or intentionally disconnects USB.

The automated verdict proves serial acceptance, inventory/state propagation and
observed mesh health. Execution reports now prove that each current droid's
software animation engine started, completed, interrupted or rejected a tracked
command. There is still no position telemetry, so visible movement,
deterministic physical trajectory and inter-droid mechanical skew require an
operator observation.

## Headless animation execution test

`tools/anim-exec-test.ps1` is the active test for slaves without physical
servos. It forces the master's attached servos off, temporarily enables only
the slave software engines, disables spontaneous motion, and restores every
captured servo/auto-animation state in `finally`.

```powershell
.\tools\anim-exec-test.ps1 -ComPort COM3
```

It requires the `animExec` and `animAccepted` capabilities. Every tracked
command must first be accepted by the master with the expected request, target,
animation and mesh/local routing, then the test validates targeted finite
animation start/completion on every slave, a broadcast where the disabled master
reports `rejected/servosOff`, and interruption of looping TALK by tracked IDLE.
It does not flash, alter configuration/calibration, or claim physical movement.

### Delivery-stage bench run — 2026-08-11

- Master `43140` flashed over USB with Build ID `9A228A09`; slaves `4216`
  and `34880` updated by OTA to Build ID `1D787B84` with explicit
  `otaResult ok=true` verdicts.
- The headless execution test passed 5/5 with `animAccepted` correlation before
  every targeted, broadcast and TALK→IDLE lifecycle check. Report:
  `b1-anim-exec-20260811-141734.json`.
- Strict autonomous regression passed 29/29, including both execution
  capabilities and a stable mesh observation. Report:
  `b1-self-test-20260811-141828.json`.
- Read-only Sequencer preflight passed 6/6 with six directed mesh links.
  Report: `b1-sequencer-bench-20260811-141836.json`.
- Final inventory: master and both slaves have Servos and Auto animation off.

### Bench run — 2026-08-11

- Topology: master `43140`, slaves `4216` and `34880`; firmware label 1.9.0,
  protocol 5; six directed topology links reported.
- Master reflashed successfully over USB on COM3. Both 974,320-byte slave images
  transferred completely by OTA (`5128/5128` chunks, MD5
  `86efff011ab00297454b8c93291024b3`) and remained stable beyond the 20-second
  anti-brick confirmation window.
- The first same-version transfers exposed the old version-only verdict:
  `ok=false, reason=rolledBack` even though the images booted and remained
  healthy. This historical limitation is now resolved by a content-derived
  Build ID; semantic version remains the human release/compatibility label.
- Active script: 15 passed, 0 failed. Covered strict preflight, calibrated master
  preview, each target, broadcast, repeated seed, rapid restart plus IDLE,
  TALK/POWER_DOWN interruption and five Loop cycles without observed inbox
  overflow. Report: `b1-sequencer-bench-20260811-092352.json`.
- Strict serial regression after flashing: 20 passed, 0 failed, including all
  invalid-command rejection/no-mutation checks and 15 seconds of stable fleet
  observation. Report: `b1-self-test-20260811-092501.json`.
- Final restored state: master config `59/60/50`; both slave configs `50/60/50`;
  calibrations unchanged; servos and automatic animations off on all three.

### Build ID validation — 2026-08-11

- Final deterministic identities: master `4DAD66EF`; slave image `72349AFE`.
  `hello`, `droids` and `otaResult` expose the identity as eight uppercase hex
  characters; the WPF fleet and OTA status surfaces parse and display it.
- Rolling-upgrade compatibility was exercised before updating the slaves: the
  new master accepted both legacy 8-byte heartbeats without a Build ID. After
  updating slave `4216`, the same inventory simultaneously decoded one current
  and one legacy slave heartbeat.
- Both 974,400-byte same-version 1.9.0 OTA transfers completed at `5129/5129`
  chunks (MD5 `56e9ae06a24828debc148ac9d461e075`). Each post-reboot verdict was
  `ok=true, fw=1.9.0, build=72349AFE`; the false `rolledBack` result is gone.
- Read-only Sequencer bench preflight: 6 passed, 0 failed, 1 intentionally
  skipped (active motion). Report:
  `b1-sequencer-bench-20260811-101337.json`.
- Strict serial regression: 22 passed, 0 failed, including full fleet Build ID
  propagation and 15 seconds of stable mesh observation. Report:
  `b1-self-test-20260811-101406.json`.

### Animation execution-report validation — 2026-08-11

- Final identities: master `00FD6D8C`; both slaves `65440D15`. Master USB flash
  and both 975,024-byte same-version OTA transfers completed successfully at
  `5132/5132` chunks (MD5 `b002fc1867d64f6d11f744d14a3c49ee`).
- Headless lifecycle test: 5 passed, 0 failed. Both slaves reported targeted
  `started` then `completed`; broadcast returned `rejected/servosOff` from the
  deliberately disabled master and completion from both slaves; TALK reported
  `interrupted` when replaced by IDLE. Report:
  `b1-anim-exec-20260811-133214.json`.
- WPF/headless suite: 35 passed, 0 failed. Offline autonomous regression: 17
  passed, 0 failed (`b1-self-test-20260811-131949.json`). Strict hardware
  regression: 24 passed, 0 failed, including `animExec` capability and 15
  seconds of stable mesh (`b1-self-test-20260811-133306.json`).
- Read-only post-test preflight: 6 passed, 0 failed, 1 active-motion skip;
  three Build IDs and six topology links present
  (`b1-sequencer-bench-20260811-133234.json`). Final restoration confirmed
  servos and automatic animations off on master `43140` and slaves
  `34880`/`4216`.

## Deliberate exclusions

Software alone cannot confirm physical direction, mechanical clearance, servo
heating, power quality, or visible movement. Flash, OTA, rollback, intentional
power loss, valid servo commands, and multi-hop range tests therefore remain
manual/explicit tests on a spare bench setup. They must never be added to the
default autonomous mode.
