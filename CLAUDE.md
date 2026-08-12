# CLAUDE.md — B1 Chat project (multi-droid B1 Battle Droid control)

Single project tracking file (merger of the old `project.md` and the console's
CLAUDE.md — **keep it up to date after every completed step**, explicit
request from the user).

This file is reloaded as context on every turn, so it holds what must be known
*before* touching anything: architecture, protocol, storage, pitfalls, and what
is currently open. Detail that is only needed once you are working on a given
area lives in the documents below — and must be updated in the same commit as
the behavior it describes.

| Document | Holds | Read it when |
| --- | --- | --- |
| [docs/SEQUENCER-BEHAVIOR.md](docs/SEQUENCER-BEHAVIOR.md) | shipped Sequencer runtime behavior: telemetry, stop levels, transport/navigation, scheduler, editing, persistence, Scene workflow | touching the console Sequencer |
| [docs/SEQUENCER-HARDENING.md](docs/SEQUENCER-HARDENING.md) | tracked backlog: `SEQ-*` items, status markers, decision log, dated evidence log | picking up the next Sequencer work item |
| [FIRMWARE-CONTRACT.md](FIRMWARE-CONTRACT.md) | console ↔ firmware protocol contract and its implementation status | changing the serial protocol |
| [TEST-PROTOCOL.md](TEST-PROTOCOL.md) | what `self-test.ps1` and the bench scripts cover, and what is deliberately excluded | before/after a validation run |
| [PROGRESS-ARCHIVE.md](PROGRESS-ARCHIVE.md) | full chronological history, older milestone blocks, and the full incident narratives | investigating why something is the way it is |
| [docs/hardware/](docs/hardware/) | servo-hub PCB concept and reduced V1 test-board scope | working on the carrier board |

## Overview

A single git repo (`stefe2/B1_Chat`), two halves:

1. **ESP32 firmware** (repo root, PlatformIO/Arduino): drives several B1
   droid heads (2 pan/tilt servos each) over a **multi-hop ESP-NOW mesh**
   network, with smooth/organic animations coordinated by a **master**
   (sound is played by the **console**, client-side — the master's own
   DFPlayer was retired, see the Progress log). Settings persisted in NVS.
2. **Supervision console** (`console/`, WPF net8.0-windows, v0.10.x): a
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

## Git commit preference

Use commit messages that are a little more detailed than a single generic
sentence. Keep a clear imperative summary, then add a short body describing
the important user-visible or architectural changes and the validation that
was performed. Mention notable risks or intentionally excluded work when
relevant. Avoid vague messages that do not make the commit's scope clear from
the history.

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
- `.\tools\self-test.ps1 [-SkipSerial] [-SkipBuild] [-ComPort COMx]` — safe
  autonomous preflight: builds both firmware roles + the console, runs the
  headless Sequencer suite, checks the hardening invariants, and (unless
  `-SkipSerial`) does read-only serial checks against a discovered master. Never
  flashes, never moves servos, never bumps `console/build.number`.
- `dotnet test console.tests\b1-chat-console.Tests.csproj` — the Sequencer suite
  alone (also run by `self-test.ps1`). 139 test methods, ~181 cases with theories.
- `.\tools\sequencer-bench-test.ps1` / `.\tools\anim-exec-test.ps1` — active bench
  scripts (real fleet): Sequencer preflight/movement and headless animation
  execution-report lifecycle. Movement requires an explicit opt-in switch.

**Automatic firmware release** (`.github/workflows/firmware-release.yml`): triggers
on push to `main` touching `src/config.h`, or manually (`workflow_dispatch`). Reads
`FW_VERSION`, skips if the `fw-vX.Y.Z` tag already exists (idempotent), otherwise builds
`b1_master`/`b1_slave` (PlatformIO on a GitHub runner), computes the SHA-256 manifest
with each role's content-derived Build ID, tags
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
| `main.cpp` | setup()/loop(), module wiring, non-blocking timers, bounded ESP-NOW callback→loop inbox |
| `config.h` | role, pins, default servo limits, mesh/audio/topology constants |
| `mesh_comm.{h,cpp}` | ESP-NOW: header {srcId,seq,ttl,type}, dedup (srcId,seq), TTL relay, truncated 8-byte HMAC-SHA256, **direct radio neighborhood** (physical sender MAC + RSSI) |
| `mesh_topology.{h,cpp}` | (master) aggregator of directed {from,to,rssi} edges of the neighborhood graph |
| `servo_engine.{h,cpp}` | native 50 Hz LEDC PWM, smootherstep easing, idle noise, calibratable limits |
| `animation.{h,cpp}` | 18 keyframe anims, non-blocking player, variation seed, `totalDurationMs()` |
| `registry.{h,cpp}` | (master) live inventory: srcId, RSSI, lastSeen, servos, autoAnim (synchronized access, see pitfalls) |
| `config_store.{h,cpp}` | NVS: per-droid names, anim params, servo calibration, adoption |
| `sequence_store.{h,cpp}` | **deleted fw 1.7.0** (was: master NVS, 8 named sequence slots) — sequences are console-only now |
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
cryptographic guarantee). The sequence/dedup/direct-neighbor caches are protected
across the Wi-Fi and Arduino loop tasks. Valid non-OTA messages are copied into a
bounded 32-frame inbox and processed from `loop()`; OTA keeps its pre-existing
callback-safe lock/mailbox fast path.

