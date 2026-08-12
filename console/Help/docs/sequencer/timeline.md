# Sequencer — Build a Timeline

The Sequencer coordinates gesture commands and PC audio on one shared clock. It
is entirely console-driven: no sequence is uploaded to a droid or master slot.

![Sequencer transport controls](../images/sequencer-transport.png)

*Figure: Start with the transport, timecode, zoom, Snap, and editing controls;
the entire Add audio lane button remains visible at the right.*

*Transport, timecode, zoom, Snap, editing, import/export, Clear, and audio-lane
controls stay together above the timeline.*

## A first sequence in five steps

1. Connect the master so live droids populate their tracks.
2. Click a track gutter to **arm** it. The highlighted track receives gesture
   chips that you click.
3. Click the ruler to place the playhead at the desired time.
4. Click a gesture in the bottom library, or drag it directly onto a track and
   time.
5. Add more droids or audio, choose **Play**, then **Export** a snapshot when the
   result is worth keeping.

## Tracks

![Gesture and audio tracks](../images/sequencer-tracks.png)

*Figure: Audio lanes sit above the broadcast, master, and slave gesture tracks.
Every visible clip is contained completely inside the capture.*

*This real sequence contains two audio lanes, a broadcast lane, and offline
droid lanes preserved from its saved roster.*

- **All droids** is a broadcast track. One clip sends one fleet-wide command.
- Each known droid receives its own track with its name and role.
- A droid saved in a file but currently absent remains as **OFFLINE**, preserving
  the arrangement until it reconnects.
- Clicking a gutter arms that track for gesture-library clicks.
- The green switch mutes a droid track during console Play. It does not edit the
  sequence file and cannot retract a gesture already sent to the mesh.
- Audio lanes are not controlled by these droid mute switches.

## Ruler, zoom, and Snap

The ruler shows time; click or drag it to move the local playhead while playback
is stopped or paused. The zoom control ranges from 20 to 300 pixels per second.
**Fit** zooms the whole sequence into view and returns horizontal scroll to the
start.

With **Snap** enabled, inserted or dragged clips round to the nearest 100 ms when
released. While held, a clip moves freely at pixel precision. Disable Snap to
retain the unsnapped millisecond position. The inspector's −0.1 s and +0.1 s
buttons always nudge by 100 ms.

## Gesture clips

![Gesture library](../images/gesture-library.png)

*Figure: All six behavior groups are visible, including Alert & Glitch and the
audio-synchronized Talk loop.*

*The built-in gestures are grouped by purpose; loop badges identify
`POWER_DOWN` and `TALK`.*

- **Insert:** click a library chip to insert it on the armed track at the
  playhead, or drag the chip to a specific track and time.
- **Move:** drag a clip horizontally in time or vertically to retarget it.
- **Select:** click a clip to open its inspector.
- **Duplicate/Delete:** right-click the clip or use the inspector buttons.
- **Inspector:** choose a different gesture or target, view its exact start in
  milliseconds, and use the ±0.1 s buttons. The displayed start value is not a
  text-entry field in the current release.

Clip widths use durations reported by firmware. The loop badge means the droid
continues that gesture until another gesture replaces it or the Sequencer sends
targeted IDLE during Stop/non-looping end cleanup; the displayed width is
only an indicative timeline duration.

## Undo and Redo

Insert, delete, duplicate, drag, nudge, add/delete/move/replace audio, Clear, and
lane creation/deletion create history entries. One drag produces one undo step.
A click without movement, a drag returned to its original placement, or another
edit that leaves the persistent document unchanged creates no Undo entry and
does not mark the sequence dirty.

Some direct property edits in the current release — notably changing a gesture
or target in the inspector, renaming an audio lane, and toggling an audio clip's
Loop flag — are not guaranteed to create their own history entry. Export before
making a set of changes you may need to recover exactly.

## Saving your work

Timeline edits are not autosaved and there is no Save-to-droid step. Use
**Export** to create a `.b1seq.json` snapshot. See [Playback](playback.md) for
what is restored at startup and the current Local Library limitation, and
[Audio](audio.md) for portable-file considerations.
