# CLAUDE.md — B1 Chat project (multi-droid B1 Battle Droid control)

Single project tracking file (merger of the old `project.md` and the console's
CLAUDE.md — **keep it up to date after every completed step**, explicit
request from the user).

## Overview

A single git repo (`stefe2/B1_Chat`), two halves:

1. **ESP32 firmware** (repo root, PlatformIO/Arduino): drives several B1
   droid heads (2 pan/tilt servos each) over a **multi-hop ESP-NOW mesh**
   network, with smooth/organic animations coordinated by a **master** that
   also plays sound (DFPlayer Mini + amp). Settings persisted in NVS.
2. **Supervision console** (`console/`, WPF net8.0-windows, v0.9.x): a
   **100% native WPF** desktop app (XAML/MVVM, `CommunityToolkit.Mvvm`) that
   owns the serial port (`System.IO.Ports`) and reproduces the old web
   page's design card by card. `console/wwwroot/index.html` (inline
   HTML+CSS+JS) is **kept intact** as a behavior/design reference, but is no
   longer rendered at runtime (the old WebView2 shell has been removed).
   Merged into this repo (it used to live in `b1-chat-console`, a separate
   repo with no remote — never published; history lost in the process, no
   real loss). `FIRMWARE-CONTRACT.md` lists the protocol extensions the
   console expects from the firmware.

Two distinct GitHub release trains within the **same** repo, distinguished
by tag prefix: `vX.Y.Z` for the console app, `fw-vX.Y.Z` for the firmware
(see `tools/release.ps1` and `console/installer/release.ps1`).

## Commands

- `pio run -e b1` — builds the firmware (pio.exe: `%USERPROFILE%\.platformio\penv\Scripts\pio.exe`)
- `pio run -e b1 -t upload` — flashes a board (role chosen via `IS_MASTER` in `src/config.h` **before** flashing: 1 = master, only one per network; 0 = slave)
- `tools\espflash.exe write-bin --port COMx -B 460800 0x10000 .pio\build\b1\firmware.bin` — flash the app **only** without PlatformIO (espflash 4.4.0, also used by the console's Firmware card). This assumes the board already carries our bootloader + partition table; a **virgin** board needs the full flash: `... 0x1000 bootloader.bin` + `... 0x8000 partitions.bin` + `... 0x10000 firmware.bin` (the console does this automatically, see "Full flash" below)
- `dotnet build` (from `console/`) — builds the WPF console
- `.\tools\release.ps1 [-Publish]` — manual firmware release (2 roles + SHA-256 manifest, tag `fw-vX.Y.Z`); in normal use, prefer bumping `FW_VERSION` (`src/config.h`) and pushing to `main` — CI publishes on its own (see below)
- `.\console\installer\release.ps1 [-Publish]` — console release (publish + NSIS installer, tag `vX.Y.Z`)
- `.\tools\ota-test.ps1 -Bin <path.bin> [-CorruptChunk N] [-StopAtChunk N] [-SecondStartAt N] [-ComPort COM3]` —
  OTA test bench: drives the master directly over the serial port (console closed),
  replays the JSON protocol (`hello`/`otaStart`/`otaChunk`/...) and lets you inject
  faults (corrupted chunk, mid-transfer abort, double `otaStart`) without depending
  on the UI. See the OTA section and Verification pt 7.

**Automatic firmware release** (`.github/workflows/firmware-release.yml`): triggers
on push to `main` touching `src/config.h`, or manually (`workflow_dispatch`). Reads
`FW_VERSION`, skips if the `fw-vX.Y.Z` tag already exists (idempotent), otherwise builds
`b1_master`/`b1_slave` (PlatformIO on a GitHub runner), computes the SHA-256 manifest, tags
and publishes the release — no local `gh auth login` needed (`GITHUB_TOKEN`
provided by Actions). Normal flow: bump `FW_VERSION`, commit, push to `main`, wait for
CI. `tools/release.ps1 -Publish` remains a manual fallback (avoid using it in addition to
CI for the same version — duplicate tag/release).

## Hardware (DOIT ESP32 DevKit V1)

| Signal | GPIO |
| --- | --- |
| Servo PAN | GPIO25 |
| Servo TILT | GPIO26 |
| DFPlayer RX (master) | GPIO17 (TX2), via 1 kΩ |
| DFPlayer TX (master) | GPIO16 (RX2) |
| DFPlayer BUSY (master) | GPIO4 (wired, **not yet used**) |
| Life LED | GPIO2 (onboard) |

- Servos on external 5V (BEC), common ground, ≥ 470 µF capacitor recommended.
- Audio: DFPlayer's DAC_L/DAC_R → external amp (PAM8403) → 1 speaker (master).
- Pins to avoid: strapping GPIO0/2/5/12/15; input-only GPIO34-39.
- 4-6 droids planned (extensible), SG90/MG996R servos.

## Firmware architecture (`src/`)

**Single** firmware; role set at build time (`IS_MASTER`), auto identity (16-bit srcId =
last 2 bytes of the MAC — plug in → flash → done, no ID to manage).

| File | Role |
| --- | --- |
| `main.cpp` | setup()/loop(), module wiring, non-blocking timers |
| `config.h` | role, pins, default servo limits, mesh/audio/topology constants |
| `mesh_comm.{h,cpp}` | ESP-NOW: header {srcId,seq,ttl,type}, dedup (srcId,seq), TTL relay, truncated 8-byte HMAC-SHA256, **direct radio neighborhood** (physical sender MAC + RSSI) |
| `mesh_topology.{h,cpp}` | (master) aggregator of directed {from,to,rssi} edges of the neighborhood graph |
| `servo_engine.{h,cpp}` | native 50 Hz LEDC PWM, smootherstep easing, idle noise, calibratable limits |
| `animation.{h,cpp}` | 18 keyframe anims, non-blocking player, variation seed, `totalDurationMs()` |
| `audio.{h,cpp}` | (master) DFPlayer wrapper, anim → track-range mapping (10 tracks `/mp3/0001..0010.mp3`) |
| `registry.{h,cpp}` | (master) live inventory: srcId, RSSI, lastSeen, servos, autoAnim (synchronized access, see pitfalls) |
| `config_store.{h,cpp}` | NVS: names, volume, anim params, per-droid servo calibration |
| `sequence_store.{h,cpp}` | (master) NVS: 8 named sequence slots, ≤ 32 steps {targetId,animId,delayMs} |
| `serial_console.{h,cpp}` | (master) USB JSON ↔ mesh bridge for the console |
| `ota_guard.{h,cpp}` | (all roles) anti-brick: NVS flag + manual rollback to the other partition if the new firmware doesn't start correctly |
| `ota_master.{h,cpp}` | (master) orchestrates an OTA session toward a slave (stop-and-wait, retry, post-reboot confirmation via heartbeat) |
| `ota_slave.{h,cpp}` | (slave) receives an OTA image relayed by the mesh, writes via `Update` |
| `droid.{h,cpp}` | high-level state machine (step 6, **not done yet**) |

Dependencies: `DFRobotDFPlayerMini`, `ArduinoJson`. Build flags: `-D MESH_TTL=4`,
`-D GROUP_KEY="changeme"` (**compile-time-only** key, no re-keying at runtime).

PlatformIO environments (`platformio.ini`): `[env:b1]` — role decided by `#define
IS_MASTER` in `config.h`, for local flash/dev (`pio run -e b1 -t upload`).
`[env:b1_master]`/`[env:b1_slave]` — dedicated to CI releases, force the role via
`-D IS_MASTER=1|0` without touching `config.h` (which guards `IS_MASTER` with an
`#ifndef`, like `MESH_TTL`/`GROUP_KEY`, so the command-line override
works); don't affect `[env:b1]`.

## Mesh protocol (ESP-NOW broadcast, fixed channel)

Frame = header + payload + HMAC(8 B, TTL excluded from the signature). Relay: dedup
(srcId,seq) in a ring buffer, then if ttl>0 → ttl-- and re-broadcast. Two B1
fleets with different `GROUP_KEY`s ignore each other; tampered messages are rejected.
Anti-replay: dedup + monotonic seq (enough for a prop, not an absolute
cryptographic guarantee).

| Type | Payload |
| --- | --- |
| `MSG_ANIM` = 1 | targetId (0xFFFF = all), animId, syncDelayMs, seed |
| `MSG_CONFIG` = 2 | targetId, freq, amplitude, speed |
| `MSG_HEARTBEAT` = 4 | uptime, state (bit0 = servos, bit1 = auto anims), firmware version (3 bytes major/minor/patch) |
| `MSG_SERVO` = 5 | targetId, enabled |
| `MSG_CALIB` = 6 | targetId, 6 pan/tilt limits (persisted by the targeted droid) |
| `MSG_PREVIEW` = 7 | targetId, pan, tilt (transient, not persisted) |
| `MSG_AUTOANIM` = 8 | targetId, enabled (pauses spontaneous idle anims) |
| `MSG_NEIGHBORS` = 9 | count + [{id, rssi}]: periodic report of the sender's **direct** radio neighborhood (3s + anti-collision jitter; RSSI is measured by the report's sender even if the report is then relayed) |
| `MSG_OTA_START` = 10 | (master→targeted slave) targetId, sessionId, totalSize, totalChunks, chunkSize, md5Hex[32] — starts an OTA session |
| `MSG_OTA_CHUNK` = 11 | (master→targeted slave) targetId, sessionId, chunkIndex, dataLen, data[190] — one fragment of the image, sent at full size |
| `MSG_OTA_ACK` = 12 | (slave→master) sessionId, kind (0=start/1=chunk/2=end), chunkIndex, status |
| `MSG_OTA_END` = 13 | (master→targeted slave) targetId, sessionId, totalChunks — finalizes (`Update.end()`) if all expected chunks were received |
| `MSG_OTA_ABORT` = 14 | (master→targeted slave) targetId, sessionId, reason — cancels the ongoing session |

