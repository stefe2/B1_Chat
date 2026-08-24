# B1 Chat Console 0.13.0

This release requires **firmware 1.12.0** and later. The gesture catalog's
content-derived identity changed (46 new gestures), so this console fails
closed — refuses gesture dispatch with a clear message — against any droid
still running firmware 1.11.0 or older, by design.

## Gesture catalog: Stage 7 first content batch

- The catalog grows from 7 to 53 gestures across all seven planned families:
  rest, attention, communication, dialogue, emotion, reaction and mechanical
  effects. See `docs/GESTURE-SEQUENCER-V2.md` for the full list and design
  notes.
- **Not yet bench-audited on hardware.** Every new gesture's timing was
  chosen conservatively (no segment faster than the already-validated
  `attention.look-right`), but none has had a physical audition pass yet.
  Redundant or weak gestures are expected to be pruned after bench testing.

## Gesture picker now reads the catalog directly

- The Gestures palette, the inspector's GESTURE combo, click/drag insertion
  and serial dispatch previously hardcoded a lookup table sized for the
  original 7 gestures. All of that is now derived from the parsed catalog
  file, so the catalog is the only place that needs to change to add,
  remove or reorder a gesture.
- Saving or exporting a Scene that uses a new gesture previously threw an
  error; this is fixed.
- Palette rows group by the catalog's family field and pick up a per-family
  color, including two new colors for the `emotion` and `mechanical`
  families.
- Hovering a gesture chip now shows what that gesture actually does (its
  catalog description), instead of a generic click/drag reminder repeated on
  every chip.

## Validation

- Console and firmware (`b1_master`, `b1_slave`) all build clean.
- Full console test suite: 352/352 passing.
- No hardware bench pass yet for the new gestures — that remains open.

The installer is self-contained for Windows x64 and includes the application,
.NET desktop runtime, current Help payload, `espflash`, and its local Visual
C++ runtime.

## Download verification

SHA-256 for `b1-chat-console-setup-0.13.0.exe`: filled in after build.
