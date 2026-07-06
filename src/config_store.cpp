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
    return _p.getUChar("vol", AUDIO_VOLUME_DEFAULT);
}

void ConfigStore::setVolume(uint8_t v) {
    if (v > 30) v = 30;
    _p.putUChar("vol", v);
}

void ConfigStore::animParams(uint8_t& freq, uint8_t& amp, uint8_t& speed) {
    freq  = _p.getUChar("af", 50);
    amp   = _p.getUChar("aa", 60);
    speed = _p.getUChar("as", 50);
}

void ConfigStore::setAnimParams(uint8_t freq, uint8_t amp, uint8_t speed) {
    _p.putUChar("af", freq);
    _p.putUChar("aa", amp);
    _p.putUChar("as", speed);
}

void ConfigStore::nameKey(uint16_t id, char out[8]) {
    // Clé NVS courte (< 15 car.) : "n" + hex de l'id, ex. "n3A7C".
    snprintf(out, 8, "n%04X", id);
}

String ConfigStore::getName(uint16_t id) {
    char key[8];
    nameKey(id, key);
    // Teste l'existence pour éviter le log d'erreur NVS « NOT_FOUND ».
    if (!_p.isKey(key)) return String("");
    return _p.getString(key, "");
}

void ConfigStore::setName(uint16_t id, const String& name) {
    char key[8];
    nameKey(id, key);
    _p.putString(key, name);
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
