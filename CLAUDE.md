# CLAUDE.md — B1 Chat project (multi-droid B1 Battle Droid control)

Single project tracking file (merger of the old `project.md` and the console's
CLAUDE.md — **keep it up to date after every completed step**, explicit
request from the user).

## Overview

A single git repo (`stefe2/B1_Chat`), two halves:

1. **ESP32 firmware** (repo root, PlatformIO/Arduino): drives several B1
   droid heads (2 pan/tilt servos each) over a **multi-hop ESP-NOW mesh**
   network, with smooth/organic animations coordinated by a **master**
   (sound is played by the **console**, client-side — the master's own
   DFPlayer was retired, see the Progress log). Settings persisted in NVS.
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
| Life LED | GPIO2 (onboard) |

- Servos on external 5V (BEC), common ground, ≥ 470 µF capacitor recommended.
- Audio (DFPlayer Mini + PAM8403 amp on GPIO16/17/4) was **retired from the
  firmware** (fw 1.6.0, see Progress log) — the console now owns audio
  entirely client-side. The wiring itself is unaffected (physically
  unplugging it is a separate, optional hardware task, out of scope for the
  firmware change).
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
| `registry.{h,cpp}` | (master) live inventory: srcId, RSSI, lastSeen, servos, autoAnim (synchronized access, see pitfalls) |
| `config_store.{h,cpp}` | NVS: names, anim params, per-droid servo calibration |
| `sequence_store.{h,cpp}` | (master) NVS: 8 named sequence slots, ≤ 32 steps {targetId,animId,delayMs} |
| `serial_console.{h,cpp}` | (master) USB JSON ↔ mesh bridge for the console |
| `ota_guard.{h,cpp}` | (all roles) anti-brick: NVS flag + manual rollback to the other partition if the new firmware doesn't start correctly |
| `ota_master.{h,cpp}` | (master) orchestrates an OTA session toward a slave (stop-and-wait, retry, post-reboot confirmation via heartbeat) |
| `ota_slave.{h,cpp}` | (slave) receives an OTA image relayed by the mesh, writes via `Update` |
| `droid.{h,cpp}` | high-level state machine (step 6, **not done yet**) |

Dependencies: `ArduinoJson`. Build flags: `-D MESH_TTL=4`,
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
| `MSG_LOCATE` = 15 | targetId, enabled — overrides the onboard LED's execution-indicator blink with solid on/off ("find me" physically), not persisted |
| `MSG_NAME` = 16 | targetId, name[24] — persists the targeted droid's own name in its own NVS (mirrors `MSG_CALIB`), never `MESH_TARGET_ALL` |

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
  `config {target,freq,amp,speed}` · `name {id,name}` ·
  `servo {target,enabled}` · `autoAnim {target,enabled}` ·
  `locate {target,enabled}` ·
  `adopt {target}` · `forget {target}` ·
  `anim {target,animId,seed}` · `preview {target,pan,tilt}` ·
  `calib {target,+6 limits}` · `getCalib {target}` · `getAnimDurations` ·
  `getMeshTopology` · `seqList` · `seqLoad {slot}` ·
  `seqSave {slot,name,loop,totalMs,steps:[{animId,target,start}]}` · `seqDelete {slot}` ·
  `seqRun {slot,fromMs?}` · `seqStop` · `seqPause` · `seqResume` · `seqState` ·
  `setMulti {ops:[...]}` · `commit` · `revert` ·
  `otaStart {target,size,md5}` · `otaChunk {seq,data}` (data = base64) · `otaAbort {}`
- **Master → console** (`evt`): `hello {ok,id,fw,proto,lineMax,anims,seqSlots,caps[],dirty}` ·
  `droids {list:[{id,name,rssi,age,role,servos,autoAnim,adopted,fw}]}` ·
  `log {msg}` · `err {msg}` · `config {freq,amp,speed}` · `calibData {target,+6}` ·
  `meshTopology {links:[{from,to,rssi}]}` · `animDurations {list:[{animId,ms}]}` ·
  `seqList {list:[{slot,name,stepCount,loop}]}` ·
  `seqData {slot,name,loop,totalMs,steps:[{animId,target,start}]}` ·
  `seqSaved {ok,slot,name}` · `seqDeleted {ok,slot}` ·
  `seqState {playing,slot,elapsedMs,totalMs,paused}` ·
  `setMultiDone {ok,applied,failedAt?,error?}` · `dirty {dirty}` · `allDone` ·
  `otaReady {target,sessionId,chunkSize,totalChunks}` · `otaChunkAck {seq,sent,total}` ·
  `otaDone {target,sessionId}` · `otaResult {target,ok,fw?,reason?}` ·
  `otaError {target?,sessionId?,reason}`

Unknown fields in a command: ignored (the console may be newer than the
firmware). Responses routed exclusively on `evt`. Line buffer: 4 KB
(`lineMax` announced at handshake; any longer line → `err`).

**No audio in this protocol** (fw 1.6.0): `volume`/`playTrack` (console→master)
and `config`'s `volume` field, plus `track`/`audioStartMs` on
`seqSave`/`seqData`/`seqList`/`seqState`, were all removed when the DFPlayer
was retired — see the Progress log. `seqRun`/`seqStop`/`seqPause`/`seqResume`
and the master's own onboard sequence player are unaffected (still fully
functional), just no longer triggered from the console's Sequencer UI (Play
is console-driven now, see that Progress entry).

