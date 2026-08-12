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
| `MSG_HEARTBEAT` = 4 | uptime, state (bit0 = servos, bit1 = auto anims), firmware version (3 bytes major/minor/patch), Build ID; the master also accepts the frozen legacy 8-byte form |
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
| `MSG_ANIM_EXEC` = 17 | originSeq, animId, phase, reason, atMs — authenticated non-blocking lifecycle report (`started`/`completed`/`interrupted`/`rejected`) |

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

- **Console → master** (`cmd`): `hello` · `ping` · `list` · `getConfig {target?}` · `getAll` ·
  `config {target,freq,amp,speed}` · `name {id,name}` ·
  `servo {target,enabled}` · `autoAnim {target,enabled}` ·
  `locate {target,enabled}` ·
  `adopt {target}` · `forget {target}` ·
  `anim {target,animId,seed,requestId?,leaseMs?}` ·
  `animLease {target,meshSeq,leaseMs}` · `safeStop {target}` ·
  `preview {target,pan,tilt}` ·
  `calib {target,+6 limits}` · `getCalib {target}` · `getAnimDurations` ·
  `getMeshTopology` ·
  `setMulti {ops:[...]}` · `commit` ·
  `otaStart {target,size,md5}` · `otaChunk {seq,data}` (data = base64) · `otaAbort {}`
- **Master → console** (`evt`): `hello {ok,id,fw,build,proto,lineMax,anims,caps[],dirty}` ·
  `droids {list:[{id,name,rssi,age,role,servos,autoAnim,adopted,fw,build?}]}` ·
  `log {msg}` · `err {msg}` · `config {target,freq,amp,speed}` · `calibData {target,+6}` ·
  `meshTopology {links:[{from,to,rssi}]}` · `animDurations {list:[{animId,ms}]}` ·
  `animAccepted {requestId,target,animId,meshSeq,meshQueued,local,leaseMs?}` ·
  `animExec {requestId,droid,meshSeq,animId,phase,reason?,atMs}` ·
  `setMultiDone {ok,applied,failedAt?,error?}` · `dirty {dirty}` · `allDone` ·
  `otaReady {target,sessionId,chunkSize,totalChunks}` · `otaChunkAck {seq,sent,total}` ·
  `otaDone {target,sessionId}` · `otaResult {target,ok,fw?,build?,reason?}` ·
  `otaError {target?,sessionId?,reason}`

Unknown fields in a command: ignored (the console may be newer than the
firmware). Responses routed exclusively on `evt`. Line buffer: 4 KB
(`lineMax` announced at handshake; any longer line → `err`).

**Animation execution telemetry** is observational and never gates the
console-side timeline. `WRITE` means the OS serial write completed, not that the
master received it. New masters then emit `animAccepted` after parsing and
validating the command (`MASTER` in the timeline), including whether ESP-NOW
accepted the broadcast frame and whether the master itself is a local target.
The master maps the console's `requestId` to the existing mesh-header sequence,
so `AnimPayload` remains byte-compatible with older slaves. New droids report
when the software animation engine starts, finishes, is interrupted, or refuses
the command because servos are disabled; broadcast replies are deterministically
jittered to avoid a response burst. The timeline aggregates reports per online
target (`ACK 2/3`, `DONE 3/3`, `REJ 1/3`). Local refusal is immediate (`NO LINK`,
`NOT READY`, `WRITE FAIL`); a failed required ESP-NOW queue is `MESH FAIL`. A
missing start report expires after 1.5 s
(`UNCONF`/`MISS n/N`);
finite gestures that start but do not send a terminal report expire after their
reported duration plus 1.5 s (`TIMEOUT`). Late reports recover the display, and
delayed duplicate `started` reports cannot regress a terminal state. These
warnings never delay or stop the show. Looping POWER_DOWN/TALK require only a
start report because completion requires a later interruption. Sequencer
playback starts those two gestures with a 5 s firmware lease and renews it every
2 s while the owning pass remains valid. Missing renewal returns the droid to
IDLE and reports `interrupted/leaseExpired`; renewals are correlated to the
originating mesh sequence so stale packets cannot extend a replacement gesture.
Pause and whole-pass Loop continue renewal, while Stop/end/restart/disconnect/
shutdown cancel it before targeted IDLE cleanup. Manual Animation-card commands
and autonomous animations remain unleased. This proves firmware execution, not
physical servo movement or mechanical inter-droid skew.

