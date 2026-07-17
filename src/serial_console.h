#pragma once

// ============================================================================
//  SerialConsole — JSON bridge over USB for the web console (master)
//
//  Protocol: one line = one JSON message (see CLAUDE.md).
//  - PC → master: {cmd:"list"|"anim"|"config"|"name"|
//                   "getConfig"|"calib"|"preview"|"getCalib"|"getAnimDurations"|
//                   "servo"|"autoAnim"|"locate"|"getMeshTopology"|"getAll"|
//                   "setMulti"|"commit"|"revert", ...}
//  - master → PC: {evt:"droids"|"log"|"config"|"meshTopology"|"err"|"allDone"|
//                   "setMultiDone"|"dirty", ...}
//  (The seq* commands/events — the 8 NVS sequence slots and the onboard
//  player — were removed in fw 1.7.0: sequences are console-driven only.)
//
//  Application logs go through log() to stay in JSON format and not pollute
//  the protocol. Hooks let the firmware act on commands (play an anim, etc.).
// ============================================================================

#include <Arduino.h>
#include <ArduinoJson.h>

class SerialConsole {
public:
    void begin();

    // Reads and processes incoming commands (to be called in loop()).
    void update();

    // Emits a log in {evt:"log","msg":...} format.
    void log(const char* fmt, ...);

    // Emits an explicit error in {evt:"err","msg":...} format — unknown/invalid
    // command, truncated line, invalid JSON. The console shows it in red
    // instead of the failure being silent.
    void pushErr(const char* fmt, ...);

    // Emits the droid list ({evt:"droids",...}) and the state ({evt:"state"}).
    void pushDroids();
    void pushState();

    // Emits the indicative duration (ms) of each gesture ({evt:"animDurations",...}).
    void pushAnimDurations();

    // Emits the mesh's detected direct links ({evt:"meshTopology",...}).
    void pushMeshTopology();

    // OTA events (see CLAUDE.md for the full flow).
    void pushOtaReady(uint16_t target, uint8_t sessionId, uint8_t chunkSize, uint16_t totalChunks);
    void pushOtaChunkAck(uint16_t seq, uint16_t sent, uint16_t total);
    void pushOtaDone(uint16_t target, uint8_t sessionId);
    void pushOtaResult(uint16_t target, bool ok, const char* fw, const char* reason);
    void pushOtaError(uint16_t target, uint8_t sessionId, const char* reason);

    // Optional hooks triggered by incoming commands.
    void onAnim(void (*cb)(uint8_t animId, uint32_t seed)) { _animCb = cb; }
    void onConfig(void (*cb)(uint8_t freq, uint8_t amp, uint8_t speed)) { _cfgCb = cb; }
    void onServo(void (*cb)(uint16_t target, bool enabled)) { _servoCb = cb; }
    void onAutoAnim(void (*cb)(uint16_t target, bool enabled)) { _autoAnimCb = cb; }
    void onLocate(void (*cb)(uint16_t target, bool enabled)) { _locateCb = cb; }
    void onCalib(void (*cb)(uint16_t target, uint8_t panMin, uint8_t panCenter, uint8_t panMax,
                            uint8_t tiltMin, uint8_t tiltCenter, uint8_t tiltMax)) { _calibCb = cb; }
    void onPreview(void (*cb)(uint16_t target, uint8_t pan, uint8_t tilt)) { _previewCb = cb; }
    void onOtaStart(bool (*cb)(uint16_t target, uint32_t size, const char* md5Hex32)) { _otaStartCb = cb; }
    void onOtaChunk(void (*cb)(uint16_t seq, const uint8_t* data, uint8_t len)) { _otaChunkCb = cb; }
    void onOtaAbort(void (*cb)()) { _otaAbortCb = cb; }

    // Master's servo state (to display it in the list).
    void setMasterServos(bool on) { _masterServos = on; }

    // Master's auto-anim state (to display it in the list).
    void setMasterAutoAnim(bool on) { _masterAutoAnim = on; }

    // Web Serial session validated via the hello/ping handshake.
    bool isClientReady() const { return _clientReady; }

private:
    // Line buffer: 4 KB to accept a large setMulti (and, historically, a
    // 32-step seqSave). (256 B before: any longer line was silently dropped.)
    static const uint16_t SERIAL_LINE_MAX = 4096;
    char     _buf[SERIAL_LINE_MAX];
    uint16_t _len = 0;
    bool     _overflow = false;
    bool     _masterServos = true;
    bool     _masterAutoAnim = true;
    bool     _clientReady = false;
    uint32_t _lastHelloMs = 0;
    static const uint32_t CLIENT_TIMEOUT_MS = 5000;

    void (*_animCb)(uint8_t, uint32_t) = nullptr;
    void (*_cfgCb)(uint8_t, uint8_t, uint8_t) = nullptr;
    void (*_servoCb)(uint16_t, bool) = nullptr;
    void (*_autoAnimCb)(uint16_t, bool) = nullptr;
    void (*_locateCb)(uint16_t, bool) = nullptr;
    void (*_calibCb)(uint16_t, uint8_t, uint8_t, uint8_t, uint8_t, uint8_t, uint8_t) = nullptr;
    void (*_previewCb)(uint16_t, uint8_t, uint8_t) = nullptr;
    bool (*_otaStartCb)(uint16_t, uint32_t, const char*) = nullptr;
    void (*_otaChunkCb)(uint16_t, const uint8_t*, uint8_t) = nullptr;
    void (*_otaAbortCb)() = nullptr;

    void handleLine(const char* line);

    // setMulti: validation then application of a batch op (name/calib/config).
    // validateOp fills `why` on rejection.
    bool validateOp(JsonObjectConst op, char* why, size_t whyLen);
    bool applyOp(JsonObjectConst op);

    // Pushes {evt:"dirty"} when the "uncommitted changes" state changes.
    void syncDirty();
    bool _lastDirtySent = false;

    void pushCalibData(uint16_t target);
};

extern SerialConsole Console;
