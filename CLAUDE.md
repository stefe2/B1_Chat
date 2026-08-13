# CLAUDE.md — B1 Chat project

This is the project bootstrap document. Read it before touching the repository,
then read the area-specific document required by the task. Keep this file
short: durable detail belongs in the linked documents.

## Source of truth

| Area | Document | Read when |
| --- | --- | --- |
| Sequencer runtime behavior | [docs/SEQUENCER-BEHAVIOR.md](docs/SEQUENCER-BEHAVIOR.md) | Changing the console Sequencer |
| Sequencer backlog and decisions | [docs/SEQUENCER-HARDENING.md](docs/SEQUENCER-HARDENING.md) | Picking up Sequencer work |
| Console ↔ firmware contract/history | [docs/FIRMWARE-CONTRACT.md](docs/FIRMWARE-CONTRACT.md) | Changing serial or mesh protocol |
| Current protocol reference | [docs/PROTOCOL-REFERENCE.md](docs/PROTOCOL-REFERENCE.md) | Checking message types or JSON commands |
| Validation scope and exclusions | [docs/TEST-PROTOCOL.md](docs/TEST-PROTOCOL.md) | Running or adding tests |
| Detailed implementation traps | [docs/KNOWN-PITFALLS.md](docs/KNOWN-PITFALLS.md) | Touching firmware, OTA, WPF, storage or timing |
| Chronological history and incidents | [docs/PROGRESS-ARCHIVE.md](docs/PROGRESS-ARCHIVE.md) | Investigating why something is this way |
| Hardware board concepts | [docs/hardware/](docs/hardware/) | Working on the carrier board |

`CLAUDE.md` defines the project-wide rules. The linked document is authoritative
for its own area. When a behavior changes, update the relevant document in the
same commit.

## Project overview

One repository contains two release trains:

1. ESP32 Arduino firmware in `src/`, driving B1 pan/tilt heads over a multi-hop
   ESP-NOW mesh. A build-time `IS_MASTER` role coordinates the fleet; settings
   are stored in NVS.
2. A native WPF `net8.0-windows` console in `console/`, using XAML/MVVM and
   owning the USB serial port. The old `console/wwwroot/index.html` remains a
   frozen design/behavior reference and is not rendered at runtime.

Console tags use `vX.Y.Z`; firmware tags use `fw-vX.Y.Z`.

## Architecture essentials

- Firmware identity is the last two MAC bytes; no manually assigned droid ID.
- `[env:b1]` is local development/flash and reads `IS_MASTER` from
  `src/config.h`. `[env:b1_master]` and `[env:b1_slave]` are CI release builds
  that override the role with build flags.
- The firmware uses native LEDC PWM, ArduinoJson, ESP-NOW broadcast, bounded
  callback-to-loop message handling, and an OTA anti-brick guard.
- The console owns audio and Sequencer execution. The firmware no longer owns
  DFPlayer audio or persistent sequence slots.
- The serial link is JSON-lines at 115200 baud, guarded by `hello`/`ping`; the
  current protocol is additive and must remain compatible with older nodes.

Hardware: DOIT ESP32 DevKit V1; PAN GPIO25, TILT GPIO26, life LED GPIO2;
servos use an external 5 V supply, common ground and recommended bulk
capacitance. Avoid ESP32 strapping pins and input-only GPIO34–39.

## Non-negotiable safety and compatibility rules

- Never rewrite a partition table on a board whose NVS must survive. A table
  change is allowed only with an intentional full chip erase.
- Keep `OtaGuard::earlyCheck()` as the first line of `setup()`.
- Never perform real OTA flash writes from the ESP-NOW callback or a critical
  section; use the mailbox and `loop()` path.
- Preserve legacy heartbeat decoding and additive capability-gated behavior.
- Do not reinterpret a persisted field under a new meaning; rename and migrate
  it instead.
- Keep normal mesh application handling in the bounded loop inbox, and keep
  NVS access outside locks and callbacks.
- Read [docs/KNOWN-PITFALLS.md](docs/KNOWN-PITFALLS.md) before changing firmware
  storage/OTA/timing or WPF layout/input behavior.

## Commands

- Firmware build: `pio run -e b1`
- Firmware local flash: `pio run -e b1 -t upload` (check `IS_MASTER` first)
- Console build: `dotnet build` from `console/`
- Console tests: `dotnet test console.tests\b1-chat-console.Tests.csproj`
- Safe preflight: `.\tools\self-test.ps1 [-SkipSerial] [-SkipBuild] [-ComPort COMx]`
- OTA bench: `.\tools\ota-test.ps1 -Bin <path.bin> ...`

Prefer `self-test.ps1` before a handoff when the task affects both halves. It
never flashes, moves servos or bumps `console/build.number`.

## Current open work

- `droid.{h,cpp}` high-level firmware state machine remains unfinished.
- Sequencer hardening backlog and evidence are tracked in
  [docs/SEQUENCER-HARDENING.md](docs/SEQUENCER-HARDENING.md), not duplicated
  here.
- Hardware gates and the servo-hub PCB remain documented in `docs/hardware/`.

For the latest release and milestone details, use [docs/PROGRESS-ARCHIVE.md](docs/PROGRESS-ARCHIVE.md).

## Git and handoff rules

Use a clear imperative commit summary with a short body describing important
architectural/user-visible changes and validation. Before finishing a change:

1. Read the relevant area-specific documents.
2. Check `git status` and preserve unrelated user changes.
3. Build and run the tests appropriate to the change.
4. Update the relevant documentation and progress record when behavior or a
   decision changed.
5. Summarize changed files, validation results and any remaining risk.
