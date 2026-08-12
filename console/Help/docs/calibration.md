# Servo Calibration

Calibration defines the safe mechanical envelope and neutral pose of one droid.
Pan is horizontal; tilt is vertical. Each axis uses degrees from 0 through 180.

> **Motion safety:** clear hands, cables, costume parts, and hard stops before
> moving a slider. Use an external regulated servo supply with common ground.
> If a servo binds, buzzes, overheats, or pulls the mechanism hard against a
> stop, disable **Servos** in the Droids card immediately.

![Servo Calibration window for the selected droid](images/calibration-window.png)

*Figure: The selected droid appears at the top; Pan and Tilt each have Reverse,
Min, Center, and Max controls plus exact-position test buttons.*

## What the six values mean

- **Min** and **Max** are the allowed motion endpoints. Firmware clamps every
  preview and gesture to this interval.
- **Center** is the droid's neutral position and the reference point from which
  gesture offsets are applied.
- **Reverse** independently inverts that servo's electrical direction. Scene,
  animation, preview, limit and center coordinates remain unchanged; only the
  physical direction is mirrored. Each selected droid must report support for
  servo reversal; the option remains disabled for an older master or slave.
- Every axis must satisfy **min ≤ center ≤ max**, and every value must be from
  0 to 180. Invalid ordering is rejected by the firmware.

Tighter limits protect a head that binds before the theoretical 0° or 180°
servo range. A carefully chosen center makes neutral and mirrored gestures look
correct without changing firmware.

## Recommended calibration procedure

1. Select the intended droid and physically verify it with **Locate** if needed.
2. Begin with conservative values near the existing center.
3. If PAN moves opposite to the intended logical direction, select its
   **Reverse** option and allow the head to return to center.
4. Adjust Pan Min slowly. The selected droid previews that position live.
5. Repeat for Pan Max, then choose a comfortable Pan Center.
6. Configure TILT Reverse if required, then its Min, Center, and Max.
7. Use **→ Min**, **→ Center**, and **→ Max** to retest exact stored positions.
8. After the final change, wait at least **1.2 seconds** without moving another
   slider or changing targets.
9. Reselect the droid to request its values again and confirm they persisted.

## Preview versus saved calibration

Every slider movement sends a transient preview immediately. The six limits and
two Reverse flags are sent only after 1.2 seconds without another change. The
target is captured when the edit is made, but selecting another droid before the
delay expires cancels that pending save.

The actual droid writes a received calibration directly to its own persistent
storage. It does not use the master's **unsaved/synced** badge, and calibration
is not included in Droids Backup. A full chip erase of that droid removes it.

## If the droid is unreachable

Calibration needs a live path to that specific target. There is no offline queue
and currently no success/error dialog. If a target loses power or mesh reachability,
reselect it after reconnection and verify all eight returned values before assuming
the change took effect.

## Sensible first test

After calibration, reduce Animation amplitude, play a small one-shot gesture,
and observe both axes through the full motion. Increase amplitude only after the
mechanism remains clear of its limits.
