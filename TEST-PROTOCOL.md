# B1 Chat — autonomous test protocol

The default self-test is intentionally non-destructive. It can be launched by
Codex or from PowerShell without answering prompts:

```powershell
.\tools\self-test.ps1
```

It never flashes a board, starts an OTA, changes a valid configuration,
enables/disables a servo, previews a position, or starts an animation.

## What it checks automatically

### Offline checks

- builds `b1_master` and `b1_slave`;
- builds the WPF console without changing `console/build.number`;
- runs `git diff --check`;
- verifies that the callback-to-loop mesh inbox is present;
- verifies the per-droid animation-parameter store and targeted protocol;
- verifies the serial, mesh, and OTA validation guards;
- verifies randomized mesh sequence initialization;
- verifies fail-closed SHA-256 handling for downloaded firmware assets;
- verifies that Help files are forced into the published payload, checked before
  installer creation, and handled safely at runtime if an image is absent;
- verifies that the installer checks Windows/CPU compatibility, warns when the
  optional Windows media stack is absent, and executes both installed binaries;
- verifies that animation/calibration debounces snapshot their target and that
  calibration loads suppress write-back callbacks.

### Safe serial integration checks

If an available B1 master is detected, the script opens it automatically and:

- validates the JSON handshake and firmware/protocol metadata;
- reads the droid inventory and confirms the master is present;
- reads targeted animation parameters and calibration;
- checks the 18-entry animation-duration catalog;
- sends invalid animation, configuration, and calibration commands and requires
  an `evt:"err"` response;
- rereads configuration/calibration to prove rejected commands changed nothing;
- observes the inventory briefly and fails if it changes unexpectedly or a
  `mesh inbox full` event appears.

Opening serial can reset some ESP32 USB boards. The console must not already own
the COM port; an unavailable/busy port is reported as `SKIP`, not as a failure.

## Options

```powershell
# Offline-only run
.\tools\self-test.ps1 -SkipSerial

# Serial-only run against an explicitly named port
.\tools\self-test.ps1 -SkipBuild -ComPort COM3 -RequireHardware

# Require automatic hardware discovery to succeed
.\tools\self-test.ps1 -RequireHardware

# Extend the short mesh observation
.\tools\self-test.ps1 -ObserveSeconds 15
```

Each run writes a JSON report under the current user's temporary directory and
prints its exact path. The process exits with code `1` if any required test
fails, making it usable from CI or another automation.

## Deliberate exclusions

Software alone cannot confirm physical direction, mechanical clearance, servo
heating, power quality, or visible movement. Flash, OTA, rollback, intentional
power loss, valid servo commands, and multi-hop range tests therefore remain
manual/explicit tests on a spare bench setup. They must never be added to the
default autonomous mode.