**Commit/revert** (anim params, names — not calibration or
sequences): setters are "live" (RAM overlay), NVS is only written on
`commit`; `revert` reloads the persisted state. The console must send `commit`
after a restore `setMulti`. [FIRMWARE-CONTRACT.md](FIRMWARE-CONTRACT.md)
tracks the implementation status (§3/§4/§5 done; §1/§2 removed fw 1.6.0 —
audio track, retired with the DFPlayer).

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
| `Services/AudioPlaybackService.cs` | console-side Sequencer audio (the master's DFPlayer was retired fw 1.6.0 — this is the only audio source now): tracks several concurrent `MediaPlayer`s (one per active clip, optionally looping), `PauseAll`/`ResumeAll` for real Play pause/resume, plus a one-off probe for a picked file's duration |
| `Services/SequenceAudioStore.cs` | client-only slot→audio-lanes (each a label + clip list) association for the 8 NVS slots (`slot-audio.json`) — the master's NVS has no room for a filesystem path |
| `Converters/` | `BoolToStyleConverter`, `BoolToTextConverter`, `BoolToVisibilityConverter`, `BoolToBrushConverter`, `StrengthToBrushConverter` (mesh link color by RSSI), `TimelineGeometryConverter`/`TimelineActiveConverter`/`AnimFamilyToBrushConverter` (Sequencer timeline) |
| `b1-chat-console.csproj` | auto-incremented build number, version from `VersionPrefix`, `IncludeNativeLibrariesForSelfExtract`, `tools/` (espflash) excluded from the single-file but copied on publish |
| `installer/b1-chat-console.nsi` + `release.ps1` | NSIS installer + GitHub release script (tag `vX.Y.Z`) |

Main grid layout (`MainWindow.xaml`): Droids (left column,
full height) · right column stacked Calibration → Mesh topology →
(Animation + Audio row side by side, equal width) · Sequencer full
width at the bottom. Firmware card taken out of the grid (separate window).

## Storage

| What | Where |
| --- | --- |
| Names, anim params, calibrations, adoption status | Master's NVS (`config_store`) |
| Sequences (8 slots, ≤ 32 steps) | Master's NVS (`sequence_store`) |
| Sequence library, last port | `%LOCALAPPDATA%\B1ChatConsole\` (console side) |
| Per-slot console-side audio lanes (label + clips, each a file path/duration/start/loop) | `%LOCALAPPDATA%\B1ChatConsole\slot-audio.json` (console side, keyed by NVS slot number — see the Sequencer audio Progress entries below) |
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
- [x] Droids card: "Locate" button (2026-07-15, fw 1.4.0, `MSG_LOCATE` = 15):
      a per-droid toggle overrides the onboard LED's normal execution-indicator
      blink with solid on/off, so a physical droid can be matched to its row
      — a mesh round-trip (console → master → relayed to the targeted slave),
      not a local-only master feature, same target/broadcast semantics as
      `MSG_SERVO`/`MSG_AUTOANIM` (`applyLocate()`, `gLocateOn` in
      `main.cpp`, checked first in the life-LED block of `loop()`). Ephemeral
      by design — not persisted in NVS, not carried in the heartbeat: a
      reboot or console restart silently drops back to the normal blink,
      consistent with `MSG_PREVIEW`'s "transient, not persisted" precedent.
      Console: `Droid.LocateOn`, `ProtocolClient.SetLocate`,
      `DroidsViewModel.ToggleLocateCommand`, styled as a `HaloToggleButtonStyle`
      `ToggleButton` (red border off / green on, matching the Master/Slave
      role selector in the Firmware card) rather than the sliding
      `OnOffSwitchStyle` used for Servos/Auto anims, since it's a momentary
      "identify" action, not a persistent setting. Also wired into the mesh
      topology's live packet-dot visualization (`PacketSent` event, lime
      `#C6FF4D`, legend row) for consistency with every other targeted
      command. Same Droids-card pass: the ID column was dropped (RSSI now
      follows VERSION directly — the hex id was rarely load-bearing once a
      droid has a name), the Servos/Auto anims columns were narrowed
      (90/90 → 60/70 px) to sit closer together, and the row-end "✕"
      (forget) button — briefly moved next to Auto anims in an earlier pass
      — was moved back after Update, at the row's end.
- [x] Sequencer: absolute-time timeline model (2026-07-16, fw 1.5.0, proto 3,
      `caps: seqTimeline`), replacing the old chained relay-only player.
      `SeqStep.delayMs` (relative, chained) → `startMs` (absolute offset from
      the sequence's own t=0); `StoredSequence` gained `totalMs` (explicit
      loop/end boundary — no longer implied by "the last step") and
      `audioStartMs` (the audio track's own cue point, previously implicit
      at step 0). `main.cpp`'s player (`sortStepsByStart` + `gSeqStartMs`
      anchor + `gSeqNextFireIdx`) can now fire several steps in the same
      `loop()` pass — the actual point of the rework: droids can start
      **together**, not just relayed one after another. Traded away:
      `gSeqWaitLocal` (the one guarantee that the master's own gesture in a
      running sequence never got cut short) — a later local step now
      interrupts it exactly like any live `MSG_ANIM`, matching what already
      happened to remote slaves (no mesh ack, never had that guarantee).
      Protocol: `seqSave`/`seqData` steps carry `start` (was `delay`) plus
      top-level `totalMs`/`audioStartMs`; `seqRun` takes `fromMs` (was
      `from`, a step index — now a scrub offset); `seqState` reports
      `elapsedMs`/`totalMs` (was `index`/`total`). Fields renamed, not just
      reinterpreted, specifically so an old console/firmware pairing drops
      an unrecognized field instead of misreading it (see Known pitfalls).
      **Breaking, on purpose**: `SequenceStore`'s NVS blob now requires an
      exact size match, so a sequence saved before this rework reads back
      as "not found" rather than replaying with the wrong timing — the 8
      slots on any master already in service need re-saving from the
      console after the update. Console (`SequenceStep.StartMs`,
      `ProtocolClient.SeqSave` computing `totalMs` server-side so every save
      path — slot save, library push — stays consistent, local rehearsal
      rewritten from one chained timer to one timer per step so steps
      sharing a `StartMs` actually fire together) adapted to match; the flat
      step-list editor itself (raw numeric fields, no visual timeline) was
      superseded shortly after by the real multi-track timeline below.