## Animations (18, aligned firmware ↔ `ANIMS` table in index.html)

0 IDLE · 1 LOOK_AROUND · 2 NOD_YES · 3 SHAKE_NO · 4 CURIOUS_TILT · 5 SCAN_SLOW ·
6 ALERT_SNAP · 7 TRACK · 8 GLITCH_STUTTER · 9 CONFUSED_TILT · 10 DOUBLE_TAKE ·
11 SLEEPY_DROOP · 12 TARGET_LOCK · 13 WHIRR_SEARCH · 14 SIGNAL_GLITCH ·
15 GREETING_NOD · 16 POWER_DOWN (**loops**) · 17 TALK (**loops**, fast tilt like
a talking mouth, meant to accompany an audio track).

The two looping gestures are excluded from the random idle draw and count as
`LOOPING_ANIM_DEFAULT_MS` (2s, indicative) in `totalDurationMs()`.
Idle behavior: the master picks a random gesture every 2.5-5s and
broadcasts it to everyone (isolated slave: 3-7s, local) — suspendable per droid
("Auto anims"), without cutting the servos or blocking Play/Sequencer.

## JSON serial protocol (console ↔ master, 115200 baud, 1 line = 1 message)

Session guarded by a handshake: `hello` → `{evt:"hello",ok,id}`, then keepalive
`ping` (5s timeout on the firmware side, `_clientReady`).

- **Console → master** (`cmd`): `hello` · `ping` · `list` · `getConfig` · `getAll` ·
  `config {target,freq,amp,speed}` · `volume {value}` · `name {id,name}` ·
  `playTrack {track}` · `servo {target,enabled}` · `autoAnim {target,enabled}` ·
  `adopt {target}` · `forget {target}` ·
  `anim {target,animId,seed}` · `preview {target,pan,tilt}` ·
  `calib {target,+6 limits}` · `getCalib {target}` · `getAnimDurations` ·
  `getMeshTopology` · `seqList` · `seqLoad {slot}` ·
  `seqSave {slot,name,loop,track,steps}` · `seqDelete {slot}` ·
  `seqRun {slot,from?}` · `seqStop` · `seqPause` · `seqResume` · `seqState` ·
  `setMulti {ops:[...]}` · `commit` · `revert` ·
  `otaStart {target,size,md5}` · `otaChunk {seq,data}` (data = base64) · `otaAbort {}`
