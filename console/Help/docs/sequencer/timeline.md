# Sequencer — Timeline

The Sequencer choreographs several droids (and audio) together on one shared
timeline, entirely from the console — there's no on-board sequencer anymore
(see [Playback](playback.md)).

## Tracks

- One horizontal track per droid, plus a synthetic **"All droids"** broadcast
  row for a gesture aimed at the whole fleet at once.
- The track gutter on the left shows each droid's name and role (MASTER /
  SLAVE); a track disconnected from the mesh (e.g. loaded from a saved file
  with droids offline) still gets its own row, marked OFFLINE, so the layout
  isn't lost.
- Click a track's row to **arm** it — new gestures inserted from the library
  land on the armed track at the current playhead position.
- Each track has its own mute switch (glossy on/off, same style as Servos/Auto
  anims elsewhere) — mute only affects local **Play** (see
  [Playback](playback.md)), it can't suppress anything once a gesture has
  actually been sent to the mesh.

## The ruler and gesture clips

The ruler across the top shows time (zoomable, 20–300 px/s) and gridlines run
down through every row so clips line up visually. Each clip is colored by
gesture family and shows the gesture name plus its real duration; the two
looping gestures (`POWER_DOWN`, `TALK`) get a small loop badge.

- **Insert**: click a gesture in the library row at the bottom to drop it on
  the armed track at the playhead, or **drag** a library chip directly onto a
  specific track+time.
- **Move**: drag a clip — it glides freely at pixel level on both time and
  track axes while held, and only snaps to the nearest 100 ms / settles onto a
  row when you release it.
- **Retarget**: dragging a clip to a different track's row changes which droid
  it plays on, in the same drag as the time move — one Undo restores both.
- **Duplicate / Delete**: right-click a clip for a context menu, or use the
  inspector panel.

## The inspector

Selecting a clip opens an inspector with the gesture, target droid, and a
precise start-time field (±0.1 s nudge buttons) — useful for adjustments finer
than a mouse drag.

## Undo / Redo

Every discrete edit (one drag, one insert, one delete) is a single undo step —
a drag doesn't create dozens of intermediate steps just because the mouse moved
many times.

## Saving your work

There's no "Save to droid" step for sequences — see [Playback](playback.md) for
how a sequence is stored and shared (Local Library, `.b1seq.json` export/import).