**Sequencer infinite-gesture cleanup:** the WPF playback controller records the
latest successfully written gesture per concrete droid. Broadcast TALK or
POWER_DOWN expands to the online roster; later targeted finite/IDLE commands
replace only their target. Stop, a non-looping natural end, application disposal,
and Play restart send tracked IDLE commands only to droids whose latest state is
still infinite. A whole-pass Loop boundary and Pause deliberately do not clean
up. Failed serial cleanup remains retryable. On firmware advertising
`animLease`, the independent 5 s lease supplies the fail-closed fallback if
cleanup cannot cross a lost serial or mesh path.

**Three stop levels:** normal Sequencer Stop cancels the transport/audio and
sends targeted IDLE only for its remaining infinite gestures; finite gestures
finish naturally. Safe Stop cancels all console work and broadcasts `safeStop`:
each current droid interrupts motion, moves to calibrated center over the normal
IDLE transition, retains servo holding torque, and transiently suppresses its
automatic animations until a later explicit animation command. Emergency Stop
has no confirmation dialog and broadcasts persistent `servo enabled:false` to
the whole fleet. It removes holding torque and may let unsupported mechanics
fall; that behavior is explicitly accepted for this project. Older firmware
without the additive `safeStop` cap receives broadcast IDLE as a best-effort
fallback but cannot suppress subsequent automatic motion.

**Pause is not a hardware stop:** it freezes the PC playhead, future timer
dispatches and local audio only. Already received finite gestures continue to
completion, TALK/POWER_DOWN keep running under their renewed lease, and their
execution reports may update while paused. Play resumes undispatched events and
audio without resending gestures that continued. The transport shows
`PAUSED · DROID MOTION CONTINUES` so this behavior is never implied to be a
physical freeze; use Stop, SAFE or E-STOP for their distinct policies above.

**Sequencer transport state is single-source:** the WPF controller owns one
guarded `Stopped`/`Playing`/`Paused` value. Play/Pause badges, LIVE tracking,
editing locks and command availability are derived from it, so contradictory UI
states cannot be constructed. Play, Resume and Loop also share one pass-start
path; a scheduler startup failure invalidates the generation and returns to
Stopped after disposing partial timers/audio.

Persistent Sequencer editing is permitted only in `Stopped`: timeline content,
Loop, inspector fields, audio lanes/clips, Undo/Redo, Import/Clear, and Local
Library Load/Delete all lock during Play and Pause. Selection/inspection, track
arming, runtime track mute, zoom/Snap/Fit/scroll, and Export remain available
because they do not change the immutable pass being performed.

Sequencer command and pointer-drag mutations use one structural snapshot edit
transaction. A real commit records one pre-edit Undo snapshot, clears Redo,
marks Dirty and refreshes tracks/ruler/timecode once; a no-op commit records
nothing. Persistent DTO fields participate in comparison, while selection,
execution telemetry, waveform peaks and drag visuals remain transient. Direct
property bindings join this transaction boundary under SEQ-C03.

Sequencer clip drags use a 5 px threshold before opening a transaction. Escape,
lost mouse capture, view unload and window deactivation restore the pre-edit
snapshot and clear gesture/audio drag state, library ghosts and ruler capture;
cancelled ruler scrubbing restores its starting playhead. Inspector properties,
sequence/audio Loop, lane labels/order and sequence name now share the same
transaction path. Undo and Redo are newest-first bounded lists retaining exactly
50 snapshots; document snapshots exclude every transient editor/telemetry field.

Sequencer state responsibilities have explicit type boundaries. `SequenceSnapshot`
contains and structurally compares only persistent document DTOs;
`SequencerEditHistory` owns begin/commit/cancel and bounded Undo/Redo without any
WPF or playback dependency; `SequencerPlaybackPlan` captures immutable runtime
events. `SequencerViewModel` coordinates them while retaining transient selection,
viewport, drag visuals, waveform and execution telemetry.

