# Droids Card

The Droids card is the fleet roster: one row per droid the master can currently
hear on the mesh, live-updated from its heartbeats.

## Adoption

A droid the master has **never seen before** (or one you've previously
**Forgotten**) doesn't join the roster automatically — it stays reachable on the
mesh (it still receives broadcast gestures) but the console asks you to
**Adopt** or **Ignore** it first. Adopting persists the decision in the master's
own storage, so it survives a master reboot; declining just means it'll ask
again next time that droid talks.

Use the **✕** button at the end of a row to **Forget** an adopted droid — this
removes it from the roster and clears its adoption status, so it comes back
through the Adopt/Ignore prompt the next time it's heard from.

## Per-row info and controls

| Column | Meaning |
| --- | --- |
| Name | Editable in place — renaming also pushes the new name to the droid itself (see below), not just the master. |
| RSSI | Signal strength as last reported by the master; shows `-` while the droid is lost. |
| Role | MASTER or SLAVE. |
| Version / Update | Firmware version reported in the droid's own heartbeat, with an update indicator once a newer release is available. |
| Servos | On/off — cuts or resumes that droid's servo output entirely. |
| Auto anims | On/off — pauses the automatic idle-gesture broadcast for that droid without touching Servos or a manually-triggered `Play`. |

A droid is marked **lost** (RSSI shown as `-`) after **4 seconds** of silence —
this is purely a display state, not another adoption prompt; it clears itself as
soon as the droid is heard from again.

## Locate

The **Locate** toggle overrides a droid's onboard status LED with a **solid**
on/off, so you can match a physical droid to its row on screen. It's transient —
not saved anywhere — so a reboot (of either the droid or the console) silently
reverts to the normal blink pattern.

## Renaming and per-droid persistence

Editing a name updates the master's own display copy (through the usual
auto-commit, see [Getting Started](getting-started.md)) **and** relays the new
name to the droid itself, which saves it in its own storage immediately. This
means a droid remembers its own name even if the master's storage is ever wiped
(e.g. a firmware re-flash that resets its saved config) — see [Flashing over
USB](firmware/flashing.md) for when that can happen.

## Backup & restore

Use **Backup** to export the whole roster's settings (names, animation
parameters) to a file, and **Restore** to push a previously-saved backup back to
the master. Restoring re-applies every setting in one atomic operation, then
auto-commits it — you don't need to touch anything else afterward.

## Firmware, per droid

Each row shows the droid's current firmware version. Once a newer release is
available, a flash button appears on the row — this starts an **OTA** update
over the mesh, no USB cable needed for that specific droid. See [OTA over the
Mesh](firmware/ota.md) for the full flow, timing, and what the different
outcomes mean.
