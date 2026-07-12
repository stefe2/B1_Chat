# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this project is

**B1 Chat — Console de supervision** (v0.8.0): a WPF (net8.0-windows) desktop app supervising a
mesh network of ESP32-based "B1" droids (animated droid heads: pan/tilt servos, gesture
animations, audio on the master) over a USB serial connection to a "master" ESP32. A
`Microsoft.Web.WebView2.Wpf.WebView2` control renders `wwwroot/index.html`, which contains the
entire dashboard UI (single file: HTML + CSS + JS, all in French). Originally a browser-only app
using the Web Serial API; this project replaced that with a real `System.IO.Ports.SerialPort` on
the C# side. Verified end-to-end against real ESP32 hardware.

The user does **not** have the firmware source code (it references `src/sequence_store.h` in a
repo we don't have). Everything here is console-side; proposed firmware protocol extensions are
specified in [FIRMWARE-CONTRACT.md](FIRMWARE-CONTRACT.md) (audio track in `seqSave`,
`getTrackDurations`, documented `getConfig` reply, atomic `setMulti`, play-from-step).

Design inspiration: **KyberEditor** (`C:\Program Files\KyberEditor`), a WPF+WebView2 companion
app for an ESP32 droid controller from the droid-builder community — mined for UX patterns
(commit/revert sync, field-by-field reconcile, bundled espflash, markdown help viewer). For the
general WPF+WebView2 bridge pattern, see the sibling project `../csharp-webview2-test`.

## Commands

- `dotnet build` — compile (auto-increments `build.number`)
- `dotnet run` — build and launch
- `bin/Debug/net8.0-windows/b1-chat-console.exe` — run the already-built exe
- `dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true` — standalone single-file exe (~154 MB) in `bin/Release/net8.0-windows/win-x64/publish/`
- `makensis installer\b1-chat-console.nsi` — build the Windows installer from the publish output → `installer/b1-chat-console-setup-<version>.exe` (~46 MB). Requires NSIS (`winget install NSIS.NSIS` or portable zip). Per-user install (no admin), WebView2 runtime check, French/English UI.

There are no configured test scripts — see "How UI changes were verified" below.

## Versioning

- `<VersionPrefix>` in the csproj = semantic version; **bump the minor when a roadmap phase completes** (0.8.0 = phases 1–9 done).
- `build.number` (repo root) auto-increments on **every** build via the `IncrementBuildNumber` MSBuild target → `FileVersion 0.8.0.N`, `InformationalVersion "0.8.0+build.N"`. Never edit by hand.
- Shown in the window title, the header sub-line, and a startup log line (page sends `{type:"getAppInfo"}` → host replies `{type:"appInfo", version}`).
- The NSIS script's `APPVERSION` default must be kept in sync when bumping (or override: `makensis /DAPPVERSION=x.y.z …`).

## Architecture

### Files

| Path | Role |
| --- | --- |
| `MainWindow.xaml.cs` | All C#: serial port, JS bridge, settings, file dialogs, sequence library storage, espflash runner |
| `wwwroot/index.html` | Entire dashboard (~2000 lines, copied to output on build; editable without recompiling — relaunch the exe) |
| `b1-chat-console.csproj` | net8.0-windows, WPF, WebView2 + System.IO.Ports; version system; copies `wwwroot\**` and `tools\**` |
| `installer/b1-chat-console.nsi` | NSIS installer script (**must stay UTF-8 with BOM** or accents get mangled) |
| `FIRMWARE-CONTRACT.md` | Proposed firmware protocol extensions (phase 8, docs-only) |
| `build.number` | Auto-incremented build counter |
| `tools/espflash.exe` | **Not committed/bundled yet** — see Flashing below |

### Two message vocabularies (do not conflate)

**1. Transport-control** — envelope between page and C# host via
`window.chrome.webview.postMessage` / `PostWebMessageAsJson`. Discriminated by `type`:

Page → host: `listPorts` · `open {port}` · `close` · `write {data}` · `getAppInfo` ·
`saveFile {suggestedName, content}` · `openFile {purpose}` · `pickBin` ·
`flash {path, address, port}` · `libList` · `libSave {id, item}` · `libDelete {id}`

Host → page: `ports {list, lastPort}` · `opened {ok, port, error?}` · `closed {unexpected?}` ·
`line {data}` · `error {message}` · `appInfo {version}` · `fileSaved {ok, path?, error?, cancelled?}` ·
`fileOpened {ok, purpose, name?, content?, cancelled?}` · `binPicked {ok, path?, name?, size?}` ·
`flashLog {line}` · `flashDone {ok, exitCode?, error?}` · `libList {list}` ·
`libSaved {ok, id?, error?}` · `libDeleted {ok, id?, error?}`

`openFile.purpose` routes the reply in the page: `"sequence"` → `importSequence()`, `"backup"` → `startRestore()`.

**2. Firmware protocol** — JSON-lines spoken with the ESP32 master (115200 baud, `\n`-terminated,
UTF-8). Travels *inside* transport messages (outbound `{type:"write", data: json+"\n"}`, inbound
`{type:"line", data}` parsed → `handleEvent()`). Discriminated by `cmd` (outbound) / `evt` (inbound):

Commands: `hello` · `ping` · `list` · `getConfig` · `config {target,freq,amp,speed}` ·
`name {id,name}` · `servo {target,enabled}` · `autoAnim {target,enabled}` ·
`anim {target,animId,seed}` · `playTrack {track}` · `volume {value}` ·
`preview {target,pan,tilt}` · `calib {target,panMin,panCenter,panMax,tiltMin,tiltCenter,tiltMax}` ·
`getCalib {target}` · `getAnimDurations` · `getMeshTopology` · `seqList` · `seqLoad {slot}` ·
`seqSave {slot,name,loop,steps}` · `seqDelete {slot}` · `seqRun {slot}` · `seqStop` · `seqState`

Events: `hello {ok}` · `droids {list:[{id,mac,name,age,rssi,state,role,servos,autoAnim}]}` ·
`log {msg}` · `state` · `calibData {target + 6 fields}` · `meshTopology {links:[{from,to,rssi}]}` ·
`animDurations {list:[{animId,ms}]}` · `seqList {list:[{slot,name,stepCount,loop}]}` ·
`seqData {slot,name,loop,steps}` · `seqSaved {ok,slot,name}` · `seqDeleted {ok,slot}` ·
`seqState {playing,slot,index,total}`

**Note**: the `getConfig` reply shape is unknown (falls through to the generic log) — the sliders
(volume/freq/amp/speed) are never populated from the device. See FIRMWARE-CONTRACT.md §3.

### Key constants (page script)

`ANIMS` (18 gestures, ids 0–17, aligned with firmware `animation.h`: IDLE, LOOK_AROUND, NOD_YES,
SHAKE_NO, CURIOUS_TILT, SCAN_SLOW, ALERT_SNAP, TRACK, GLITCH_STUTTER, CONFUSED_TILT, DOUBLE_TAKE,
SLEEPY_DROOP, TARGET_LOCK, WHIRR_SEARCH, SIGNAL_GLITCH, GREETING_NOD, POWER_DOWN, TALK) ·
`SEQ_STEP_MAX = 32` · `SEQ_SLOT_MAX = 8` · `TRACK_COUNT = 10` · target `65535` = "Tous" (broadcast) ·
droid considered "perdu" after ~6 s without signal (master is always "local"/online).

### C# host (`MainWindow.xaml.cs`)

- `CoreWebView2_WebMessageReceived` — one `switch` over transport `type`.
- `OpenPort` (115200, `NewLine="\n"`, UTF-8, 500 ms read timeout) + background `ReadLoop` posting each line via `Dispatcher.Invoke`. On loop death **without** cancellation (USB unplugged) it closes the port and sends `{type:"closed", unexpected:true}`.
- Settings: `%LOCALAPPDATA%\B1ChatConsole\settings.json` (`lastPort`), loaded at startup, saved on each successful open; `lastPort` rides on every `ports` reply.
- File dialogs (`Microsoft.Win32`): saveFile/openFile (JSON filter), pickBin (`*.bin`).
- Sequence library: `%LOCALAPPDATA%\B1ChatConsole\library\<id>.json`; `SendLibrary()` returns full parsed items; ids are sanitized (`SafeLibId`).
- Flashing: `FindEspflash()` probes `tools\espflash.exe` (next to exe) → `C:\Program Files\KyberEditor\Tools\espflash.exe` → `PATH`. `StartFlash` closes the port, tells the page, runs `espflash write-bin --port <p> -B 460800 <addr> <bin>` streaming stdout+stderr as `flashLog` lines, then `flashDone {ok, exitCode}`.

### Page subsystems (`wwwroot/index.html`, one `<script>` block)

Cards: Droïdes (+ Sauvegarder…/Restaurer… buttons) · Calibration servos (live preview + debounced
auto-save) · Animation (play gesture + config sliders) · Audio (volume, test track) · Firmware
(flash card) · Topologie du mesh (SVG neighbor graph, worst-of-both-directions RSSI) ·
Séquenceur (catalog + local library + editor + timeline) · Activité (log, 300 lines max).

Key global state: `droids` (Map id→droid) · `portOpen` / `sessionReady` (handshake `hello` gates
all commands except hello/ping — `sendCmd()` is the single outbound choke point) · keepalive every
1.5 s (`ping`, or `hello` until session ready) · `markUiBusy`/`pendingDroidsRender` defer droid
table re-renders while the user interacts (`UI_INTERACTION_SELECTOR`).

**Auto-reconnect**: one-shot auto-connect to `lastPort` on the first `ports` reply
(`autoConnectTried` guard — a later manual Rescan never auto-connects); on
`closed {unexpected:true}` (and not `manualClose`), status "Reconnexion…" + rescan every 3 s
(`startReconnect`/`stopReconnect`), reopening the port when it reappears. Manual
Connect/Disconnect always cancels reconnection.

**Sequencer editor**: `seq` (array of `{animId,target,delay}`), `currentSlot` (NVS slot being
edited, `null` = new/unsaved; highlighted "· en édition" in the catalog), `seqDirty` +
`setSeqDirty()` ("● non sauvegardé" pill; gates confirmations on Charger / + Nouvelle / Importer /
Jouer; **any dirty=true also stops rehearsal**), `animDurationMs` (from `getAnimDurations`; drives
suggested delays, total duration, conflict warnings). `stepConflict(i)`: warns (orange delay
input + timeline outline) when a step's delay < its gesture duration AND the next step (or step 0
when looping) targets the same droid or "Tous" — different targets = intentional parallel
choreography, never warned. Per-row actions: ▶ test gesture now · ▶▶ rehearse from this step ·
⧉ duplicate · ✕ delete · ≡ drag-handle reorder. After `seqSaved`/`seqDeleted` ok the page
re-requests `seqList`.

