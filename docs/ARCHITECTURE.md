# B1 Chat — Architecture map

Where each responsibility lives, on both sides of the USB cable, and where each
piece of data is persisted. The code remains authoritative; this document is the
map that saves you from reading it. Update it in the same commit that adds,
removes or repurposes a module.

Behavior documents complement it: [SEQUENCER-BEHAVIOR.md](SEQUENCER-BEHAVIOR.md)
for what the Sequencer does, [PROTOCOL-REFERENCE.md](PROTOCOL-REFERENCE.md) for
what travels on the wire, [KNOWN-PITFALLS.md](KNOWN-PITFALLS.md) for the traps
inside these modules.

## Firmware modules (`src/`)

**One** firmware for both roles; the role is chosen at build time (`IS_MASTER`).
Identity is automatic — the 16-bit `srcId` is the last two bytes of the MAC, so
a board is plug in → flash → done, with no ID to assign or track.

| File | Role |
| --- | --- |
| `main.cpp` | `setup()`/`loop()`, module wiring, non-blocking timers, bounded ESP-NOW callback→loop inbox |
| `config.h` | role, pins, default servo limits, mesh and topology constants, `FW_VERSION` |
| `mesh_comm.{h,cpp}` | ESP-NOW: header `{srcId, seq, ttl, type}`, dedup on `(srcId, seq)`, TTL relay, truncated 8-byte HMAC-SHA256, direct radio neighborhood (physical sender MAC + RSSI) |
| `mesh_topology.{h,cpp}` | (master) aggregates the directed `{from, to, rssi}` edges into the neighborhood graph |
| `servo_engine.{h,cpp}` | native 50 Hz LEDC PWM, smootherstep easing, idle noise, calibratable limits, per-axis reverse |
| `animation.{h,cpp}` | 18 keyframe gestures, non-blocking player, variation seed, `totalDurationMs()`, execution lifecycle reporting and infinite-gesture leases |
| `registry.{h,cpp}` | (master) live inventory: `srcId`, RSSI, `lastSeen`, servo state, autoAnim — synchronized access, see the pitfalls document |
| `config_store.{h,cpp}` | NVS: per-droid names, animation params, servo calibration, adoption |
| `serial_console.{h,cpp}` | (master) USB JSON ↔ mesh bridge for the console |
| `ota_guard.{h,cpp}` | (all roles) anti-brick: NVS flag plus manual rollback to the other partition when new firmware fails to start correctly |
| `ota_master.{h,cpp}` | (master) drives an OTA session toward one slave: stop-and-wait, retry, post-reboot confirmation through the heartbeat |
| `ota_slave.{h,cpp}` | (slave) receives the relayed image and writes it through `Update` |

`droid.{h,cpp}` — the planned high-level state machine — does not exist yet; it
is the one structural item still open on the firmware side.
`sequence_store.{h,cpp}` was **deleted in fw 1.7.0** with the master's 8 NVS
sequence slots and its onboard player; sequences are console-only now.

Dependency: `ArduinoJson`. Build flags: `-D MESH_TTL=4` and
`-D GROUP_KEY="changeme"` — a **compile-time-only** key, with no runtime
re-keying.

PlatformIO environments (`platformio.ini`):

- `[env:b1]` — role decided by `#define IS_MASTER` in `config.h`; this is the
  local development and flashing environment (`pio run -e b1 -t upload`).
- `[env:b1_master]` / `[env:b1_slave]` — reserved for CI releases. They force
  the role with `-D IS_MASTER=1|0` without touching `config.h`, which guards
  `IS_MASTER` behind an `#ifndef` (like `MESH_TTL` and `GROUP_KEY`) precisely so
  the command-line override works. They do not affect `[env:b1]`.

## Console structure (`console/`)

Native WPF, `net8.0-windows`, 100 % XAML, driven by `CommunityToolkit.Mvvm`
(`[ObservableProperty]` / `[RelayCommand]`). The WebView2 shell was replaced in
the 2026-07-13 rewrite; `wwwroot/index.html` stays on disk as a frozen
design reference and is never loaded at runtime.

