#include "ota_slave.h"

#if !IS_MASTER
#include <Update.h>
#include "ota_guard.h"

OtaSlave OtaS;

namespace {
// RAII : voir la meme construction dans ota_master.cpp — protege l'etat partage, jamais
// tenu pendant un Mesh.send()/esp_now_send() ni un delay()/ESP.restart().
struct CriticalGuard {
    portMUX_TYPE& mux;
    explicit CriticalGuard(portMUX_TYPE& m) : mux(m) { portENTER_CRITICAL(&mux); }
    ~CriticalGuard() { portEXIT_CRITICAL(&mux); }
};
}  // namespace

void OtaSlave::sendAck(uint8_t kind, uint16_t chunkIndex, uint8_t status) {
    OtaAckPayload ack{_sessionId, kind, chunkIndex, status};
    Mesh.send(MSG_OTA_ACK, &ack, sizeof(ack), OTA_MESH_TTL);
}

void OtaSlave::onStart(uint16_t srcId, const OtaStartPayload& p) {
    (void)srcId;
    if (p.targetId != Mesh.myId()) return;

    uint8_t ackStatus;
    {
        CriticalGuard guard(_mux);
        if (_state == RECEIVING && p.sessionId == _sessionId) {
            // Retransmission dupliquée du START (ack précédent perdu) : re-ack
            // idempotent, surtout ne pas relancer Update.begin() en double.
            ackStatus = OTA_OK;
        } else {
            if (_state == RECEIVING) Update.abort();

            if (!Update.begin(p.totalSize)) {
                ackStatus = OTA_ERR_SIZE;
            } else {
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
    }
    sendAck(0, 0, ackStatus);
}

void OtaSlave::onChunk(uint16_t srcId, const OtaChunkPayload& p) {
    (void)srcId;
    uint8_t ackStatus;
    {
        CriticalGuard guard(_mux);
        if (p.targetId != Mesh.myId() || _state != RECEIVING || p.sessionId != _sessionId) {
            ackStatus = OTA_ERR_SESSION;
        } else {
            _lastActivityMs = millis();

            if (p.chunkIndex == _expectedChunkIndex) {
                if (Update.write(const_cast<uint8_t*>(p.data), p.dataLen) != p.dataLen) {
                    ackStatus = OTA_ERR_WRITE;
                } else {
                    _expectedChunkIndex++;
                    ackStatus = OTA_OK;
                }
            } else if (_expectedChunkIndex > 0 && p.chunkIndex == _expectedChunkIndex - 1) {
                // Chunk déjà écrit (notre ack précédent s'est perdu) : Update.write()
                // est append-only, on ne le rappelle JAMAIS pour un index déjà écrit
                // — on se contente de ré-émettre l'ack.
                ackStatus = OTA_OK;
            } else {
                ackStatus = OTA_ERR_SESSION;
            }
        }
    }
    sendAck(1, p.chunkIndex, ackStatus);
}

void OtaSlave::onEnd(uint16_t srcId, const OtaEndPayload& p) {
    (void)srcId;
    uint8_t ackStatus;
    bool doRestart = false;
    {
        CriticalGuard guard(_mux);
        if (p.targetId != Mesh.myId() || _state != RECEIVING || p.sessionId != _sessionId
            || _expectedChunkIndex != p.totalChunks) {
            ackStatus = OTA_ERR_SESSION;
        } else if (!Update.end(true)) {
            // Intégrité/format invalide : on reste sur l'image actuelle, aucun reboot.
            ackStatus = OTA_ERR_MD5;
            _state = IDLE;
        } else {
            Guard.armPendingReboot();
            ackStatus = OTA_OK;
            doRestart = true;
        }
    }
    sendAck(2, 0, ackStatus);
    if (doRestart) {
        delay(250); // laisse partir la trame d'ack avant de couper la radio
        ESP.restart();
    }
}

void OtaSlave::onAbort(uint16_t srcId, const OtaAbortPayload& p) {
    (void)srcId;
    CriticalGuard guard(_mux);
    if (p.targetId != Mesh.myId() || _state != RECEIVING || p.sessionId != _sessionId) return;
    Update.abort();
    _state = IDLE;
}

void OtaSlave::update(uint32_t nowMs) {
    CriticalGuard guard(_mux);
    if (_state == RECEIVING && nowMs - _lastActivityMs > OTA_SESSION_TIMEOUT_MS) {
        Update.abort();
        _state = IDLE;
    }
}
#endif