**Undo/redo**: snapshot stack (`seqHistory`/`seqFuture`, 50 deep) of `{seq, name, loop}`, pushed
*before* each mutation; name field captures its snapshot on focus (so it holds the old name);
loop checkbox reconstructs the previous state. Ctrl+Z/Ctrl+Y (skipped when a text input has
focus, native undo wins) + ↶/↷ buttons. History cleared on load/new/import.

**Timeline** (`renderTimeline()`, called from `renderSeq()`): audio base lane + one lane per
distinct target ("Tous" first, then by id). Block left = cumulative delays (`stepStartMs`), width
= real gesture duration; clicking a block flashes the matching table row. Ruler ticks 1 s (5 s
beyond 30 s). Playhead (`updatePlaybackUi()`) follows `lastSeqState` **only when the played slot
=== `currentSlot`** (works for rehearsal too since it synthesizes `slot: currentSlot`, including
`null === null`); slides via CSS `left` transition lasting the current step's delay; also
highlights the current table row + block.

**Audio metadata is console-side only** (until firmware phase 8): localStorage keys
`b1.trackDurations` (track № → ms, measured with the "Mesurer la durée" stopwatch button — the
firmware can't report track lengths) and `b1.audioBySlot` (NVS slot → track №, kept in sync on
load/save/push). `lsGet`/`lsSet` wrap localStorage in try/catch.

