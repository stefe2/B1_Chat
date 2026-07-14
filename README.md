# B1 Chat — Multi-droid B1 Battle Droid control

ESP32 firmware (PlatformIO / Arduino) to animate several **B1 battle droid**
heads over a **multi-hop ESP-NOW mesh** network, with **smooth, organic**
movements and **sound played by a master droid**.

> Detailed plan and progress tracking: [`project.md`](project.md).

## Features

- **1 ESP32 per droid** — single firmware, master/slave role via build flag.
- **Multi-hop ESP-NOW mesh network** (TTL + deduplication relay): droids out
  of direct range are reached via relay.
- **Pan/tilt head** — 2 servos per droid, 50 Hz interpolation engine with
  *easing* and idle noise for a lifelike feel.
- **Random animations** coordinated by the master (synced or offset).
- **Audio** played by the master via a **DFPlayer Mini** (DAC output → amp).
- **Auto identity** — each droid's ID is derived from its MAC: a new droid
  is flashed as-is, **zero configuration**.
- **Network security** — **HMAC-SHA256** group key: two independent B1
  fleets ignore each other and tampered messages are rejected.
- **USB web console** — standalone page (Web Serial API) plugged into the
  master to list droids, trigger animations, set the volume, name droids,
  and change the group key.

## Hardware

- Board: **DOIT ESP32 DevKit V1** (1 per droid)
- 2 standard PWM servos (SG90 / MG996R) per droid
- Master: **DFPlayer Mini** + SD card + **external amp** (e.g. PAM8403) + speaker
- **External 5V** power supply for the servos (common ground with the ESP32)

### Wiring (summary)

| Function | ESP32 GPIO |
|----------|-----------|
| Pan servo | GPIO25 |
| Tilt servo | GPIO26 |
| DFPlayer TX2 → RX (via 1 kΩ) | GPIO17 |
| DFPlayer RX2 ← TX | GPIO16 |
| DFPlayer BUSY (optional) | GPIO4 |

Full details (audio DAC → amp, pins to avoid) in [`project.md`](project.md).

## Build & flash (PlatformIO)

The role is set in [`src/config.h`](src/config.h):

```cpp
#define IS_MASTER 1   // 1 = master (only one), 0 = slave
```

Then flash every board with the same environment:

```bash
pio run -e b1 -t upload
```

- **Master**: set `IS_MASTER 1`, flash **one** board.
- **Slaves**: set `IS_MASTER 0`, flash all the others.

Default group key set in `platformio.ini` (`-D GROUP_KEY`); changeable
afterward via the web console.

## Project structure

```
platformio.ini    b1 environment, dependencies, build flags
project.md        detailed plan + progress tracking
src/
  config.h        role (IS_MASTER), pins, angle limits, mesh/audio params
  main.cpp         entry point
web/
  dashboard.html  Web Serial supervision console (coming soon)
```

## Project status

In development, in stages. See the *Implementation steps* section of
[`project.md`](project.md) for up-to-date progress.
