#include "config_store.h"
#include "config.h"

ConfigStore Config;

namespace {
const char* NVS_NS = "b1";
}

void ConfigStore::begin() {
    // false = lecture/écriture.
    _p.begin(NVS_NS, false);
}

uint8_t ConfigStore::volume() {
    if (_pendVolSet) return _pendVol;
    return _p.getUChar("vol", AUDIO_VOLUME_DEFAULT);
}

void ConfigStore::setVolume(uint8_t v) {
    if (v > 30) v = 30;
    _pendVol = v;
    _pendVolSet = true;
    _dirty = true;
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
    // Clé NVS courte (< 15 car.) : "n" + hex de l'id, ex. "n3A7C".
    snprintf(out, 8, "n%04X", id);
}

String ConfigStore::getName(uint16_t id) {
    // La surcouche RAM (modif non engagée) a priorité sur la NVS.
    for (uint8_t i = 0; i < PENDING_NAMES_MAX; i++) {
        if (_pendNames[i].used && _pendNames[i].id == id) return _pendNames[i].name;
    }
    char key[8];
    nameKey(id, key);
    // Teste l'existence pour éviter le log d'erreur NVS « NOT_FOUND ».
    if (!_p.isKey(key)) return String("");
    return _p.getString(key, "");
}

void ConfigStore::setName(uint16_t id, const String& name) {
    // Écrit la surcouche RAM (slot existant, sinon slot libre).
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
    // Surcouche pleine (improbable : 32 slots) : dégrade en écriture directe
    // pour ne rien perdre.
    char key[8];
    nameKey(id, key);
    _p.putString(key, name);
}

void ConfigStore::refreshDirty() {
    _dirty = _pendVolSet || _pendAnimSet;
    for (uint8_t i = 0; !_dirty && i < PENDING_NAMES_MAX; i++) {
        if (_pendNames[i].used) _dirty = true;
    }
}

void ConfigStore::commitPending() {
    if (_pendVolSet) _p.putUChar("vol", _pendVol);
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
    _pendVolSet = false;
    _pendAnimSet = false;
    refreshDirty();
}

void ConfigStore::revertPending() {
    _pendVolSet = false;
    _pendAnimSet = false;
    for (uint8_t i = 0; i < PENDING_NAMES_MAX; i++) {
        _pendNames[i] = {false, 0, String()};
    }
    refreshDirty();
}

void ConfigStore::calibKey(uint16_t id, char out[8]) {
    // Clé NVS courte : "c" + hex de l'id, ex. "c3A7C".
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
    // Clé NVS courte : "a" + hex de l'id, ex. "a3A7C".
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
