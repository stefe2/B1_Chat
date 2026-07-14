#include "ota_slave.h"

#if !IS_MASTER
#include <Update.h>
#include "ota_guard.h"

OtaSlave OtaS;

namespace {
// RAII: protects ONLY the mailbox (see ota_slave.h). No Update.*,
// Mesh.send(), or delay()/ESP.restart() must be called under this lock.
struct CriticalGuard {
    portMUX_TYPE& mux;
    explicit CriticalGuard(portMUX_TYPE& m) : mux(m) { portENTER_CRITICAL(&mux); }
    ~CriticalGuard() { portEXIT_CRITICAL(&mux); }
};
}  // namespace

// ---------------------------------------------------------------------------
//  ESP-NOW callback side (Wi-Fi task): mailbox drop only
// ---------------------------------------------------------------------------

void OtaSlave::post(PendingType type, const void* payload, size_t len) {
    CriticalGuard guard(_mux);
    memcpy(_pendingBuf, payload, len);
    _pendingType = type;
}

void OtaSlave::onStart(uint16_t srcId, const OtaStartPayload& p) {
    (void)srcId;
    if (p.targetId != Mesh.myId()) return;
    post(PEND_START, &p, sizeof(p));
}

void OtaSlave::onChunk(uint16_t srcId, const OtaChunkPayload& p) {
    (void)srcId;
    if (p.targetId != Mesh.myId()) return;
    post(PEND_CHUNK, &p, sizeof(p));
}

void OtaSlave::onEnd(uint16_t srcId, const OtaEndPayload& p) {
    (void)srcId;
    if (p.targetId != Mesh.myId()) return;
    post(PEND_END, &p, sizeof(p));
}

void OtaSlave::onAbort(uint16_t srcId, const OtaAbortPayload& p) {
    (void)srcId;
    if (p.targetId != Mesh.myId()) return;
    post(PEND_ABORT, &p, sizeof(p));
}

// ---------------------------------------------------------------------------
//  loop() side: actual processing (validation + flash + ack), outside the lock
// ---------------------------------------------------------------------------

void OtaSlave::sendAck(uint8_t kind, uint16_t chunkIndex, uint8_t status) {
    OtaAckPayload ack{_sessionId, kind, chunkIndex, status};
    Mesh.send(MSG_OTA_ACK, &ack, sizeof(ack), OTA_MESH_TTL);
}

void OtaSlave::processStart(const OtaStartPayload& p) {
    uint8_t ackStatus;
    if (_state == RECEIVING && p.sessionId == _sessionId) {
        // Duplicate START retransmission (previous ack lost): idempotent
        // re-ack, above all don't call Update.begin() a second time.
        ackStatus = OTA_OK;
    } else {
        if (_state == RECEIVING) Update.abort();

        if (!Update.begin(p.totalSize)) {
            Serial.printf("OTA: Update.begin(%lu) refused (err %u)\n",
                          (unsigned long)p.totalSize, Update.getError());
            ackStatus = OTA_ERR_SIZE;
        } else {
            Serial.printf("OTA: session %u started (%lu B, %u chunks)\n",
                          p.sessionId, (unsigned long)p.totalSize, p.totalChunks);
            char md5[33];
            memcpy(md5, p.md5Hex, 32);
            md5[32] = '\0';
            Update.setMD5(md5);

            _sessionId = p.sessionId;
            _totalChunks = p.totalChunks;
            _expectedChunkIndex = 0;
            _lastActivityMs = millis();
            _state = RECEIVING;
            ackStatus = OTA_OK;
        }
    }
    sendAck(0, 0, ackStatus);
}