**Rehearsal** (`startRehearsal(from)`/`stopRehearsal()`): console plays the in-editor sequence —
`playTrack` at step 0 (audio can't start mid-file; from>0 = gestures only, logged) then `anim`
per step via setTimeout at cumulative delays; loops if "Boucle" and from===0. Synthesizes
`lastSeqState` (with `rehearsal:true`) for the playhead; while rehearsing, master `seqState`
events are ignored (`renderSeqPlayState` early-returns). Stopped by: any edit, load/new/import,
Stop button (also sends `seqStop`), disconnect, flash start.

**Sequence export/import**: `.b1seq.json` = `{type:"b1-sequence", version:1, name, loop,
audioTrack, audioDurationMs, steps}`. Import loads as an unsaved draft (`currentSlot=null`,
dirty), merges the file's audio duration only if none measured locally.

**Local library** (phase 9): `seqLibrary` mirrors the host's `library/` dir (items = b1-sequence
payload + `id` slug + `savedAt`). Flows: "+ Depuis l'éditeur" → `saveToLibrary` (slug by name,
confirm overwrite) · "Charger" → `importSequence` · "→ ESP32" → `pushToMaster` (same-name slot
overwritten after confirm, else first free slot; sets `pushingLib` so the `seqSaved` ack skips
the editor-state update and just refreshes the catalog) · catalog "→ Biblio" → background
`requestSeqSlot` then `saveToLibrary`.