- [x] Sequencer: real multi-track visual timeline (2026-07-16), replacing the
      flat step-list editor entirely. New `Views/SequenceTimelineView`
      (embedded in `SequencerCardView`, `SequencerViewModel` as `DataContext`,
      no new ViewModel): one horizontal track per droid + a synthetic
      "All droids" broadcast row (`Models/TimelineTrack`), a ruler with
      zoomable ticks (`Models/TimelineTick`, 20-300 px/s slider), draggable
      colored gesture clips positioned/sized via `Converters/
      TimelineGeometryConverter` (one converter, `Left`/`Width`/`Top`/
      `Duration` `ConverterParameter` modes) and colored by family via
      `Converters/AnimFamilyToBrushConverter`, an inspector panel
      (gesture/target/start-time with ±0.1s nudge, duplicate/delete), and a
      click-to-insert gesture library row. Clip dragging is raw mouse capture
      (`SequenceTimelineView.xaml.cs` — first mouse-interaction code in this
      app, no `Thumb`/native `DragDrop` precedent existed) snapping to 100ms,
      one `PushHistory()` per drag gesture so Undo restores it in one step.
      Playhead: local scrub via the ruler when idle, or synced to real
      hardware playback via `ProtocolClient.SeqStateReceived` (previously
      unconsumed) — a 30ms `DispatcherTimer` computes position directly from
      an elapsed-time anchor (no easing, unlike Mesh Topology's telemetry
      tickers) so it can't be scrubbed while `IsLiveTracking`. Also closed a
      second previously-flagged gap: `ProtocolClient.AnimDurationMs` (fetched
      via `getAnimDurations`, previously unconsumed by any UI) now drives
      clip width, self-correcting once real durations arrive post-handshake
      (`AnimDurationsReceived` event, new). Verified live against the real
      4-droid fleet (COM3): correct per-track/per-time placement, drag +
      snap, zoom rescaling, gesture insertion on the armed track, and the
      inspector — the last found and fixed three WPF-specific bugs along the
      way (`ComboBox.SelectedValue`/`SelectedValuePath` unreliable against
      `DarkComboBoxStyle`'s fully-replaced `ControlTemplate` → replaced with
      a `SelectedItem`-bound `SelectedStepTrack` wrapper property; that
      wrapper then went stale across a `DroidsChanged`-triggered track
      rebuild until explicitly re-`OnPropertyChanged`'d; `TimelineTrack`
      needed its own `ToString()` since `DarkComboBoxStyle` renders
      `SelectedItem` via `SelectionBoxItem`, which falls back to
      `ToString()` rather than `DisplayMemberPath`). Out of scope, parked for
      a later phase: a local "My Sequences" project bin (distinct from the
      existing per-item local library) and a live gesture recorder.
- [x] Sequencer: console-side audio, DFPlayer set aside "for now" (2026-07-16):
      per explicit decision, a sequence's audio no longer comes from the
      master's DFPlayer (`AudioTrack`/track-number field kept internally for
      wire-protocol compatibility — `SeqSave` still sends it, always 0 now —
      but no longer surfaced in the UI). Instead the **console** plays a
      local audio file directly during `Rehearse (local)`, via the new
      `Services/AudioPlaybackService` (thin `System.Windows.Media.MediaPlayer`
      wrapper, no new NuGet dependency — also used to probe a picked file's
      exact duration, `ProbeDurationMsAsync`, opening the file just long
      enough to read `NaturalDuration`). `SequencerViewModel` gained
      `AudioFilePath`/`AudioDurationMs` (browse/clear commands, undo/redo,
      export/import, local-library round-trip); `TotalDurationMs()` (ruler
      extent, rehearsal end timer) now takes `Math.Max` of the steps' span
      and the audio's real duration, so a long audio-only sequence still
      gets a correct ruler/loop boundary. The timeline gained a dedicated,
      non-arm-able "♪ AUDIO" row showing the file as a bar from t=0 (fixed,
      not draggable — rehearsal always starts it at the pass's own t=0, no
      offset UI yet). Because the master's 8 NVS slots have no room for a
      filesystem path, the slot↔file association lives **client-side only**,
      in the new `Services/SequenceAudioStore` (`slot-audio.json`, see
      Storage table) — a slot pulled/pushed/deleted from a different console
      install or after a local cache wipe simply has no audio attached
      (same "no worse than before" fallback as a missing local-library
      item). Real-hardware playback sync (starting the file in sync with a
      live `Play` on the mesh, using the already-wired `audioStartMs`) was
      explicitly deferred — confirmed with the user as scope for a later
      pass, current scope is `Rehearse (local)` only. Verified live against
      the real fleet (COM3): file picked via the new "…"/"✕" controls next
      to the (now-unused) Loop checkbox, `AudioDurationMs` probed correctly
      and reflected on the ruler/audio bar, `Rehearse (local)` starts the
      file (`AudioPlaybackService.Play`) together with the gesture timers and
      auto-stops it cleanly at the end of the pass with no exceptions.
      **Superseded the same day** by the multi-lane/multi-clip rework below —
      the single `AudioFilePath`/`AudioDurationMs` fields and the toolbar's
      "…"/"✕" picker no longer exist.