| Type | Payload |
| --- | --- |
| `MSG_ANIM` = 1 | targetId (0xFFFF = all), animId, syncDelayMs, seed; high bit of syncDelayMs requests execution telemetry while preserving the legacy payload |
| `MSG_CONFIG` = 2 | targetId, freq, amplitude, speed |
| `MSG_HEARTBEAT` = 4 | uptime, state (bit0 = servos, bit1 = auto anims, bit2 = Locate), firmware version (3 bytes major/minor/patch), Build ID; the master also accepts the frozen legacy 8-byte form |
| `MSG_SERVO` = 5 | targetId, enabled |
| `MSG_CALIB` = 6 | targetId, 6 legacy pan/tilt limits (persisted by the targeted droid) |
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
| `MSG_ANIM_EXEC` = 17 | originSeq, animId, phase, reason, atMs — authenticated non-blocking lifecycle report (`started`/`completed`/`interrupted`/`rejected`) |
| `MSG_ANIM_LEASED` = 18 | tracked infinite animation with initial fail-closed lease |
| `MSG_ANIM_LEASE_RENEW` = 19 | renews only the correlated active leased animation |
| `MSG_SAFE_STOP` = 20 | targetId — centered servo-powered hold with spontaneous motion suppressed |
| `MSG_CALIB_V2` = 21 | targetId, 6 limits, PAN/TILT Reverse flags; sent after legacy `MSG_CALIB` |
| `MSG_CAPABILITIES` = 22 | source droid feature bits, including independent servo Reverse support |

## Animations (18, aligned firmware ↔ `ANIMS` table in index.html)

0 IDLE · 1 LOOK_AROUND · 2 NOD_YES · 3 SHAKE_NO · 4 CURIOUS_TILT · 5 SCAN_SLOW ·
6 ALERT_SNAP · 7 TRACK · 8 GLITCH_STUTTER · 9 CONFUSED_TILT · 10 DOUBLE_TAKE ·
11 SLEEPY_DROOP · 12 TARGET_LOCK · 13 WHIRR_SEARCH · 14 SIGNAL_GLITCH ·
15 GREETING_NOD · 16 POWER_DOWN (**loops**) · 17 TALK (**loops**, fast tilt like
a talking mouth, meant to accompany an audio track).

The two looping gestures are excluded from the random idle draw.
`totalDurationMs()` returns a finite gesture's nominal duration, one nominal
cycle for POWER_DOWN/TALK (3600/300 ms), and 0 for immediate IDLE. The structured
duration catalog distinguishes these meanings; its legacy `ms` field retains a
2 s indicative value for IDLE/looping gestures only for old web clients.
Idle behavior: the master picks a random gesture every 2.5-5s and
broadcasts it to everyone (isolated slave: 3-7s, local) — suspendable per droid
("Auto anims"), without cutting the servos or blocking Play/Sequencer.

## JSON serial protocol (console ↔ master, 115200 baud, 1 line = 1 message)

Session guarded by a handshake: `hello` → `{evt:"hello",ok,id}`, then keepalive
`ping` (5s timeout on the firmware side, `_clientReady`).

- **Console → master** (`cmd`): `hello` · `ping` · `list` · `getConfig {target?}` · `getAll` ·
  `config {target,freq,amp,speed}` · `name {id,name}` ·
  `servo {target,enabled}` · `autoAnim {target,enabled}` ·
  `locate {target,enabled}` ·
  `adopt {target}` · `forget {target}` ·
  `anim {target,animId,seed,requestId?,leaseMs?}` ·
  `animLease {target,meshSeq,leaseMs}` · `safeStop {target}` ·
  `preview {target,pan,tilt}` ·
  `calib {target,+6 limits,panReversed?,tiltReversed?}` · `getCalib {target}` · `getAnimDurations` ·
  `getMeshTopology` ·
  `setMulti {ops:[...]}` · `commit` ·
  `otaStart {target,size,md5}` · `otaChunk {seq,data}` (data = base64) · `otaAbort {}`