- **Master → console** (`evt`): `hello {ok,id,fw,proto,lineMax,anims,seqSlots,trackCount,caps[],dirty}` ·
  `droids {list:[{id,name,rssi,age,role,servos,autoAnim,adopted,fw}]}` ·
  `log {msg}` · `err {msg}` · `config {volume,freq,amp,speed}` · `calibData {target,+6}` ·
  `meshTopology {links:[{from,to,rssi}]}` · `animDurations {list:[{animId,ms}]}` ·
  `seqList {list:[{slot,name,stepCount,loop,track}]}` · `seqData {slot,name,loop,track,steps}` ·
  `seqSaved {ok,slot,name}` · `seqDeleted {ok,slot}` ·
  `seqState {playing,slot,index,total,track,paused}` ·
  `setMultiDone {ok,applied,failedAt?,error?}` · `dirty {dirty}` · `allDone` ·
  `otaReady {target,sessionId,chunkSize,totalChunks}` · `otaChunkAck {seq,sent,total}` ·
  `otaDone {target,sessionId}` · `otaResult {target,ok,fw?,reason?}` ·
  `otaError {target?,sessionId?,reason}`

Unknown fields in a command: ignored (the console may be newer than the
firmware). Responses routed exclusively on `evt`. Line buffer: 4 KB
(`lineMax` announced at handshake; any longer line → `err`).

**Commit/revert** (volume, anim params, names — not calibration or
sequences): setters are "live" (RAM overlay), NVS is only written on
`commit`; `revert` reloads the persisted state. The console must send `commit`
after a restore `setMulti`. [FIRMWARE-CONTRACT.md](FIRMWARE-CONTRACT.md)
tracks the implementation status (§1/§3/§4/§5 done; §2 track durations deferred —
BUSY-pin approach).

**Droid adoption** (`registry`/`config_store`): a droid never seen before
(`adopted:false` in `evt:droids`) stays in the mesh (broadcast anims received
normally) but is absent from individual controls until the console has
sent `adopt`. `adopt` persists the status in NVS (survives master reboots);
`forget` removes the entry from the registry **and** clears its NVS status — a
droid "forgotten" this way, or whose adoption was declined, therefore asks again as soon as it
talks again. The "lost" badge (4s of silence, `DROID_TIMEOUT_MS`) never
re-triggers this question on its own.

## Firmware OTA (slaves, relayed by the mesh)

An adopted slave can be reflashed **without USB**, triggered by a
"Flash (OTA)" button on its row in the Droids card. The `.bin` travels over the
existing serial link (console → master, base64-encoded) then over the ESP-NOW mesh
(master → targeted slave), `stop-and-wait`: one 190-byte fragment in flight at a
time, acknowledgment required before the next (`Update.write()` on the slave side is
sequential/append-only, no out-of-order handling). **Only one session at a
time** across the whole fleet.

Flow: `otaStart` (console, with size + MD5) → the master validates the target
(known to the registry) and sends `MSG_OTA_START` → once acked, `evt:otaReady`
→ the console pushes the chunks one by one via `otaChunk`, each relayed as
`MSG_OTA_CHUNK` → `evt:otaChunkAck` after each ack (triggers sending the
next one from the console) → last chunk acked → `MSG_OTA_END` → `evt:otaDone`
(the master is done, the slave reboots) → the master then monitors the
target's heartbeats until `OTA_REBOOT_WAIT_MS` (~90s) and pushes
`evt:otaResult`. Since the console can't reliably know the
version baked into an arbitrary `.bin`, success is determined by comparing
the version reported **after** reboot to the one from **before** the OTA (captured
at `otaStart` time) rather than to an announced version — `ok:true` if it
changed, `reason:"rolledBack"` if it's identical, `reason:"unreachable"` if
no heartbeat arrives within the delay. Grace window (`OTA_REBOOT_GRACE_MS`,
5s): a sign of life at an unchanged version in the first few seconds is
ignored — the slave only reboots ~250 ms after its END ack, one last
heartbeat from the old image can still arrive (false "rolledBack" rendered
940 ms after `otaDone`, observed at the bench); a real rollback takes ≥ 10-30s.

Anti-brick safety net (`ota_guard.{h,cpp}`): before finalizing (`Update.end(true)`,
which already checks size/MD5 and refuses to reboot into an invalid image),
the slave arms an NVS flag then reboots. On the next boot, if this flag is
present, an attempt counter increments; past
`OTA_MAX_BOOT_ATTEMPTS` (3), the firmware itself switches
(`esp_ota_set_boot_partition`) to the other partition (`esp_ota_get_next_update_partition`
necessarily alternates app0/app1) and reboots — a manual rollback via the standard
`esp_ota_ops` API, without relying on ESP-IDF's bootloader rollback
(not simply exposed under `framework=arduino`). If the firmware runs for
`OTA_VERIFY_UPTIME_MS` (~20s) without a reset, the flag is cleared: the image is
confirmed good.

**Residual risk accepted**: a crash occurring *before* `OtaGuard::earlyCheck()`
(1st line of `setup()`) would never be counted or caught. Reduced in
practice by the MD5/format check already done before any reboot, which
filters out most corrupted-transfer cases — the only remaining case is "the image
is valid but crashes almost instantly". An `esp_task_wdt` (10s) is
also armed to catch a new firmware that loops/hangs without yielding.

**Realistic duration**: ~5,240 fragments for a ~1 MB image → **8 to 15 minutes**
per slave under normal conditions, up to 20-30 min over a weak or
multi-hop link. Displayed in the console as a progress indicator (fragments sent
out of total), not a promised fixed duration.

## Old reference web page (`console/wwwroot/index.html`)

Inline HTML+CSS+JS page kept **intact** (unmodified since the WPF
rewrite) — serves only as a behavioral/visual spec (exact French text,
palette, card-by-card behavior) for the native implementation below.
No longer **rendered** by the application (the old WebView2 shell, which
loaded it via `window.chrome.webview.postMessage`, has been removed).

