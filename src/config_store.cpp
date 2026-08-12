#include "config_store.h"
#include "config.h"

ConfigStore Config;

namespace {
const char* NVS_NS = "b1";

struct StoredAnimParams {
    uint8_t freq;
    uint8_t amp;
    uint8_t speed;
};

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

void ConfigStore::animParams(uint8_t& freq, uint8_t& amp, uint8_t& speed) {
    animParamsFor(_localId, freq, amp, speed);
}

void ConfigStore::setAnimParams(uint8_t freq, uint8_t amp, uint8_t speed) {
    setAnimParamsFor(_localId, freq, amp, speed);
}

void ConfigStore::setAnimParamsImmediate(uint8_t freq, uint8_t amp, uint8_t speed) {
    writeAnimParams(_localId, freq, amp, speed);
}

void ConfigStore::animKey(uint16_t id, char out[8]) {
    snprintf(out, 8, "p%04X", id);
}

void ConfigStore::animParamsFor(uint16_t id, uint8_t& freq, uint8_t& amp, uint8_t& speed) {
    for (uint8_t i = 0; i < PENDING_ANIMS_MAX; i++) {
        if (_pendAnims[i].used && _pendAnims[i].id == id) {
            freq = _pendAnims[i].freq;
            amp = _pendAnims[i].amp;
            speed = _pendAnims[i].speed;
            return;
        }
    }

    char key[8];
    animKey(id, key);
    StoredAnimParams stored{50, 60, 50};
    if (_p.getBytesLength(key) == sizeof(stored)) {
        _p.getBytes(key, &stored, sizeof(stored));
    } else if (id != 0 && id == _localId) {
        // Backward-compatible read of firmware <= 1.9.0's global keys.
        stored.freq = _p.getUChar("af", 50);
        stored.amp = _p.getUChar("aa", 60);
        stored.speed = _p.getUChar("as", 50);
    }
    // Sanitizes values written by older firmware/backups before strict input
    // validation existed.
    freq = stored.freq > 100 ? 100 : stored.freq;
    amp = stored.amp > 100 ? 100 : stored.amp;
    speed = stored.speed > 100 ? 100 : stored.speed;
}

void ConfigStore::setAnimParamsFor(uint16_t id, uint8_t freq, uint8_t amp, uint8_t speed) {
    if (id == 0) return;
    int freeSlot = -1;
    for (uint8_t i = 0; i < PENDING_ANIMS_MAX; i++) {
        if (_pendAnims[i].used && _pendAnims[i].id == id) {
            _pendAnims[i].freq = freq;
            _pendAnims[i].amp = amp;
            _pendAnims[i].speed = speed;
            _dirty = true;
            return;
        }
        if (!_pendAnims[i].used && freeSlot < 0) freeSlot = i;
    }
    if (freeSlot >= 0) {
        _pendAnims[freeSlot] = {true, id, freq, amp, speed};
        _dirty = true;
        return;
    }
    // A full overlay must not lose a setting; persist the extra entry now.
    writeAnimParams(id, freq, amp, speed);
}

void ConfigStore::writeAnimParams(uint16_t id, uint8_t freq, uint8_t amp, uint8_t speed) {
    if (id == 0) return;
    char key[8];
    animKey(id, key);
    const StoredAnimParams stored{freq, amp, speed};
    _p.putBytes(key, &stored, sizeof(stored));

    // Keep the old keys synchronized for a possible anti-brick rollback to a
    // <=1.9.0 image, which only knows the global format.
    if (id == _localId) {
        _p.putUChar("af", freq);
        _p.putUChar("aa", amp);
        _p.putUChar("as", speed);
    }
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

bool ConfigStore::autoAnimEnabled(bool defaultValue) {
    return _p.getBool("autoOn", defaultValue);
}

void ConfigStore::setAutoAnimEnabledImmediate(bool enabled) {
    _p.putBool("autoOn", enabled);
}

void ConfigStore::refreshDirty() {
    _dirty = false;
    for (uint8_t i = 0; !_dirty && i < PENDING_ANIMS_MAX; i++) {
        if (_pendAnims[i].used) _dirty = true;
    }
    for (uint8_t i = 0; !_dirty && i < PENDING_NAMES_MAX; i++) {
        if (_pendNames[i].used) _dirty = true;
    }
}

void ConfigStore::commitPending() {
    for (uint8_t i = 0; i < PENDING_ANIMS_MAX; i++) {
        if (!_pendAnims[i].used) continue;
        writeAnimParams(_pendAnims[i].id, _pendAnims[i].freq,
                        _pendAnims[i].amp, _pendAnims[i].speed);
        _pendAnims[i] = {false, 0, 0, 0, 0};
    }
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
