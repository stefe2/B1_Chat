#include "ota_slave.h"

#if !IS_MASTER
#include <Update.h>
#include "ota_guard.h"

OtaSlave OtaS;

void OtaSlave::sendAck(uint8_t kind, uint16_t chunkIndex, uint8_t status) {
    OtaAckPayload ack{_sessionId, kind, chunkIndex, status};
    Mesh.send(MSG_OTA_ACK, &ack, sizeof(ack), OTA_MESH_TTL);
}

void OtaSlave::onStart(uint16_t srcId, const OtaStartPayload& p) {
    (void)srcId;
    if (p.targetId != Mesh.myId()) return;

    if (_state == RECEIVING && p.sessionId == _sessionId) {
        // Retransmission dupliquée du START (ack précédent perdu) : re-ack
        // idempotent, surtout ne pas relancer Update.begin() en double.
        sendAck(0, 0, OTA_OK);
        return;
    }
    if (_state == RECEIVING) Update.abort();

    if (!Update.begin(p.totalSize)) {
        sendAck(0, 0, OTA_ERR_SIZE);
        return;
    }
    char md5[33];
    memcpy(md5, p.md5Hex, 32);
    md5[32] = '\0';
    Update.setMD5(md5);

    _sessionId = p.sessionId;
    _totalChunks = p.totalChunks;
    _expectedChunkIndex = 0;
    _lastActivityMs = millis();
    _state = RECEIVING;
    sendAck(0, 0, OTA_OK);
}

void OtaSlave::onChunk(uint16_t srcId, const OtaChunkPayload& p) {
    (void)srcId;
    if (p.targetId != Mesh.myId() || _state != RECEIVING || p.sessionId != _sessionId) {
        sendAck(1, p.chunkIndex, OTA_ERR_SESSION);
        return;
    }
    _lastActivityMs = millis();

    if (p.chunkIndex == _expectedChunkIndex) {
        if (Update.write(const_cast<uint8_t*>(p.data), p.dataLen) != p.dataLen) {
            sendAck(1, p.chunkIndex, OTA_ERR_WRITE);
            return;
        }
        _expectedChunkIndex++;
        sendAck(1, p.chunkIndex, OTA_OK);
    } else if (_expectedChunkIndex > 0 && p.chunkIndex == _expectedChunkIndex - 1) {
        // Chunk déjà écrit (notre ack précédent s'est perdu) : Update.write()
        // est append-only, on ne le rappelle JAMAIS pour un index déjà écrit
        // — on se contente de ré-émettre l'ack.
        sendAck(1, p.chunkIndex, OTA_OK);
    } else {
        sendAck(1, p.chunkIndex, OTA_ERR_SESSION);
    }
}

void OtaSlave::onEnd(uint16_t srcId, const OtaEndPayload& p) {
    (void)srcId;
    if (p.targetId != Mesh.myId() || _state != RECEIVING || p.sessionId != _sessionId
        || _expectedChunkIndex != p.totalChunks) {
        sendAck(2, 0, OTA_ERR_SESSION);
        return;
    }

    if (!Update.end(true)) {
        // Intégrité/format invalide : on reste sur l'image actuelle, aucun reboot.
        sendAck(2, 0, OTA_ERR_MD5);
        _state = IDLE;
        return;
    }

    Guard.armPendingReboot();
    sendAck(2, 0, OTA_OK);
    delay(250); // laisse partir la trame d'ack avant de couper la radio
    ESP.restart();
}

void OtaSlave::onAbort(uint16_t srcId, const OtaAbortPayload& p) {
    (void)srcId;
    if (p.targetId != Mesh.myId() || _state != RECEIVING || p.sessionId != _sessionId) return;
    Update.abort();
    _state = IDLE;
}

void OtaSlave::update(uint32_t nowMs) {
    if (_state == RECEIVING && nowMs - _lastActivityMs > OTA_SESSION_TIMEOUT_MS) {
        Update.abort();
        _state = IDLE;
    }
}
#endif
