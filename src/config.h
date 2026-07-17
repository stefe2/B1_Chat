#pragma once

// ============================================================================
//  B1 Battle Droid — Hardware configuration and global constants
//  See project.md (section 3 Wiring) for wiring details.
// ============================================================================

#include <Arduino.h>

// ---------------------------------------------------------------------------
//  FIRMWARE VERSION — source of truth for GitHub releases.
//  Announced to the console in the hello response ("fw" field); the console
//  compares it to the latest stefe2/B1_Chat release to offer an update. Bump
//  on every release (tools/release.ps1 relies on it).
// ---------------------------------------------------------------------------
#define FW_VERSION "1.7.0"

// Console<->master serial protocol version (bumped when a change isn't
// backward-compatible; adding fields/commands doesn't require it).
// 4: seq* commands/events removed (the 8 NVS sequence slots + onboard player
//    were retired — sequences are entirely console-driven now).
#define FW_PROTO 4

// ---------------------------------------------------------------------------
//  DROID ROLE  —  SET HERE BEFORE FLASHING (pio run -e b1 -t upload)
//  1 = MASTER (only one per network: coordination + sound + web console)
//  0 = SLAVE (default value for every other droid)
//  Overridable via build_flags (-D IS_MASTER=0|1) — used by the
//  b1_master/b1_slave environments (platformio.ini) for CI releases,
//  which build both roles without touching this file.
// ---------------------------------------------------------------------------
#ifndef IS_MASTER
#define IS_MASTER 1
#endif

// Temporary pause of servos/animations on the MASTER (protects the servos
// while tuning the web page). Set to 0 to re-enable.
#define MASTER_ANIM_PAUSED 1

// ---------------------------------------------------------------------------
//  Network & group key (defined by the build flags in platformio.ini)
// ---------------------------------------------------------------------------
#ifndef MESH_TTL
#define MESH_TTL 4              // max number of hops for mesh relay
#endif

#ifndef GROUP_KEY
#define GROUP_KEY "changeme"    // default network key (compiled in)
#endif

// ---------------------------------------------------------------------------
//  Life LED (onboard) — program-running indicator
// ---------------------------------------------------------------------------
static const uint8_t PIN_LED_ONBOARD = 2;   // onboard blue LED on DOIT DevKit V1
static const uint16_t LED_BLINK_MS   = 500; // blink period

// ---------------------------------------------------------------------------
//  Servos (all droids) — PWM signal
// ---------------------------------------------------------------------------
static const uint8_t PIN_SERVO_PAN  = 25;   // GPIO25 -> pan servo
static const uint8_t PIN_SERVO_TILT = 26;   // GPIO26 -> tilt servo

// Mechanical limits (degrees). Adjust to fit the head assembly.
static const uint8_t SERVO_PAN_MIN   = 20;
static const uint8_t SERVO_PAN_MAX   = 160;
static const uint8_t SERVO_PAN_CENTER = 90;

static const uint8_t SERVO_TILT_MIN   = 60;
static const uint8_t SERVO_TILT_MAX   = 120;
static const uint8_t SERVO_TILT_CENTER = 90;

// Pulse widths (µs) for ESP32Servo calibration.
static const uint16_t SERVO_MIN_US = 500;
static const uint16_t SERVO_MAX_US = 2400;

// Motion engine update frequency.
static const uint16_t SERVO_UPDATE_HZ = 50;

// ---------------------------------------------------------------------------
//  ESP-NOW network
// ---------------------------------------------------------------------------
static const uint8_t MESH_WIFI_CHANNEL = 1;   // radio channel shared by the whole group
static const uint8_t MESH_DEDUP_CACHE  = 32;  // anti-duplicate cache size

// Mesh topology (direct radio neighborhood, independent of relaying).
static const uint8_t  MAX_NEIGHBORS      = 12;   // max direct neighbors per report
static const uint32_t NEIGHBOR_REPORT_MS = 3000; // neighborhood broadcast period
static const uint32_t NEIGHBOR_STALE_MS  = 9000; // radio link staleness (~3x the period)

// ---------------------------------------------------------------------------
//  Animation timing (ms)
// ---------------------------------------------------------------------------
static const uint32_t IDLE_ANIM_MIN_MS = 3000;   // min delay before a local anim
static const uint32_t IDLE_ANIM_MAX_MS = 9000;   // max delay before a local anim
static const uint32_t HEARTBEAT_MS     = 2000;   // heartbeat emission period

// ---------------------------------------------------------------------------
//  Firmware OTA (slaves, relayed by the mesh) — see CLAUDE.md
// ---------------------------------------------------------------------------
static const uint8_t  OTA_MESH_TTL          = 2;        // reduced TTL dedicated to OTA packets
static const uint32_t OTA_MAX_IMAGE_SIZE    = 1200000UL; // margin under an app partition's 1.25 MB
static const uint32_t OTA_ACK_TIMEOUT_MS    = 400;      // delay before retransmitting a chunk/start/end
static const uint8_t  OTA_MAX_RETRIES       = 5;        // attempts before session failure (master)
static const uint32_t OTA_SESSION_TIMEOUT_MS = 60000;   // max slave-side inactivity before auto-abort
                                                         // (> OTA_SERIAL_IDLE_TIMEOUT_MS: the master must
                                                         // always give up BEFORE the slave, so it can warn
                                                         // it via MSG_OTA_ABORT — a resolved serial hiccup
                                                         // must not find a slave that already left)
static const uint32_t OTA_SERIAL_IDLE_TIMEOUT_MS = 45000; // max master-side inactivity (console gone)
static const uint8_t  OTA_MAX_BOOT_ATTEMPTS = 3;        // boot attempts before rollback (OtaGuard)
static const uint32_t OTA_VERIFY_UPTIME_MS  = 20000;    // uptime required to confirm a new firmware
static const uint32_t OTA_REBOOT_WAIT_MS    = 90000;    // max delay to confirm a post-OTA heartbeat
static const uint32_t OTA_REBOOT_GRACE_MS   = 5000;     // window during which a sign of life at an
                                                         // UNCHANGED version is ignored: the slave only
                                                         // reboots ~250 ms after its END ack, one last
                                                         // heartbeat from the old image can still arrive
                                                         // (false "rolledBack" observed 940 ms after
                                                         // otaDone). A real rollback takes >= 10-30 s
                                                         // (failed boots + partition switch).
