#pragma once

// ============================================================================
//  SerialConsole — JSON bridge over USB for the web console (master)
//
//  Protocol: one line = one JSON message (see project.md §10).
//  - PC → master: {cmd:"list"|"anim"|"config"|"volume"|"name"|"playTrack"|
//                   "getConfig"|"calib"|"preview"|"getCalib"|"getAnimDurations"|
//                   "seqState"|"servo"|"autoAnim"|"getMeshTopology"|"getAll"|
//                   "setMulti"|"commit"|"revert"|"seqPause"|"seqResume", ...}
//  - master → PC: {evt:"droids"|"log"|"config"|"meshTopology"|"err"|"allDone"|
//                   "setMultiDone"|"dirty", ...}
//
//  Application logs go through log() to stay in JSON format and not pollute
//  the protocol. Hooks let the firmware act on commands (play an anim, set
//  the volume, etc.).
// ============================================================================

#include <Arduino.h>
#include <ArduinoJson.h>
#include "sequence_store.h"

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

    // Emits a sequence's playback state ({evt:"seqState",...}).
    // track = audio track of the running sequence (0 = none).
    void pushSeqState(bool playing, uint8_t slot, uint8_t index, uint8_t total,
                      uint8_t track = 0, bool paused = false);

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
    void onVolume(void (*cb)(uint8_t volume)) { _volCb = cb; }
    void onTrack(void (*cb)(uint8_t track)) { _trackCb = cb; }
    void onConfig(void (*cb)(uint8_t freq, uint8_t amp, uint8_t speed)) { _cfgCb = cb; }
    void onServo(void (*cb)(uint16_t target, bool enabled)) { _servoCb = cb; }
    void onAutoAnim(void (*cb)(uint16_t target, bool enabled)) { _autoAnimCb = cb; }
    void onSeqSave(bool (*cb)(uint8_t slot, const StoredSequence& seq)) { _seqSaveCb = cb; }
    void onSeqList(uint8_t (*cb)(StoredSequenceMeta* out, uint8_t maxOut)) { _seqListCb = cb; }
    void onSeqLoad(bool (*cb)(uint8_t slot, StoredSequence& out)) { _seqLoadCb = cb; }
    void onSeqRun(void (*cb)(uint8_t slot, uint8_t from)) { _seqRunCb = cb; }
    void onSeqStop(void (*cb)()) { _seqStopCb = cb; }
    void onSeqPause(void (*cb)(bool paused)) { _seqPauseCb = cb; }
    void onSeqDelete(bool (*cb)(uint8_t slot)) { _seqDeleteCb = cb; }
    void onCalib(void (*cb)(uint16_t target, uint8_t panMin, uint8_t panCenter, uint8_t panMax,
                            uint8_t tiltMin, uint8_t tiltCenter, uint8_t tiltMax)) { _calibCb = cb; }
    void onPreview(void (*cb)(uint16_t target, uint8_t pan, uint8_t tilt)) { _previewCb = cb; }
    void onSeqQuery(void (*cb)()) { _seqQueryCb = cb; }
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
    // Line buffer: 4 KB to accept a 32-step seqSave and setMulti.
    // (256 B before: any longer line was silently dropped.)
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
    void (*_volCb)(uint8_t) = nullptr;
    void (*_trackCb)(uint8_t) = nullptr;
    void (*_cfgCb)(uint8_t, uint8_t, uint8_t) = nullptr;
    void (*_servoCb)(uint16_t, bool) = nullptr;
    void (*_autoAnimCb)(uint16_t, bool) = nullptr;
    bool (*_seqSaveCb)(uint8_t, const StoredSequence&) = nullptr;
    uint8_t (*_seqListCb)(StoredSequenceMeta*, uint8_t) = nullptr;
    bool (*_seqLoadCb)(uint8_t, StoredSequence&) = nullptr;
    void (*_seqRunCb)(uint8_t, uint8_t) = nullptr;
    void (*_seqStopCb)() = nullptr;
    void (*_seqPauseCb)(bool) = nullptr;
    bool (*_seqDeleteCb)(uint8_t) = nullptr;
    void (*_calibCb)(uint16_t, uint8_t, uint8_t, uint8_t, uint8_t, uint8_t, uint8_t) = nullptr;
    void (*_previewCb)(uint16_t, uint8_t, uint8_t) = nullptr;
    void (*_seqQueryCb)() = nullptr;
    bool (*_otaStartCb)(uint16_t, uint32_t, const char*) = nullptr;
    void (*_otaChunkCb)(uint16_t, const uint8_t*, uint8_t) = nullptr;
    void (*_otaAbortCb)() = nullptr;

    void handleLine(const char* line);

    // setMulti: validation then application of a batch op (name/calib/
    // volume/config/seqSave/seqDelete). validateOp fills `why` on rejection.
    bool validateOp(JsonObjectConst op, char* why, size_t whyLen);
    bool applyOp(JsonObjectConst op);

    // Pushes {evt:"dirty"} when the "uncommitted changes" state changes.
    void syncDirty();
    bool _lastDirtySent = false;

    void pushSeqList();
    void pushSeqData(uint8_t slot, const StoredSequence& seq);
    void pushCalibData(uint16_t target);
    void pushSeqSaved(bool ok, uint8_t slot, const char* name);
    void pushSeqDeleted(bool ok, uint8_t slot);
};

extern SerialConsole Console;
