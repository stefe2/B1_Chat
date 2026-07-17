#include "config_store.h"
#include "config.h"

ConfigStore Config;

namespace {
const char* NVS_NS = "b1";
}

void ConfigStore::begin() {
    // false = read/write.
    _p.begin(NVS_NS, false);
}

void ConfigStore::animParams(uint8_t& freq, uint8_t& amp, uint8_t& speed) {
    if (_pendAnimSet) {
        freq = _pendFreq; amp = _pendAmp; speed = _pendSpeed;
        return;
    }
    freq  = _p.getUChar("af", 50);
    amp   = _p.getUChar("aa", 60);
    speed = _p.getUChar("as", 50);
}

void ConfigStore::setAnimParams(uint8_t freq, uint8_t amp, uint8_t speed) {
    _pendFreq = freq; _pendAmp = amp; _pendSpeed = speed;
    _pendAnimSet = true;
    _dirty = true;
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
    _dirty = _pendAnimSet;
    for (uint8_t i = 0; !_dirty && i < PENDING_NAMES_MAX; i++) {
        if (_pendNames[i].used) _dirty = true;
    }
}

void ConfigStore::commitPending() {
    if (_pendAnimSet) {
        _p.putUChar("af", _pendFreq);
        _p.putUChar("aa", _pendAmp);
        _p.putUChar("as", _pendSpeed);
    }
    for (uint8_t i = 0; i < PENDING_NAMES_MAX; i++) {
        if (!_pendNames[i].used) continue;
        char key[8];
        nameKey(_pendNames[i].id, key);
        _p.putString(key, _pendNames[i].name);
        _pendNames[i] = {false, 0, String()};
    }
    _pendAnimSet = false;
    refreshDirty();
}

void ConfigStore::calibKey(uint16_t id, char out[8]) {
    // Short NVS key: "c" + hex of the id, e.g. "c3A7C".
    snprintf(out, 8, "c%04X", id);
}

ServoCalib ConfigStore::getCalib(uint16_t id) {
    ServoCalib c{SERVO_PAN_MIN, SERVO_PAN_CENTER, SERVO_PAN_MAX,
                 SERVO_TILT_MIN, SERVO_TILT_CENTER, SERVO_TILT_MAX};
    char key[8];
    calibKey(id, key);
    if (_p.isKey(key)) _p.getBytes(key, &c, sizeof(c));
    return c;
}

void ConfigStore::setCalib(uint16_t id, const ServoCalib& c) {
    char key[8];
    calibKey(id, key);
    _p.putBytes(key, &c, sizeof(c));
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
