#include "registry.h"

Registry Droids;

bool Registry::seen(uint16_t id, int rssi, uint32_t now) {
    for (uint8_t i = 0; i < _count; i++) {
        if (_e[i].id == id) {
            _e[i].rssi = (int16_t)rssi;
            _e[i].lastSeen = now;
            return false;
        }
    }
    if (_count < MAX) {
        _e[_count].id = id;
        _e[_count].rssi = (int16_t)rssi;
        _e[_count].lastSeen = now;
        _e[_count].servos = true;
        _e[_count].autoAnim = true;
        _count++;
        return true;
    }
    return false;  // table pleine
}

void Registry::setServos(uint16_t id, bool on) {
    for (uint8_t i = 0; i < _count; i++) {
        if (_e[i].id == id) { _e[i].servos = on; return; }
    }
}

void Registry::setAutoAnim(uint16_t id, bool on) {
    for (uint8_t i = 0; i < _count; i++) {
        if (_e[i].id == id) { _e[i].autoAnim = on; return; }
    }
}
