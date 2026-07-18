# Flashing over USB

Open the **Firmware…** window from the header to flash a board directly over
USB — this is the only path that works on a board with **no working firmware
yet** (a blank chip, or one that can't talk to the console at all).

## Choosing a role and a source

Pick **Master** or **Slave** first (only one master per fleet), then a source:
a downloaded GitHub release (recommended, self-verifying) or a local `.bin`
file.

## App-only vs. full flash

- **App-only** (default): writes just the application image at its usual
  address. The bootloader and partition table are left untouched, so a board's
  existing saved settings (names, calibration) survive. This is the right
  choice for routine updates of a board that has **only ever** been flashed
  over USB.
- **"New / erased board (full erase + flash)"**: fully erases the chip, then
  writes bootloader + partition table + app. This is the **only** path that
  ever touches the partition table, and it's paired with a full erase on
  purpose — see the warning below. Required for:
  - a genuinely blank/new board,
  - a board that has ever done **even one OTA update** (see
    [OTA over the Mesh](ota.md)) — an OTA flips which partition the board boots
    from, and an app-only USB flash afterward would silently keep booting the
    *old* image without any error.

> **Never rewrite the partition table without also erasing.** Writing a
> partition table that isn't byte-identical to the one already on the chip can
> shift where the board's saved settings actually live in flash, resurrecting
> old data or silently losing recent changes — with no error shown. That's why
> a full flash and a full erase are tied together in this app rather than
> offered as independent options.

## Steps

1. Pick the **Port**.
2. Pick the role and source (above).
3. Tick the erase/full-flash checkbox only if one of the two cases above
   applies to this board.
4. Press **Flash** and watch the progress and log. The console reconnects
   automatically once the new firmware boots.

## Save the log

Use **Save Log…** in the flash dialog to keep a copy for troubleshooting.
