# B1 Chat Console 0.10.5

## Guided Help experience

- Reworks the Help window into one continuous document: scrolling naturally
  advances through every topic while the table of contents follows the active
  section, and selecting a menu item jumps directly to that section.
- Expands the documentation to 16 practical topics covering first setup,
  droids, calibration, mesh behavior, animation, sequencing, firmware, console
  updates, backups, troubleshooting, terminology, and operating tips.
- Adds 14 screenshots captured from the real console with a connected master
  and two slaves. Controls and status areas remain fully visible in every crop.
- Clarifies backup scope, adoption behavior, OTA prerequisites, safe flashing,
  audio-file portability, and recovery steps for first-time users.

## Contextual guidance

- Adds concise US English tooltips throughout the main console, calibration,
  firmware, Mesh topology, animation controls, and sequencer.
- Explains actions with non-obvious consequences, including live servo motion,
  automatic calibration saving, full-chip erase, track arming, snapping,
  timeline scrubbing, drag-and-drop, and right-click actions.
- Adds detailed Mesh node information and makes live packet tooltips reachable.
- Introduces a dark, wrapping tooltip style consistent with the console and
  keeps essential guidance available on disabled transport and flash controls.

## Window behavior and validation

- Centers the main console at startup, preserves its current width, and uses
  the full available screen height without covering the Windows taskbar.
- Uses the same 1500-pixel design width for the Help and main windows, with a
  1000-pixel Help height.
- Verifies that all Help pages and image references are present in the publish
  payload and that the self-contained x64 application and bundled espflash tool
  pass their installation checks.

The installer includes the .NET desktop runtime and espflash 4.4.0; no separate
.NET installation is required.

## Download verification

SHA-256 for `b1-chat-console-setup-0.10.5.exe`:

`d8beb267e86516f4970c15635136b127efb900315d5c60567b9e667cd8c4dca9`
