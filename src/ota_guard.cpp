#include "ota_guard.h"
#include "config.h"

#include <Preferences.h>
#include "esp_ota_ops.h"

OtaGuard Guard;

namespace {
const char* NVS_NS = "ota";
}

bool OtaGuard::earlyCheck() {
    Preferences p;
    p.begin(NVS_NS, false);
    const bool pending = p.getBool("pending", false);
    if (!pending) {
        p.end();
        return false;
    }

    const uint8_t attempts = p.getUChar("attempts", 0) + 1;
    if (attempts > OTA_MAX_BOOT_ATTEMPTS) {
        // Too many failed boots since the OTA: switch back to the other
        // partition (esp_ota_get_next_update_partition necessarily
        // alternates app0/app1).
        p.remove("pending");
        p.remove("attempts");
        p.end();
        const esp_partition_t* prev = esp_ota_get_next_update_partition(nullptr);
        if (prev) {
            esp_ota_set_boot_partition(prev);
        }
        esp_restart();
        return true; // never reached (esp_restart doesn't return)
    }

    p.putUChar("attempts", attempts);
    p.end();
    _pendingActive = true;
    _bootMs = millis();
    return false;
}

void OtaGuard::confirmIfPending(uint32_t nowMs) {
    if (!_pendingActive) return;
    if (nowMs - _bootMs < OTA_VERIFY_UPTIME_MS) return;

    Preferences p;
    p.begin(NVS_NS, false);
    p.remove("pending");
    p.remove("attempts");
    p.end();
    _pendingActive = false;
}

void OtaGuard::armPendingReboot() {
    Preferences p;
    p.begin(NVS_NS, false);
    p.putBool("pending", true);
    p.putUChar("attempts", 0);
    p.end();
}
