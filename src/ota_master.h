#pragma once

// ============================================================================
//  OtaMaster — orchestrates an OTA session toward ONE slave (master only)
//
//  Receives decoded chunks (base64 -> binary) from serial_console.cpp,
//  relays them over the mesh in stop-and-wait (one chunk in flight, ACK
//  required before the next — Update.write() on the slave side is
//  append-only, so handling out-of-order chunks would be needless
//  complexity). One session at a time.
//
//  Does NOT depend on serial_console.h: events to push to the console are
//  fetched via pollEvent() and translated to JSON by main.cpp, like the
//  rest of the firmware (only SerialConsole speaks JSON/Serial).
// ============================================================================

#include "config.h"

#if IS_MASTER
#include "mesh_comm.h"

class OtaMaster {
public:
    enum EventType : uint8_t { EV_NONE, EV_READY, EV_CHUNK_ACK, EV_DONE, EV_RESULT, EV_ERROR };

    struct Event {
        EventType type = EV_NONE;
        uint16_t  target = 0;
        uint8_t   sessionId = 0;
        uint8_t   chunkSize = 0;
        uint16_t  chunkIndex = 0;
        uint16_t  sent = 0;
        uint16_t  total = 0;
        bool      ok = false;
        char      fw[16] = {0};
        char      reason[24] = {0};
    };

    // Starts a session. false = busy, unknown target, or invalid size
    // (the caller must then push otaError itself — see serial_console.cpp).
    bool begin(uint16_t target, uint32_t size, const char* md5Hex32);

    // Decoded chunk (from base64) received over serial for the current
    // session. Ignored if `index` doesn't match the expected chunk or is out of session.
    void onSerialChunk(uint16_t index, const uint8_t* data, uint8_t len);

    // User cancellation.
    void abort();

    // Mesh acknowledgment received (called from onMeshMessage, type MSG_OTA_ACK).
    void onAck(uint16_t srcId, const OtaAckPayload& p);

    // Timeout/retry tick + post-reboot monitoring (called from loop()).
    void update(uint32_t nowMs);

    bool busy() const { return _state != OM_IDLE; }

    // Consumes the pending event (type EV_NONE if nothing new).
    Event pollEvent();

private:
    enum State { OM_IDLE, OM_AWAIT_START_ACK, OM_AWAIT_SERIAL_CHUNK, OM_AWAIT_CHUNK_ACK,
                 OM_AWAIT_END_ACK, OM_AWAIT_REBOOT };

    State    _state = OM_IDLE;
    uint16_t _target = 0;
    uint8_t  _sessionId = 0;
    uint32_t _totalSize = 0;
    uint16_t _totalChunks = 0;
    uint16_t _nextChunkIndex = 0;
    uint8_t  _retryCount = 0;
    uint32_t _lastSendMs = 0;
    uint32_t _serialWaitSince = 0;
    uint32_t _rebootStartMs = 0;
    uint32_t _lastSeenAtRebootStart = 0;
    // Version reported by the target BEFORE the OTA (captured in begin()):
    // the console can't reliably know the version baked into an arbitrary
    // .bin, so post-reboot confirmation compares "changed relative to
    // before" rather than "matches what was announced".
    uint8_t  _prevFwMajor = 0, _prevFwMinor = 0, _prevFwPatch = 0;

    uint8_t  _lastSentType = 0;
    uint8_t  _lastSentBuf[sizeof(OtaChunkPayload)];
    uint8_t  _lastSentLen = 0;

    Event _pending;

    // Protects all the state above: onAck() runs from the ESP-NOW callback
    // (internal Wi-Fi task, potentially another core) while
    // begin()/onSerialChunk()/abort()/update()/pollEvent() run from loop() —
    // without this lock, two concurrent accesses can corrupt the state or
    // drop an event (observed in testing: the session stalls on a
    // different chunk each attempt, the typical signature of a race
    // condition rather than a deterministic bug).
    portMUX_TYPE _mux = portMUX_INITIALIZER_UNLOCKED;

    // fail() only mutates state (called from inside a section already
    // locked by the caller); the actual MSG_OTA_ABORT is sent by the
    // caller once the lock is released (Mesh.send()/esp_now_send() must
    // never be invoked with interrupts disabled).
    bool     _pendingAbortNeeded = false;
    uint16_t _pendingAbortTarget = 0;
    uint8_t  _pendingAbortSession = 0;

    void sendFrame(uint8_t type, const void* payload, uint8_t len);
    void resend();
    void fail(const char* reason);
};

extern OtaMaster OtaM;
#endif
