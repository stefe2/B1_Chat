#include "registry.h"
#include "config_store.h"

Registry Droids;

namespace {
// RAII : même construction que dans ota_master.cpp/ota_slave.cpp. Ne JAMAIS
// faire d'accès NVS/flash ni d'appel bloquant sous ce verrou.
struct CriticalGuard {
    portMUX_TYPE& mux;
    explicit CriticalGuard(portMUX_TYPE& m) : mux(m) { portENTER_CRITICAL(&mux); }
    ~CriticalGuard() { portEXIT_CRITICAL(&mux); }
};
}  // namespace

bool Registry::seen(uint16_t id, int rssi, uint32_t now) {
    {
        CriticalGuard guard(_mux);
        for (uint8_t i = 0; i < _count; i++) {
            if (_e[i].id == id) {
                _e[i].rssi = (int16_t)rssi;
                _e[i].lastSeen = now;
                return false;
            }
        }
        if (_count >= MAX) return false;  // table pleine
    }

    // Droïde encore inconnu : lecture NVS HORS verrou (accès flash interdit
    // sous portENTER_CRITICAL — leçon du gel OTA au chunk 21), puis
    // re-vérification à l'insertion (un autre message du même droïde a pu
    // l'insérer entre-temps).
    const bool adopted = Config.isAdopted(id);

    CriticalGuard guard(_mux);
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
        _e[_count].adopted = adopted;
        _e[_count].fwMajor = 0;
        _e[_count].fwMinor = 0;
        _e[_count].fwPatch = 0;
        _count++;
        return true;
    }
    return false;  // table pleine
}

void Registry::setServos(uint16_t id, bool on) {
    CriticalGuard guard(_mux);
    for (uint8_t i = 0; i < _count; i++) {
        if (_e[i].id == id) { _e[i].servos = on; return; }
    }
}

void Registry::setAutoAnim(uint16_t id, bool on) {
    CriticalGuard guard(_mux);
    for (uint8_t i = 0; i < _count; i++) {
        if (_e[i].id == id) { _e[i].autoAnim = on; return; }
    }
}

void Registry::setFwVersion(uint16_t id, uint8_t major, uint8_t minor, uint8_t patch) {
    CriticalGuard guard(_mux);
    for (uint8_t i = 0; i < _count; i++) {
        if (_e[i].id == id) { _e[i].fwMajor = major; _e[i].fwMinor = minor; _e[i].fwPatch = patch; return; }
    }
}

void Registry::setAdopted(uint16_t id, bool v) {
    CriticalGuard guard(_mux);
    for (uint8_t i = 0; i < _count; i++) {
        if (_e[i].id == id) { _e[i].adopted = v; return; }
    }
}

bool Registry::forget(uint16_t id) {
    CriticalGuard guard(_mux);
    for (uint8_t i = 0; i < _count; i++) {
        if (_e[i].id == id) {
            for (uint8_t j = i; j < _count - 1; j++) _e[j] = _e[j + 1];
            _count--;
            return true;
        }
    }
    return false;
}

uint8_t Registry::count() const {
    CriticalGuard guard(_mux);
    return _count;
}

Registry::Entry Registry::at(uint8_t i) const {
    CriticalGuard guard(_mux);
    return _e[i];
}

bool Registry::online(uint8_t i, uint32_t now, uint32_t timeoutMs) const {
    CriticalGuard guard(_mux);
    return (int32_t)(now - _e[i].lastSeen) < (int32_t)timeoutMs;
}
