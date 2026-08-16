#pragma once

// ============================================================================
//  ConfigStore — settings persistence in NVS (Preferences)
//
//  Stores names, calibration, servo state and adoption (srcId-keyed mappings).
//  Survives reboots.
//
//  Commit model (inspired by KyberEditor) for names: setters write to a RAM
//  overlay and NVS is only touched on commitPending() — the console
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
    uint8_t panReversed, tiltReversed;
};

class ConfigStore {
public:
    void begin();

    // A droid's name (empty if unset).
    String getName(uint16_t id);
    void   setName(uint16_t id, const String& name);

    // Immediately persists a droid's OWN name (see class comment) — bypasses
    // the RAM overlay/commit-revert draft entirely.
    void   setNameImmediate(uint16_t id, const String& name);

    // Immediately persists THIS droid's OWN servo enabled state —
    // same immediate-persistence pattern as setNameImmediate/setCalib, so a
    // droid remembers these across a reboot instead of always resetting to
    // its compile-time default. `defaultValue` is only used the first time
    // (key never written yet).
    bool servosEnabled(bool defaultValue);
    void setServosEnabledImmediate(bool enabled);

    // A droid's servo calibration (config.h limits if never set).
    // Immediate persistence — outside the commit/revert model.
    ServoCalib getCalib(uint16_t id);
    void       setCalib(uint16_t id, const ServoCalib& c);

    // A droid's adoption status (false = never adopted). Immediate
    // persistence, outside the commit/revert model: setAdopted(id, false)
    // erases the key rather than writing false, to start fresh cleanly.
    bool isAdopted(uint16_t id);
    void setAdopted(uint16_t id, bool adopted);

    // Commit model (names).
    bool dirty() const { return _dirty; }
    void commitPending();   // writes the RAM overlay to NVS then clears it

private:
    Preferences _p;
    static void nameKey(uint16_t id, char out[8]);
    static void calibKey(uint16_t id, char out[8]);
    static void reverseKey(uint16_t id, char out[8]);
    static void adoptKey(uint16_t id, char out[8]);
    // RAM overlay of uncommitted changes.
    bool    _dirty = false;
    static const uint8_t PENDING_NAMES_MAX = 32;   // = Registry::MAX
    struct PendingName { bool used; uint16_t id; String name; };
    PendingName _pendNames[PENDING_NAMES_MAX];

    void refreshDirty();
};

extern ConfigStore Config;
