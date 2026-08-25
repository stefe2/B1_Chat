# B1 Chat Console 0.13.1

This corrective release requires **firmware 1.12.1** and later. It carries no
new gesture content — same 53-entry catalog as 0.13.0/1.12.0 — only bugs found
during the first bench audition pass of the Stage 7 gesture batch.

## Bugs found and fixed

- **Clip label showed `?` for every new gesture.** `AnimIdToNameConverter` was
  a sixth hardcoded animId→name table missed when the picker was made
  catalog-driven; it now reads the same parsed catalog as everything else.
- **Delete/Undo/Redo/Restart went dead after Play.** A window-level keyboard
  handler excluded any focused `ButtonBase` from every shortcut, not just
  Space; a transport button keeps focus after being clicked, so those
  shortcuts silently did nothing until the operator clicked a clip again.
- **`Yes` never executed on hardware.** A leftover pre-V2 legacy check
  (`AnimId is 16 or 17`) misidentified generated ID 17 as the old numeric
  alias for `dialogue.talk`'s continuous/leased behavior. The console then
  requested a lease for a plain one-shot gesture, which firmware correctly
  refused — silently, with no visible error beyond an `UNCONF` timeout badge
  on the clip. Execution kind is now derived from the catalog everywhere.
- **Every other new continuous gesture also silently failed** (`rest.idle-sway`,
  `attention.scan`, `attention.follow-slow`, `dialogue.listen`,
  `emotion.excited`, `emotion.confused`, `emotion.affection`, `emotion.bored`,
  `mechanical.self-check`, `mechanical.scan-vertical`) once the console
  started correctly requesting leases for continuous gestures: firmware's
  lease mechanism itself only accepted `dialogue.talk` by hardcoded ID. Fixed
  in firmware 1.12.1 — see its notes.
- **Clip tooltip simplified.** It no longer appends live per-droid delivery
  detail (`Request N: serial: written; master: accepted...`) under the static
  interaction hint; mixing a diagnostic trace into a tooltip whose first line
  is generic instructions read as confusing. That detail remains available
  via the clip's compact execution badge (`WRITE`/`MASTER`/`DONE`/`UNCONF`/...).

## Validation

- Console and firmware (`b1_master`, `b1_slave`) all build clean.
- Confirmed on a real master+slave bench: `Yes` and all ten continuous
  gestures now execute after the firmware update. The slave still needs the
  same firmware update before its own continuous gestures will work.
- Console test suite has 24 known-stale failures in
  `SequencerPreflightServiceTests` (fixtures built around the removed
  `AnimId is 16 or 17` magic numbers, not a functional regression) — tracked
  as follow-up, not fixed in this release.

The installer is self-contained for Windows x64 and includes the application,
.NET desktop runtime, current Help payload, `espflash`, and its local Visual
C++ runtime.

## Download verification

SHA-256 for `b1-chat-console-setup-0.13.1.exe`:

`b3ba44951a4a71128f83c89e2aabad80ea62a894e086e997ee27b991c5c89f2c`
