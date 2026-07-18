# Servo Calibration

Each droid has two servos — **pan** and **tilt** — and this card lets you set
the safe range each one is allowed to move within, per droid.

## Live preview

Pick a droid, then drag the pan/tilt sliders: the droid moves **live** as you
drag. This preview is transient — it's not saved, and it doesn't interrupt
whatever animation state the droid was in beyond the preview itself; releasing
the slider (or switching to another droid) simply stops sending preview
updates.

## Limits

The six limit values (pan min/center/max, tilt min/center/max) define the
physical range the droid's own firmware will ever drive that servo to — every
gesture and every preview is clamped against them. Setting tighter limits is
the way to protect a droid's specific mechanical range (e.g. a head that binds
before reaching a full 180°) without touching firmware.

## Where this is saved

Unlike animation parameters and names, calibration is **not** part of the
header's auto-commit badge. Sending a calibration change is relayed straight to
the targeted droid, which writes it to its own storage **immediately** — the
same "own storage, own droid" pattern used for droid names, see
[Droids](droids.md). This means calibration survives even if the master's own
storage is ever wiped.

## If a droid is unreachable

Calibration requires a live round-trip to the specific droid — if it's out of
range or powered off, the change simply won't apply (no error dialog, no way to
"queue" it for later). Reselect the droid once it's back to confirm the values
took.
