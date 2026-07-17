# Firmware contract — proposed extensions to the B1 protocol (fully resolved)

This document originally proposed evolutions of the JSON-lines protocol
(`{cmd:...}` → `{evt:...}`, 115200 baud, one line per message) for the master
ESP32 firmware to implement, written on 2026-07-12 while the firmware source
code didn't exist yet.

**Status: every section below is now resolved** — either implemented as
proposed, or superseded/fully removed by a later redesign. There is nothing
outstanding from this contract; it's kept as the historical rationale for
those decisions (in particular the field-rename reasoning §6 explains, which
CLAUDE.md's "Known pitfalls" section still references). For the protocol's
current, authoritative shape, see CLAUDE.md's "JSON serial protocol" section;
for how each item actually evolved over time, see its Progress log.

## ⚑ Resolution summary (current firmware: 1.8.0, proto 5)

| Section | Outcome |
| --- | --- |
| §1/§2 audio track / `getTrackDurations` | Implemented (fw 1.0.0), then fully **removed** (fw 1.6.0) — DFPlayer retired firmware-wide, the console now owns multi-track audio playback client-side; see CLAUDE.md |
| §3 `getConfig` response = `{evt:"config",...}` | **Implemented** (fw 1.0.0), unchanged since (now `freq`/`amp`/`speed` only — `volume` dropped alongside DFPlayer) |
| §4 atomic `setMulti` (full validation before application) | **Implemented** (fw 1.0.0), unchanged since |
| §5 `seqRun {from}`, `seqPause`/`seqResume`, `seqState.paused` + per-step push | Implemented (fw 1.0.0), then fully **removed** (fw 1.7.0) — the whole seq* family (8 NVS slots + onboard player) was retired; sequences are entirely console-driven now, see CLAUDE.md |
| §6 absolute-time sequence model (`start`/`totalMs`/`fromMs`/`elapsedMs`) | Implemented (fw 1.5.0), then fully **removed** (fw 1.7.0) with the rest of the seq* machinery |

**Beyond the original contract** (also resolved, inspired by the KyberEditor
protocol, not part of the initial proposal): line buffer raised to 4 KB
(`lineMax` announced); `{evt:"err", msg}` for any invalid command; enriched
`hello` handshake (`fw`, `proto`, `lineMax`, `anims`, `caps[]`, `dirty` —
`seqSlots` dropped in fw 1.7.0 with the slot machinery);
`{cmd:"getAll"}` = full dump (burst of existing events ending with `{evt:"allDone"}`);
a commit model for anim params/names (`{cmd:"commit"}` / `{evt:"dirty"}` —
setters are "live", NVS is only written on commit; the console auto-commits
2s after the last change, and must also send `commit` after a restore
`setMulti`). Its manual counterpart, `{cmd:"revert"}`, was implemented
alongside `commit` and later fully **removed** (fw 1.8.0/proto 5) once the
console's auto-commit made a manual "discard my recent edits" action
unreachable — see CLAUDE.md.

---

## 1-2. Audio track / track duration — removed (fw 1.6.0)

These two sections proposed attaching a DFPlayer track to a sequence
(`track`/`audioStartMs` on `seqSave`/`seqData`/`seqList`/`seqState`) and
measuring track durations. Both were implemented, then **fully removed**
(fw 1.6.0) when the DFPlayer was retired firmware-wide — the console now
owns multi-track audio playback entirely client-side (`AudioLane`/
`AudioClip`/`AudioPlaybackService`) and drives it in sync with real mesh
`anim` commands from the Sequencer's own `Play`, no firmware audio
involvement at all. `volume`/`playTrack` (console→master) and the
`config` evt's `volume` field are gone with it. See CLAUDE.md's Progress
log for the full removal.

## 3. Reading the general configuration — implemented (fw 1.0.0)

**Before**: the console sent `getConfig` at handshake but didn't know the
shape of the response (it logged it without interpreting it) — the
frequency/amplitude/speed sliders showed default values on startup, and
backup restore couldn't compare these settings (offered "blindly", unchecked
by default).

**Implemented as**:

```json
→ {"cmd":"getConfig"}
← {"evt":"config","freq":50,"amp":60,"speed":50}
```

The console populates its sliders on connection and does a true
field-by-field reconciliation of these values on restore.

## 4. Atomic batch write — `setMulti` — implemented (fw 1.0.0)

Inspired by the Kyber firmware's `SETM`: backup restore used to send
a burst of commands spaced 200 ms apart (names, calibrations,
sequences) — slow and interruptible partway through.

**Implemented as**:

```json
→ {"cmd":"setMulti","ops":[
     {"cmd":"name","id":513,"name":"Rex"},
     {"cmd":"calib","target":513,"panMin":20,...}
   ]}
← {"evt":"setMultiDone","ok":true,"applied":2}
```

- All or nothing: if one op fails, none are persisted, and the response reports
  the offending index: `{"evt":"setMultiDone","ok":false,"failedAt":1,"error":"..."}`.
- Size bounded by the serial buffer: accepts at least 4 KB per line, and the
  console fragments its batches beyond that.
- The example above no longer includes a `seqSave` op — `setMulti` could
  carry one back when sequences were still firmware-side (fw 1.0.0-1.6.x);
  fw 1.7.0 removed the whole seq* family, so it never appears in an op today.

## 5. Enriched playback control — implemented (fw 1.0.0), then removed (fw 1.7.0)

Was implemented as proposed (`{"cmd":"seqPause"}` / `{"cmd":"seqResume"}` with
`{"evt":"seqState","paused":true,...}`, and `seqRun {from}` superseded by
`fromMs`, see §6) — then the whole seq* command/event family, including this
one, was removed in fw 1.7.0 along with the master's 8 NVS sequence slots and
its onboard player. Sequences are entirely console-driven now (own timers,
`anim` commands per step); none of this exists in the current firmware.

## 6. Absolute-time sequence model — implemented (fw 1.5.0), then removed (fw 1.7.0)

Kept in full below for its field-rename rationale, still referenced by
CLAUDE.md's "Known pitfalls" — but this entire model (and the `seq*` command
family it belongs to) no longer exists in the firmware as of fw 1.7.0; skip to
the bullet list's last point if you just want the outcome.

The original model was a **chained list**: each step waited `delay` ms after
the *previous* step before firing — inherently serial, even across droids
(two droids could never start together, only in relay). Reworked so a
sequence is a set of steps with **absolute offsets from the sequence's own
t=0** — several steps due at the same offset fire on the same pass, which is
what actually enables cross-droid choreography instead of a relay.

```json
→ {"cmd":"seqSave","slot":2,"name":"Parade","loop":false,
   "totalMs":8000,
   "steps":[{"animId":0,"target":513,"start":0},{"animId":5,"target":1279,"start":0}]}
← {"evt":"seqSaved","ok":true,"slot":2,"name":"Parade"}

→ {"cmd":"seqRun","slot":2,"fromMs":3200}
← {"evt":"seqState","playing":true,"slot":2,"elapsedMs":3200,"totalMs":8000,"paused":false}
```

- `steps[].start` (was `delay`) — ms from t=0, **not** a delay from the
  previous step. Two steps sharing the same `start` fire together.
- `totalMs` — explicit loop/end boundary. Required because, once steps can
  overlap, "the last step" no longer reliably marks the intended end (e.g. a
  short gesture on one track finishing well before a longer one on another).
- `audioStartMs` existed here originally (cue point for the DFPlayer audio
  `track`) — removed in fw 1.6.0 along with the audio track itself (§1-2).
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