- [x] Sequencer: multi-lane/multi-clip audio, gesture drag-and-drop, per-track
      mute (2026-07-16), replacing the single-audio-file model above the same
      day, plus three more editing features requested in the same batch —
      confirmed via two `AskUserQuestion` rounds and a written plan before
      any code changed, per this project's confirm-before-coding rule.
      **Audio**: `AudioFilePath`/`AudioDurationMs` → `Models/AudioLane`
      (named row, e.g. "AUDIO"/"AMBIENT") each holding an
      `ObservableCollection<Models/AudioClip>` (`FilePath`, `DurationMs`,
      `StartMs`, `Loop`). Clips are independently draggable in time and may
      freely overlap **within** a lane (no collision handling — layered
      rendering only); a lane's "+" adds a clip via file picker, a clip's
      right-click `ContextMenu` (new `Themes/Effects.xaml` dark
      `ContextMenuStyle`/`MenuItemStyle` — first `ContextMenu` use in this
      app) offers Replace file…/Loop/Delete. `AudioPlaybackService` now
      tracks a `List<MediaPlayer>` instead of one, so several clips (e.g. an
      SFX plus a looping ambient bed) play concurrently; `Loop` wires
      `MediaEnded` to restart the player, torn down by `StopAll()` at the end
      of a rehearsal pass (never outlives `IsRehearsing`). Persistence
      (`SequenceAudioStore` → `slot-audio.json`, `SequenceLibraryItem`,
      `SequenceSnapshot`, export/import) all moved from a single path+
      duration pair to `List<AudioLaneDto>` (nested `AudioClipDto` list) —
      breaking, on purpose, same day the singular shape shipped, so no
      migration path was written (see the sequence-timeline-rework pitfall
      below on reinterpreting a stored field's meaning — this instead
      replaced the shape outright before anything depended on the old one).
      **Gesture retargeting**: dragging an existing clip now also updates
      `SequenceStep.Target` from the drag's Y position (new
      `SequencerViewModel.TrackAtY(double)`, `TimelineTrack.RowHeight`/
      `RowGap` math), alongside the existing horizontal `StartMs` drag —
      one `Undo` restores both axes together (still a single
      `BeginStepDrag()`/`PushHistory()` per gesture, not per pixel).
      **Gesture-library drag-and-drop**: chips gained a click-vs-drag
      threshold (5px) in `SequenceTimelineView.xaml.cs` — under threshold
      falls back to the existing click-inserts-on-armed-track-at-playhead
      behavior unchanged; past it, a floating ghost (`DragGhostCanvas`, a
      `Panel.ZIndex="999"` overlay Canvas spanning the whole card, positioned
      every `MouseMove` via `Canvas.SetLeft/Top`) follows the cursor and
      dropping over `TracksCanvas` calls the new `SequencerViewModel.
      InsertGestureAt(animId, track, startMs)` with the drop's actual
      track+time instead of the armed-track/playhead defaults — still no
      native `DragDrop.DoDragDrop` anywhere in this app, same raw-mouse-
      capture idiom as every other drag here, just driving a manually
      positioned ghost element instead of repositioning a real item.
      **Per-track mute**: `TimelineTrack` gained `[ObservableProperty] bool
      Muted` (the class itself went from a plain POCO to an `ObservableObject`
      for this one property) plus a small `Button` in each gutter row
      (`ToggleMuteCommand`) — deliberately a real `Button`, not a `Border`+
      `MouseBinding` like the row's own arm-click, because `ButtonBase`
      marks its `Click` handled and stops it bubbling to the row's
      `MouseBinding`; a `Border`+`MouseBinding` mute toggle would have also
      armed the track underneath it on every click. `RebuildTracks()` (fired
      on every heartbeat-driven `DroidsChanged`, wholesale-replacing
      `Tracks`) now carries `Muted` forward by `Id` from the previous
      generation, same pattern already used for `ArmedTrack`, so a mute
      doesn't silently reset a few seconds later. **Mute only ever affects
      `Rehearse (local)`** (`ScheduleRehearsalPass()` skips arming a timer
      for a muted target) — a real hardware `Play` cannot honor it: `seqRun`
      just starts the master, which then replays its own NVS-stored steps
      from its own `loop()`, and the console has no per-step veto over that
      once it's been sent. Mute state is not saved with the sequence (not in
      `Snapshot()`/export/slot data) — it's a live editing/audition aid, reset
      whenever a sequence is loaded/created. Verified live against the real
      4-droid fleet (COM3): two overlapping SFX clips on "AUDIO" dragged
      independently, a looping clip added to "AMBIENT", an existing gesture
      clip dragged from one droid's row to another's (retargeted correctly,
      confirmed in the inspector), a library chip dragged directly onto a
      specific droid+time cell (landed exactly there, distinct from a plain
      click), a track muted and dimmed, and a full `Rehearse (local)` pass
      with all of the above active at once — ran cleanly, stopped exactly at
      the computed total duration, no exceptions.