void OtaSlave::processChunk(const OtaChunkPayload& p) {
    uint8_t ackStatus;
    if (_state != RECEIVING || p.sessionId != _sessionId) {
        Serial.printf("OTA: chunk %u rejected (state=%d, session %u vs %u)\n",
                      p.chunkIndex, (int)_state, p.sessionId, _sessionId);
        ackStatus = OTA_ERR_SESSION;
    } else {
        _lastActivityMs = millis();

        if (p.chunkIndex == _expectedChunkIndex) {
            if (Update.write(const_cast<uint8_t*>(p.data), p.dataLen) != p.dataLen) {
                Serial.printf("OTA: write failed for chunk %u (err %u)\n",
                              p.chunkIndex, Update.getError());
                ackStatus = OTA_ERR_WRITE;
            } else {
                _expectedChunkIndex++;
                ackStatus = OTA_OK;
            }
        } else if (_expectedChunkIndex > 0 && p.chunkIndex == _expectedChunkIndex - 1) {
            // Chunk already written (our previous ack was lost): Update.write()
            // is append-only, NEVER call it again for an already-written index
            // — just re-emit the ack.
            ackStatus = OTA_OK;
        } else {
            Serial.printf("OTA: chunk %u out of sequence (expected %u)\n",
                          p.chunkIndex, _expectedChunkIndex);
            ackStatus = OTA_ERR_SESSION;
        }
    }
    sendAck(1, p.chunkIndex, ackStatus);
}

void OtaSlave::processEnd(const OtaEndPayload& p) {
    uint8_t ackStatus;
    bool doRestart = false;
    if (_state != RECEIVING || p.sessionId != _sessionId
        || _expectedChunkIndex != p.totalChunks) {
        Serial.printf("OTA: END rejected (state=%d, session %u vs %u, %u/%u chunks)\n",
                      (int)_state, p.sessionId, _sessionId, _expectedChunkIndex, p.totalChunks);
        ackStatus = OTA_ERR_SESSION;
    } else if (!Update.end(true)) {
        // Invalid integrity/format: stay on the current image, no reboot.
        Serial.printf("OTA: Update.end refused (err %u)\n", Update.getError());
        ackStatus = OTA_ERR_MD5;
        _state = IDLE;
    } else {
        Guard.armPendingReboot();
        ackStatus = OTA_OK;
        doRestart = true;
    }
    sendAck(2, 0, ackStatus);
    if (doRestart) {
        delay(250); // let the ack frame go out before cutting the radio
        ESP.restart();
    }
}

void OtaSlave::processAbort(const OtaAbortPayload& p) {
    if (_state != RECEIVING || p.sessionId != _sessionId) return;
    Serial.printf("OTA: abort received (reason %u) at chunk %u\n", p.reason, _expectedChunkIndex);
    Update.abort();
    _state = IDLE;
}

void OtaSlave::update(uint32_t nowMs) {
    PendingType type;
    uint8_t buf[sizeof(OtaChunkPayload)];
    {
        CriticalGuard guard(_mux);
        type = _pendingType;
        if (type != PEND_NONE) {
            memcpy(buf, _pendingBuf, sizeof(buf));
            _pendingType = PEND_NONE;
        }
    }

    switch (type) {
    case PEND_START: processStart(*reinterpret_cast<const OtaStartPayload*>(buf)); break;
    case PEND_CHUNK: processChunk(*reinterpret_cast<const OtaChunkPayload*>(buf)); break;
    case PEND_END:   processEnd(*reinterpret_cast<const OtaEndPayload*>(buf)); break;
    case PEND_ABORT: processAbort(*reinterpret_cast<const OtaAbortPayload*>(buf)); break;
    default: break;
    }

    // SIGNED comparison is mandatory: _lastActivityMs comes from a fresh
    // millis() taken during the processing above, so it can be LATER than
    // the nowMs captured at the start of loop() — in unsigned math, the
    // negative difference overflows (~4 billion) and the 20s timeout fires
    // instantly (observed in testing: session abandoned 5 ms after it started).
    if (_state == RECEIVING && (int32_t)(nowMs - _lastActivityMs) > (int32_t)OTA_SESSION_TIMEOUT_MS) {
        Serial.printf("OTA: inactivity, session %u abandoned at chunk %u\n",
                      _sessionId, _expectedChunkIndex);
        Update.abort();
        _state = IDLE;
    }
}
#endif
