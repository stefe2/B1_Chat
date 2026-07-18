# B1 Chat — Multi-droid B1 Battle Droid control

A single repo, two halves:

1. **ESP32 firmware** (repo root, PlatformIO/Arduino) — drives several **B1
   battle droid** heads (2 pan/tilt servos each) over a **multi-hop ESP-NOW
   mesh** network, with smooth, organic animations coordinated by a
   **master** board. Supports **OTA firmware updates over the mesh**
   (no USB needed for a slave already in the field) and settings persisted
   in NVS.
2. **Supervision console** (`console/`, WPF `net8.0-windows`) — a **100%
   native WPF** desktop app (no WebView2/browser) that owns the serial port
   and gives full control over the fleet: droid roster & adoption, servo
   calibration, live mesh topology (radar view), animation triggers, a
   multi-track sequencer (with console-side audio), firmware flashing/OTA,
   and an in-app Help window.

> Full architecture, protocol reference, and a detailed progress log:
> [`CLAUDE.md`](CLAUDE.md). [`FIRMWARE-CONTRACT.md`](FIRMWARE-CONTRACT.md)
> tracks the console ↔ firmware protocol contract specifically.

## Features

### Firmware

- **1 ESP32 per droid** — single firmware image, master/slave role chosen
  at build/flash time.
- **Multi-hop ESP-NOW mesh** (TTL + dedup relay, truncated HMAC-SHA256
  signed): droids out of the master's direct range are reached via relay;
  tampered messages are rejected and two independently-keyed fleets ignore
  each other.
- **Pan/tilt head** — 2 servos per droid, native 50 Hz LEDC PWM,
  *smootherstep* easing and idle noise for a lifelike feel.
- **18 built-in gestures** plus a coordinated random-idle behavior across
  the fleet.
- **OTA updates over the mesh** — reflash an adopted slave with no USB
  cable, with an anti-brick safety net (automatic rollback to the previous
  image if the new one fails to boot cleanly).
- **Auto identity** — each droid's ID is derived from its own MAC address:
  plug in, flash, done — no manual ID/config step.
- Settings (names, servo calibration, animation parameters) persisted in
  each board's own NVS; auto-committed from the console.

### Console

- **100% native WPF** (XAML/MVVM, `CommunityToolkit.Mvvm`) — no browser
  shell, direct ownership of the serial port.
- **Droids** card: live roster, adoption/forget, per-droid Servos/Auto
  anims/Locate toggles, firmware version, backup & restore.
- **Servo Calibration**: live pan/tilt preview and per-droid limits.
- **Mesh Topology**: a radar-style live view of the mesh — direct/relayed
  links, signal strength, real-time traffic.
- **Sequencer**: a multi-track timeline to choreograph several droids (and
  console-side audio, multi-lane with waveform preview) together, with
  Play/Pause/Resume/Loop, a local library, and `.b1seq.json` export/import.
- **Firmware**: USB flashing (app-only or full erase+flash for a virgin
  board) and OTA updates over the mesh, both with GitHub-release
  auto-discovery and SHA-256 verification.
- **Help**: an in-app Markdown-based help window (native `FlowDocument`
  rendering via `Markdig.Wpf`, not a browser) covering every card.

## Hardware

- Board: **DOIT ESP32 DevKit V1** (1 per droid)
- 2 standard PWM servos (SG90 / MG996R) per droid, on external 5V (BEC),
  common ground with the ESP32, ≥ 470 µF capacitor recommended.
- Audio (originally a DFPlayer Mini + amp on the master) has been
  **retired from the firmware** — all sequencer audio is now played
  client-side by the console. The wiring is unaffected; unplugging it is
  optional and out of scope for the firmware.

### Wiring (summary)

| Function | ESP32 GPIO |
|----------|-----------|
| Pan servo | GPIO25 |
| Tilt servo | GPIO26 |
| Life LED | GPIO2 (onboard) |

Pins to avoid: strapping GPIO0/2/5/12/15, input-only GPIO34-39. Full
details in [`CLAUDE.md`](CLAUDE.md).

## Build & flash (firmware, PlatformIO)

The role is set in [`src/config.h`](src/config.h):

```cpp
#define IS_MASTER 1   // 1 = master (only one per fleet), 0 = slave
```

Then, for local dev:

```bash
pio run -e b1 -t upload
```

- **Master**: set `IS_MASTER 1`, flash **one** board.
- **Slaves**: set `IS_MASTER 0`, flash all the others.

Default group key set in `platformio.ini` (`-D GROUP_KEY`, compile-time
only). CI (`.github/workflows/firmware-release.yml`) builds and publishes
both roles automatically on a `FW_VERSION` bump pushed to `main`, using the
dedicated `b1_master`/`b1_slave` environments — no local flashing needed
for a release.

## Build & run (console)

```powershell
cd console
dotnet build
dotnet run
```

Requires the .NET 8 SDK. See [`console/installer/`](console/installer) for
the NSIS installer/release script.

## Project structure

```
platformio.ini        firmware build config (env b1 / b1_master / b1_slave)
CLAUDE.md              full architecture, protocol, and progress log
FIRMWARE-CONTRACT.md   console <-> firmware protocol contract
src/                   firmware (mesh, animation, servo engine, OTA, ...)
console/               WPF supervision console
  MainWindow.xaml(.cs)   header + card grid
  FirmwareWindow.xaml    flashing/update window
  HelpWindow.xaml        in-app help
  Models/ ViewModels/ Views/ Services/ Converters/   MVVM app code
  Help/                  in-app help content (Markdown + manifest)
  installer/             NSIS installer + release script
```

## Releases

Two independent GitHub release trains in this same repo, distinguished by
tag prefix:

- `vX.Y.Z` — the console app (installer via
  `console/installer/release.ps1`).
- `fw-vX.Y.Z` — the firmware, both roles + a SHA-256 manifest (published
  automatically by CI on a `FW_VERSION` bump, or manually via
  `tools/release.ps1`).

## Project status

In active development. See [`CLAUDE.md`](CLAUDE.md)'s *Progress* section
for the up-to-date, detailed changelog of every completed step.
