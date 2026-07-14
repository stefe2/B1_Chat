#pragma once

// ============================================================================
//  OtaSlave — receives an OTA image relayed by the mesh (slave only)
//
//  Minimal state machine: one transfer at a time, strictly sequential
//  reception (Update.write() is append-only, no out-of-order handling is
//  attempted). See CLAUDE.md for the full protocol.
//
//  Callback -> loop() architecture: the on*() run from the ESP-NOW callback
//  (internal Wi-Fi task) and NEVER touch flash or the state machine — they
//  drop the raw message into a single-slot mailbox, which update() (loop())
//  pops, validates, and processes.
//  Reason: Update.begin/write/end perform real SPI flash access (sector
//  erase ~every 21 chunks of 190 B, MD5 over the whole image at end) —
//  forbidden from the Wi-Fi task and even more so under portENTER_CRITICAL
//  (freeze/panic observed systematically at chunk 21, the first overflow of
//  Update's 4 KB sector buffer).
// ============================================================================

#include "config.h"

#if !IS_MASTER
#include "mesh_comm.h"

class OtaSlave {
public:
    // Called from the ESP-NOW callback: targetId filtering + mailbox drop
    // only (no flash access, no state mutated).
    void onStart(uint16_t srcId, const OtaStartPayload& p);
    void onChunk(uint16_t srcId, const OtaChunkPayload& p);
    void onEnd(uint16_t srcId, const OtaEndPayload& p);
    void onAbort(uint16_t srcId, const OtaAbortPayload& p);

    // Pops the mailbox (validation + Update.* + ack, outside the lock)
    // then handles auto-abort if there's no more activity for
    // OTA_SESSION_TIMEOUT_MS (safety net if a MSG_OTA_ABORT is lost).
    // Called from loop().
    void update(uint32_t nowMs);

private:
    enum State { IDLE, RECEIVING } _state = IDLE;
    uint8_t  _sessionId = 0;
    uint16_t _expectedChunkIndex = 0;
    uint16_t _totalChunks = 0;
    uint32_t _lastActivityMs = 0;

    // Callback -> loop() mailbox. A single slot is enough (stop-and-wait:
    // one message in flight); a new post overwrites the old one — losing a
    // duplicate has no effect (the master retransmits), and keeping the
    // most recent one guarantees an ABORT is never stuck behind a CHUNK.
    enum PendingType : uint8_t { PEND_NONE, PEND_START, PEND_CHUNK, PEND_END, PEND_ABORT };
    PendingType _pendingType = PEND_NONE;
    uint8_t _pendingBuf[sizeof(OtaChunkPayload)];   // sized for the largest payload

    // Only protects the mailbox: everything else in the state above is
    // only ever touched by loop(), so no race is possible on it.
    portMUX_TYPE _mux = portMUX_INITIALIZER_UNLOCKED;

    void post(PendingType type, const void* payload, size_t len);
    void processStart(const OtaStartPayload& p);
    void processChunk(const OtaChunkPayload& p);
    void processEnd(const OtaEndPayload& p);
    void processAbort(const OtaAbortPayload& p);
    void sendAck(uint8_t kind, uint16_t chunkIndex, uint8_t status);
};

extern OtaSlave OtaS;
#endif
