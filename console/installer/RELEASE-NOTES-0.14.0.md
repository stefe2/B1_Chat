# B1 Chat Console 0.14.0

This release pairs with **firmware 1.12.2**, published at the same time. The
console still works with firmware 1.12.1, but only 1.12.2 stops the head from
recentering after a calibration save (see below). Every change comes from a
bench-test session.

## Servo calibration no longer moves the head on its own

- **Sliders only edit values.** Moving a Min, Center or Max slider no longer
  drives the servo there. The servo moves only when you press **→ Min**,
  **→ Center** or **→ Max**. If a change is still waiting for the 1.2-second
  auto-save, pressing a button sends it first, so the servo reaches the value
  shown on screen.
- **Firmware 1.12.2 keeps the head where it is when calibration is saved.**
  Earlier firmware recentered the head after every save. Now a head only moves
  if its position falls outside the new range, and then only to the nearest
  new limit. A droid still centers itself at boot.
- **Direction labels.** Min and Max now show which way they go: PAN Min is
  *left* and Max is *right*; TILT Min is *down* and Max is *up*. If → Min moves
  the wrong way, use **Reverse**; never swap the Min and Max values.

## Per-clip audio volume

- Right-click an audio clip and use its **Volume** slider (0–200 %, in 5 %
  steps), or **Reset volume to 100 %**.
- 100 % is exactly the level every clip played at before, so existing Scenes
  sound unchanged. A clip that is not at 100 % shows a small 🔊 percentage on
  the timeline.
- The change is non-destructive: the audio file is never modified. Releasing
  the slider is one undo step, and the new level applies from the next Play.

## Multi-selection on the timeline

- **Ctrl+click** gesture or audio clips to add them to a selection, or remove
  them from it. Selected clips are outlined in white.
- Dragging any selected clip moves the whole selection in time, keeping the
  spacing between clips. Rows, targets and lanes do not change, and the whole
  move is one undo step.
- A plain click on another clip, or on empty timeline space, ends the selection.

## Scene format change

Scenes are now saved as **`b1-scene` version 2**, which stores each audio
clip's volume. Version 1 Scenes still open, with every clip at 100 %. A Scene
saved by 0.14.0 **cannot be opened by an older console**, so update every PC
that shares Scenes.

## Validation

- Console and firmware build clean.
- Full console test suite: 363/363 passed. This includes 24 older
  continuous-gesture tests that had been failing since 0.13.1 because they
  still used the legacy gesture IDs 16/17. They now use real catalog gestures,
  so Stop, Pause, Restart and link-loss cleanup are covered again.
- Firmware 1.12.2 flashed and booted on the bench master.

The installer is self-contained for Windows x64 and includes the application,
.NET desktop runtime, current Help payload, `espflash`, and its local Visual
C++ runtime.

## Download verification

SHA-256 for `b1-chat-console-setup-0.14.0.exe`:

`(filled in after the installer is built)`
