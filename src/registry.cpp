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
        _count++;
        return true;
    }
    return false;  // table pleine
}
