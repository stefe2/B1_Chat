# Firmware contract — proposed extensions to the B1 protocol

This document describes the evolutions of the JSON-lines protocol (`{cmd:...}` → `{evt:...}`,
115200 baud, one line per message) that the console expects from the master ESP32 firmware.
The console (≥ v0.7.0) works **without** these extensions — each section describes the
current fallback behavior and what will improve once the extension is implemented.

Written on 2026-07-12, while the firmware source code wasn't yet available.

## ⚑ Implementation status (firmware ≥ 1.0.0, 2026-07-07)

| Section | Status |
| --- | --- |
| §1 audio track (`track` in seqSave/seqData/seqList/seqState, played by seqRun) | ✅ implemented |
| §2 `getTrackDurations` | ⏳ deferred (BUSY-pin approach planned) |
| §3 `getConfig` response = `{evt:"config",...}` | ✅ implemented |
| §4 atomic `setMulti` (full validation before application) | ✅ implemented |
| §5 `seqRun {from}`, `seqPause`/`seqResume`, `seqState.paused` + per-step push | ✅ implemented, then superseded — see "Absolute-time sequence model" below (fw ≥ 1.5.0, `caps: seqTimeline`) |
| Absolute-time sequence model (`start`/`totalMs`/`audioStartMs`/`fromMs`/`elapsedMs`) | ✅ implemented (fw 1.5.0) |

**Beyond the contract** (inspired by the KyberEditor protocol): line buffer raised to
4 KB (`lineMax` announced); `{evt:"err", msg}` for any invalid command; enriched
`hello` handshake (`fw`, `proto`, `lineMax`, `anims`, `seqSlots`,
`trackCount`, `caps[]`, `dirty`); `{cmd:"getAll"}` = full dump (burst of
existing events ending with `{evt:"allDone"}`); commit/revert model for
volume/anim params/names (`{cmd:"commit"}` / `{cmd:"revert"}` / `{evt:"dirty"}` —
setters are "live", NVS is only written on commit; **the console must
send `commit` after a restore `setMulti`**).

---

## 1. Audio track attached to a sequence (high priority)

**Today**: the console associates an audio track (1-10) with each sequence slot,
but only in its own `localStorage` (`b1.audioBySlot`). A `seqRun` triggered
by the master plays **no** sound at all; only the console's "Rehearse" mode
syncs the audio (it sends `playTrack` itself).

**Requested**: `seqSave` accepts a `track` field (integer 1-10, or `null` = no audio),
persisted in NVS with the sequence.

```json
→ {"cmd":"seqSave","slot":2,"name":"Parade","loop":false,"track":3,"steps":[...]}
← {"evt":"seqSaved","ok":true,"slot":2,"name":"Parade"}

→ {"cmd":"seqLoad","slot":2}
← {"evt":"seqData","slot":2,"name":"Parade","loop":false,"track":3,"steps":[...]}
```

- `seqRun` on a slot with a non-null `track`: the master starts the track
  (equivalent to `playTrack`) at `audioStartMs` (see §6), independently of
  when any gesture step fires.
- `seqList` includes `track` in each entry (catalog display).
- Missing/unknown field = `null` (backward-compatible).

**Console migration**: once `seqData` carries `track`, the console will use it as
the source of truth and migrate its `localStorage` to the firmware on the next `seqSave`.

## 2. Audio track duration (high priority)

**Today**: the console times tracks by hand (a "Measure duration" button)
to draw the audio track to scale on the timeline.

**Requested**: if the audio module supports it (DFPlayer & co. can sometimes read
the duration, otherwise fall back to a table maintained alongside the files):

```json
→ {"cmd":"getTrackDurations"}
← {"evt":"trackDurations","list":[{"track":1,"ms":12400},{"track":2,"ms":8100}]}
```

Tracks of unknown duration: omitted from the list. The console will keep the
manual measurement as a fallback for those.

## 3. Reading the general configuration (medium priority)