Sequencer file import is validate-then-apply. `SequenceImportService` strictly
parses `b1-sequence` schemas 1–4 into a temporary `ImportedSequenceDocument`,
checks identities, bounded counts/strings/timing and target/gesture ranges, and
runs named migrations before the ViewModel mutates. V1 `delayMs` values are
cumulative waits after the current gesture, producing absolute starts from the
sum of prior delays. Retired numeric DFPlayer `audioTrack` metadata is validated
but intentionally discarded; it cannot identify a console-side audio file.

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
own timers and stores sequences locally (Local Library + `.b1seq.json`
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
the content-derived Build ID reported **after** reboot to the one from **before**
the OTA (captured at `otaStart` time). Semantic version remains a fallback for a
legacy slave without a Build ID — `ok:true` if either identity changed,
`reason:"unchanged"` if both are identical, `reason:"unreachable"` if no
heartbeat arrives within the delay. Grace window (`OTA_REBOOT_GRACE_MS`,
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
| `MainWindow.xaml(.cs)` | header (logo, connection status, "unsaved" auto-commit badge, "Firmware…"/"Help" buttons) + card grid |
| `FirmwareWindow.xaml(.cs)` | separate window hosting `Views/FirmwareCardView` (espflash flashing + GitHub update), opened from the header button |
| `HelpWindow.xaml(.cs)` | separate window: table-of-contents sidebar + one continuous `FlowDocumentScrollViewer` assembled from `Help/docs/*.md` (native, via `Markdig.Wpf` — deliberately not WebView2); menu clicks jump to sections and scrolling synchronizes the active menu page |
| `CalibrationWindow.xaml(.cs)` | separate window hosting `Views/CalibrationCardView`, opened from each Droids-card row's ⛭ "Configure" button — pre-targeted at that row's droid before the window shows, same singleton-reopen pattern as `FirmwareWindow`/`HelpWindow` |
| `App.xaml(.cs)` | composition root: converters + merged resource dictionaries |
| `Themes/Theme.xaml` | palette (brushes), button/LED/mesh-node gradients — ported from index.html's CSS custom properties |
| `Themes/Effects.xaml` | shared styles: `CardBorderStyle`, `BeveledButtonStyle`, `HaloBadge*Style`, `MetalSliderStyle`, `DarkComboBoxStyle`, `CardIconBoxStyle`, `MeshNodeEllipseStyle`, dark `ScrollBar` (implicit, app-wide), etc. |
| `Models/` | `Droid`, `MeshNodeVisual`/`MeshEdgeVisual`, sequences, calibration, `HelpManifest`/`HelpSection`/`HelpPage` — view-bound objects |
| `ViewModels/` | `MainViewModel` + one per card (`DroidsViewModel`, `CalibrationViewModel`, `AnimationViewModel`, `AudioViewModel`, `FirmwareViewModel`, `MeshTopologyViewModel`, `SequencerViewModel`) + `HelpViewModel` (standalone, no `ProtocolClient` dependency — Help content is local-only) |
| `Views/` | one XAML `UserControl` per card (no more Activity card) |
| `Services/SerialLinkService.cs` | native serial port (`System.IO.Ports`), auto-reconnect (3s) |
| `Services/ProtocolClient.cs` | central state: parses incoming JSON `evt`, builds outgoing `cmd` (C# equivalent of JS's `sendCmd()`/`handleEvent()`) |
| `Services/UpdateService.cs` / `FlashService.cs` / `LibraryService.cs` / `SettingsService.cs` | GitHub updates, espflash flashing, local sequence library, `settings.json` |
| `Services/OtaService.cs` | drives an OTA session (one slave at a time): reads the `.bin`, computes the MD5, sends one fragment per `evt:otaChunkAck` received |
| `Services/AudioPlaybackService.cs` | console-side Sequencer audio (the master's DFPlayer was retired fw 1.6.0 — this is the only audio source now): tracks several concurrent `MediaPlayer`s (one per active clip, optionally looping), `PauseAll`/`ResumeAll` for real Play pause/resume, plus a one-off probe for a picked file's duration |
| `Services/SequenceAudioStore.cs` | client-only slot→audio-lanes (each a label + clip list) association for the 8 NVS slots (`slot-audio.json`) — the master's NVS has no room for a filesystem path |
| `Services/DarkTitleBar.cs` | recolors the native Win32 title bar dark (`DwmSetWindowAttribute`, Windows 11 22H2+) to match the app's own header — applied to all 4 windows |
| `Converters/` | `BoolToStyleConverter`, `BoolToTextConverter`, `BoolToVisibilityConverter`, `BoolToBrushConverter`, `StrengthToBrushConverter` (mesh link color by RSSI), `TimelineGeometryConverter`/`TimelineActiveConverter`/`AnimFamilyToBrushConverter` (Sequencer timeline), `MarkdownToFlowDocumentConverter` (Help window) |
| `Help/manifest.json` + `Help/docs/**/*.md` | in-app Help content: sections → pages (same shape as KyberEditor's own Help viewer), rendered by `HelpWindow`/`HelpViewModel` — copied to the output dir as Content, not embedded |
| `b1-chat-console.csproj` | auto-incremented build number, version from `VersionPrefix`, `IncludeNativeLibrariesForSelfExtract`, `tools/` (espflash + app-local VC143 x64 runtime) excluded from the single-file but copied on publish |
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
| Sequences | Console only (`.b1seq.json` export/import, with droid roster and audio paths) — the master's 8 NVS slots were removed in fw 1.7.0; the current Local Library UI can only load/delete pre-existing entries |
| Sequence library, last port, last exported/imported sequence path | `%LOCALAPPDATA%\B1ChatConsole\` (`library\*.json` plus `settings.json`) |
| Console-side audio lanes (label + clips, each a file path/duration/start/loop) | Stored inside the sequence library/export JSON; audio bytes remain at their original PC paths |
| OTA anti-brick flag (pending/attempts) | NVS of **each droid** flashed via OTA, separate `"ota"` namespace (`ota_guard`) |

## Progress

Full detailed history: see [PROGRESS-ARCHIVE.md](PROGRESS-ARCHIVE.md).

**Still open:**
- [ ] Step 6: `droid.{h,cpp}` state machine.
- [ ] Help window, phase 2 remainder (not started): a per-card "?" button
      opening Help directly on that card's page (`HelpViewModel.OpenAtPage`,
      mapping in the plan file `regarde-dans-ce-répertoire-swift-dawn.md`).

**Recent milestones** (2026-08-11):
- Console-originated gestures now carry non-blocking execution correlation
  without changing `MSG_ANIM`: the existing mesh sequence is echoed through
  `MSG_ANIM_EXEC`, mapped back to a serial `requestId`, and rendered on each
  timeline clip. Firmware reports started/completed/interrupted/rejected;
  broadcast replies are jittered and legacy slaves still execute normally.
  A no-hardware-slave bench passed targeted, broadcast and TALK→IDLE lifecycle
  checks 5/5, followed by 24/24 strict serial checks.
- Sequencer execution correlation now expires missing start and finite-gesture
  completion reports without blocking transport. Timeline clips distinguish
  `UNCONF`/`MISS` from `TIMEOUT`, accept late recovery, and ignore delayed START
  regressions after a terminal report; the headless suite passes 55/55.
- Animation delivery now exposes the stages the current transport can prove:
  local serial write (`WRITE`), parsed/validated master acceptance (`MASTER`),
  and per-target execution. Disconnected, pre-handshake and failed writes are
  rejected immediately instead of receiving a misleading sent state.
- Sequencer Stop/end now tracks and terminates only its own active TALK and
  POWER_DOWN targets with per-droid IDLE. Broadcast plus targeted overrides,
  repeat/restart, failed cleanup retry, natural end and Loop boundaries are
  covered by the headless transport suite.
- Firmware identity now has two layers: human `FW_VERSION` and an automatic,
  deterministic 8-hex Build ID derived from normalized firmware source,
  PlatformIO configuration and role. It is generated before every build,
  included in release manifests, heartbeats, `hello`/`droids`/`otaResult`, and
  displayed by the WPF console. The master accepts both current and legacy
  heartbeat sizes during rolling upgrades. A three-node bench upgrade confirmed
  same-version OTA success via Build ID (`4DAD66EF` master, `72349AFE` slaves),
  followed by 22/22 strict serial checks.
- In-app Help was rewritten as a task-oriented 16-page US-English guide and
  cross-checked against the current WPF/firmware behavior. It now includes
  first-install/first-fleet setup, mechanical and flash/OTA safety, exact
  persistence/backup boundaries, console updating, data locations, a glossary,
  expanded troubleshooting, and honest Sequencer limitations (console-driven
  fire-and-forget playback, non-autosaved edits, current read-only Local Library
  creation path). Fourteen focused screenshots captured from the real WPF app
  now illustrate all 16 pages, including a connected three-droid fleet,
  calibration, mesh radar, animation controls, audio and gesture tracks,
  complete gesture library, backups, Firmware, and update status. Crops retain
  full controls and card boundaries; Help image styling allows readable
  640px-wide screenshots. Manifest, Markdown targets, and local image links
  validate with 16/16 pages present. The Help reader now assembles those pages
  into one continuous document: scrolling crosses page boundaries naturally,
  menu clicks jump to anchored sections, and the active menu item follows the
  current reading position.
- Autonomous preflight: `tools/self-test.ps1` now builds both firmware roles
  and the WPF console without changing `console/build.number`, checks the
  critical hardening invariants, and auto-detects an available master for
  non-destructive serial reads plus invalid-command rejection tests. It never
  flashes, starts OTA, moves servos, or applies valid settings. JSON reports
  are written to the user's temporary directory; full usage and exclusions
  are in `TEST-PROTOCOL.md`.
- Pre-test hardening: animation tuning is now genuinely keyed and persisted
  per droid (with migration from the old global NVS keys); `getConfig` and its
  response carry a target, and v2 backups preserve per-ID values. Animation
  and calibration debounces snapshot their target/values and cancel on target
  changes; loading calibration no longer emits preview/save traffic.
- Strict input boundaries: serial commands and `setMulti` validate targets,
  types, ranges, name length, calibration ordering, OTA size/MD5, and payload
  shape before applying anything. Authenticated mesh commands receive the
  equivalent validation, including finite 0..100 config floats and OTA chunk
  bounds. OTA START failures now acknowledge the requested session correctly.
- Mesh sequences start at a random 16-bit value after boot, avoiding false
  duplicate rejection when a droid reboots while its previous low sequence
  numbers remain cached by neighbors.
- GitHub firmware/support-image downloads now fail closed if their manifest
  SHA-256 is absent or malformed; the UI can no longer label an unverified
  release as verified. Master, slave, and WPF console builds pass.
- ESP-NOW callback isolation: ordinary mesh messages are now copied into a
  bounded 32-frame inbox and drained from `loop()` (maximum 16 per pass).
  NVS, logging, registry/topology, servo, and animation work therefore no
  longer runs on the internal Wi-Fi task. Queue overflows are counted and
  reported from `loop()`. `MeshComm` now also protects its sequence,
  deduplication, and direct-neighbor caches across tasks. OTA retains its
  already-safe low-latency callback mailbox/lock path. Master and slave builds
  pass; the inbox raises static RAM use to 17.8%.
- Firmware animation safety: randomized movement durations now remain signed
  until they are clamped to at least 1 ms. A negative duration jitter on the
  shortest keyframes used to wrap through `uint16_t` and occasionally turn a
  ~50 ms movement into a ~65-second apparent freeze. Compile-time assertions
  cover the negative and normal-duration cases.
- Calibrated animation centers: keyframe offsets now go through
  `ServoEngine::setTargetOffset()`, which resolves them against each droid's
  persisted pan/tilt centers. Gestures no longer drift back toward the
  compile-time 90-degree defaults after calibration. Both `b1_master` and
  `b1_slave` builds pass with these changes.

**Previous milestones** (2026-07-19 — full detail in the archive):
- Droids card: ⛭ "Configure" button opens Servo Calibration in its own
  window, pre-targeted per droid; Calibration removed from the main grid,
  Mesh Topology promoted to its spot (Droids | Mesh Topology · Animation ·
  Sequencer, 3 rows instead of 4). Bug fixed along the way: `Droid` had no
  `ToString()`, so the Calibration window's own droid picker showed the
  CLR type name once opened standalone.
- Console startup: auto-connects to the last known port (retries every 3s
  until found) and silently reloads the last exported/imported sequence.
- Bug fix: an unplugged master now clears the Droids/Mesh Topology state
  immediately, like a manual Disconnect — it used to freeze on stale data
  while auto-reconnecting in the background.
- Bug fix: main-window mouse-wheel scroll made authoritative everywhere
  (tunneling `PreviewMouseWheel` on the outer `ScrollViewer`) — was
  occasionally sticking/going jerky over the densely packed Animation card.
- Native window chrome recolored dark (DWM title bar + an implicit
  `ScrollBar` style), replacing the default light Windows chrome across
  all 4 windows.

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
`ConfigStore::setNameImmediate()` — bypassing the master's own commit
draft entirely (that draft is a master-side UI concern for its own display
cache, unrelated to what a remote droid should keep). The master's own
name-editing UX (the header's "unsaved" auto-commit badge) is unchanged;
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
- `HeartbeatPayload` (`mesh_comm.h`): Build ID enlarged this payload. The master
  explicitly accepts both the current struct and the frozen 8-byte
  `LegacyHeartbeatPayload`, recording Build ID 0 for legacy nodes. Preserve that
  dual decoder during rolling upgrades; any future payload-size change needs an
  equally explicit compatibility form instead of silently freezing telemetry.
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
