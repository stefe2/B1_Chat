#pragma once

// ============================================================================
//  Registry — inventaire vivant des droïdes (maître)
//
//  Alimenté par les messages reçus (heartbeat, anim, ...). Suit pour chaque
//  droïde : srcId, RSSI, date de dernière vue. Permet de détecter les
//  nouvelles connexions et les droïdes hors ligne (timeout).
//  Voir project.md (§10).
// ============================================================================

#include <Arduino.h>

class Registry {
public:
    static const uint8_t MAX = 32;

    struct Entry {
        uint16_t id;
        int16_t  rssi;
        uint32_t lastSeen;
        bool     servos;   // état des servos rapporté par le droïde
    };

    // Enregistre/actualise un droïde. Retourne true si nouvellement ajouté.
    bool seen(uint16_t id, int rssi, uint32_t now);

    // Met à jour l'état des servos d'un droïde (via heartbeat).
    void setServos(uint16_t id, bool on);

    uint8_t count() const { return _count; }
    const Entry& at(uint8_t i) const { return _e[i]; }

    // Droïde considéré en ligne s'il a été vu depuis moins de `timeoutMs`.
    bool online(uint8_t i, uint32_t now, uint32_t timeoutMs) const {
        return (now - _e[i].lastSeen) < timeoutMs;
    }

private:
    Entry   _e[MAX];
    uint8_t _count = 0;
};

extern Registry Droids;
