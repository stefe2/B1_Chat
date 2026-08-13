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
| 2 `MSG_CONFIG` | Target frequency, amplitude and speed |
| 4 `MSG_HEARTBEAT` | Uptime, state bits, firmware version and Build ID |
| 5 `MSG_SERVO` | Target servo enable state |
| 6 `MSG_CALIB` | Legacy target pan/tilt limits |
| 7 `MSG_PREVIEW` | Transient target pan/tilt position |
| 8 `MSG_AUTOANIM` | Enable or pause spontaneous animation |
| 9 `MSG_NEIGHBORS` | Direct radio neighborhood and RSSI report |
| 10–14 `MSG_OTA_*` | Start, chunk, ack, end and abort |
| 15 `MSG_LOCATE` | Non-persistent physical locate LED state |
| 16 `MSG_NAME` | Persistent name on the targeted droid |
| 17 `MSG_ANIM_EXEC` | Authenticated animation lifecycle telemetry |
| 18 `MSG_ANIM_LEASED` | Infinite animation with a fail-closed lease |
| 19 `MSG_ANIM_LEASE_RENEW` | Renew the correlated active lease |
| 20 `MSG_SAFE_STOP` | Centered hold with automatic motion suppressed |
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
hello, ping, list, getConfig, getAll, config, name, servo, autoAnim,
locate, adopt, forget, anim, animLease, safeStop, preview, calib, getCalib,
getAnimDurations, getMeshTopology, setMulti, commit, otaStart, otaChunk,
otaAbort
```

Important event families are `hello`, `droids`, `log`, `err`, `config`,
`calibData`, `meshTopology`, `animDurations`, `animAccepted`, `animExec`,
`setMultiDone`, `dirty`, `allDone`, `otaReady`, `otaChunkAck`, `otaDone`,
`otaResult` and `otaError`. See `serial_console.cpp` for exact field names and
validation.

## Animation and Sequencer boundaries

The 18 animation IDs are aligned with the firmware table and the frozen web
reference: `IDLE`, `LOOK_AROUND`, `NOD_YES`, `SHAKE_NO`, `CURIOUS_TILT`,
`SCAN_SLOW`, `ALERT_SNAP`, `TRACK`, `GLITCH_STUTTER`, `CONFUSED_TILT`,
`DOUBLE_TAKE`, `SLEEPY_DROOP`, `TARGET_LOCK`, `WHIRR_SEARCH`, `SIGNAL_GLITCH`,
`GREETING_NOD`, `POWER_DOWN` and `TALK`. `POWER_DOWN` and `TALK` loop and are
excluded from autonomous random idle selection.

The console owns sequences and fires per-step `anim` commands. Firmware no
longer has `seq*` commands, onboard sequence playback or the old eight NVS
sequence slots. Audio and DFPlayer commands were also removed from firmware;
audio is client-side.

Safety invariants:

- Execution telemetry is observational; it never gates or stops the timeline.
- Pause is not a hardware stop; already dispatched gestures continue.
- Stop, Safe Stop and Emergency Stop remain three distinct controls.
- Sequencer-started infinite gestures use a 5-second lease renewed every 2
  seconds; manual and autonomous gestures remain unleased.
- Persistent editing happens only while stopped. Export is `b1-sequence` v5;
  import accepts v1–v5 through named migrations.
