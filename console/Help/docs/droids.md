# Droids Card

![Droid roster and mesh overview](images/fleet-overview.png)

*Figure: A live fleet with one master and two slaves. Read the roster and radar
together before sending motion or starting an update.*

The Droids card is the fleet's operational roster. It includes the USB-connected
master and every slave currently retained by the master's live registry. A
silent droid remains visible as **lost** so you do not lose its row immediately.

## A practical identification workflow

When assembling or troubleshooting a fleet:

1. Power one unknown slave at a time.
2. Choose **Adopt** when its **new** row appears.
3. Enter a meaningful name and press Enter or click outside the name box.
4. Turn on **Locate** and confirm the matching physical board's onboard LED.
5. Turn Locate off and continue with the next board.

## Adoption, Ignore, and Forget

A slave the master has never adopted appears with **Adopt** and **Ignore** in
place of its normal controls. It can still relay mesh traffic and receive a
broadcast gesture while pending.

- **Adopt** stores the decision in the master's persistent storage.
- **Ignore** removes the current pending row, but it is not a blacklist. A
  powered slave may reappear on its next heartbeat and ask again.
- **✕ Forget** removes an adopted slave and clears its adoption status. If it is
  still active, it returns as a new pending droid.

Use Ignore for a board you do not want to decide about yet. A separate fleet
should use a different compile-time group key so its frames are rejected instead
of repeatedly appearing for adoption.

## Reading a row

| Column | Meaning |
| --- | --- |
| Name | Editable identity. Commit with Enter or by leaving the field. The name is sent to the droid and also cached by the master. |
| Version | Firmware reported by that board. Its color/tooltip indicates whether it matches the latest discovered release. |
| RSSI | Last signal strength in dBm. The master shows its local COM port; a lost slave shows `-`. Values closer to zero are stronger. |
| State | **online** or **lost**. Lost means 4 seconds without a heartbeat, not forgotten or unadopted. |
| Role | The USB-connected fleet coordinator is master; mesh members are slaves. |
| Servos | Enables or cuts that board's servo output. Use this first if motion is unsafe. |
| Auto anims | Allows or suppresses spontaneous idle gestures on that droid. Manual Play and Sequencer commands still work. |
| Locate | Temporarily overrides the onboard LED with a solid on/off state for physical identification. |
| Update | Opens USB flashing for the master or starts OTA for an adopted slave when a newer release is available. |
| Gear | Opens Servo Calibration already targeted to that row. |
| ✕ | Forgets an adopted slave. |

Locate is transient and is not saved. A board or console restart returns the LED
to its normal status pattern; current firmware reports that reset state so the
row no longer remains optimistically lit after the droid reappears.

On a virgin or full-erased board, **Servos** and **Auto anims** also begin off
and servo PWM stays detached until explicitly enabled. Once changed by the
operator, those two switches persist across ordinary reboots and firmware
updates; Locate never persists.

## Names and persistence

Renaming sends the new name to the target droid, where it is stored immediately,
and marks the master's working configuration dirty. The master copy is committed
after the header returns to **● synced**. Wait for that badge before resetting
the master.

Keeping a copy on the droid helps the master recover its display name after a
master configuration loss, but a **full erase** of that droid still destroys its
local name and calibration.

## Backup and restore: exact scope

**Backup…** exports a JSON file containing:

- the visible roster's droid IDs and names;
- saved frequency, amplitude, and speed values available per droid.

It does **not** contain servo calibration, adoption state, Servos/Auto anims/
Locate switch state, firmware images, sequences, or audio files.

**Restore…** overwrites names and animation settings from that file. With current
firmware, operations are validated and applied in size-bounded batches; a large
restore may require more than one batch. Older firmware falls back to individual
commands. Do not treat the whole file as one guaranteed rollback transaction:
leave the fleet powered and connected until the header returns to **● synced**,
then inspect the rows you care about.

See [Data & Backups](reference/data-and-backups.md) before a full flash or moving
a show to another PC.

## When a droid is lost

Do not immediately Forget it. Check power, then look at
[Mesh Topology](mesh-topology.md). A droid can be outside the master's direct
range and remain reachable through another slave. Commands sent while no path
exists are not queued for later delivery.

## Per-droid firmware updates

An adopted slave that is behind the latest release displays **Flash (OTA)**.
The master uses USB only; select its Flash action or the header's Firmware button.
Read [OTA over the Mesh](firmware/ota.md) before starting a transfer.

After startup connection and release checking, the console waits briefly for a
stable online roster. If one or more droids run an older published version, a
single **Fleet Firmware Update** window offers **Update all** or **Later**. It
never includes offline/pending droids, newer firmware, or a same-version custom
build. Accepting stops Sequencer playback, downloads and verifies each required
image, updates slaves one at a time by OTA, then app-only flashes the connected
master by USB last. The window shows each droid plus one overall progress bar and
stops at the first failure. Full erase is never selected by this assistant.