**Today**: the console sends `getConfig` at handshake but **doesn't know the
shape of the response** (it logs it without interpreting it). Consequences: the
volume/frequency/amplitude/speed sliders show default values on
startup, and backup restore can't compare these settings
(they're offered "blindly", unchecked by default).

**Requested**: document/standardize the response as follows:

```json
→ {"cmd":"getConfig"}
← {"evt":"config","volume":20,"freq":50,"amp":60,"speed":50}
```

The console will then populate its sliders on connection and do a true
field-by-field reconciliation of these values on restore.

## 4. Atomic batch write — `setMulti` (medium priority)

Inspired by the Kyber firmware's `SETM`: backup restore currently sends
a burst of commands spaced 200 ms apart (names, calibrations,
sequences) — slow and interruptible partway through.

**Requested**:

```json
→ {"cmd":"setMulti","ops":[
     {"cmd":"name","id":513,"name":"Rex"},
     {"cmd":"calib","target":513,"panMin":20,...},
     {"cmd":"seqSave","slot":0,"name":"Parade","loop":false,"steps":[...]}
   ]}
← {"evt":"setMultiDone","ok":true,"applied":3}
```

- All or nothing: if one op fails, none are persisted, and the response reports
  the offending index: `{"evt":"setMultiDone","ok":false,"failedAt":1,"error":"..."}`.
- Size bounded by the serial buffer: accept at least 4 KB per line, and the
  console will fragment its batches beyond that.

## 5. Enriched playback control (nice-to-have)

- `{"cmd":"seqPause"}` / `{"cmd":"seqResume"}` — with `{"evt":"seqState","paused":true,...}`.
- `seqState` during playback: push the event **on every step** (already seems to
  be the case) and include `track` if there's an audio track.
- `seqRun {from}` — originally "start at step N"; superseded by `fromMs`, see below.

## 6. Absolute-time sequence model (fw ≥ 1.5.0, breaking change, `caps: seqTimeline`)

The original model was a **chained list**: each step waited `delay` ms after
the *previous* step before firing — inherently serial, even across droids
(two droids could never start together, only in relay). Reworked so a
sequence is a set of steps with **absolute offsets from the sequence's own
t=0** — several steps due at the same offset fire on the same pass, which is
what actually enables cross-droid choreography instead of a relay.

```json
→ {"cmd":"seqSave","slot":2,"name":"Parade","loop":false,"track":3,
   "totalMs":8000,"audioStartMs":500,
   "steps":[{"animId":0,"target":513,"start":0},{"animId":5,"target":1279,"start":0}]}
← {"evt":"seqSaved","ok":true,"slot":2,"name":"Parade"}

→ {"cmd":"seqRun","slot":2,"fromMs":3200}
← {"evt":"seqState","playing":true,"slot":2,"elapsedMs":3200,"totalMs":8000,"track":3,"paused":false}
```

- `steps[].start` (was `delay`) — ms from t=0, **not** a delay from the
  previous step. Two steps sharing the same `start` fire together.
- `totalMs` — explicit loop/end boundary. Required because, once steps can
  overlap, "the last step" no longer reliably marks the intended end (e.g. a
  short gesture on one track finishing well before a longer one on another).
- `audioStartMs` — when the sequence's audio `track` begins, relative to t=0
  (ignored if `track` is null). Previously implicit ("starts when step 0
  fires"); now explicit, since step 0 no longer has to be at `start:0`.
- `seqRun {fromMs}` (was `{from}`, a step index) — starting offset in ms,
  i.e. scrub to a point in the timeline rather than skip to a step number.
- `seqState` reports `elapsedMs`/`totalMs` (was `index`/`total`) — a time
  position rather than a step count, since several steps can be "current" at
  once.
- **Field names changed, not just meaning**, specifically so an old console
  talking to new firmware (or vice versa) drops the unrecognized field and
  falls back to a safe default (`start`/`fromMs` absent → 0) instead of a new
  field being silently misinterpreted under old semantics or vice versa.
- **Old sequences don't carry over.** The on-flash NVS blob layout changed
  size, so a sequence saved before fw 1.5.0 reads back as "not found" rather
  than being replayed with the wrong timing — the 8 slots on a master
  updated past this point need to be re-saved from the console.

## Implementation reminders

- One line = one complete JSON object terminated by `\n`; ignore invalid lines.
- Unknown fields in a command: **ignore** them (the console may be newer
  than the firmware). Never fail on an extra field.
- Any new response must keep a single, stable `evt` field — the console
  routes exclusively on it.
