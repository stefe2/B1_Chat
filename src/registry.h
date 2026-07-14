#pragma once

// ============================================================================
//  Registry — live droid inventory (master)
//
//  Fed by incoming messages (heartbeat, anim, ...). Tracks for each droid:
//  srcId, RSSI, last-seen timestamp. Lets us detect new connections and
//  offline droids (timeout).
//  See project.md (§10).
//
//  Concurrency: the "receive" setters (seen/setServos/setAutoAnim/
//  setFwVersion) are called from the ESP-NOW callback (internal Wi-Fi task)
//  while everything else (pushDroids/OtaMaster/loop reads, adopt/forget)
//  runs on the loop() task. Every public method is therefore atomic
//  (portMUX spinlock) and at() returns a COPY of the entry rather than a
//  reference into a mutable array. Since removals (forget) only happen on
//  the loop() side, a count()/at() iteration from loop() can't see an entry
//  shift under it — at worst it misses a droid freshly inserted by the
//  Wi-Fi task, with no consequence.
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
        bool     adopted;   // false = pending adoption (see config_store)
        uint8_t  fwMajor = 0, fwMinor = 0, fwPatch = 0;  // version reported via heartbeat
    };

    // Registers/refreshes a droid. Returns true if newly added.
    bool seen(uint16_t id, int rssi, uint32_t now);

    // Updates a droid's servo state (via heartbeat).
    void setServos(uint16_t id, bool on);

    // Updates a droid's auto-anim state (via heartbeat).
    void setAutoAnim(uint16_t id, bool on);

    // Updates the firmware version reported by a droid (via heartbeat).
    void setFwVersion(uint16_t id, uint8_t major, uint8_t minor, uint8_t patch);

    // Marks a droid as adopted/not adopted (RAM status, see config_store for NVS).
    void setAdopted(uint16_t id, bool v);

    // Removes a droid from the registry (Forget / adoption declined). Returns
    // true if it was found and removed.
    bool forget(uint16_t id);

    uint8_t count() const;

    // Copy of entry i (never a reference: the underlying array is mutated
    // by the Wi-Fi task).
    Entry at(uint8_t i) const;

    // A droid is considered online if it was seen less than `timeoutMs` ago.
    // SIGNED difference: lastSeen (timestamped by the Wi-Fi task) can be
    // later than `now` captured at the start of loop() — in unsigned math,
    // the droid would flicker "offline" (same bug family as pushDroids's
    // age, see CLAUDE.md pitfalls).
    bool online(uint8_t i, uint32_t now, uint32_t timeoutMs) const;

private:
    Entry   _e[MAX];
    uint8_t _count = 0;
    mutable portMUX_TYPE _mux = portMUX_INITIALIZER_UNLOCKED;
};

extern Registry Droids;
