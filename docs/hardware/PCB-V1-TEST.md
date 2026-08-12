# B1 Chat Servo Hub — reduced V1 test PCB

Status: agreed prototype scope, not yet a manufacturing schematic or routed
PCB.

The larger set of deferred ideas is preserved in
[PCB-CONCEPT.md](PCB-CONCEPT.md). The existing
[concept render](b1-chat-servo-hub-concept-v1.png) illustrates the general
form factor but includes several components deliberately omitted from this
reduced V1.

## Purpose

Build the smallest practical carrier-board prototype needed to validate the
mechanical fit, raw servo-power distribution, six servo connectors, ESP32
power conversion, USB coexistence and one local WS2812 output.

## Mechanical requirements

- Target PCB size: approximately 60 x 45 mm.
- Socketed and replaceable standard 30-pin DOIT ESP32 DevKit V1.
- DevKit dimensions: 51.5 x 28.5 mm.
- Header pitch: 2.54 mm.
- Header-row spacing: 25.4 mm.
- The DevKit Micro-USB connector must remain accessible.
- The ESP32 antenna must extend beyond the carrier-board edge, with no copper
  or components under its keepout area.
- Four mounting holes.
- One PCB design for both master and slave firmware roles.

## Six servo outputs

Six male 3-pin, 2.54 mm headers, all using the same pin order:
`GND | V_SERVO | SIGNAL`.

| Output | Marking | GPIO |
| --- | --- | ---: |
| Servo 1 | PAN | 25 |
| Servo 2 | TILT | 26 |
| Servo 3 | AUX1 | 32 |
| Servo 4 | AUX2 | 33 |
| Servo 5 | AUX3 | 27 |
| Servo 6 | AUX4 | 14 |

V1 deliberately has no series resistor and no pull-down resistor on the six
servo signal lines. The current firmware only drives PAN and TILT; AUX1–AUX4
require future firmware work.

## Raw battery and servo power

- Two-position terminal block for the battery or external supply.
- Intended initial battery source: 2S pack.
- Electrical input range of the board: 4.8 to 12 V maximum.
- `V_SERVO` is connected directly to the input rail.
- Servo output voltage is therefore always identical to input voltage.
- The user must select servos compatible with the connected supply voltage;
  a 2S LiPo reaches 8.4 V when fully charged.
- Provisional shared-rail target: 10 A continuous and 15 A transient.
- Main 25 V bulk-capacitor footprint for approximately 1000 to 2200 uF.
- Final current capability must be validated against the selected terminal,
  copper weight, copper geometry, vias, temperature rise and assembly.

The following protections are deliberately omitted from the V1 test PCB:

- no replaceable or cable fuse included in the board design;
- no reverse-polarity protection;
- no hardware servo-power cutoff MOSFET;
- no `SERVO_POWER_ENABLE` GPIO reservation;
- no servo-power indicator LED;
- no other servo safety or diagnostic function.

This makes V1 a supervised bench prototype, not a user-ready power product.
Power must be disconnected before wiring changes, polarity must be checked
manually, and the supply should provide its own appropriate current limiting
during initial tests.

## ESP32 5 V supply

- Separate buck-boost regulator producing 5 V for the DevKit VIN pin.
- Input operation across the full 4.8 to 12 V board range.
- Target output capacity: 1.5 to 2 A.
- Isolation between the regulator output and USB power to prevent backfeeding.
- Local filtering and decoupling near the DevKit.
- USB power must never feed the raw servo rail.

The exact buck-boost circuit or module and the USB-isolation implementation
remain to be selected before schematic capture.

## WS2812 output

- One 3-pin connector: `GND | +5V_LED | DATA`.
- Data output: GPIO23.
- Approximately 330 to 470 ohm series resistor on DATA.
- Separate protection or solder-jumper isolation on `+5V_LED`.
- The onboard regulated 5 V rail may power a small local WS2812 group.
- A longer strip may use an external regulated 5 V supply with common ground;
  the onboard `+5V_LED` connection must then be isolated to prevent
  backfeeding.
- Maximum onboard-powered LED count will be determined after the buck-boost
  regulator is selected and its thermal performance is tested.
- No 3.3-to-5 V data-level shifter is included in reduced V1. Reserve that
  improvement for the future-feature revision if direct 3.3 V signaling is
  unreliable with the selected WS2812 devices and wiring length.

## Deliberately absent from V1

- No general-purpose expansion connector.
- No I2C or UART expansion header.
- No voltage, current or battery measurement.
- No console diagnostics for board power.
- No integrated battery charger or cell balancer.
- No hardware low-voltage cutoff.
- No additional user-facing safety features beyond careful PCB labeling.
- No audio, display or SD-card hardware.

## Required labeling

- `PAN`, `TILT`, `AUX1`, `AUX2`, `AUX3`, `AUX4`.
- `GND`, `V+`, and `SIG` at every servo output.
- `BAT / SERVO IN 4.8–12 V MAX` at the terminal block.
- `SERVO OUTPUT VOLTAGE = INPUT VOLTAGE`.
- Battery `+` and `-` polarity clearly visible from the wiring side.
- `WS2812: GND | +5V | DATA`.
- `V1 TEST — SUPERVISED USE`.

## Open decisions before schematic capture

- Exact terminal-block family, pitch and verified current rating.
- Buck-boost topology/module and its efficiency across 4.8 to 12 V.
- USB-isolation implementation.
- PCB copper weight and high-current pour geometry.
- Bulk-capacitor diameter, height and exact value.
- WS2812 connector family and maximum onboard-powered LED count.
- Final mounting-hole diameter and clearances.