Cards it documents: Droids (names, servos, auto anims, backup/
restore) · Servo calibration (live preview + auto-save) · Animation ·
Audio · Firmware (espflash flashing) · Mesh topology (SVG graph of direct
links, bidirectional merge at the weakest RSSI) · Sequencer (catalog of
8 slots + unlimited local library + editor + multi-track timeline + audio
track + console-side Rehearse mode + undo/redo + export/import `.b1seq.json`) ·
Activity (card removed on the WPF side, see Progress).

The firmware protocol (`cmd`/`evt`, above) is carried there through `write`
(outgoing) and `line` (incoming, parsed → `handleEvent()`); the WebView2
transport vocabulary (`listPorts`/`open`/`write`/`flash`/`libList`/...) no longer
applies on the WPF side, replaced by a direct call to `Services/SerialLinkService.cs` +
`Services/ProtocolClient.cs` (no postMessage bridge).

### Console architecture (`console/`) — native WPF (XAML/MVVM)

Complete rewrite (2026-07-13): the old WebView2 shell is replaced by a
**100% XAML** UI, card by card, driven by `CommunityToolkit.Mvvm`
(`[ObservableProperty]`/`[RelayCommand]`). `index.html` stays on disk, intact,
as a design/behavior reference (section above) but is no longer
loaded by the application.

| Folder/file | Role |
| --- | --- |
| `MainWindow.xaml(.cs)` | header (logo, connection status, commit/revert, "Firmware…" button) + card grid |
| `FirmwareWindow.xaml(.cs)` | separate window hosting `Views/FirmwareCardView` (espflash flashing + GitHub update), opened from the header button |
| `App.xaml(.cs)` | composition root: converters + merged resource dictionaries |
| `Themes/Theme.xaml` | palette (brushes), button/LED/mesh-node gradients — ported from index.html's CSS custom properties |
| `Themes/Effects.xaml` | shared styles: `CardBorderStyle`, `BeveledButtonStyle`, `HaloBadge*Style`, `MetalSliderStyle`, `DarkComboBoxStyle`, `CardIconBoxStyle`, `MeshNodeEllipseStyle`, etc. |
| `Models/` | `Droid`, `MeshNodeVisual`/`MeshEdgeVisual`, sequences, calibration — view-bound objects |
| `ViewModels/` | `MainViewModel` + one per card (`DroidsViewModel`, `CalibrationViewModel`, `AnimationViewModel`, `AudioViewModel`, `FirmwareViewModel`, `MeshTopologyViewModel`, `SequencerViewModel`) |
| `Views/` | one XAML `UserControl` per card (no more Activity card) |
| `Services/SerialLinkService.cs` | native serial port (`System.IO.Ports`), auto-reconnect (3s) |
| `Services/ProtocolClient.cs` | central state: parses incoming JSON `evt`, builds outgoing `cmd` (C# equivalent of JS's `sendCmd()`/`handleEvent()`) |
| `Services/UpdateService.cs` / `FlashService.cs` / `LibraryService.cs` / `SettingsService.cs` | GitHub updates, espflash flashing, local sequence library, `settings.json` |
| `Services/OtaService.cs` | drives an OTA session (one slave at a time): reads the `.bin`, computes the MD5, sends one fragment per `evt:otaChunkAck` received |
| `Converters/` | `BoolToStyleConverter`, `BoolToTextConverter`, `BoolToVisibilityConverter`, `BoolToBrushConverter`, `StrengthToBrushConverter` (mesh link color by RSSI) |
| `b1-chat-console.csproj` | auto-incremented build number, version from `VersionPrefix`, `IncludeNativeLibrariesForSelfExtract`, `tools/` (espflash) excluded from the single-file but copied on publish |
| `installer/b1-chat-console.nsi` + `release.ps1` | NSIS installer + GitHub release script (tag `vX.Y.Z`) |

Main grid layout (`MainWindow.xaml`): Droids (left column,
full height) · right column stacked Calibration → Mesh topology →
(Animation + Audio row side by side, equal width) · Sequencer full
width at the bottom. Firmware card taken out of the grid (separate window).

## Storage

| What | Where |
| --- | --- |
| Names, volume, anim params, calibrations, adoption status | Master's NVS (`config_store`) |
| Sequences (8 slots, ≤ 32 steps) | Master's NVS (`sequence_store`) |
| Track durations + slot→audio-track | Page's `localStorage` (temporary, see contract §1-2) |
| Sequence library, last port | `%LOCALAPPDATA%\B1ChatConsole\` (console side) |
| OTA anti-brick flag (pending/attempts) | NVS of **each droid** flashed via OTA, separate `"ota"` namespace (`ota_guard`) |

## Progress

- [x] Steps 1-5, 7-10: servo_engine, mesh_comm (HMAC, relay), animation (18
      gestures), audio, config_store + registry, serial_console, dashboard,
      sequence_store + standalone playback.
- [x] Sequencer overhaul: catalog by name (hidden slots), real durations
      (`getAnimDurations`), live progress (`seqState`), seqSaved/seqDeleted acks,
      `anim.isPlaying()` guard for steps targeting the master.
- [x] Per-droid "Auto anims" pause (MSG_AUTOANIM, heartbeat bit1, UI column).
- [x] Mesh topology (MSG_NEIGHBORS, mesh_topology module, SVG graph card).
- [x] WPF console v0.8.0: native serial port, auto-reconnect, integrated flashing,
      local library, backup/restore, timeline, Rehearse,
      export/import. `index.html` replaces `web/dashboard_V7.html`.
- [x] "Ecosystem" phase (2026-07-07), firmware 1.0.0: 4 KB serial buffer +
      explicit `err`, enriched handshake (fw/proto/lineMax/caps/dirty), `getAll`
      + `allDone`, contract §3 (`evt:config`), §1 (per-sequence audio track, played
      standalone, per-gesture sounds suppressed when a track exists), §5 (`seqRun
      from`, pause/resume with DFPlayer pause), §4 (atomic `setMulti`),
      KyberEditor-style commit/revert (volume/params/names). Page + C# (GitHub
      checkUpdates, download+install, firmware and console release scripts) shipped.
- [x] Repo merge (2026-07-13): the console (`b1-chat-console`, a
      local repo never pushed) is brought into `console/` in this repo (plain
      copy, one commit, the 6-commit console history not kept — nothing
      was published). Only one GitHub repo now (`stefe2/B1_Chat`), two
      tag trains (`vX.Y.Z` app, `fw-vX.Y.Z` firmware); `MainWindow.xaml.cs`
      adapted (lists the repo's releases and filters by tag prefix instead
      of `/releases/latest`, which would have mixed the two trains).
- [x] Complete rewrite of the console in native WPF (2026-07-13, `index.html`
      kept intact as a reference, no longer rendered at runtime): 8 cards
      ported to XAML/MVVM (`CommunityToolkit.Mvvm`), C# `ProtocolClient` as the
      new central state (native equivalent of JS's `sendCmd()`/`handleEvent()`),
      auto-reconnecting serial, sequencer undo/redo, mesh topology (Canvas +
      circular layout ported as-is).
- [x] Visual polish + rearrangement (2026-07-13): Servo Calibration card taken
      as the reference model (metal slider, value pills, dark
      ComboBox) then propagated to the other cards; header redesigned (logo, status,
      accent CTA); Mesh Topology card redesigned (glossy nodes, radar
      rings, links colored by signal strength) and moved between Calibration and
      Animation; Activity card removed; Firmware card taken out of the grid
      into a separate window (`FirmwareWindow`, dedicated header button);
      Animation and Audio placed side by side above the Sequencer.
- [x] Console firmware-flow overhaul (Master/Slave role chosen explicitly
      before the source, self-sufficient GitHub source with verification, address
      relegated to advanced options) + automatic firmware release via CI
      (`.github/workflows/firmware-release.yml`, triggered by a `FW_VERSION`
      bump; `IS_MASTER` made overridable via `#ifndef` for the
      new `b1_master`/`b1_slave` PlatformIO environments).
