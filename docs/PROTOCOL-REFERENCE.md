# B1 Chat — Current protocol reference

This is the compact current reference for the firmware mesh and the USB JSON
bridge. The implementation remains authoritative in `src/mesh_comm.h`,
`src/mesh_comm.cpp` and `src/serial_console.cpp`; update this document with any
protocol change.

## Mesh

Frames contain `{srcId, seq, ttl, type}`, a payload and an 8-byte truncated
HMAC-SHA256. TTL is excluded from the signature because relays decrement it.
Nodes deduplicate `(srcId, seq)`, relay while TTL is positive, and reject a
different `GROUP_KEY` or invalid signature. Normal messages are copied from the
ESP-NOW callback into the bounded loop inbox; OTA retains its mailbox path.

The fixed message types are:

| Type | Meaning |
| --- | --- |
| 1 `MSG_ANIM` | Target, animation, sync delay/telemetry bit and seed |
| 2 | Retired (former animation configuration) |
| 4 `MSG_HEARTBEAT` | Uptime, state bits, firmware version and Build ID |
| 5 `MSG_SERVO` | Target servo enable state |
| 6 `MSG_CALIB` | Legacy target pan/tilt limits |
| 7 `MSG_PREVIEW` | Transient target pan/tilt position |
| 9 `MSG_NEIGHBORS` | Direct radio neighborhood and RSSI report |
| 10–14 `MSG_OTA_*` | Start, chunk, ack, end and abort |
| 15 `MSG_LOCATE` | Non-persistent physical locate LED state |
| 16 `MSG_NAME` | Persistent name on the targeted droid |
| 17 `MSG_ANIM_EXEC` | Authenticated animation lifecycle telemetry |
| 18 `MSG_ANIM_LEASED` | Infinite animation with a fail-closed lease |
| 19 `MSG_ANIM_LEASE_RENEW` | Renew the correlated active lease |
| 20 `MSG_SAFE_STOP` | Centered hold with stale/untracked motion blocked |
| 21 `MSG_CALIB_V2` | Calibration plus PAN/TILT reverse flags |
| 22 `MSG_CAPABILITIES` | Source droid feature bits |

The master accepts both the current heartbeat and the frozen legacy 8-byte
heartbeat, recording Build ID zero for legacy nodes.

## USB JSON bridge

The master speaks one JSON object per line at 115200 baud. A session begins with
`hello` and remains alive with `ping`; firmware timeout is 5 seconds. Unknown
fields are ignored, responses are routed by `evt`, and the announced line limit
is 4 KB.

Console commands:

```text
hello, ping, list, getAll, name, servo,
locate, adopt, forget, gesture, animLease, safeStop, preview, calib, getCalib,
getGestureCatalog, getMeshTopology, commit, otaStart, otaChunk,
otaAbort
```

Important event families are `hello`, `droids`, `log`, `err`,
`calibData`, `meshTopology`, `gestureCatalog`, `animAccepted`, `animExec`,
`dirty`, `allDone`, `otaReady`, `otaChunkAck`, `otaDone`,
`otaResult` and `otaError`. See `serial_console.cpp` for exact field names and
validation.

## Gesture V2 and Sequencer boundaries

The generated V2 catalog currently exposes `idle.center`, `communicate.nod`
and `dialogue.talk`. Scenes and USB commands use the key; compact wire IDs are
generated firmware details. `getGestureCatalog` returns the exact catalog
identity/hash, kind, nominal duration and frame count. `hello` carries the
same catalog identity; the console refuses Scene playback if it differs.

The console owns sequences and fires per-step `gesture` commands. Firmware no
longer has `seq*` commands, onboard sequence playback or the old eight NVS
sequence slots. Audio and DFPlayer commands were also removed from firmware;
audio is client-side.

Gesture targets retain deterministic pose variation, but it never changes a
gesture's nominal duration. There is no global or per-droid animation speed,
frequency, or amplitude configuration.

Safety invariants:

- Execution telemetry is observational; it never gates or stops the timeline.
- Pause is not a hardware stop; already dispatched gestures continue.
- Stop, Safe Stop and Emergency Stop remain three distinct controls.
- Sequencer-started continuous gestures use a 5-second lease renewed every 2
  seconds; there is no autonomous gesture path.
- Persistent editing happens only while stopped. Console Scene Export/Import is
  `b1-scene` V1 and binds the named gesture catalog; old `b1-sequence` files
  are rejected without migration.
