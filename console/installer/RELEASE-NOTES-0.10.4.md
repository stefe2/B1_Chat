# B1 Chat Console 0.10.4

## Reliability and safety

- Moves ESP-NOW message processing out of the Wi-Fi callback and into a bounded
  main-loop inbox, with synchronization around shared mesh state.
- Adds strict serial, mesh, configuration, calibration, and OTA payload
  validation.
- Randomizes the initial mesh sequence number after boot to reduce false
  duplicate detection following a restart.
- Requires a valid SHA-256 before downloaded firmware can be marked as verified.

## Droid controls

- Stores animation parameters per droid and keeps targeted console responses
  associated with the correct droid.
- Makes animation and calibration debounces retain the target selected when the
  edit was made.
- Preserves each droid's saved calibration center and suppresses write-back while
  calibration values are being loaded.

## Help and installer

- Guarantees that every Help page and image is copied into the published payload.
- Prevents a missing Help image or Markdown rendering failure from freezing the
  console and records UI errors in the diagnostic trace.
- Adds release-time and post-install payload verification.
- Checks the Windows version and CPU architecture before installation.
- Verifies the bundled .NET/WPF application, managed libraries, and espflash tool
  on the destination computer.
- Warns when optional Windows Media Foundation components are unavailable.

The .NET desktop runtime and espflash 4.4.0 are included in the installer; no
separate .NET installation is required.
