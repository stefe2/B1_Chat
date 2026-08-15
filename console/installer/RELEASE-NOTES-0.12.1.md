# B1 Chat Console 0.12.1

This corrective console release remains compatible with **firmware 1.11.0** and
**protocol 5**. It changes no firmware or serial/mesh command and requires no
droid update.

## Help brought up to date

- Reviews every in-app Help page against the current WPF controls, ViewModels,
  firmware behavior and storage boundaries.
- Documents the supervised Fleet update prompt and progress window, the
  clickable **update available** badge, Advanced-only full erase, persistent
  flash result feedback and the current console-update flow.
- Corrects calibration save timing, Servos/Auto anim persistence, transient
  Locate behavior, firmware version plus Build ID identity, Scene endpoints,
  Play-from-cursor behavior and supported Scene schemas.
- Replaces nine stale screenshots captured from the running application and
  adds a dedicated Fleet update image.

## Complete tooltip audit

- Every operator-facing button, toggle, checkbox, combo box, text field,
  slider, context-menu item and selectable list now has an action tooltip.
- Tooltips remain available while supported controls are disabled, so an
  unavailable Save, Open, Loop, Auto or Flash action can still explain itself.
- Corrects misleading descriptions for Animation broadcast and looping
  gestures, the 1.2-second calibration save delay, Locate's solid-on override,
  local firmware support images, Scene endpoint clamping and the Sequencer's
  0.2-second duplicate offset.
- Clarifies the master's **unsaved/synced** configuration badge and the
  conditional Fleet-versus-Firmware behavior of the update badge.

## Regression protection

- Adds a XAML-wide test that rejects any new operator-facing interactive control
  without a tooltip.
- Adds focused checks for high-risk timing/safety wording and for disabled
  tooltip support in every shared interactive style.
- The complete headless console suite passes 293 tests, and the non-destructive
  repository self-test passes all 21 checks while preserving build number 360.

The installer is self-contained for Windows x64 and includes the application,
.NET desktop runtime, current Help payload, `espflash`, and its local Visual C++
runtime. Firmware 1.11.0 remains the matching current droid release.

## Download verification

The published installer contains console build 361. SHA-256 for
`b1-chat-console-setup-0.12.1.exe`:

`fe2207baaa86582b190557b5073d2d26d5c24dee0650ca0edfa0ef2b0e1c58d0`