- [x] Sequencer: waveform preview + cross-lane audio drag (2026-07-16), two
      more additions to the same batch above. **Waveform**: new `NAudio`
      NuGet dependency (`console/Services/WaveformService.cs`) — the only
      decoder in this app with raw sample access, `AudioPlaybackService`'s
      `MediaPlayer` has none. Decodes a clip's file to a fixed 120-point
      min/max-style peak envelope (0..1) off the UI thread, cached by file
      path (`ConcurrentDictionary`, shared across every clip that happens to
      reference the same file) so re-showing or duplicating a clip never
      re-decodes. `AudioClip.Peaks` (new, nullable `float[]`) is populated
      asynchronously from every clip-creation path (`AddAudioClip`,
      `ReplaceAudioClip`, `ApplyAudioLanesFromDto` on load/import/library
      pull). Rendered via `Converters/WaveformToGeometryConverter.cs`
      (`Peaks` → a filled `PathGeometry` in a fixed `x:[0,119] y:[0,2]`
      domain) hosted inside a `Viewbox Stretch="Fill"` layered behind the
      clip's label — the fixed-domain geometry rescales to whatever pixel
      width the clip currently has (zoom, drag) without ever recomputing
      peaks. Not persisted (`Peaks` is derived, not part of `AudioClipDto`)
      — reloading a sequence just re-decodes from the stored file path.
      **Cross-lane drag**: audio clips previously only moved horizontally
      within their own lane's `Canvas` — a live re-parent into another
      lane's `Canvas` mid-drag isn't practical the way it is for gesture
      clips (which all share one flat `TracksCanvas`, only `Canvas.Top`
      changes). Reused the app's existing ghost-drag idiom instead (same
      `DragGhostCanvas`/`GhostBorder` already built for the gesture-library
      drop): `SequencerViewModel.AudioLaneAtY(double)` (mirrors `TrackAtY`,
      new `AudioLane.RowHeight`/`RowGap` consts alongside the pre-existing
      `TimelineTrack` ones) is checked on every `AudioClip_MouseMove`; once
      the cursor crosses into a different lane's row, the real clip dims
      (new transient `AudioClip.Dragging` bool, not persisted) and a ghost
      follows the cursor instead, snapping back the moment it re-enters its
      origin lane. The actual move (`SequencerViewModel.
      MoveAudioClipToLane` — remove from the source lane's `Clips`, add to
      the target's, `StartMs` carried over from the live horizontal drag)
      only happens once, at `MouseUp`, inside the same `BeginAudioClipDrag()`
      `PushHistory()` already armed at `MouseDown` — one Undo restores both
      the lane and the time position together. **Also**: the two default
      seeded lanes were reordered to `AMBIENT` then `AUDIO` (was the
      reverse) per direct request — cosmetic, no data-shape change.
      Verified live (2026-07-16, once the foreground window matched on a
      retry): added a real `.wav` file to the AMBIENT lane, waveform
      rendered correctly; dragged the same clip from AMBIENT into AUDIO,
      it re-parented cleanly (source lane emptied, waveform intact in the
      new lane). `dotnet build` clean throughout.
- [x] Sequencer timeline: visual pass toward the "Sequencer v2" concept mockup
      (2026-07-16), first batch of 4 purely-visual items picked from the mockup
      by the user (screenshots + a short back-and-forth, confirmed before
      coding per this project's standing rule) — no protocol/behavior changes.
      **Track-gutter rows** (`TimelineTrack.Role`, new "MASTER"/"SLAVE"/
      "BROADCAST" caption set in `RebuildTracks()`): each row now shows the
      droid name plus a small caps role line underneath, and the old 🔇/🔊
      text `Button` was replaced by a real `OnOffSwitchStyle` `ToggleButton`
      (glossy green/red slider, already used for Servos/Auto anims/Locate) —
      `IsChecked` bound through the new `Converters/BoolInvertConverter.cs`
      so ON/green reads as "audible" rather than showing green for "muted",
      keeping the same green=active convention as everywhere else; still a
      real `ToggleButton` (not `Border`+`MouseBinding`) for the same
      click-bubbling reason as the button it replaced. **Timeline grid**:
      two new `ItemsControl`s added as the first (bottommost-rendered)
      children of `TracksCanvas` and of each audio lane's `Canvas` — one
      bound to `Tracks`/`AudioLanes` drawing a full-width row background +
      1px bottom separator (`#59000000`) per row, one bound to the *same*
      `RulerTicks` collection the ruler itself uses (so grid lines can never
      drift out of sync with the time labels) drawing 1px vertical
      `Rectangle`s at each tick, dimmer for minor ticks than major —
      broadcast row and audio lanes get a faint accent tint (`#FF9D2E` at
      3-4% opacity) matching the mockup's `.track-lane.audio`/`.broadcast`.
      **Transport bar**: the two previously-separate button rows
      (Undo/Redo/New/Save/→Library above the timeline, and
      Play/Stop/Pause/Resume/Rehearse/Export/Import below it, both in
      `SequencerCardView.xaml`) are consolidated into *one* recessed dark
      bar now living in `SequenceTimelineView.xaml`'s own toolbar row —
      round icon buttons (new `IconTransportButtonStyle`, same beveled
      chrome as every other button, just square+glyph) for Play/Stop/
      Pause/Resume, a `⟲ Loop` `HaloToggleButtonStyle` toggle (moved off
      the Name row, which now just has the name field), a live timecode
      pill (new `SequencerViewModel.TimecodeText`, "mm:ss.mmm /
      mm:ss.mmm", refreshed on every `PlayheadMs` change and on every
      ruler-tick rebuild since total duration can change), the existing
      zoom/snap, a new **Fit** button (`SequenceTimelineView.xaml.cs`,
      code-behind rather than a ViewModel command since it needs the
      `ScrollViewer`'s actual pixel width — a view concern —
      `TotalDurationMs()` made `public` for this one caller), then
      Undo/Redo/Rehearse/Export/Import/Add-audio-lane/Live-badge. `New`/
      `Save (ESP32)`/`→ Library` are the only buttons left in
      `SequencerCardView.xaml` — they don't have a mockup equivalent, so
      weren't moved. **Gutter header**: the empty 24px spacer above the
      track rows now reads "TRACKS" (small bold caps, muted gray,
      bottom-bordered), matching the mockup's `.ruler-spacer`, aligned
      with the ruler's own 24px height. Verified live (2026-07-16, once the
      foreground window matched on a later retry) against the real 4-droid
      fleet: gutter shows correct MASTER/SLAVE/BROADCAST roles and glossy
      toggles, transport bar renders and its Fit button correctly rescales
      zoom to the sequence length, "TRACKS" header aligned with the ruler.
      `dotnet build` clean throughout.
- [x] Sequencer timeline: 3 follow-up fixes from direct user feedback after
      seeing the above live (2026-07-16), found and fixed with a temporary
      red debug-`Rectangle` at canvas origin to pin down the first one
      empirically rather than guessing from a screenshot:
      **(1) Pre-existing centering bug** (not caused by this session's other
      changes — reproduced back through earlier screenshots too): the
      `ScrollViewer`'s content `StackPanel` (ruler + audio lanes + tracks)
      had no explicit `HorizontalAlignment`, so whenever the sequence is
      short/zoomed out (content narrower than the viewport), WPF's default
      content stretching centered it inside the scroll viewport instead of
      pinning it flush against the gutter — the ruler's "0.0s" origin then
      floated away from x=0 with a wide gap that scaled with window width
      (confirmed via the debug rectangle landing exactly on the gap's far
      edge, matching the ruler/playhead exactly, not just visually near it).
      Fixed with one `HorizontalAlignment="Left"` on that `StackPanel`.
      **(2) Gutter toggles at 50%**: the new `OnOffSwitchStyle` track-row
      switches (from the batch above) are now wrapped in a `LayoutTransform
      ScaleTransform 0.5/0.5` — `LayoutTransform`, not `RenderTransform`, so
      the row's `Auto`-sized column shrinks along with the visual instead of
      leaving dead space; scoped to just this one `ToggleButton` instance so
      the shared `OnOffSwitchStyle` (Servos/Auto anims/Locate in the Droids
      card) stays full-size everywhere else. **(3) Active-row highlight**: a
      new `DataTrigger` on the row `Border` (`Muted=False` → soft green
      `DropShadowEffect`, `#3DDC84` at 0.45 opacity) makes an audible/active
      track's whole row glow, not just its small switch — mirrors the same
      green already used for the toggle's own on-state. Verified live
      (screenshot comparison before/after): gap gone (ruler origin flush
      against the gutter), switches visibly smaller, "All droids" row
      glowing green while un-muted. `dotnet build` clean.
- [x] Sequencer: gesture library grouped by family + card header badge
      (2026-07-16), continuing the same visual pass as the batch above.
      **Gesture library**: the flat wrapped row of 18 chips became labeled
      rows (`SequencerViewModel.GestureFamilies`, `Models/GestureFamily.cs`)
      — "IDLE & REST", "LOOK & CURIOSITY", "AFFIRMATION", "SCAN & TRACK",
      "ALERT & GLITCH", "TALK (AUDIO-SYNCED, LOOPS)". Grouping/labels come
      from a new `AnimFamilyToBrushConverter.Families` static table (single
      source of truth, reused by the ViewModel so the grouping can never
      drift from the colors every clip/chip already uses) instead of
      duplicating the animId→family mapping a second time. The flat
      `GestureLibrary` list stays too (used by the drag-ghost's name lookup
      in `SequenceTimelineView.xaml.cs`). **Card header**: `SequencerCardView`
      gained the same icon-box + subtitle + right-aligned badge treatment
      already used by Droids/Mesh Topology — a small vector "steps" glyph,
      "Multi-track timeline — parallel per-droid lanes, absolute start
      times" subtitle, and a live badge (`SequencerViewModel.
      SequenceBadgeText`, updates on `CurrentSlot`/`Name` change) reading
      `SLOT 2 · "PARADE"` or `UNSAVED · NEW SEQUENCE`. `dotnet build` clean.
- [x] DFPlayer retired everywhere, firmware and console (fw 1.6.0,
      2026-07-16) — the console already owned multi-track audio playback
      client-side (see the Sequencer-audio entries above); this removes the
      master's own DFPlayer entirely rather than just leaving it unused.
      **Firmware**: `src/audio.{h,cpp}` deleted outright (the
      `AudioPlayer`/`Audio` wrapper, anim→track-range table); every
      `Audio.*` call site in `main.cpp` removed (`setup()`'s init, the
      per-gesture sound on `playLocalAnim`/incoming `MSG_ANIM`/the master's
      own idle-anim draw, the stored-sequence player's audio-cue block and
      its DFPlayer-only-plays-one-track-at-a-time gesture-sound
      suppression, `onSeqPauseCmd`'s `Audio.pause()/resume()`) — the
      sequence *player* itself (step firing via `anim.play()`/mesh
      broadcast) is untouched, only the DFPlayer calls interleaved with it
      are gone. `src/serial_console.{h,cpp}`: `playTrack`/`volume` command
      handlers and their `onVolume`/`onTrack` hook plumbing removed;
      `track`/`audioStartMs` dropped from `seqSave`/`seqData`/`seqList`/
      `seqState` (`pushSeqState` lost its `track` parameter entirely);
      `trackCount` dropped from the `hello` ack; the now-meaningless
      `seqTrack` capability flag removed from `caps[]`.
      `src/config_store.{h,cpp}`: `volume()`/`setVolume()` and the `"vol"`
      NVS key removed, along with their branches in `refreshDirty()`/
      `commitPending()`/`revertPending()` (volume was part of the
      commit/revert draft, same machinery as anim params/names).
      `src/sequence_store.{h,cpp}`: `track`/`audioStartMs` dropped from
      `StoredSequence`/`SequenceBlob` — the blob shrinks, and the existing
      **exact-size-match** guard (same one added for the fw 1.5.0 timeline
      rework) means every slot saved before this update reads back as "not
      found" rather than being misread — **all 8 slots need re-saving from
      the console** after updating a master past fw 1.6.0. `platformio.ini`
      lost the `dfrobot/DFRobotDFPlayerMini` dependency.
      `FIRMWARE-CONTRACT.md` §1 (audio track) and §2 (`getTrackDurations`,
      already deferred/unimplemented) marked removed. **Console**:
      `Views/AudioCardView.xaml(.cs)` + `ViewModels/AudioViewModel.cs`
      deleted outright (the whole "AUDIO (MASTER)" card — volume slider,
      test-track button); `MainWindow.xaml`'s Animation/Audio 2-column row
      collapsed to just `AnimationCardView` at full width;
      `ProtocolClient.SetVolume`/`PlayTrack`/`LastVolume`/`TrackCount` and
      the `evt:config` volume parse / `hello` trackCount parse removed;
      `SeqSave`'s `track`/`audioStartMs` parameters dropped (every caller
      updated). `SequencerViewModel.AudioTrack` (already vestigial —
      always 0, no UI control bound to it since the client-side multi-lane
      audio rework, see above) removed along with every read/write site
      (`Snapshot`/`Apply`/`NewSequence`/`OnSeqData`/`SaveToSlot`/
      `SaveToLibrary`/`LoadFromLibrary`/`PushToMaster`/`Export`/`Import`);
      `SequenceSlotMeta.Track`/`SequenceLibraryItem.AudioTrack`/
      `SequenceSnapshot.Track` dropped from their model records.
      `DroidsViewModel`'s Backup/Restore no longer reads/writes a `"volume"`
      key (old backup files with one are simply ignored on restore, same
      safe-additive-removal precedent as everywhere else this session).
      Physically unplugging the DFPlayer/amp is a separate, optional
      hardware task — out of scope here, the firmware just stops calling
      into it. Verified: `pio run` clean on `b1`/`b1_master`/`b1_slave`;
      `dotnet build` clean.
- [x] Sequencer: Play and Rehearse merged into one console-driven Play, with
      real Pause/Resume (fw unaffected, console-only — 2026-07-16). Until
      now the transport had two disconnected paths: hardware `Play`/`Stop`/
      `Pause`/`Resume` (sent `seqRun`/`seqStop`/`seqPause`/`seqResume` to
      the master, which replayed its own NVS-stored sequence — required a
      prior "Save (ESP32)") and a separate "Rehearse (local)" toggle (the
      console scheduled its own timers straight from the editor — no save
      needed — firing real per-step `anim` mesh commands plus local audio,
      but no pause/resume and no playhead feedback). Per direct request
      ("quand je presse sur Play l'animation joue dans la console avec le
      son et déclenche en même temps les animations sur les droids"): the
      old hardware-backed Play/Stop/Pause/Resume are gone, and Play now
      always works the way Rehearse did — console-driven, no slot required.
      `SequencerViewModel`: `IsRehearsing` → `IsPlaying`/`IsPaused`;
      `ScheduleRehearsalPass()` → `ScheduleTimers(int fromMs)`, now taking
      an offset so only steps/clips whose `StartMs >= fromMs` get armed
      (delay `StartMs - fromMs`) — this is what makes `Resume()` skip
      everything that already fired before a pause instead of replaying it.
      `Pause()` computes elapsed from the same anchor the playhead ticker
      already used, disposes pending timers (nothing to "pause" about a
      one-shot trigger that hasn't fired yet) and calls the new
      `AudioPlaybackService.PauseAll()` (clips already mid-playback keep
      their position natively via `MediaPlayer.Pause()` — no manual seek
      math); `Resume()` calls `PauseAll()`'s counterpart `ResumeAll()` and
      re-arms the remaining timers via `ScheduleTimers(elapsedAtPause)`.
      The hardware-`seqState`-driven playhead/LIVE-badge logic
      (`OnSeqState`) and the new console-driven path now share one
      `StartPlayheadTicker(fromElapsedMs)` helper instead of duplicating
      the anchor/`DispatcherTimer` setup, so both sources animate the
      playhead identically. Mute's documented limitation ("only affects
      Rehearse, a real hardware Play can't honor it") is now moot — Play
      *is* the console-driven mechanism, so mute applies unconditionally.
      **Accepted trade-off, confirmed explicitly**: the console no longer
      has any way to trigger the firmware's own onboard `seqRun` playback
      (the tested "sequence saved → master reboot → Play works without a
      PC" path) — the firmware keeps that capability fully intact and
      working (see fw Verification above), just nothing in the Sequencer UI
      calls it anymore. `Views/SequenceTimelineView.xaml`: the standalone
      "Rehearse (local)" button removed; the existing 4 icon buttons (▶ ■
      ⏸ ⏵) rebound to the new commands, Pause/Resume auto-disable via
      `[RelayCommand(CanExecute=...)]` (`CanPause`/`CanResume`, same pattern
      already used for Undo/Redo). Verified: `dotnet build` clean; not yet
      re-verified live against the real fleet this pass (see the DFPlayer
      entry above for the build verification already done) — needs a
      hands-on Play/Pause/Resume/Stop/Loop pass against real hardware.

## Full flash (virgin board support)

A PlatformIO build emits three images: `bootloader.bin` (0x1000),
`partitions.bin` (0x8000), `firmware.bin` (0x10000, the app). The console's
Firmware card and the espflash one-liner historically wrote **only the app**
at 0x10000 — which boots only if the board already carries our bootloader +
partition table. A **virgin ESP32** (or one erased / with a different
partition scheme) flashed app-only appears to flash fine but never runs.

First fix (2026-07-15) auto-armed a full flash whenever the two support
images happened to be available (next to the picked `firmware.bin`, or
downloaded from GitHub), on the theory that "rewriting an identical
bootloader/partition table on a board that already has them is harmless".
**That assumption was wrong and cost a droid's saved names**: `.pio/build/b1/`
*always* contains `bootloader.bin` + `partitions.bin` next to `firmware.bin`,
so every routine dev reflash silently became a full flash — including a
partition-table rewrite. On this occasion the freshly-written partition table
wasn't byte-identical to the one already on the board (drift from an earlier
PlatformIO/esp-idf default, from before this feature existed), which shifted
the *physical window* the NVS driver reads as "current" — the master's
`config_store` (which holds **every** droid's name, all in the master's own
NVS, see the Storage table) fell back to years-old pages, resurrecting a
name ("B1-Bleu") from long before it was renamed "B1-Maitre", and losing the
other droids' names outright. NVS wasn't erased — the partition table's
window onto it moved.

Corrected design (2026-07-15, same day): full flash is now **tied to the
existing "Fully erase the chip" checkbox** (relabeled "New / erased board
(full erase + flash)" in `FirmwareCardView.xaml`), instead of auto-arming
from file presence:

- Unchecked (default): **app-only** at `Address` (0x10000) — the partition
  table is never touched, so this failure mode can't recur. This is also the
  correct fix for a *second*, pre-existing latent bug: checking "erase chip"
  used to combine a full chip erase (which wipes the bootloader/partition
  table too) with an **app-only** write — leaving a board with nothing to
  boot into. Tying erase to a full 3-image write fixes both bugs with one
  change.
- Checked: full flash (bootloader + partitions + app) — but only if the two
  support images are actually available (`FirmwareViewModel.
  SupportImagesAvailable`); otherwise `Flash()` blocks with an explanatory
  error instead of proceeding (`NeedsSupportImagesWarning` also shows an
  inline warning in the card). This is the only path that ever rewrites the
  partition table, and it's also the only path that already discards NVS
  (via the chip erase) — so there's no longer a scenario where the
  partition table changes while NVS is expected to survive.

Support-image sourcing is unchanged: **Local file…** looks beside the picked
`.bin` (`DetectSupportImagesBeside`, e.g. `.pio/build/b1/`); **From GitHub**
downloads the release's shared `bootloader.bin` + `partitions.bin`
(role-independent — `IS_MASTER` only affects the app, so ~26 KB once, not per
role; chosen over a single merged 0x0 image because OTA independently needs
the bare app bin, so separate files reuse it instead of shipping the app
twice). Wired in `firmware-release.yml` + `tools/release.ps1` (manifest roles
`bootloader`/`partitions`), `UpdateService`, `FlashService`
(`Start(IReadOnlyList<FlashImage>)`, sequential byte-weighted progress).
Older releases without the two files → app-only stays the only option.
`boot_app0`/otadata (0xe000) is deliberately NOT shipped (a virgin chip's
otadata is blank → boots app0); a board previously OTA'd to app1 then
re-USB-flashed is the one residual edge case, handled by the same "erase"
checkbox.

**Confirmed at the bench (2026-07-15)**: a slave OTA'd earlier in the same
session (otadata now pointing at app1) was then USB-flashed app-only
(erase unchecked) with a newer build — the write succeeded with no error,
but the console kept reporting the OLD version after reboot. Root cause:
app-only writes a fixed address (0x10000 = app0) without touching otadata,
so the bootloader kept booting app1, silently running the old image the
whole time. Checking "New / erased board (full erase + flash)" for that
board's next USB flash (which resets otadata to blank → boots app0) fixed
it. Lesson: **a USB flash's app-only mode is only a safe "update" path for
a board never touched by OTA** — any board that has ever done even one OTA
session needs the full-erase path for its next USB flash, not just a
virgin/never-flashed board.

## Per-droid name resilience (`MSG_NAME`)

Prompted directly by the incident above: droid **names** were the one piece
of per-droid configuration that lived ONLY in the master's own NVS
(`config_store`, keyed by srcId) — never relayed to the droid itself, unlike
servo calibration (`MSG_CALIB`), which a droid already persists in its own
NVS on receipt. A slave therefore had no memory of its own name; only the
master's cache did, and that cache is exactly what a partition-table mismatch
or an intentional full erase can wipe.

Fix (2026-07-15): renaming a droid (`cmd:"name"` and the `setMulti`/restore
path in `applyOp`) now ALSO relays `MSG_NAME` (targetId + name[24]) over the
mesh; the targeted droid persists it immediately in its own NVS via the new
`ConfigStore::setNameImmediate()` — bypassing the master's own commit/revert
draft entirely (that draft is a master-side UI concern for its own display
cache, unrelated to what a remote droid should keep). The master's own
name-editing UX (the "unsaved / Save / Revert" header badge) is unchanged;
only the mesh-received copy on the OTHER droid is immediate, mirroring how
`applyCalib` already behaves. Additive mesh message — an older slave simply
ignores `MSG_NAME`, no fleet-wide reflash required (unlike a `HeartbeatPayload`
change).

## Known pitfalls

- **Never rewrite the partition table on a board whose NVS must survive**,
  even with bytes that look identical to what's already there — see "Full
  flash" above. A generation/offset mismatch between the new table and the
  old one shifts the physical window the NVS driver treats as current,
  silently resurrecting stale data or losing recent data, with no error.
  Only ever pair a partition-table write with a full chip erase (which
  discards NVS anyway) — never as a "harmless" side effect of an app update.
- **A stored field can be dangerous to reinterpret even when its size never
  changes** — same lesson as the partition-table pitfall above, one level
  up the stack. `SeqStep::startMs` (`sequence_store.h`) used to be a relative
  delay from the previous step; the fw 1.5.0 timeline rework made it an
  absolute offset from the sequence's own t=0, same `uint16_t`, same offset
  in the struct. `SequenceStore`'s blob reader deliberately requires an
  *exact* size match (`totalMs`/`audioStartMs` were appended alongside, so
  the overall blob did grow) rather than the old "accept an older, smaller
  size too" pattern used for the `track` field — that pattern is only safe
  for a field that's purely *additive* (old data still means what it always
  meant). Do not resurrect the lenient version for a field whose meaning
  changes: a pre-rework sequence would otherwise replay with completely
  wrong timing, silently. Same principle applied at the JSON layer:
  `seqRun.from` (step index) was renamed to `fromMs` (time offset) instead
  of keeping the key and changing what the number means — an old console
  or firmware on either side of the pairing just ignores the unrecognized
  field (falls back to 0) instead of misreading it.
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
- A `Button` (or anything deriving `ButtonBase`) inside an element that
  itself has a `MouseBinding` (e.g. a Sequencer track-gutter row that arms
  on click) marks its own `Click` handled, which stops the routed event
  from bubbling to the ancestor's `MouseBinding` — a real `Button` is
  therefore the correct choice for a "second click target" nested inside a
  clickable row (the per-track mute toggle uses this). A `Border`+
  `MouseBinding` for the same purpose would NOT stop the bubble, and the
  ancestor's click handler (arming the track) would fire too.
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
2. Smooth servo sweep; `MSG_ANIM` relayed ≥ 2 hops without a broadcast storm;
   2 different group keys ignore each other.
3. Console connected: droid list, anim/name, persistence after reboot.
4. Sequence saved to a slot, master reboot, then `seqRun` sent directly
   (e.g. `tools/ota-test.ps1`-style serial) → still plays without a PC — the
   master's own onboard sequence player is unaffected by the fw 1.6.0 audio
   removal (**caveat**: the console's own Sequencer `Play` no longer sends
   `seqRun` at all, see the Progress log — this is no longer exercised from
   the console UI, only worth re-checking if that firmware capability is
   ever revived for something else).
5. Topology: move a slave out of the master's direct range → its direct
   link disappears from the graph, relayed links remain.
6. OTA — **only on a spare board, never a droid in service** —
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