- **Master → console** (`evt`): `hello {ok,id,fw,build,proto,lineMax,anims,caps[],dirty}` ·
  `droids {list:[{id,name,rssi,age,role,servos,autoAnim,locate,adopted,fw,build?,servoReverse?}]}` ·
  `log {msg}` · `err {msg}` · `config {target,freq,amp,speed}` ·
  `calibData {target,+6,panReversed,tiltReversed}` ·
  `meshTopology {links:[{from,to,rssi}]}` ·
  `animDurations {list:[{animId,ms,kind,nominalMs,frameCount,settleMs?}]}` ·
  `animAccepted {requestId,target,animId,meshSeq,meshQueued,local,leaseMs?}` ·
  `animExec {requestId,droid,meshSeq,animId,phase,reason?,atMs}` ·
  `setMultiDone {ok,applied,failedAt?,error?}` · `dirty {dirty}` · `allDone` ·
  `otaReady {target,sessionId,chunkSize,totalChunks}` · `otaChunkAck {seq,sent,total}` ·
  `otaDone {target,sessionId}` · `otaResult {target,ok,fw?,build?,reason?}` ·
  `otaError {target?,sessionId?,reason}`

Unknown fields in a command: ignored (the console may be newer than the
firmware). Responses routed exclusively on `evt`. Line buffer: 4 KB
(`lineMax` announced at handshake; any longer line → `err`).

**Sequencer and animation runtime behavior now lives in
[docs/SEQUENCER-BEHAVIOR.md](docs/SEQUENCER-BEHAVIOR.md)** (moved there
2026-08-12): telemetry states, infinite-gesture cleanup/lease, stop levels,
transport and navigation, scheduler, editing policy, edit transactions, type
boundaries, import/schema, Dirty/persistence and the Scene document workflow.
Keep that file updated in the same commit as any behavior change.

The invariants below stay here because they are safety- or
compatibility-critical, and must not be broken silently:

- **Execution telemetry is observational**: it never gates, delays or stops the
  timeline, and it proves firmware execution only — not physical movement or
  mechanical inter-droid skew.
- **`requestId` is mapped onto the existing mesh sequence**, so `AnimPayload`
  stays byte-compatible with older slaves. Don't widen the mesh payload to
  correlate console commands.
- **Pause is not a hardware stop**: already dispatched gestures keep running,
  and the UI must keep saying so (`PAUSED · DROID MOTION CONTINUES`).
- **Three distinct stop levels, never collapsed into one control**: Stop
  (targeted IDLE for its own infinite gestures only), Safe Stop (`safeStop`
  broadcast — centered, holding torque kept, automatic motion suppressed),
  Emergency Stop (persistent `servo enabled:false` — torque removed, an
  unsupported head may fall; explicitly accepted for this project).
- **Sequencer-started TALK/POWER_DOWN carry a 5 s firmware lease** renewed every
  2 s and correlated to the originating mesh sequence; the manual Animation card
  and autonomous idle gestures stay deliberately unleased.
- **Persistent editing happens only in `Stopped`**: the pass being performed is
  immutable.
- **Export writes `b1-sequence` v5**; import accepts v1–v5 through named
  migrations. A field whose *meaning* changes gets a new name instead of a
  lenient reader — see Known pitfalls.
- **Older firmware degrades, never breaks**: `safeStop`, `animLease`,
  `animAccepted` and `animExec` are additive and capability-gated.

**No audio in this protocol** (fw 1.6.0): `volume`/`playTrack` (console→master)
and `config`'s `volume` field were removed when the DFPlayer was retired —
see the Progress log.

**No sequences in this protocol either** (fw 1.7.0, proto 4): the whole `seq*`
family — `seqList`/`seqLoad`/`seqSave`/`seqDelete`/`seqRun`/`seqStop`/
`seqPause`/`seqResume`/`seqState` commands, the matching `seqList`/`seqData`/
`seqSaved`/`seqDeleted`/`seqState` events, `hello`'s `seqSlots` field and the
`seqTimeline`/`seqPause` caps — was removed along with the master's 8 NVS
sequence slots and its onboard player (see the Progress log). Sequences are
entirely console-driven: the console fires per-step `anim` commands from its
one scheduler and stores sequences locally (Local Library + `.b1seq.json`
export, both carrying the droid roster for offline layout).

**Commit** (per-droid anim params, names — not calibration or sequences): setters are
"live" (RAM overlay), NVS is only written on `commit`. The console auto-commits
2s after the last change (debounced, see `ProtocolClient.ScheduleAutoCommit`)
instead of offering a manual save — the header only shows a passive "unsaved"
badge now. The console must also send `commit` after a restore `setMulti`. The
manual `{cmd:"revert"}` (discard the RAM overlay, reload the persisted state)
was removed in fw 1.8.0/proto 5 — nothing needed it once auto-commit landed.
[FIRMWARE-CONTRACT.md](FIRMWARE-CONTRACT.md) tracks the implementation status
(§3/§4/§5 done; §1/§2 removed fw 1.6.0 — audio track, retired with the DFPlayer).

**Droid adoption** (`registry`/`config_store`): a droid never seen before
(`adopted:false` in `evt:droids`) stays in the mesh (broadcast anims received
normally) but is absent from individual controls until the console has
sent `adopt`. `adopt` persists the status in NVS (survives master reboots);
`forget` removes the entry from the registry **and** clears its NVS status — a
droid "forgotten" this way, or whose adoption was declined, therefore asks again as soon as it
talks again. The "lost" badge (4s of silence, `DROID_TIMEOUT_MS`) never
re-triggers this question on its own.

