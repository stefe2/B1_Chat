# Tips & Shortcuts

![Complete gesture library organized by behavior](../images/gesture-library.png)

*Figure: Color-coded categories make the library faster to scan. Arm a target
before clicking a chip, or drag it when target and timing must be explicit.*

## Everyday fleet work

- Name droids by physical location or role, then verify each one with Locate.
- An RSSI number closer to zero is stronger. Compare trends rather than treating
  RSSI as a precise distance.
- A lost droid usually needs power or route recovery, not Forget.
- Wait for **● synced** before switching off the master after a name or animation
  tuning change.
- After calibration, wait 1.2 seconds and reselect the droid to verify the values.
- Hover over controls for a concise tooltip.
- Select the green **update available** header badge to reopen the eligible
  Fleet plan, or the Firmware window when only the console app is newer.

## Sequencer editing

- Arm a target, then click a gesture chip to place it there at the playhead. A
  click with **NO TRACK ARMED** inserts nothing; drag when you want a specific
  track/time in one action.
- With Snap selected, placement rounds to 100 ms on release; without Snap,
  dragged positions retain millisecond precision.
- Drag a gesture vertically to retarget it. One Undo restores both time and
  target for that drag.
- Right-click gesture clips for Duplicate/Delete and audio clips for Replace,
  Loop, or Delete.
- Click an audio lane's name to rename it. Right-click the gutter to delete the
  lane.
- **Fit** shows the complete sequence and returns horizontal scroll to the start.
- Use `Ctrl+wheel` to zoom around the pointer and `Shift+wheel` to pan. Both
  suspend Follow; click **Follow** when you want the view to catch up again.
- **Play** is a Play/Pause/Resume toggle. Use **Restart** for an explicit pass
  from zero; normal **Stop** retains the playhead, and the separate return button
  moves it back to the beginning.
- The timecode shows current position / authoritative Scene endpoint (automatic
  content tail or manual **END SET**).
- Open **Preflight** for advisory Scene-content hints. Leave it open while
  correcting clips: its findings refresh automatically until you press the
  button again to close it.
- Use **Ctrl+S** to save the open Scene often and **Ctrl+O** to switch Scenes
  through the browser. Export external copies from the **…** menu only for
  backup or transfer. Undo history and unsaved edits do not survive an
  application restart.

## Reliable show preparation

- Keep the PC awake and disable disruptive sleep/update behavior for a show.
- Use local audio files on a stable drive; avoid removable/network paths.
- Test the exact master USB port, audio output, and mesh layout in advance.
- Start with all required droids online. Missed timeline events are not replayed
  when a droid reconnects.
- Remember that Pause cannot retract a gesture already sent. Normal Stop cleans
  up Sequencer-owned `TALK`/`POWER_DOWN`, but finite gestures finish naturally;
  use Safe Stop or E-STOP for their distinct fleet-wide safety policies.

## Firmware work

- Write down whether a board has ever completed OTA. Its next USB flash should
  use full erase + flash.
- Record calibration before any full erase; Droids Backup does not contain it.
- Prefer From GitHub for release binaries with SHA-256 verification.
- For local files, confirm role, group key, and the presence of support images
  before enabling full erase.