| Folder / file | Role |
| --- | --- |
| `MainWindow.xaml(.cs)` | header (logo, connection status, unsaved auto-commit badge, Firmware and Help buttons) plus the card grid |
| `FirmwareWindow.xaml(.cs)` | separate window hosting `Views/FirmwareCardView` — espflash flashing and GitHub updates |
| `HelpWindow.xaml(.cs)` | separate window: table-of-contents sidebar plus one continuous `FlowDocumentScrollViewer` assembled from `Help/docs/*.md` through `Markdig.Wpf` (deliberately not WebView2); menu clicks jump to sections and scrolling syncs the active page |
| `CalibrationWindow.xaml(.cs)` | separate window hosting `Views/CalibrationCardView`, opened from each Droids row's ⛭ button and pre-targeted at that droid before the window shows |
| `SceneBrowserWindow` / `SceneDecisionWindow` / `SceneNameWindow` | modal Scene dialogs: searchable library browser, save/discard/cancel replacement decision, themed name prompt. App-owned and themed; only Import/Export uses a native file picker |
| `App.xaml(.cs)` | composition root: converters and merged resource dictionaries |
| `Themes/Theme.xaml` | palette, button/LED/mesh-node gradients — ported from the reference page's CSS custom properties |
| `Themes/Effects.xaml` | shared styles: `CardBorderStyle`, `BeveledButtonStyle`, `HaloBadge*Style`, `MetalSliderStyle`, `DarkComboBoxStyle`, `CardIconBoxStyle`, `MeshNodeEllipseStyle`, app-wide dark `ScrollBar`. `Themes/HelpStyles.xaml` holds the Help window's `FlowDocument` styling |
| `Models/` | view-bound objects (`Droid`, mesh visuals, `HelpDoc`, `UpdateInfo`) and the Sequencer's explicit type boundaries: `SequenceSnapshot` (persistent document only), `SequencerPlaybackPlan` (immutable runtime pass), `AnimationDurationMetadata`, `SequenceLibraryModels` |
| `ViewModels/` | `MainViewModel` plus one per card (`DroidsViewModel`, `CalibrationViewModel`, `AnimationViewModel`, `FirmwareViewModel`, `MeshTopologyViewModel`, `SequencerViewModel`) and `HelpViewModel`, which has no `ProtocolClient` dependency because Help is local-only |
| `Views/` | one XAML `UserControl` per card, plus `SequenceTimelineView` |
| `Services/SerialLinkService.cs` | native serial port (`System.IO.Ports`) with 3 s auto-reconnect |
| `Services/ProtocolClient.cs` | central state: parses incoming `evt` JSON, builds outgoing `cmd` JSON |
| `Services/UpdateService.cs` · `FlashService.cs` · `LibraryService.cs` · `SettingsService.cs` | GitHub updates (per-train tag filtering, semantic maximum), espflash flashing, Scene library, `settings.json` |
| `Services/OtaService.cs` | drives an OTA session one slave at a time: reads the `.bin`, computes the MD5, sends one fragment per `evt:otaChunkAck` |
| `Services/AudioPlaybackService.cs` | console-side Sequencer audio, the only audio source since the DFPlayer was retired in fw 1.6.0: several concurrent `MediaPlayer`s, `PauseAll`/`ResumeAll`, and a one-off duration probe |
| `Services/SequencerAbstractions.cs` | the test seams: injectable monotonic clock, timer, protocol sender, audio player and dialog boundaries — what lets `console.tests` run playback headlessly |
| `Services/SequencerEditHistory.cs` | begin/commit/cancel edit transactions plus bounded newest-first Undo/Redo (50 each), with no WPF or playback dependency |
| `Services/SequenceImportService.cs` · `SequencerPersistenceServices.cs` | side-effect-free strict parser/migrator for `b1-sequence` v1–v5, and atomic sibling-temp-plus-rename writing |
| `Services/AnimationDurationProvider.cs` | single source for each gesture's kind (immediate/finite/infinite), effective tail, target-speed-aware range, provisional state and inspector text |
| `Services/PlaybackGeneration.cs` · `WaveformService.cs` | per-pass generation and cancellation identity; audio waveform peak decoding |
| `Services/DarkTitleBar.cs` | recolors the native Win32 title bar (`DwmSetWindowAttribute`, Windows 11 22H2+) on all 7 app-owned windows |
| `Services/InstallationVerifier.cs` | backs `--verify-install`, the self-check the NSIS installer runs against the installed binaries |
| `Services/TraceLog.cs` | optional serial trace to `%LOCALAPPDATA%\B1ChatConsole\serial-trace.log` |
| `Converters/` | binding converters: boolean/visibility/brush/text, `StrengthToBrushConverter` (mesh link color by RSSI), the timeline set (`TimelineGeometryConverter`, `TimelineActiveConverter`, `AnimFamilyToBrushConverter`, `WaveformToGeometryConverter`, `TrackMutedConverter`), firmware status, and `MarkdownToFlowDocumentConverter` for Help |
| `Help/manifest.json` + `Help/docs/**/*.md` | in-app Help content, sections → pages, copied to the output directory as Content rather than embedded |
| `b1-chat-console.csproj` | auto-incremented build number, version from `VersionPrefix`, `IncludeNativeLibrariesForSelfExtract`, `tools/` (espflash plus the app-local VC143 x64 runtime) excluded from the single file but copied on publish |
| `console.tests/` (repo root, `b1-chat-console.Tests.csproj`) | headless xUnit suite: playback plan and integration, transport state boundaries, edit history, import/persistence, Scene library, duration provider, plus `Fixtures/Sequences/sequence-v1..v4.json` golden files. Runs without WPF or hardware and must not bump `console/build.number` |
| `installer/b1-chat-console.nsi` + `release.ps1` | NSIS installer and the GitHub release script (tag `vX.Y.Z`) |

Main grid layout (`MainWindow.xaml`): Droids (left column) · Mesh Topology
(right column, same row) · Animation (full width) · Sequencer (full width,
bottom). Firmware and Servo Calibration are deliberately **out** of the grid, in
separate windows — Firmware from the header button, Calibration from each Droids
row's ⛭ button.

## Storage — what is persisted where

| What | Where |
| --- | --- |
| Names, per-droid animation-param cache, calibrations, adoption status | Master's NVS (`config_store`); each droid also persists its own name, animation params and calibration locally |
| Scenes | Console only. Normal Save/Save As uses the versioned Scene library; `.b1seq.json` Export/Import is the external-copy path. Both retain the droid roster and linked audio paths. The master's 8 NVS slots were removed in fw 1.7.0 |
| Scene library, recoverable trash, last port, last Scene ID / external path | `%LOCALAPPDATA%\B1ChatConsole\` — `library\*.b1scene.json`, `library\trash\`, and `settings.json` |
| Console-side audio lanes (label plus clips: file path, duration, start, loop) | Inside the Scene/export JSON; the audio bytes stay at their original PC paths |
| OTA anti-brick flag (pending, attempts) | NVS of **each droid** flashed over OTA, in a separate `"ota"` namespace (`ota_guard`) |