## Firmware OTA (slaves, relayed by the mesh)

An adopted slave is reflashed **without USB** from the "Flash (OTA)" button on
its Droids-card row. The `.bin` crosses the serial link (console → master,
base64-encoded) then the ESP-NOW mesh (master → targeted slave) in
`stop-and-wait`: one 190-byte fragment in flight at a time, ack required before
the next, because the slave's `Update.write()` is sequential/append-only with no
out-of-order handling. **Only one session at a time across the whole fleet.**

Flow: `otaStart` (size + MD5) → master validates the target against the registry
→ `MSG_OTA_START` → `evt:otaReady` → one `otaChunk` pushed per received
`evt:otaChunkAck` → last chunk acked → `MSG_OTA_END` → `evt:otaDone` (master
done, slave reboots) → master watches the target's heartbeats until
`OTA_REBOOT_WAIT_MS` (~90 s) → `evt:otaResult`.

Rules that must not be relaxed:

- **The success verdict is identity-based, not version-based.** The console
  cannot know the version baked into an arbitrary `.bin`, so success means the
  content-derived Build ID changed between `otaStart` and the post-reboot
  heartbeat. Semantic version is only the fallback for a legacy slave without a
  Build ID; otherwise `reason:"unchanged"` or `reason:"unreachable"`.
- **`OTA_REBOOT_GRACE_MS` (5 s) exists because of a real observed failure**: the
  slave reboots ~250 ms after its END ack, so one last heartbeat from the *old*
  image can still arrive and used to render a false "rolledBack" 940 ms after
  `otaDone`. A genuine rollback takes ≥ 10-30 s.
- **Anti-brick** (`ota_guard.{h,cpp}`): an NVS flag is armed before
  `Update.end(true)` (which already checks size/MD5 and refuses to reboot into an
  invalid image). Past `OTA_MAX_BOOT_ATTEMPTS` (3) failed boots the firmware
  itself switches partition via `esp_ota_set_boot_partition` — a manual rollback
  through `esp_ota_ops`, because ESP-IDF's bootloader rollback isn't simply
  exposed under `framework=arduino`. Running `OTA_VERIFY_UPTIME_MS` (~20 s)
  without a reset clears the flag; an `esp_task_wdt` (10 s) catches a new image
  that hangs without yielding.
- **Residual risk accepted**: a crash occurring *before* `OtaGuard::earlyCheck()`
  (first line of `setup()`) is never counted or caught. Mitigated in practice by
  the MD5/format check performed before any reboot — the remaining case is "valid
  image that crashes almost instantly".
- **Realistic duration**: ~5,240 fragments for a ~1 MB image → **8 to 15 minutes**
  per slave, up to 20-30 min over a weak or multi-hop link. Shown as fragments
  sent out of total, never as a promised fixed duration.

Full design notes and bench observations:
[PROGRESS-ARCHIVE.md](PROGRESS-ARCHIVE.md).

## Old reference web page (`console/wwwroot/index.html`)

Frozen French HTML+CSS+JS page, **kept intact and no longer rendered** (the
WebView2 shell is gone). It survives only as the original design/behavior spec
the WPF rewrite was checked against — and is the sole deliberate exception to
the English-everywhere rule. Its WebView2 transport vocabulary
(`listPorts`/`open`/`write`/`flash`/`libList`/…) does **not** apply to the WPF
side, which calls `Services/SerialLinkService.cs` + `Services/ProtocolClient.cs`
directly; only the firmware `cmd`/`evt` protocol is shared. Card-by-card
description in [PROGRESS-ARCHIVE.md](PROGRESS-ARCHIVE.md).

## Console architecture (`console/`) — native WPF (XAML/MVVM)

Complete rewrite (2026-07-13): the old WebView2 shell is replaced by a
**100% XAML** UI, card by card, driven by `CommunityToolkit.Mvvm`
(`[ObservableProperty]`/`[RelayCommand]`). `index.html` stays on disk, intact,
as a design/behavior reference (section above) but is no longer
loaded by the application.