**Backup/restore** (phase 6): `backupConfig()` collects names + per-droid `getCalib` + per-slot
`seqLoad` **in background** — `seqCollector`/`calibCollector` intercept the next
`seqData`/`calibData` (checked at the top of the `seqData` branch and `applyCalibData`) so the
editor and sliders don't move; promise + 2.5 s timeout per request. Backup file =
`{type:"b1-backup", version:1, savedAt, names, calib, sequences (with audioTrack), trackDurations,
uiParams}`. Restore re-reads the device, diffs field-by-field, and shows a checkbox modal
(`#modalOverlay`); uiParams can't be read from the device so they're listed **unchecked**;
applies with 200 ms spacing to avoid flooding the master's serial buffer, then refreshes
`list` + `seqList`.

**Flashing card**: pick `.bin` → address (0x0 merged image / 0x10000 app-only) → confirm →
page sets `manualClose=true`, stops rehearsal/reconnect, sends `flash`; host closes port and
streams the log; on success the page reopens the port after 2.5 s (board reboot). Address
validated as `/^0x[0-9a-fA-F]+$/`. **espflash.exe is not in the repo**: a permission gate blocked
committing/executing the downloaded binary without user review. `FindEspflash()`'s KyberEditor
fallback makes flashing work on this PC today; for distribution, drop an official
espflash 4.x exe (esp-rs GitHub releases, `espflash-x86_64-pc-windows-msvc.zip`) into `tools\`.

## Storage locations

| What | Where |
| --- | --- |
| Last serial port | `%LOCALAPPDATA%\B1ChatConsole\settings.json` |
| Sequence library | `%LOCALAPPDATA%\B1ChatConsole\library\*.json` |
| Track durations + slot→track mapping | page `localStorage` (`b1.trackDurations`, `b1.audioBySlot`) — lives in the WebView2 user-data folder |
| Backups / sequence exports | wherever the user saves them (native dialogs) |

## How UI changes were verified (no test framework)

Throwaway jsdom harnesses (session scratchpad, not committed): a PowerShell script copies
`index.html`, injects **before** the main script a `window.chrome.webview` shim (records
`postMessage` calls in `window.__sent`; `dispatch(data)` fires the page's message listener), and
appends a test `<script>` that drives `handleEvent()` with fake firmware events / dispatches
transport messages / clicks buttons, then writes results into `document.title`
(`TESTRESULT:{json}`). A Node runner loads it with
`new JSDOM(html, {runScripts:"dangerously", pretendToBeVisual:true, url:"https://localhost/x"})`
(https URL so localStorage works) and asserts. Patterns that matter: override `window.confirm`
before clicking anything guarded; an "auto-responder" interval that answers each
`getCalib`/`seqLoad` write **per request index** (not per target — restore re-asks the same
targets); top-level `let`/`function` in the page script are reachable from the appended test
script (same global lexical scope). Final state: 97 checks across 7 harnesses, all green.
`node --check` on the extracted script is the quick syntax gate. (Edge `--headless --dump-dom`
produced no output in this sandbox; jsdom is the way.)

## Gotchas

- The `.nsi` must be **UTF-8 with BOM** (makensis reads ANSI otherwise → mangled accents; bit us once).
- `Directory.Build`-style version injection: `IncrementBuildNumber` runs before `GetAssemblyVersion;GenerateAssemblyInfo` — keep that ordering if touching the csproj.
- Stop the running app before `dotnet build` (file lock): `Stop-Process -Name b1-chat-console`.
- `handleEvent` is **wrapped** (`oldHandleEvent` pattern) — sequencer events are handled in the wrapper, the rest falls through to the original. Add new `evt` handling in the wrapper.
- Interceptors (`seqCollector`/`calibCollector`/`pushingLib`) must stay at the **top** of their respective handlers or background collection corrupts the editor.
- Many top-level `let` declarations live *after* functions that reference them — safe because everything user-triggered runs post-load, but code executed during initial script evaluation must not call those functions (TDZ).
- WebView2 page is `file://` — no fetch/CDN; everything inline. Native dialogs/files must go through the host bridge.
- French UI throughout; code comments in French (page) and French (C#). Keep it consistent.

## Roadmap (agreed with the user, 2026-07-12)

1. ✅ Sequencer quick wins (per-step ▶, total duration, dirty flag + confirmations, "en édition" highlight, delay-conflict warnings)
2. ✅ Auto-reconnect (last port remembered, startup auto-connect, rescan loop on unexpected close)
3. ✅ Multi-track timeline with the audio track as base layer + `seqState` playhead
4. ✅ Console-side rehearsal mode (play in-editor sequence, audio + gestures, from any step)
5. ✅ Advanced editing: drag & drop, duplicate, undo/redo, `.b1seq.json` export/import (+ host file bridge)
6. ✅ Full droid backup/restore with field-by-field reconcile modal
7. ✅ Integrated firmware flashing (espflash runner + Firmware card; binary not bundled — see Gotchas/Flashing)
8. ✅ (docs only — no firmware source) FIRMWARE-CONTRACT.md: `track` in seqSave, getTrackDurations, documented getConfig, atomic setMulti, richer playback
9. ✅ Local sequence library (unlimited, file-per-sequence) with two-way ESP32 sync

Cross-cutting (done): version system (see Versioning). Possible next steps discussed but not
committed: splitting index.html into modules, auto-reconcile on connect (KyberEditor-style linked
files), bundling espflash + firmware manifest with SHA-256 once the user's `.bin`s exist,
implementing FIRMWARE-CONTRACT.md when firmware source becomes available.

**Keep this file updated at the end of every phase (explicit user request).** Bump
`<VersionPrefix>` minor + NSIS `APPVERSION` at each completed phase.
