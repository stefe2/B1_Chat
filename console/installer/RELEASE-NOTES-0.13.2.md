# B1 Chat Console 0.13.2

This corrective release requires **firmware 1.12.1** (unchanged — no firmware
update needed for this release). It carries no new gesture content, only one
cosmetic bug found while refreshing the in-app Help screenshots after Stage 7.

## Bug found and fixed

- **The GESTURES palette's loop badge (⟲) pointed at the wrong chips.** A
  fifth, previously-missed spot of the `AnimId is 16 or 17` legacy check
  fixed console-wide in 0.13.1 was still live in the palette chip template
  (`SequenceTimelineView.xaml`): it marked `Yes` and `attention.quizzical` as
  looping and missed all eleven gestures that actually are (`Talk`, `Listen`,
  `Idle sway`, `Scan`, `Follow slow`, `Excited`, `Confused`, `Affection`,
  `Bored`, `Self check`, `Scan vertical`). Purely cosmetic — it never affected
  which gestures the Sequencer actually treated as continuous, only which
  chip looked like it in the library. `GestureLibraryEntry` now carries
  `IsContinuous`, derived from the parsed catalog like everything else in the
  0.13.1 sweep, and the badge binds to it directly instead of a two-case
  `DataTrigger`.

## Validation

- Console builds clean.
- Verified live against a real master+slave bench: the badge now lands on
  exactly the eleven continuous gestures and no others.

The installer is self-contained for Windows x64 and includes the application,
.NET desktop runtime, current Help payload, `espflash`, and its local Visual
C++ runtime.

## Download verification

SHA-256 for `b1-chat-console-setup-0.13.2.exe`:

`TBD`
