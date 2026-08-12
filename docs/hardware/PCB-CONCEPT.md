# B1 Chat Servo Hub — PCB concept

Status: future-feature reserve, not the reduced V1 test-board specification.

The active reduced prototype scope is documented in
[PCB-V1-TEST.md](PCB-V1-TEST.md). Keep this document as the backlog for a
later, more integrated and user-facing PCB revision.

Concept render: [b1-chat-servo-hub-concept-v1.png](b1-chat-servo-hub-concept-v1.png)

## Purpose

One compact carrier board shared by master and slave droids. It makes the
ESP32 replaceable, distributes raw servo power, powers the ESP32 through a
separate regulated rail, and reserves interfaces for future mechanisms and
sensors.

## Mechanical concept

- Target prototype size: approximately 60 x 45 mm.
- Socketed standard 30-pin DOIT ESP32 DevKit V1.
- DevKit dimensions: 51.5 x 28.5 mm.
- Header pitch: 2.54 mm; row spacing: 25.4 mm.
- ESP32 antenna extends over a board edge with a copper/component keepout.
- Micro-USB connector remains accessible at the opposite edge.
- Four plated mounting holes.
- Six servo headers placed along board edges with one consistent orientation.
- Two-layer prototype with 2 oz copper proposed for the high-current rails.

## Power architecture

- `V_SERVO_IN`: 4.8 to 12 V maximum.
- Servo output voltage is the raw input voltage; the board does not regulate it.
- The user must select an input voltage compatible with the installed servos.
- A 2S LiPo is supported only when the selected servos accept 8.4 V at full
  charge.
- Raw input is protected against reverse polarity.
- Replaceable or external fuse in the servo branch.
- Hardware servo-power cutoff using a high-side MOSFET, default-off where
  practical.
- Provisional shared-rail design goal: 10 A continuous and 15 A transient,
  subject to connector, copper and thermal validation.
- Large 25 V bulk-capacitor footprint for approximately 1000 to 2200 uF.
- Separate 5 V buck-boost supply for the ESP32:
  - input range must cover the full 4.8 to 12 V board range with margin;
  - 5 V regulated output;
  - target output capacity of 1.5 to 2 A;
  - USB/BEC isolation to prevent backfeeding;
  - local filtering and decoupling near the DevKit.
- Common ground with high-current servo returns routed away from the ESP32
  logic return path.
- No battery charger or LiPo balancing circuit on the first PCB.

## Servo outputs

All servo connectors are male 3-pin 2.54 mm headers using the same order:
`GND | V_SERVO | SIGNAL`. Each signal gets a 100–220 ohm series resistor and
a pull-down resistor around 10 kohm.

| Output | Initial role | GPIO |
| --- | --- | ---: |
| Servo 1 | PAN | 25 |
| Servo 2 | TILT | 26 |
| Servo 3 | AUX1 | 32 |
| Servo 4 | AUX2 | 33 |
| Servo 5 | AUX3 | 27 |
| Servo 6 | AUX4 | 14 |

GPIO13 is reserved for `SERVO_POWER_ENABLE`. The current firmware only drives
PAN and TILT; AUX1–AUX4 and physical rail switching require future firmware.

## WS2812 output

- One dedicated 3-pin output: `GND | +5V_LED | DATA`.
- Data GPIO: GPIO23.
- 3.3 V to 5 V logic conversion using an AHCT-family buffer.
- Series data resistor near the connector, approximately 330–470 ohm.
- Input pull-down so the LEDs remain quiet during ESP32 startup.
- The LED 5 V rail comes from the regulated logic supply, never from raw
  `V_SERVO_IN`.
- Separate current protection or solder-jumper isolation for the LED 5 V rail.
- The onboard 5 V output is intended for a small local LED group. A longer
  strip must use an external 5 V supply with common ground; the permitted LED
  count will be set after the buck-boost converter is selected.

## Expansion and sensing

- 3.3 V I2C expansion: GPIO21/SDA and GPIO22/SCL, plus 3V3 and GND.
- Auxiliary UART pads: GPIO16/RX and GPIO17/TX.
- Raw input-voltage measurement through a protected divider on GPIO34/ADC1.
- GPIO35/ADC1 reserved for a future analog or current-sense input.
- Future console display and warning for measured servo-supply voltage.
- Clearly accessible test pads for:
  - `V_SERVO_IN` / `V_SERVO`;
  - regulated 5 V;
  - 3.3 V;
  - GND;
  - servo signals;
  - GPIO0 and EN for recovery/debugging.

## User-facing and safety features

- Clear `S`, `+`, and `-` markings at every servo output.
- Large PAN, TILT, AUX1–AUX4 labels.
- Input marking: `SERVO SUPPLY 4.8–12 V MAX`.
- Warning: `OUTPUT VOLTAGE = INPUT VOLTAGE`.
- Writable area for installed servo type and maximum voltage.
- Power indicator and a servo-power indicator located after the cutoff.
- Physical servo-power disconnect for calibration and maintenance.
- Same PCB for master and slave roles; role remains firmware-selected.
- Polarized high-current input connector, exact family still to be selected.

## Explicitly outside the first revision

- Integrated LiPo charging or cell balancing.
- Audio amplifier, speaker driver or SD card.
- Display.
- Soldered-down ESP32 module.
- Automatically identifying a servo's permissible voltage.

## Open engineering decisions

- Exact high-current input connector (for example XT30, JST-VH or a pluggable
  terminal).
- Buck-boost module versus a fully integrated regulator circuit.
- Exact MOSFET, fuse and reverse-polarity implementation.
- Confirmed current and thermal rating after servo and copper tests.
- Maximum onboard-powered WS2812 count.
- Final board outline, mounting-hole diameter and enclosure clearances.
