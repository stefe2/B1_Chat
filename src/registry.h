#pragma once

// ============================================================================
//  Registry — live droid inventory (master)
//
//  Fed by incoming messages (heartbeat, anim, ...). Tracks for each droid:
//  srcId, RSSI, last-seen timestamp. Lets us detect new connections and
//  offline droids (timeout).
//  See project.md (§10).
//
//  Incoming application messages are copied by the ESP-NOW callback into
//  main.cpp's bounded inbox, then these setters run from loop(). Public
//  methods remain synchronized defensively, and at() returns a COPY rather
//  than exposing a reference into the mutable array.
// ============================================================================

#include <Arduino.h>

class Registry {
public:
    static const uint8_t MAX = 32;

    struct Entry {
        uint16_t id;
        int16_t  rssi;
        uint32_t lastSeen;
        bool     servos;    // servo state reported by the droid
        bool     autoAnim;  // spontaneous idle anims active, reported by the droid
        bool     locate;    // transient onboard-LED override reported by the droid
        bool     adopted;   // false = pending adoption (see config_store)
        uint8_t  fwMajor = 0, fwMinor = 0, fwPatch = 0;  // version reported via heartbeat
        uint32_t buildId = 0;  // 0 = legacy/unknown; otherwise FW_BUILD_ID
        uint32_t capabilities = 0;
    };

    // Registers/refreshes a droid. Returns true if newly added.
    bool seen(uint16_t id, int rssi, uint32_t now);

    // Updates a droid's servo state (via heartbeat).
    void setServos(uint16_t id, bool on);

    // Updates a droid's auto-anim state (via heartbeat).
    void setAutoAnim(uint16_t id, bool on);

    void setLocate(uint16_t id, bool on);

    // Updates the firmware identity reported by a droid (via heartbeat).
    void setFwIdentity(uint16_t id, uint8_t major, uint8_t minor, uint8_t patch,
                       uint32_t buildId);

    void setCapabilities(uint16_t id, uint32_t capabilities);

    // Marks a droid as adopted/not adopted (RAM status, see config_store for NVS).
    void setAdopted(uint16_t id, bool v);

    // Removes a droid from the registry (Forget / adoption declined). Returns
    // true if it was found and removed.
    bool forget(uint16_t id);

    uint8_t count() const;

    // Copy of entry i (never a reference into the mutable array).
    Entry at(uint8_t i) const;

    // A droid is considered online if it was seen less than `timeoutMs` ago.
    // SIGNED difference: the mesh inbox is pumped after `now` is captured at
    // the start of loop(), so lastSeen can be slightly later than `now`. In unsigned math,
    // the droid would flicker "offline" (same bug family as pushDroids's
    // age, see CLAUDE.md pitfalls).
    bool online(uint8_t i, uint32_t now, uint32_t timeoutMs) const;

private:
    Entry   _e[MAX];
    uint8_t _count = 0;
    mutable portMUX_TYPE _mux = portMUX_INITIALIZER_UNLOCKED;
};

extern Registry Droids;