| Folder/file | Role |
| --- | --- |
| `MainWindow.xaml(.cs)` | header (logo, connection status, "unsaved" auto-commit badge, "Firmware…"/"Help" buttons) + card grid |
| `FirmwareWindow.xaml(.cs)` | separate window hosting `Views/FirmwareCardView` (espflash flashing + GitHub update), opened from the header button |
| `HelpWindow.xaml(.cs)` | separate window: table-of-contents sidebar + one continuous `FlowDocumentScrollViewer` assembled from `Help/docs/*.md` (native, via `Markdig.Wpf` — deliberately not WebView2); menu clicks jump to sections and scrolling synchronizes the active menu page |
| `CalibrationWindow.xaml(.cs)` | separate window hosting `Views/CalibrationCardView`, opened from each Droids-card row's ⛭ "Configure" button — pre-targeted at that row's droid before the window shows, same singleton-reopen pattern as `FirmwareWindow`/`HelpWindow` |
| `SceneBrowserWindow` / `SceneDecisionWindow` / `SceneNameWindow` `.xaml(.cs)` | modal Scene dialogs (2026-08-12): searchable library browser, save/discard/cancel replacement decision, and themed Scene-name prompt. All three are app-owned and themed; only Import/Export uses a native file picker |
| `App.xaml(.cs)` | composition root: converters + merged resource dictionaries |
| `Themes/Theme.xaml` | palette (brushes), button/LED/mesh-node gradients — ported from index.html's CSS custom properties |
| `Themes/Effects.xaml` | shared styles: `CardBorderStyle`, `BeveledButtonStyle`, `HaloBadge*Style`, `MetalSliderStyle`, `DarkComboBoxStyle`, `CardIconBoxStyle`, `MeshNodeEllipseStyle`, dark `ScrollBar` (implicit, app-wide), etc. |
| `Models/` | `Droid`, `MeshNodeVisual`/`MeshEdgeVisual`, sequences, calibration, `HelpManifest`/`HelpSection`/`HelpPage` — view-bound objects — plus the Sequencer's explicit boundaries: `SequenceSnapshot` (persistent document only), `SequencerPlaybackPlan` (immutable runtime pass), `AnimationDurationMetadata`, `SequenceLibraryModels` |
| `ViewModels/` | `MainViewModel` + one per card (`DroidsViewModel`, `CalibrationViewModel`, `AnimationViewModel`, `FirmwareViewModel`, `MeshTopologyViewModel`, `SequencerViewModel`) + `HelpViewModel` (standalone, no `ProtocolClient` dependency — Help content is local-only). There is no `AudioViewModel`: the Audio card left with the DFPlayer (fw 1.6.0) and Sequencer audio is edited in the timeline |
| `Views/` | one XAML `UserControl` per card (no more Activity card) |
| `Services/SerialLinkService.cs` | native serial port (`System.IO.Ports`), auto-reconnect (3s) |
| `Services/ProtocolClient.cs` | central state: parses incoming JSON `evt`, builds outgoing `cmd` (C# equivalent of JS's `sendCmd()`/`handleEvent()`) |
| `Services/UpdateService.cs` / `FlashService.cs` / `LibraryService.cs` / `SettingsService.cs` | GitHub updates, espflash flashing, local sequence library, `settings.json` |
| `Services/OtaService.cs` | drives an OTA session (one slave at a time): reads the `.bin`, computes the MD5, sends one fragment per `evt:otaChunkAck` received |
| `Services/AudioPlaybackService.cs` | console-side Sequencer audio (the master's DFPlayer was retired fw 1.6.0 — this is the only audio source now): tracks several concurrent `MediaPlayer`s (one per active clip, optionally looping), `PauseAll`/`ResumeAll` for real Play pause/resume, plus a one-off probe for a picked file's duration |
| `Services/SequencerAbstractions.cs` | the test seams (SEQ-E01): injectable monotonic clock, timer, protocol sender, audio player and dialog boundaries — what lets `console.tests` run playback headlessly |
| `Services/SequencerEditHistory.cs` | begin/commit/cancel edit transactions + bounded newest-first Undo/Redo (50 each), with no WPF or playback dependency |
| `Services/SequenceImportService.cs` / `SequencerPersistenceServices.cs` | side-effect-free strict parser/migrator for `b1-sequence` v1–v5, and atomic sibling-temp + rename Export/save writing |
| `Services/AnimationDurationProvider.cs` | single source for each gesture's kind (immediate/finite/infinite), effective tail, target-speed-aware range, provisional state and inspector text — consumed by geometry, active highlighting, cached total and the playback plan |
| `Services/PlaybackGeneration.cs` / `WaveformService.cs` | per-pass generation/cancellation identity; audio waveform peak decoding for the timeline |
| `Services/DarkTitleBar.cs` | recolors the native Win32 title bar dark (`DwmSetWindowAttribute`, Windows 11 22H2+) to match the app's own header — applied to all 7 app-owned windows |
| `Converters/` | `BoolToStyleConverter`, `BoolToTextConverter`, `BoolToVisibilityConverter`, `BoolToBrushConverter`, `StrengthToBrushConverter` (mesh link color by RSSI), `TimelineGeometryConverter`/`TimelineActiveConverter`/`AnimFamilyToBrushConverter` (Sequencer timeline), `MarkdownToFlowDocumentConverter` (Help window) |
| `Help/manifest.json` + `Help/docs/**/*.md` | in-app Help content: sections → pages (same shape as KyberEditor's own Help viewer), rendered by `HelpWindow`/`HelpViewModel` — copied to the output dir as Content, not embedded |
| `b1-chat-console.csproj` | auto-incremented build number, version from `VersionPrefix`, `IncludeNativeLibrariesForSelfExtract`, `tools/` (espflash + app-local VC143 x64 runtime) excluded from the single-file but copied on publish |
| `console.tests/` (repo root, `b1-chat-console.Tests.csproj`) | headless xUnit suite for the Sequencer (SEQ-H01): playback plan/integration, transport state boundaries, edit history, import/persistence, Scene library, duration provider, plus `Fixtures/Sequences/sequence-v1..v4.json` golden files. Runs without WPF UI or hardware and must not bump `console/build.number` |
| `installer/b1-chat-console.nsi` + `release.ps1` | NSIS installer + GitHub release script (tag `vX.Y.Z`) |

Main grid layout (`MainWindow.xaml`, reorganized 2026-07-19): Droids (left
column) · Mesh Topology (right column, same row) · Animation (full width) ·
Sequencer (full width, bottom). Firmware and Servo Calibration are both out
of the grid, in separate windows — Firmware via the header button,
Calibration via each Droids-card row's ⛭ "Configure" button (pre-targeted
at that row's droid). This paragraph previously described a Calibration/Mesh
Topology/Audio arrangement that had already drifted from the actual code
(the Audio card was removed with the DFPlayer, fw 1.6.0) — corrected in the
same pass that moved Calibration out.

## Storage

| What | Where |
| --- | --- |
| Names, per-droid anim-param cache, calibrations, adoption status | Master's NVS (`config_store`); each droid also persists its own name/anim params/calibration locally |
| Scenes | Console only: normal Save/Save As uses the versioned Local Library; `.b1seq.json` Export/Import is the external-copy path. Both retain droid roster and linked audio paths. The master's 8 NVS slots were removed in fw 1.7.0. |
| Scene library, recoverable trash, last port, last Scene ID/external path | `%LOCALAPPDATA%\B1ChatConsole\` (`library\*.b1scene.json`, `library\trash\`, and `settings.json`) |
| Console-side audio lanes (label + clips, each a file path/duration/start/loop) | Stored inside the sequence library/export JSON; audio bytes remain at their original PC paths |
| OTA anti-brick flag (pending/attempts) | NVS of **each droid** flashed via OTA, separate `"ota"` namespace (`ota_guard`) |

## Progress

Full detailed history: see [PROGRESS-ARCHIVE.md](PROGRESS-ARCHIVE.md).

**Still open:**
- [ ] Step 6: `droid.{h,cpp}` state machine.
- [ ] Help window, phase 2 remainder (not started): a per-card "?" button
      opening Help directly on that card's page (`HelpViewModel.OpenAtPage`,
      mapping in the plan file `regarde-dans-ce-répertoire-swift-dawn.md`).
- [ ] Sequencer hardening backlog — **the tracked source of truth is
      [docs/SEQUENCER-HARDENING.md](docs/SEQUENCER-HARDENING.md)** (epics A–K,
      status markers, decision log DEC-001…023, dated evidence log). Keep that
      file current instead of duplicating item status here. Still open as of
      2026-08-12: EPIC F audio robustness (1/8 — probe timeout/typed errors,
      zero-duration clips, `MediaPlayer` lifecycle, stale waveform, audio-loop
      endpoint), EPIC G preflight (0/14 required, incl. P0 SEQ-G04 unterminated
      infinite gestures and SEQ-G05 implicit-broadcast insertion), SEQ-E05
      explicit end/Loop semantics, SEQ-A08 arming lifecycle, and validation
      items SEQ-H02/H04/H05/H06/H08.
- [ ] Hardware gates still open: SEQ-F01 measured gesture durations, SEQ-J01
      full-erase inert boot, SEQ-J02 visible PAN/TILT Reverse, and SEQ-H07
      (operator-confirmed motion, inter-droid skew, WPF Pause/Resume with
      simultaneous PC audio, disconnect/offline/weak-link). Rendered-UI checks
      for SEQ-G14…G18 in the Release console are also pending.
- [ ] Servo hub PCB: concept and reduced V1 test-board scope are specified in
      [docs/hardware/PCB-V1-TEST.md](docs/hardware/PCB-V1-TEST.md) (deferred
      ideas in [PCB-CONCEPT.md](docs/hardware/PCB-CONCEPT.md)); no schematic or
      routed board yet.

**Recent milestones** (2026-08-12):
- Sequencer transport is now conventional and non-destructive: one
  Play/Pause/Resume toggle (`Space`), an explicit `Restart` (`Ctrl+Enter`), and
  Stop/Safe/E-STOP that retain the playhead with a separate `Return to start`
  (`Ctrl+Home`). A second Play press can no longer resend choreography from
  zero. Added an operator-controlled `Follow` mode (15–72 % comfort corridor)
  and pointer-anchored `Ctrl+wheel` zoom (1.15/notch, 20–300 px/s) plus
  `Shift+wheel` pan.
- The Scene is edited like a document: the raw Local Library list under the
  timeline was replaced by a New/Open/Save bar, a secondary menu and a
  searchable modal Scene browser, with explicit save/discard/cancel replacement
  and an explicit stop decision when a pass is running. All app-owned windows
  now share the dark native title bar; status pills lost their color-bleeding
  glow.
- Gesture durations are explicit and target-aware: firmware reports structured
  immediate/finite/infinite metadata, one console provider reproduces the
  10–100 % speed clamp and ±60 ms jitter, and broadcast clips aggregate every
  online target with a visible mixed-speed warning. Schema v5 persists a real
  POWER_DOWN/TALK endpoint (`endAfterMs`) with ownership-safe IDLE termination.
- Playback cost is bounded: unchanged 1.5 s droid telemetry no longer rebuilds
  tracks/ruler/duration (the visible UI hitch is gone), and ruler generation
  spans milliseconds→hours under a strict 600-tick ceiling shared by all three
  ruler consumers.
- Validation state at build 357: `tools\self-test.ps1 -SkipSerial` passes 19/19
  (both firmware roles + console build clean, headless Sequencer suite green,
  build number preserved).

Earlier milestone blocks (2026-08-11 and 2026-07-19) were moved verbatim to
[PROGRESS-ARCHIVE.md](PROGRESS-ARCHIVE.md) on 2026-08-12. Add new milestones
at the top of this section and push the previous block down into the archive
when it stops being the current one.

## Full flash vs app-only (virgin boards, NVS safety)

A PlatformIO build emits three images: `bootloader.bin` (0x1000),
`partitions.bin` (0x8000), `firmware.bin` (0x10000, the app). The console's
Firmware card and the espflash one-liner write **only the app** by default,
which boots only if the board already carries our bootloader + partition table.

Operational rules — every one of them was paid for at the bench on 2026-07-15;
the full narratives (what broke, why, and how it was diagnosed) are in
[PROGRESS-ARCHIVE.md](PROGRESS-ARCHIVE.md), section *Incidents*:

- **App-only is the default** ("New / erased board" unchecked): writes `Address`
  (0x10000) only and never touches the partition table. This is the only safe
  "update" mode for a board whose NVS must survive.
- **Full flash is tied to the "New / erased board (full erase + flash)"
  checkbox**, never auto-armed from file presence. It requires both support
  images (`FirmwareViewModel.SupportImagesAvailable`), otherwise `Flash()`
  blocks with an explanatory error plus an inline
  `NeedsSupportImagesWarning`. It is the only path that rewrites the partition
  table, and it already discards NVS through the chip erase — so the table can
  never change while NVS is expected to survive.
- **Any board that has completed even one OTA session needs the full-erase path
  for its next USB flash.** App-only writes a fixed 0x10000 (app0) without
  touching otadata, so the bootloader keeps booting app1: the flash "succeeds"
  and the old image silently keeps running.
- Support images come from beside the picked `.bin`
  (`DetectSupportImagesBeside`, e.g. `.pio/build/b1/`) or from the release's
  shared role-independent `bootloader.bin` + `partitions.bin`
  (`firmware-release.yml`, `tools/release.ps1`, `UpdateService`,
  `FlashService.Start(IReadOnlyList<FlashImage>)`). `boot_app0`/otadata
  (0xe000) is deliberately never shipped. Older releases without the two files
  stay app-only.

## Per-droid name resilience (`MSG_NAME`)

Droid **names** used to live only in the master's own NVS (`config_store`,
keyed by srcId), unlike servo calibration which each droid already persisted
locally on receipt of `MSG_CALIB`. One partition-table shift therefore lost or
resurrected every droid's name at once.

Fix (2026-07-15): renaming (`cmd:"name"` and the `setMulti`/restore path in
`applyOp`) also relays `MSG_NAME` (targetId + name[24]); the targeted droid
persists it immediately via `ConfigStore::setNameImmediate()`, bypassing the
master's commit draft — that draft is a master-side display concern, unrelated
to what a remote droid should keep. Mirrors how `applyCalib` already behaves.
Additive message: an older slave simply ignores it, so no fleet-wide reflash is
required (unlike a `HeartbeatPayload` change).

## Known pitfalls

- **Never rewrite the partition table on a board whose NVS must survive**,
  even with bytes that look identical to what's already there — see
  "Full flash vs app-only" above. A generation/offset mismatch between the new table and the
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
- `serial_console`: the historical 256-byte line buffer bug (fixed by the 4 KB
  buffer + explicit `err`, see [PROGRESS-ARCHIVE.md](PROGRESS-ARCHIVE.md)) —
  any new line-oriented parsing must respect the announced `lineMax`.
- `IS_MASTER` lives in `config.h`: check its value before every flash (it
  goes into commits with whatever value was last used).
- `handleRaw()`: neighbor recording must stay **before** the
  `srcId==_myId` early-return and the dedup (even a relayed echo of our own
  message proves a direct radio link with the relay).
- `HeartbeatPayload` (`mesh_comm.h`): Build ID enlarged this payload. The master
  explicitly accepts both the current struct and the frozen 8-byte
  `LegacyHeartbeatPayload`, recording Build ID 0 for legacy nodes. Preserve that
  dual decoder during rolling upgrades; any future payload-size change needs an
  equally explicit compatibility form instead of silently freezing telemetry.
- Everything is now in English (GUI, code comments, docs) — see the
  2026-07-14 milestone in [PROGRESS-ARCHIVE.md](PROGRESS-ARCHIVE.md).
  `console/wwwroot/index.html` is the sole,
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
- Animation duration variation must stay signed until after it is bounded.
  Casting a negative jitter term to `uint16_t` wraps it near 65 seconds; use
  `clampMoveDurationMs()` before narrowing. Animation keyframes are offsets,
  so they must also use `ServoEngine::setTargetOffset()` rather than adding
  the compile-time `SERVO_*_CENTER` constants themselves — the latter ignores
  each droid's persisted calibration center.
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
- Firmware/support-image assets obtained from GitHub must have a valid
  64-character SHA-256 from `firmware_manifest.json`; a missing or unreadable
  manifest is a hard failure, never a reason to silently download and label
  the file as verified.
- WPF slider debounces must snapshot both the target ID and values when they
  are armed, and cancel when selection changes. Reading `SelectedTarget` from
  the delayed callback can apply a previous droid's edit to the newly selected
  droid. Programmatic calibration/config loads must also suppress their change
  hooks so opening a card does not write the values straight back.
- In an `ItemsControl` whose `ItemsPanel` is a `Canvas`, `Canvas.Left`/
  `Canvas.Top` bound inside the `DataTemplate` are **silently ignored — even
  on the template root**: each item gets wrapped in a `ContentPresenter`,
  which is the Canvas's real direct child, and the attached properties never
  transfer to it, so every item renders at the canvas origin (0,0). The only
  reliable fix: make the template root a `Canvas` and position a *child*
  element absolutely inside it. The mesh packet dots hit this until
  2026-07-15; it recurred 2026-07-16 in the Sequencer timeline grid (row
  backgrounds all piled on row 0 — read as a "broadcast tint" — and every
  vertical gridline stacked at x=0), caught during the visual-polish pass.
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
- **Timestamps written after `now` was captured at the start of `loop()`**
  (registry `lastSeen` while pumping the mesh inbox, or OtaMaster fields from
  the callback fast path): they can be LATER than that `now`. Any subtraction
  `now - timestamp` must be compared as **signed** (`(int32_t)(diff) >
  threshold`) or clamped — in unsigned math, the negative difference overflows to ~4e9:
  timeouts that fire instantly (OTA bug fw ≤ 1.3.7) or `age` at 4 billion
  in `evt:droids` that crashed `HandleDroids` on the console side.
- `ProtocolClient.OnLineReceived` isolates every line in a try/catch: a
  malformed line from the firmware must NEVER kill the read loop (silent
  death of the link, historically) or the application. Don't "simplify" by
  removing this guard.
- `Registry` (`registry.{h,cpp}`): incoming application messages are now
  handled from `loop()` via main.cpp's bounded inbox. Its public methods stay
  synchronized defensively and `at()` returns a **copy**, never a reference
  into the mutable array. NVS access (`Config.isAdopted()` inside `seen()`)
  must stay **outside** the lock (flash access is forbidden under
  `portENTER_CRITICAL`, same lesson as the OTA freeze at chunk 21).
- `DarkComboBoxStyle`'s `ControlTemplate` renders the *closed* selected value
  via `SelectionBoxItem`, which falls back to `ToString()` rather than
  respecting `DisplayMemberPath` — any model bound as a ComboBox's
  `ItemsSource` needs its own `ToString()` override, or the closed box shows
  the bare CLR type name instead of the intended label (the dropdown list
  itself still renders fine via `DisplayMemberPath`; only the collapsed
  view is affected). Hit twice independently: `TimelineTrack` (Sequencer,
  2026-07-16) and `Droid` (Calibration window, 2026-07-19, only surfaced
  once Calibration could be opened standalone outside the main grid) —
  check for this on any *new* ComboBox-bound model before it ships.

## Verification (reminders)

Automated safe preflight: `.\tools\self-test.ps1` (see
`TEST-PROTOCOL.md`). This complements rather than replaces the physical checks
below.

1. `pio run -e b1` builds (also test `IS_MASTER 0`).
2. Smooth servo sweep; `MSG_ANIM` relayed ≥ 2 hops without a broadcast storm;
   2 different group keys ignore each other.
3. Console connected: droid list, anim/name, persistence after reboot.
4. ~~Sequence saved to a slot, master reboot, `seqRun` → plays without a PC~~ —
   **obsolete since fw 1.7.0**: the onboard sequence player and its 8 NVS
   slots were removed outright (sequences are console-driven only). Instead:
   console `Play` on a loaded sequence fires the right `anim` commands on the
   right droids at the right times, and Pause/Resume/Stop behave.
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
