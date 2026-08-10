# B1 Chat Console 0.10.6

## Installer compatibility fix

- Bundles the complete Microsoft VC143 x64 runtime beside `espflash.exe`, so
  firmware flashing works on clean Windows installations without requiring a
  separate Visual C++ Redistributable installation.
- Fixes installer failure `-1073741515` (`0xC0000135`) when the target computer
  did not already have `VCRUNTIME140.dll` installed globally.
- Adds a generated runtime manifest with SHA-256 hashes for every bundled DLL.
- Verifies the runtime architecture, file presence, and integrity both before
  creating the installer and again from the installed application payload.
- Improves the installation error message if antivirus software quarantines or
  damages the flashing tool or one of its local runtime files.

This release includes all Help, tooltip, and window improvements from v0.10.5.
The installer remains self-contained and requires no separate .NET or Visual C++
runtime installation.

## Download verification

SHA-256 for `b1-chat-console-setup-0.10.6.exe`:

`545d50594ba53341ef8fd4d70caf8618d2f7740215e74f2d64c8bdcdc841b042`