- [x] Droid adoption (2026-07-13, fw 1.1.0): a droid never seen before (or
      "forgotten"/declined) is no longer automatically added to the list — the
      console offers Adopt/Ignore (`cmd adopt`/`forget`, status persisted in
      NVS via `config_store`, never in the ephemeral RAM `registry`). "Forget"
      button to remove an already-adopted droid. Fixed along the way:
      `ProtocolClient.HandleDroids` never removed an entry that had disappeared from
      `evt:droids` (latent bug, no visible effect before this feature).
- [x] Per-droid firmware version (2026-07-13, fw 1.2.0): each slave
      reports its version in its heartbeat (3 bytes major/minor/patch),
      stored in the `registry` and exposed via `evt:droids.fw`; new
      FW column in the Droids card. Breaks heartbeat binary
      compatibility (see pitfalls) — the whole fleet must be reflashed together.
- [x] Droids card polish (2026-07-13): fixed-width NAME column, fixed-width
      centered-text STATE/ROLE badges, RSSI shown as `-` (instead of the
      last frozen value) when a droid is "lost", "lost" threshold
      lowered to 4s (`DROID_TIMEOUT_MS` + console-side threshold), sliding
      on/off switches (`OnOffSwitchStyle`) for Servos/Auto anims.
- [ ] Step 6: `droid.{h,cpp}` state machine.
- [ ] Contract §2: track durations measured via the BUSY pin (GPIO4) — deferred.
- [ ] Anim freq/amp/speed params: received + persisted but **no effect**
      (`onConfig` hook never wired up in main.cpp; sliders marked "coming
      soon" in the UI).
- [x] Automatic firmware release operational (`fw-v1.0.0`, `fw-v1.1.0`
      published via CI). Console: still manual (`gh auth login` once,
      then `console\installer\release.ps1 -Publish`).
- [x] Firmware OTA relayed by the ESP-NOW mesh (2026-07-14, fw 1.3.0): an
      adopted slave can be reflashed without USB from the Droids card
      (`MSG_OTA_START/CHUNK/ACK/END/ABORT`, stop-and-wait, one session at a
      time) + `ota_guard` (manual anti-brick rollback, see the OTA section
      above). So far only tested on a dedicated spare board (see
      Verification) — not yet validated on a real fleet droid.
- [x] Nominal OTA path **validated at the bench** (2026-07-14, fw 1.3.11): full
      transfer 1.3.9 → 1.3.11 (~4 min, 5111 chunks at ~22/s, single hop),
      clean reboot, correct `otaResult{ok:true}` verdict, FW column up to date.
      Required a long bug hunt (fw 1.3.2 → 1.3.11 + console):
      flash access under `portENTER_CRITICAL` in the ESP-NOW callback (freeze at
      chunk 21 → callback→loop mailbox), unsigned overflow in
      `now - timestamp` (instant timeouts, false `age` ~4e9 that crashed
      `HandleDroids` and killed the read loop — the "freezes" at chunks
      716/848/859), serial segment with no retry (console watchdog 3s ×5 +
      master re-ack + 2 KB UART buffers + slave 60s timeout > master's 45s),
      false `rolledBack` (verdict rendered on a pre-reboot heartbeat → 5s
      grace window), and lexicographic sorting of the GitHub `/releases` API (the
      console flashed 1.3.9 thinking it was the latest → semantic
      max). Permanent serial trace added
      (`%LOCALAPPDATA%\B1ChatConsole\serial-trace.log`).
      Still to do (Verification pt 7): corrupted `.bin`, forced rollback,
      console abort mid-transfer, anti-double-session guard,
      multi-hop.
- [x] OTA robustness validated at the bench (2026-07-14 night, fw 1.3.12) via
      `tools/ota-test.ps1` (scriptable serial test bench: replays the
      console protocol and injects faults — corrupted chunk, abort,
      double otaStart):
      · corrupted chunk in flight → `ERR_MD5` (err 7) at END, **no reboot**,
        the slave stays on its image;
      · double `otaStart` → `otaError "busy"`, the in-progress session
        continues undisturbed;
      · console abort at chunk 1200 → master aborts at exactly 45s (reason 11),
        the slave purges the session, the next one starts cleanly;
      · anti-brick rollback (`-D OTA_TEST_FORCE_CRASH` build, inert hook
        kept in main.cpp): 3 boot panics → automatic partition switch
        on the 4th boot → old image recovered in **3.6s**, genuine
        `rolledBack` verdict on the master's side;
      · nominal regression 1.3.11 → 1.3.12 (with the Registry synchronized
        on both sides) → `otaResult{ok:true}` ~1s post-reboot.
      Remaining: multi-hop (3rd board required) and a first OTA on a real
      fleet droid.
- [x] Console v0.9.0 (2026-07-14): OTA hardening cycle (permanent serial
      trace, chunk watchdog, more reliable verdict, semantic release
      max, colored FW column, OTA from GitHub). NSIS installer
      published on GitHub (tag `v0.9.0`, `console\installer\release.ps1 -Publish`).
- [x] Console/firmware English pass + icon + Droids/Mesh-Topology/header
      overhaul (2026-07-14): app icon designed and wired in everywhere
      (csproj, windows, installer); Droids card reworked (disambiguated
      VERSION/UPDATE columns, flash buttons hidden once a droid's firmware is
      confirmed up to date, "Up to date ✓" badge); Mesh Topology card given
      the full treatment — ambient effects (rotating radar sweep, spinning
      ring on the master, starfield, master/slave legend, auto-scaling
      radius) and live-telemetry effects (per-node signal halo,
      heartbeat-flash pulse, OTA travel indicator, hop-wave ripple on
      broadcast, TALK-sync pulse, mini-stats readout); header bar
      regrouped into three visually separated clusters. Entire codebase
      (console C#/XAML, firmware `src/*.h/.cpp` comments, docs, build
      scripts) switched from French to English — see the updated
      convention below. `console/wwwroot/index.html` deliberately left
      untouched (frozen design reference, never rendered).
      **Post-translation crash found and fixed**: connecting to a real
      master with ≥1 droid crashed the console immediately
      (`XamlParseException` → `Cannot animate '(0).(1)' on an object
      instance that cannot be modified`) — see the new Known pitfalls
      entry below for the root cause and fix.
- [x] Mesh Topology: force-directed layout + live packet visualization
      (2026-07-14): node placement replaced the old fixed circular layout
      with a force-directed simulation (`MeshTopologyViewModel.ComputeForceLayout`,
      120 iterations per render, warm-started from the previous frame's
      positions) — master pinned at the canvas center, repulsion between
      all nodes, springs along real links whose rest length now encodes
      RSSI (strong signal pulls a node in, weak signal lets it drift out,
      on top of the existing thickness/opacity encoding), weak gravity so
      unreachable nodes don't drift off-canvas. Added small colored dots
      (`Packets` collection, ~30 fps ticker, `MeshPacketVisual`) that
      travel hop-by-hop along the real BFS-derived master↔node path for
      every mesh frame the console can actually observe: outgoing
      `anim`/`servo`/`autoAnim`/`config`/`calib`/`preview` (new
      `ProtocolClient.PacketSent` event), OTA chunks (one dot per
      `evt:otaChunkAck`, riding the existing dashed travel line), and an
      inbound droid→master dot standing in for heartbeat/neighbor-report
      refreshes (the console never sees individual `MSG_HEARTBEAT`/
      `MSG_NEIGHBORS` frames or any inter-slave relay traffic — only
      what the master reports over serial — so this is a faithful
      visualization of what's observable, not a packet capture). Legend
      row added below the Master/Slave legend. Colors registered in
      `Converters/PacketKindToBrushConverter.cs`.
- [x] Mesh Topology: classic green radar screen skin (2026-07-14, from a
      user-provided `radar.html` mockup): the old dark-square recess +
      orange rings + white starfield background was replaced with a
      circular green-phosphor radar disc (dark green→black radial
      gradient, glowing `#2DFF6F` rim, 3 concentric range rings, 12
      angular spokes, a faster 4s rotating sweep beam, plus a subtle
      vignette + CRT scanline overlay — both implemented as circular
      `Ellipse` fills so they self-clip without an explicit
      `Canvas.Clip`). Scoped entirely to this card via local XAML
      resources, not the global orange `AccentBrush` theme. Node
      placement is now radially clamped (`MeshTopologyViewModel.
      ComputeForceLayout`, `MaxNodeRadius`) instead of per-axis, so a
      node never renders outside the visible disc. Functional accent
      indicators (master ring, heartbeat pulse, hop-wave ripple, TALK
      pulse) were deliberately left orange for contrast against the
      green backdrop. Verified visually with temporary seeded test data
      (canvas is otherwise hidden until droids are present) then reverted.
- [x] Mesh Topology: radar skin brought to full radar.html fidelity
      (2026-07-15, user-approved via an HTML preview artifact before any code
      change): range rings made solid (dashes removed), crosshair brightened
      to .22 vs the .11 diagonal spokes (hierarchy matching the mockup's
      conic ticks), and the old narrow ~13° sweep triangle replaced by a
      wide ~42° beam — WPF has no conic gradient, so the falloff is stepped
      into twelve 3.5° pie slices of increasing alpha plus a glowing
      full-brightness leading-edge radius, all rotating together (4s) in one
      Canvas. Second fidelity pass after user comparison against the HTML
      preview: disc base made an opaque phosphor gradient (#0A3016 →
      #021006 at 62% → black, exactly radar.html's bottom layer) instead
      of a translucent glow over black (read grey, not green), scanline
      overlay dropped 0.06 → 0.02, and the three 25/50/75 % rings replaced
      by two at r ≈ 45/91 px — the CSS color-stop percentages resolve
      against the farthest-corner ray (130·√2), so the mockup really shows
      two rings, the third falling outside the disc. Verified visually
      (seeded test data + automated window screenshot, then reverted):
      smooth falloff, no visible banding, green phosphor look matching the
      approved HTML preview.
- [x] Mesh Topology: fixed-bearing polar layout + packet-dot fix + 300 px
      canvas (2026-07-15): the force-directed simulation was replaced by a
      deterministic polar layout — master pinned at center, each slave at a
      fixed evenly-spaced bearing (3 slaves = 120° apart, assigned by
      ascending id, first at 12 o'clock, reshuffled only when the slave set
      changes) and **only the radius moves with RSSI** (strong ≈ 42 px,
      weak ≈ 122 px; multi-hop child = parent radius + RSSI-scaled hop
      segment; unreachable = rim). Radius changes are eased by a ~30 fps
      exponential lerp (`LayoutTick`, `RadiusLerpRate` 3 s⁻¹) so RSSI
      jitter glides instead of jumping — `MeshNodeVisual`/`MeshEdgeVisual`
      became mutable ObservableObjects so the ticker drags nodes, edges,
      packet paths and the OTA line without rebuilding collections. Fixed
      along the way: the traveling packet dots had NEVER rendered on links
      — they all piled up at the canvas origin (see the new ItemsControl/
      Canvas pitfall below); verified live on the real 4-droid fleet
      (heartbeat dot caught mid-flight on a link). Drawing canvas enlarged
      260 → 300 (disc unchanged at 260, offset inner canvas at 20,20) so
      the rim's green glow and edge labels are no longer clipped.

## Full flash (virgin board support)

A PlatformIO build emits three images: `bootloader.bin` (0x1000),
`partitions.bin` (0x8000), `firmware.bin` (0x10000, the app). The console's
Firmware card and the espflash one-liner historically wrote **only the app**
at 0x10000 — which boots only if the board already carries our bootloader +
partition table. A **virgin ESP32** (or one erased / with a different
partition scheme) flashed app-only appears to flash fine but never runs.

Fix (2026-07-15): the console does a **full flash** (all three offsets) when
the two support images are available, falling back to app-only otherwise —
no user choice, no detection round-trips (rewriting an identical
bootloader/partition table on a board that already has them is harmless):

- **Local file…**: if `bootloader.bin` + `partitions.bin` sit next to the
  picked `firmware.bin` (as in `.pio/build/b1/`), full flash is armed
  automatically (`FirmwareViewModel.DetectSupportImagesBeside`).
- **From GitHub**: the firmware release now ships one **shared**
  `bootloader.bin` + `partitions.bin` (role-independent — `IS_MASTER` only
  affects the app, so ~26 KB once, not per role); the console downloads all
  three. Chosen over a single merged 0x0 image because OTA independently
  needs the bare app bin, so separate files reuse it instead of shipping the
  app twice. Wired in `firmware-release.yml` + `tools/release.ps1` (manifest
  roles `bootloader`/`partitions`), `UpdateService`, `FlashService`
  (`Start(IReadOnlyList<FlashImage>)`, sequential byte-weighted progress),
  `FirmwareViewModel` (`FullFlashReady`). Older releases without the two
  files → app-only (backward compatible). `boot_app0`/otadata (0xe000) is
  deliberately NOT shipped (a virgin chip's otadata is blank → boots app0);
  a board previously OTA'd to app1 then re-USB-flashed is the one residual
  edge case, handled by the "erase chip first" option.

## Known pitfalls

- `serial_console`: 256-byte line buffer (see the bug above).
- `IS_MASTER` lives in `config.h`: check its value before every flash (it
  goes into commits with whatever value was last used).
- `handleRaw()`: neighbor recording must stay **before** the
  `srcId==_myId` early-return and the dedup (even a relayed echo of our own
  message proves a direct radio link with the relay).
- `HeartbeatPayload` (`mesh_comm.h`): reception requires an exact `len ==
  sizeof(HeartbeatPayload)` ([main.cpp](src/main.cpp)) — any change to this
  struct's size (e.g. adding the FW version) silently breaks
  compatibility with a droid still on the old firmware: its heartbeats
  are just ignored (no error), so servos/auto-anims/FW freeze for it
  on the registry/console side. Reflash the whole fleet together on every change to
  this struct.
- Everything is now in English (GUI, code comments, docs) — see the
  2026-07-14 milestone above. `console/wwwroot/index.html` is the sole,
  deliberate exception: it stays French and untouched, as a frozen
  design reference no longer rendered at runtime.
- **WPF `Storyboard` inside a `DataTemplate` (e.g. `ItemsControl.ItemTemplate`)
  must target animated `Transform`s by name** (`x:Name` + `Storyboard.TargetName`),
  never via an implicit compound path like
  `(Ellipse.RenderTransform).(RotateTransform.Angle)`. When the template is
  instantiated more than once (≥2 items), WPF freezes/shares the unnamed
  Freezable declared directly in markup across the clones; the first
  `Storyboard.Begin()` then throws `InvalidOperationException: Cannot
  animate '(0).(1)' on an object instance that cannot be modified`
  (surfaces as an unhandled `XamlParseException` crashing the whole app).
  Only reproduces with ≥2 items — a single-item test looks fine. Also:
  `Storyboard.TargetName` cannot be set from inside a `Style.Triggers`
  (`Style` has no NameScope) — a `DataTrigger`-driven `BeginStoryboard`
  that needs to target a named sibling element must live in
  `DataTemplate.Triggers` instead (see `MeshTopologyCardView.xaml`'s
  master-ring and heartbeat-pulse-ring animations for the fixed pattern).
  Static, one-off elements outside any repeating template (e.g. the
  topology card's starfield/radar-sweep) are unaffected.
- ESP32Servo abandoned (double-attach bug) → native LEDC only.
- DFPlayer: can't report a track's duration; the BUSY pin (GPIO4)
  is the only way to observe when playback ends.
- KyberEditor (`C:\Program Files\KyberEditor`): UX inspiration source for the
  console and origin of `tools\espflash.exe`; its firmwares/bootloaders are of no
  use to us (PlatformIO generates ours).
- A single GitHub repo for the app and the firmware: never use the
  `/releases/latest` API (it ignores the tag prefix and would mix the two
  trains) — always list `/releases` and filter by prefix (`v` excluding `fw-`
  for the app, `fw-` for the firmware), see `GetLatestReleaseAsync` in
  `console/Services/UpdateService.cs`. **And never trust the order of the
  `/releases` list**: observed sorted lexicographically by tag
  (`fw-v1.3.9` before `fw-v1.3.10`/`fw-v1.3.11`), not chronologically — the
  console flashed a 1.3.9 thinking it was the latest. Parse the
  versions and take the semantic maximum.
- In an `ItemsControl` whose `ItemsPanel` is a `Canvas`, `Canvas.Left`/
  `Canvas.Top` bound inside the `DataTemplate` only work if they sit on the
  **template root** — each item gets wrapped in a `ContentPresenter`, which
  is the Canvas's real direct child, so the attached properties set on a
  deeper element are silently ignored and every item renders at the canvas
  origin (0,0). Fix: make the template root a `Canvas` (position children
  absolutely inside it) — the Nodes template always did this; the packet
  dots didn't and piled up invisible in the corner until 2026-07-15.
- WPF named color `Transparent` is **transparent white** (`#00FFFFFF`), not
  transparent black like CSS's `transparent`. In a gradient toward an opaque
  dark color, the interpolation passes through semi-transparent greys and
  paints a visible grey haze (seen as a grey ring over the radar disc's
  vignette). Always spell out `#00RRGGBB` matching the opaque stop's RGB
  (e.g. `#00000000` → `#94000000`).
- WPF `Setter.TargetName` can't target a named `Freezable` nested inside
  a property (e.g. a `TranslateTransform` in `Border.RenderTransform`, a
  `DropShadowEffect` in `Border.Effect`): the `Trigger` must replace the whole
  parent property with a new object rather than naming the child.
- `DockPanel.LastChildFill` defaults to `True`: the **last** child ignores
  its own `Dock` and stretches to fill the remaining space — a classic pitfall
  for a group meant to stay stuck to an edge (e.g. the header's connection
  controls); set `LastChildFill="False"` if every child must
  respect its `Dock`.
- `IS_MASTER` has two distinct configuration mechanisms, don't confuse them:
  `[env:b1]` (local flash/dev) reads the value hardcoded in `config.h`;
  `[env:b1_master]`/`[env:b1_slave]` (CI release) ignore it and force the role
  via `-D IS_MASTER=1|0`. Editing `config.h` never affects the latter two.
- `OtaGuard::earlyCheck()` (`ota_guard.cpp`) must remain the very first
  line of `setup()` — any code that crashes before this call is never counted
  by the anti-brick mechanism (residual risk accepted, see the OTA section).
- `OtaSlave::processChunk()`: `Update.write()` is sequential/append-only. A
  retransmitted ack for an already-written chunk must **never** call
  `Update.write()` again — only the re-ack should repeat, otherwise the written
  image is silently corrupted.
- `Update.begin/write/end` (real SPI flash access: sector erase every
  ~21 chunks of 190 B, MD5 over the whole image at `end`) must **never**
  run from the ESP-NOW callback (Wi-Fi task) or under
  `portENTER_CRITICAL` — systematic freeze/panic at chunk 21 (first
  overflow of `Update`'s 4 KB sector buffer). Hence `OtaSlave`'s mailbox:
  the `on*()` (callback) only drop the raw message,
  `update()` (loop()) validates, writes to flash, and acks, outside the lock.
- `OTA_CHUNK_DATA_MAX` (`mesh_comm.h`) is authoritative on the firmware side and announced
  to the console via `evt:otaReady.chunkSize` — never hardcode it on the
  C# side (`OtaService.cs` reads it dynamically).
- **Timestamps written by the ESP-NOW callback (Wi-Fi task)** (registry's
  `lastSeen`, OtaMaster's `_lastSendMs`/`_serialWaitSince`, etc.): they can
  be LATER than the `now` captured at the start of `loop()`. Any subtraction
  `now - timestamp` must be compared as **signed** (`(int32_t)(diff) >
  threshold`) or clamped — in unsigned math, the negative difference overflows to ~4e9:
  timeouts that fire instantly (OTA bug fw ≤ 1.3.7) or `age` at 4 billion
  in `evt:droids` that crashed `HandleDroids` on the console side.
- `ProtocolClient.OnLineReceived` isolates every line in a try/catch: a
  malformed line from the firmware must NEVER kill the read loop (silent
  death of the link, historically) or the application. Don't "simplify" by
  removing this guard.
- `Registry` (`registry.{h,cpp}`, fw 1.3.12): same precautions as
  `OtaMaster`/`OtaSlave` — `seen/setServos/setAutoAnim/setFwVersion` are
  called from the ESP-NOW callback (Wi-Fi task) while `loop()` reads
  via `count()/at()` (which now returns a **copy**, never a
  reference into the mutable array). Any new public method must
  lock `_mux`; NVS access (`Config.isAdopted()` inside `seen()`)
  must stay **outside** the lock (flash access forbidden under
  `portENTER_CRITICAL`, same lesson as the OTA freeze at chunk 21).

## Verification (reminders)

1. `pio run -e b1` builds (also test `IS_MASTER 0`).
2. Smooth servo sweep; `MSG_ANIM` relayed ≥ 2 hops without a broadcast storm.
3. Master anim → associated sound track; 2 different group keys ignore each other.
4. Console connected: droid list, anim/volume/name, persistence after reboot.
5. Sequence saved → master reboot → `Play` works without a PC.
6. Topology: move a slave out of the master's direct range → its direct
   link disappears from the graph, relayed links remain.
7. OTA — **only on a spare board, never a droid in service** —
   all these points are ✅ validated at the bench (2026-07-14, fw 1.3.12, via
   `tools/ota-test.ps1`, see Progress):
   nominal transfer (progress, `otaResult{ok:true}`, `evt:droids.fw` up
   to date) ✅; corrupted `.bin` → `ERR_MD5` at the end, no reboot ✅; serial
   abort (close the console mid-transfer) → auto-abort on the master's side,
   next session clean ✅; rollback — test build that crashes right after
   `earlyCheck()` (`-D OTA_TEST_FORCE_CRASH`), pushed via OTA, must revert
   on its own to the old image after `OTA_MAX_BOOT_ATTEMPTS` failed boots ✅;
   anti-double-session guard (`otaError "busy"`) ✅.
   Remaining: multi-hop (3rd board) and a first OTA on a real droid.
