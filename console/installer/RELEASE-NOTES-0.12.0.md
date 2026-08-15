# B1 Chat Console 0.12.0

This console release remains compatible with **firmware 1.10.0**. It changes no
firmware or serial/mesh protocol and requires no droid update.

## Sequencer Preflight, simplified

- **Preflight is now an optional Scene-content assistant.** It runs only when
  its panel is opened, never blocks Play, Restart or Resume, and deliberately
  ignores temporary connection, handshake, master and online-droid state.
- While open, the panel stays visible and refreshes immediately when timeline
  content changes. A second click on **Preflight** closes it; there is no extra
  Close button and no persistent red result badge in the toolbar.
- The remaining findings focus on actionable authored content: missing or
  unreadable audio, uncertain duration, overlapping or ambiguous gestures, and
  invalid TALK/POWER_DOWN endpoints. **Go to** still navigates to the affected
  clip.
- Real delivery and execution failures remain visible on gesture clips during
  playback (`NO LINK`, `NOT READY`, `WRITE FAIL`, `MASTER`, `DONE`, `REJECT`,
  `TIMEOUT`, and related per-target states).

## Scene timing and transport

- Every Scene now has one visible, authoritative endpoint. **END AUTO** follows
  the latest content tail; **Set End** creates a persistent **END SET** boundary
  for intentional silence or a longer timed pass, and **Auto** restores the
  calculated tail.
- Looping audio repeats until the Scene endpoint. Natural completion, Stop,
  whole-Scene Loop, Pause/Resume and stale scheduler callbacks share the same
  deterministic boundary rules.
- Play from a retained cursor seeks into audio clips that already overlap that
  position, including the correct modulo position for looping audio. Earlier
  gestures remain skipped rather than reconstructing unknown mechanical state.
- Moving the playhead during Pause now abandons the paused pass through normal
  Stop cleanup; clicking without moving preserves ordinary Resume.
- Follow resumes after a horizontal scrollbar drag, while deliberate Fit,
  zoom and pan continue to leave viewport control with the operator.

## Audio robustness

- Duration probing now has typed outcomes, cancellation and a ten-second bound,
  with dispatcher-safe media teardown on every success, failure and timeout
  path.
- Missing or unreadable audio clips remain selectable as narrow warning clips
  with an orange border, badge and reason tooltip, but their stale duration can
  no longer extend the Scene.
- Playback retires each media handle when its clip ends or fails, so Resume
  cannot restart an already completed clip. Failures are named once in the
  visible **AUDIO** status while the rest of the pass continues.
- Waveform decoding uses a bounded metadata-aware cache and rejects results from
  replaced files or stale asynchronous work.

## Editing and presentation

- Gesture-library clicks require an explicitly armed target; fresh startup no
  longer guesses the broadcast row. Direct drag-and-drop remains explicitly
  targeted by its destination lane.
- Preflight identifies full represented-duration overlaps, duplicate timestamps
  and broadcast/target ambiguity, while runtime **SCHEDULE** feedback preserves
  deterministic editor-order semantics.
- Main-window startup now fits and centers against the actual monitor that owns
  the WPF window, including smaller secondary displays, mixed DPI and offset or
  negative desktop coordinates. The title bar remains reachable for resizing.

## Validation

- 279 headless console tests cover the new advisory/live Preflight behavior,
  endpoint and transport semantics, audio lifecycle, real MP3 decoding and WPF
  Media Foundation teardown.
- The non-destructive repository self-test builds master and slave firmware,
  builds the WPF console, runs the complete suite, verifies installer
  prerequisites and performs the real Media Foundation smoke test.

The installer is self-contained for Windows x64 and includes the application,
.NET desktop runtime, Help payload, `espflash`, and its local Visual C++ runtime.
