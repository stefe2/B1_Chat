# Troubleshooting

## Console won't connect / keeps disconnecting

- Check the **Port** dropdown — press **Rescan** if the board isn't listed.
- Only one application can hold a serial port at a time; close any other
  terminal/monitor tool talking to the same COM port.
- The console auto-reconnects on its own every few seconds once the port
  reappears — you don't need to press Connect again after a firmware flash or
  a power cycle, just wait a moment.

## A droid never shows up in the Droids card

- It may be waiting on **Adopt/Ignore** — see [Droids](../droids.md).
- Check [Mesh Topology](../mesh-topology.md) — if it has no link back to the
  master at all (direct or relayed), it's genuinely out of range.
- A droid shown with RSSI `-` is just **lost** (4 seconds of silence) — it
  clears itself automatically once it's heard from again, no action needed.

## A droid's saved name/calibration reverted unexpectedly

Most likely a **full USB flash** rewrote that board's partition table without
also erasing — see the warning in [Flashing over USB](../firmware/flashing.md).
Names and calibration are meant to survive this (each droid keeps its own copy,
see [Droids](../droids.md)), but the *master's* own display cache can still
show stale data until it hears from the droid again.

## OTA update failed or "rolled back"

See [OTA over the Mesh](../firmware/ota.md) — a genuine rollback means the new
image failed to boot cleanly and the droid's own safety net reverted it
automatically; nothing to recover manually. If the verdict is **unreachable**,
confirm the droid is powered and back in range, then retry.

## Sequencer audio doesn't play

Audio only plays through the **console**, during console-driven Play — see
[Sequencer → Audio](../sequencer/audio.md). Check the track/lane isn't muted,
and that the referenced audio file still exists at its saved path (moving or
renaming a file breaks the link).
