# Gestures & Idle Behavior

## The 18 gestures

Every droid shares the same built-in catalog of 18 gestures — from simple
`IDLE`/`LOOK_AROUND`/`NOD_YES` moves to alert/glitch effects and two **looping**
gestures (`POWER_DOWN`, `TALK`) meant to play continuously rather than run once.
`TALK` in particular is designed to accompany audio — a fast tilt motion like a
talking mouth — see [Sequencer → Audio](sequencer/audio.md) for pairing a
gesture with a sound clip.

Triggering a gesture from the Animation card sends it to the selected
target(s); it plays immediately and, for a non-looping gesture, ends on its
own after its natural duration.

## Automatic idle behavior

When a droid isn't running a manually-triggered gesture, the master picks a
random gesture (excluding the two looping ones) every 2.5–5 seconds and
broadcasts it to the whole fleet, so idle droids stay subtly alive instead of
sitting frozen. A droid that's out of the master's direct range but still on
the mesh does its own local idle draws on a similar cadence instead.

Use the **Auto anims** toggle on a droid's row in the [Droids card](droids.md)
to suspend this for that droid specifically — its servos stay enabled and it
still reacts to anything you trigger manually (Animation card, Sequencer), it
just stops receiving the random idle broadcast.

## Durations

Gesture durations shown throughout the app (Sequencer clip widths, etc.) are
read directly from the firmware rather than hardcoded, so they self-correct if
a firmware update changes a gesture's timing.
