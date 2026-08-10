#pragma once

// ============================================================================
//  ConfigStore — settings persistence in NVS (Preferences)
//
//  Stores per-droid animation parameters and names (srcId-keyed mappings).
//  Survives reboots.
//
//  Commit model (inspired by KyberEditor) for anim params / names: setters
//  write to a RAM overlay ("pending", the effect is immediate via the
//  getters) and NVS is only touched on commitPending() — the console
//  triggers this itself ~2s after the last change (see ProtocolClient's
//  auto-commit debounce; there's no manual "revert" anymore, fw 1.8.0).
//  Servo calibration (setCalib) stays IMMEDIATELY persisted: it's
//  a physical adjustment made live on the targeted droid. setNameImmediate()
//  is the same kind of exception, used when a droid persists its OWN name
//  upon receiving MSG_NAME (mesh-pushed) — bypassing the master's own
//  commit draft entirely, since that draft is a master-side UI
//  concern that doesn't apply to a remote droid's local copy.
// ============================================================================

#include <Arduino.h>
#include <Preferences.h>

// A droid's mechanical limits (degrees), persisted individually.
struct ServoCalib {
    uint8_t panMin, panCenter, panMax;
    uint8_t tiltMin, tiltCenter, tiltMax;
};

class ConfigStore {
public:
    void begin();

    // Must be set once MeshComm has derived this board's ID. It lets the
    // keyed per-droid store transparently read the legacy global af/aa/as
    // keys written by firmware <= 1.9.0.
    void setLocalId(uint16_t id) { _localId = id; }

    // Animation parameters (0..100 each). The no-ID forms address THIS
    // droid; the *For forms are also used by the master as a central cache
    // for every remote droid.
    void animParams(uint8_t& freq, uint8_t& amp, uint8_t& speed);
    void animParamsFor(uint16_t id, uint8_t& freq, uint8_t& amp, uint8_t& speed);
    void setAnimParams(uint8_t freq, uint8_t amp, uint8_t speed);
    void setAnimParamsFor(uint16_t id, uint8_t freq, uint8_t amp, uint8_t speed);

    // Immediately persists params received via mesh MSG_CONFIG (see main.cpp's
    // applyAnimParamsEffect) — bypasses the RAM overlay/commit draft entirely,
    // same reasoning as setNameImmediate/setCalib: a slave has no "commit"
    // command to ever flush the draft with, so it must persist right away to
    // survive its own reboot. The master uses setAnimParamsFor() for its
    // per-ID console-driven draft (auto-committed ~2s after the last change).
    void setAnimParamsImmediate(uint8_t freq, uint8_t amp, uint8_t speed);

    // A droid's name (empty if unset).
    String getName(uint16_t id);
    void   setName(uint16_t id, const String& name);

    // Immediately persists a droid's OWN name (see class comment) — bypasses
    // the RAM overlay/commit-revert draft entirely.
    void   setNameImmediate(uint16_t id, const String& name);

    // Immediately persists THIS droid's OWN servos/auto-anim enabled state —
    // same immediate-persistence pattern as setNameImmediate/setCalib, so a
    // droid remembers these across a reboot instead of always resetting to
    // its compile-time default. `defaultValue` is only used the first time
    // (key never written yet).
    bool servosEnabled(bool defaultValue);
    void setServosEnabledImmediate(bool enabled);
    bool autoAnimEnabled(bool defaultValue);
    void setAutoAnimEnabledImmediate(bool enabled);

    // A droid's servo calibration (config.h limits if never set).
    // Immediate persistence — outside the commit/revert model.
    ServoCalib getCalib(uint16_t id);
    void       setCalib(uint16_t id, const ServoCalib& c);

    // A droid's adoption status (false = never adopted). Immediate
    // persistence, outside the commit/revert model: setAdopted(id, false)
    // erases the key rather than writing false, to start fresh cleanly.
    bool isAdopted(uint16_t id);
    void setAdopted(uint16_t id, bool adopted);

    // Commit model (anim params, names).
    bool dirty() const { return _dirty; }
    void commitPending();   // writes the RAM overlay to NVS then clears it

private:
    Preferences _p;
    uint16_t _localId = 0;
    static void nameKey(uint16_t id, char out[8]);
    static void calibKey(uint16_t id, char out[8]);
    static void adoptKey(uint16_t id, char out[8]);
    static void animKey(uint16_t id, char out[8]);
    void writeAnimParams(uint16_t id, uint8_t freq, uint8_t amp, uint8_t speed);

    // RAM overlay of uncommitted changes.
    bool    _dirty = false;
    static const uint8_t PENDING_ANIMS_MAX = 33; // master + Registry::MAX slaves
    struct PendingAnim {
        bool used;
        uint16_t id;
        uint8_t freq, amp, speed;
    };
    PendingAnim _pendAnims[PENDING_ANIMS_MAX];
    static const uint8_t PENDING_NAMES_MAX = 32;   // = Registry::MAX
    struct PendingName { bool used; uint16_t id; String name; };
    PendingName _pendNames[PENDING_NAMES_MAX];

    void refreshDirty();
};

extern ConfigStore Config;
