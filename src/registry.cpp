#include "registry.h"
#include "config_store.h"

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
        _e[_count].adopted = Config.isAdopted(id);
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

void Registry::setAdopted(uint16_t id, bool v) {
    for (uint8_t i = 0; i < _count; i++) {
        if (_e[i].id == id) { _e[i].adopted = v; return; }
    }
}

bool Registry::forget(uint16_t id) {
    for (uint8_t i = 0; i < _count; i++) {
        if (_e[i].id == id) {
            for (uint8_t j = i; j < _count - 1; j++) _e[j] = _e[j + 1];
            _count--;
            return true;
        }
    }
    return false;
}
