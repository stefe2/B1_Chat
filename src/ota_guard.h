#pragma once

// ============================================================================
//  OtaGuard — anti-brick safety net for OTA (all roles)
//
//  Before rebooting into a freshly written image, an NVS flag is armed
//  ("pending" + attempt counter). On the next boot, if this flag is
//  present, the counter is incremented; past OTA_MAX_BOOT_ATTEMPTS, it
//  manually switches esp_ota_set_boot_partition() to the OTHER partition
//  (esp_ota_get_next_update_partition necessarily alternates app0/app1) and
//  reboots — a rollback done "by hand" with the standard esp_ota_ops API,
//  without relying on ESP-IDF's bootloader rollback (not simply exposed
//  under the Arduino framework). If the firmware runs without a reset for
//  OTA_VERIFY_UPTIME_MS, the flag is cleared: the image is confirmed good.
//
//  earlyCheck() MUST be the very first line of setup(), before any other
//  code: a crash occurring before this call would never be counted
//  (residual risk accepted, see CLAUDE.md).
// ============================================================================

#include <Arduino.h>

class OtaGuard {
public:
    // Returns true if a rollback was triggered (never actually returns in
    // practice: esp_restart() is called before returning).
    bool earlyCheck();

    // To be called on every loop() iteration.
    void confirmIfPending(uint32_t nowMs);

    // To be called right before a successful Update.end(true), before ESP.restart().
    void armPendingReboot();

private:
    bool _pendingActive = false;
    uint32_t _bootMs = 0;
};

extern OtaGuard Guard;
