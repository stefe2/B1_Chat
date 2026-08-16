#include "config_store.h"
#include "config.h"

ConfigStore Config;

namespace {
const char* NVS_NS = "b1";

// Keep the original six-byte calibration record intact so a rollback to older
// firmware still sees valid limits. Axis direction lives in a separate key.
struct StoredServoLimits {
    uint8_t panMin, panCenter, panMax;
    uint8_t tiltMin, tiltCenter, tiltMax;
};
}

void ConfigStore::begin() {
    // false = read/write.
    _p.begin(NVS_NS, false);
}

void ConfigStore::nameKey(uint16_t id, char out[8]) {
    // Short NVS key (< 15 chars): "n" + hex of the id, e.g. "n3A7C".
    snprintf(out, 8, "n%04X", id);
}

String ConfigStore::getName(uint16_t id) {
    // The RAM overlay (uncommitted change) takes priority over NVS.
    for (uint8_t i = 0; i < PENDING_NAMES_MAX; i++) {
        if (_pendNames[i].used && _pendNames[i].id == id) return _pendNames[i].name;
    }
    char key[8];
    nameKey(id, key);
    // Checks existence to avoid the NVS "NOT_FOUND" error log.
    if (!_p.isKey(key)) return String("");
    return _p.getString(key, "");
}

void ConfigStore::setName(uint16_t id, const String& name) {
    // Writes the RAM overlay (existing slot, otherwise a free slot).
    int free = -1;
    for (uint8_t i = 0; i < PENDING_NAMES_MAX; i++) {
        if (_pendNames[i].used && _pendNames[i].id == id) {
            _pendNames[i].name = name;
            _dirty = true;
            return;
        }
        if (!_pendNames[i].used && free < 0) free = i;
    }
    if (free >= 0) {
        _pendNames[free] = {true, id, name};
        _dirty = true;
        return;
    }
    // Overlay full (unlikely: 32 slots): falls back to a direct write so
    // nothing is lost.
    char key[8];
    nameKey(id, key);
    _p.putString(key, name);
}

void ConfigStore::setNameImmediate(uint16_t id, const String& name) {
    char key[8];
    nameKey(id, key);
    _p.putString(key, name);
}

bool ConfigStore::servosEnabled(bool defaultValue) {
    return _p.getBool("srvOn", defaultValue);
}

void ConfigStore::setServosEnabledImmediate(bool enabled) {
    _p.putBool("srvOn", enabled);
}

void ConfigStore::refreshDirty() {
    _dirty = false;
    for (uint8_t i = 0; !_dirty && i < PENDING_NAMES_MAX; i++) {
        if (_pendNames[i].used) _dirty = true;
    }
}

void ConfigStore::commitPending() {
    for (uint8_t i = 0; i < PENDING_NAMES_MAX; i++) {
        if (!_pendNames[i].used) continue;
        char key[8];
        nameKey(_pendNames[i].id, key);
        _p.putString(key, _pendNames[i].name);
        _pendNames[i] = {false, 0, String()};
    }
    refreshDirty();
}

void ConfigStore::calibKey(uint16_t id, char out[8]) {
    // Short NVS key: "c" + hex of the id, e.g. "c3A7C".
    snprintf(out, 8, "c%04X", id);
}

void ConfigStore::reverseKey(uint16_t id, char out[8]) {
    snprintf(out, 8, "r%04X", id);
}

ServoCalib ConfigStore::getCalib(uint16_t id) {
    ServoCalib c{SERVO_PAN_MIN, SERVO_PAN_CENTER, SERVO_PAN_MAX,
                 SERVO_TILT_MIN, SERVO_TILT_CENTER, SERVO_TILT_MAX, 0, 0};
    char key[8];
    calibKey(id, key);
    StoredServoLimits limits{};
    if (_p.getBytesLength(key) == sizeof(limits) &&
        _p.getBytes(key, &limits, sizeof(limits)) == sizeof(limits)) {
        c.panMin = limits.panMin;
        c.panCenter = limits.panCenter;
        c.panMax = limits.panMax;
        c.tiltMin = limits.tiltMin;
        c.tiltCenter = limits.tiltCenter;
        c.tiltMax = limits.tiltMax;
    }
    reverseKey(id, key);
    const uint8_t directions = _p.getUChar(key, 0);
    c.panReversed = (directions & 0x01) != 0;
    c.tiltReversed = (directions & 0x02) != 0;
    return c;
}

void ConfigStore::setCalib(uint16_t id, const ServoCalib& c) {
    char key[8];
    calibKey(id, key);
    const StoredServoLimits limits{c.panMin, c.panCenter, c.panMax,
                                   c.tiltMin, c.tiltCenter, c.tiltMax};
    _p.putBytes(key, &limits, sizeof(limits));
    reverseKey(id, key);
    const uint8_t directions = (c.panReversed ? 0x01 : 0) |
                               (c.tiltReversed ? 0x02 : 0);
    _p.putUChar(key, directions);
}

void ConfigStore::adoptKey(uint16_t id, char out[8]) {
    // Short NVS key: "a" + hex of the id, e.g. "a3A7C".
    snprintf(out, 8, "a%04X", id);
}

bool ConfigStore::isAdopted(uint16_t id) {
    char key[8];
    adoptKey(id, key);
    if (!_p.isKey(key)) return false;
    return _p.getBool(key, false);
}

void ConfigStore::setAdopted(uint16_t id, bool adopted) {
    char key[8];
    adoptKey(id, key);
    if (adopted) _p.putBool(key, true);
    else _p.remove(key);
}
