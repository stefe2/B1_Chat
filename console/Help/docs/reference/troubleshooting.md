# Troubleshooting

Start with the first heading that matches what you see. Avoid full erase as a
generic troubleshooting step; it permanently removes local calibration and is
necessary only for the cases described in [USB Flashing](../firmware/flashing.md).

![Healthy connected state in the console header](../images/connection-controls.png)

*Figure: Use this as the connection baseline: green status with firmware
version, an explicit COM port, Rescan, and a complete Disconnect control.*

## No COM port appears

- Confirm the cable carries data; many charging cables do not.
- Try another USB port without a hub.
- Open Device Manager → **Ports (COM & LPT)** and watch while unplugging/replugging.
- Install the USB-to-serial driver recommended by the board manufacturer. DOIT
  ESP32 variants commonly use CP210x or CH34x bridges.
- If Windows shows an unknown or failed device, resolve that before Rescan can
  find it.

## Port exists, but Connect fails or keeps disconnecting

- Choose **Rescan**, then reselect the current COM number.
- Close PlatformIO Monitor, Arduino Serial Monitor, terminal programs, and any
  second console instance. Only one process can own a COM port.
- Use a short known-good cable and stable board power.
- The console retries an unexpectedly lost port every 3 seconds. A deliberate
  **Disconnect** stops retries.

## Status stays at “handshake”

The port opened, but the expected B1 master did not answer.

- Confirm this board was flashed with the **Master** role.
- Reset it once and allow reconnection.
- Check that firmware and console are reasonably current.
- A blank board or broken application must be recovered through
  [USB Flashing](../firmware/flashing.md), which does not require a handshake.

## A droid does not appear or repeatedly asks for adoption

- A pending row needs **Adopt**. **Ignore** is temporary; an active board can
  return on its next heartbeat.
- Confirm the slave and master use the same compile-time fleet group key.
- Check power and wait several seconds for heartbeat/neighbor reports.
- Use [Mesh Topology](../mesh-topology.md) to look for a direct or relayed path.
- A retained row becomes **lost** after 4 seconds of silence and recovers
  automatically when another heartbeat arrives.

## A command or calibration change had no effect

- Commands are not queued for offline droids. Restore the route and repeat.
- Verify the target with Locate before assuming the wrong mechanism moved.
- Calibration saves after 1.2 seconds of inactivity. Changing targets sooner
  cancels the pending calibration send.
- Reselect the calibration target to read its values back.
- For names, wait for **● synced** before power-cycling the master.

## A name or calibration disappeared after flashing

- **App-only USB flash** preserves NVS when the existing partition layout is the
  expected one.
- **Full erase + flash** intentionally deletes that board's name, calibration,
  and all other NVS settings. Re-enter the recorded name and recalibrate.
- An older/manual procedure that rewrote a different partition table without
  erasing may have moved the NVS region. Do not repeat it; perform one deliberate
  full erase/recovery, then rebuild the board's settings.
- The master's cached name can look stale until it hears the droid's own stored
  name again.

## USB flashing cannot connect

- Close every application using the Flash Port.
- Confirm the selected COM port belongs to the target board.
- Try another cable/USB port and remove unstable servo power loads.
- Some ESP32 boards require holding **BOOT** while the connection begins; release
  it when writing starts. Use EN/RESET according to the board manufacturer.
- Full erase requires `bootloader.bin` and `partitions.bin` as well as the app.
  **From GitHub** supplies them for current releases.

![Firmware recovery choices](../images/firmware-source-options.png)

*Figure: Before recovery, recheck the role and Flash Port. Leave full erase off
unless the documented recovery case actually requires it.*

There is no Save Log button in the current Firmware window. Take a screenshot of
the visible flash output before closing it if you need to report a failure.
The separate result panel above the log should remain visible after completion
and state **FLASH COMPLETED** or **FLASH FAILED** with a **Close window** button.

## “Update available” appears, but no fleet prompt opens

- The startup offer waits for the release check and a stable online roster.
- Only adopted, online droids running an older semantic firmware version enter
  the automatic plan. Offline/pending droids, newer development firmware, and a
  same-version build with another Build ID are deliberately left alone.
- Select the green **update available** badge to reopen an eligible plan after
  choosing Later. If only the Windows console is newer, the badge opens the
  regular Firmware window instead.

## OTA failed, rolled back, or became unreachable

- **Rolled back** means the reported version did not change after reboot; the
  previous image most likely recovered automatically.
- **Unreachable** means no heartbeat returned within the wait window. Restore
  power and range, then inspect the current version before retrying.
- A serial disconnect, silent fragment, integrity error, or busy session can
  end the transfer before reboot.
- Do not launch repeated retries until master USB, target power, and mesh path
  are stable.

See [OTA over the Mesh](../firmware/ota.md) for the complete outcome model.

## Sequencer Play does not stop a moving droid

Pause and Stop cancel future timeline sends; they cannot retract a gesture already
received by firmware. One-shot gestures finish naturally. Replace the running
continuous gesture with another gesture, or disable Servos for an immediate
motion cut.

## A sequence was not there after restart

Timeline edits are not autosaved. Use **Save** for normal Local Library work, or
**Save As** for a separate Scene. The console reloads the last library Scene or
imported/exported file, but not unsaved changes made afterward. Export a
`.b1scene.json` copy for backup or transfer. See
[Data & Backups](data-and-backups.md).

## Audio is silent or has no waveform

- Confirm the PC output device and system/app volume.
- Confirm the file still exists at the exact stored path.
- Audio lanes have no mute switch; droid-track mute affects gestures only.
- Test MP3, WAV, WMA, or OGG in another Windows player.
- A missing waveform can indicate a decoder problem even if another format
  plays correctly.

## Diagnostic files

The console recreates this timestamped serial trace on every launch:

`%LOCALAPPDATA%\B1ChatConsole\serial-trace.log`

It contains truncated serial TX/RX lines and connection errors, including OTA
serial activity. Copy it **before launching the console again** if it captures a
problem you need to keep. It is not a complete ESP-NOW packet capture and does
not contain all `espflash` console output.
