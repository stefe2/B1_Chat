# OTA over the Mesh

An **adopted** slave can be reflashed without a USB cable, straight from its
row in the [Droids card](../droids.md) — the `.bin` travels console → master
over serial, then master → droid over the ESP-NOW mesh.

## Starting an update

Once a newer firmware release is available, a flash button appears on that
droid's row. Only **one OTA session runs at a time across the whole fleet** —
starting a second one while one is already in progress is rejected outright
rather than queued.

## What to expect

- Progress is shown as fragments sent out of the total — not a fixed time
  estimate. A realistic transfer takes **8–15 minutes** over a good single-hop
  link, more over a weak or multi-hop one.
- When the transfer finishes, the droid reboots on its own, and the console
  watches for it to come back — this can take up to about 90 seconds.
- The result is decided by comparing the droid's firmware version **before**
  and **after** the reboot (not by trusting an announced version number):
  - **Success** — the version changed.
  - **Rolled back** — the version is unchanged; the safety net below reverted
    to the previous image, most likely because the new image failed to run
    correctly.
  - **Unreachable** — no heartbeat arrived from the droid within the wait
    window; check that it's powered and back in range.

## Anti-brick safety net

Before finalizing an update, the droid checks the transferred image's
integrity and arms a "pending" flag before rebooting into it. If the new image
fails to boot cleanly a few times in a row, the droid **automatically switches
back** to its previous image on its own — no console involvement needed. Once
a freshly-flashed image runs for about 20 seconds without resetting, it's
considered confirmed good and the flag clears.

## After an OTA

A droid that has ever received an OTA update has its boot partition flipped
compared to a droid that's only ever been USB-flashed. If you later need to
flash that same droid over **USB**, use the full-erase option, not the
app-only default — see [Flashing over USB](flashing.md) for why an app-only
USB flash on a previously-OTA'd board can silently boot the old firmware.
