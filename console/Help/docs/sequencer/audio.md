# Sequencer — Audio

All sequence audio is played by the **console itself**, on your PC's speakers —
no droid has an onboard audio player. This means audio is only ever heard
during console-driven playback (see [Playback](playback.md)); a droid alone on
the mesh, with no console connected, never plays sound.

## Audio lanes and clips

Below the droid tracks, the timeline has one or more **audio lanes** (e.g.
"AUDIO", "AMBIENT"), each a named row that can hold several **clips**:

- **Add a clip**: use a lane's "+" to pick a sound file. The clip shows a
  waveform preview once its file has been analyzed.
- **Move**: drag a clip in time, same smooth pixel-level drag + release-to-snap
  behavior as gesture clips.
- **Move between lanes**: drag a clip across into a different lane's row — it
  dims while "in hand" and re-parents into the new lane on release.
- **Overlap**: clips in the same lane may freely overlap (e.g. a short sound
  effect layered on a looping ambient bed) — there's no collision handling,
  they just play concurrently.
- **Right-click** a clip for Replace file… / Loop / Delete.
- **Rename or delete a lane**: right-click the lane's row in the gutter — you'll
  be asked to confirm if it still holds clips.

## Looping clips

A clip marked **Loop** restarts automatically for as long as the sequence
keeps running past its natural end — useful for an ambient bed under a shorter
sequence of gestures.

## Where this is saved

Audio lanes/clips are saved **with the sequence** (Local Library entry or
`.b1seq.json` export/import) — but the actual sound files stay wherever they
are on your disk; only the file path is stored. Moving or renaming a referenced
audio file will break the link the next time the sequence is loaded.
