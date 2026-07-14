#include "ota_slave.h"

#if !IS_MASTER
#include <Update.h>
#include "ota_guard.h"

OtaSlave OtaS;

namespace {
// RAII : protege UNIQUEMENT la boite aux lettres (voir ota_slave.h). Aucun
// Update.*, Mesh.send() ni delay()/ESP.restart() ne doit etre appele sous
// ce verrou.
struct CriticalGuard {
    portMUX_TYPE& mux;
    explicit CriticalGuard(portMUX_TYPE& m) : mux(m) { portENTER_CRITICAL(&mux); }
    ~CriticalGuard() { portEXIT_CRITICAL(&mux); }
};
}  // namespace

// ---------------------------------------------------------------------------
//  Côté callback ESP-NOW (tâche Wi-Fi) : dépôt en boîte aux lettres seulement
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
//  Côté loop() : traitement réel (validation + flash + ack), hors verrou
// ---------------------------------------------------------------------------

void OtaSlave::sendAck(uint8_t kind, uint16_t chunkIndex, uint8_t status) {
    OtaAckPayload ack{_sessionId, kind, chunkIndex, status};
    Mesh.send(MSG_OTA_ACK, &ack, sizeof(ack), OTA_MESH_TTL);
}

void OtaSlave::processStart(const OtaStartPayload& p) {
    uint8_t ackStatus;
    if (_state == RECEIVING && p.sessionId == _sessionId) {
        // Retransmission dupliquée du START (ack précédent perdu) : re-ack
        // idempotent, surtout ne pas relancer Update.begin() en double.
        ackStatus = OTA_OK;
    } else {
        if (_state == RECEIVING) Update.abort();

        if (!Update.begin(p.totalSize)) {
            Serial.printf("OTA: Update.begin(%lu) refuse (err %u)\n",
                          (unsigned long)p.totalSize, Update.getError());
            ackStatus = OTA_ERR_SIZE;
        } else {
            Serial.printf("OTA: session %u demarree (%lu o, %u chunks)\n",
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
        Serial.printf("OTA: chunk %u rejete (etat=%d, session %u vs %u)\n",
                      p.chunkIndex, (int)_state, p.sessionId, _sessionId);
        ackStatus = OTA_ERR_SESSION;
    } else {
        _lastActivityMs = millis();

        if (p.chunkIndex == _expectedChunkIndex) {
            if (Update.write(const_cast<uint8_t*>(p.data), p.dataLen) != p.dataLen) {
                Serial.printf("OTA: echec ecriture chunk %u (err %u)\n",
                              p.chunkIndex, Update.getError());
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
            Serial.printf("OTA: chunk %u hors sequence (attendu %u)\n",
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
        Serial.printf("OTA: END rejete (etat=%d, session %u vs %u, %u/%u chunks)\n",
                      (int)_state, p.sessionId, _sessionId, _expectedChunkIndex, p.totalChunks);
        ackStatus = OTA_ERR_SESSION;
    } else if (!Update.end(true)) {
        // Intégrité/format invalide : on reste sur l'image actuelle, aucun reboot.
        Serial.printf("OTA: Update.end refuse (err %u)\n", Update.getError());
        ackStatus = OTA_ERR_MD5;
        _state = IDLE;
    } else {
        Guard.armPendingReboot();
        ackStatus = OTA_OK;
        doRestart = true;
    }
    sendAck(2, 0, ackStatus);
    if (doRestart) {
        delay(250); // laisse partir la trame d'ack avant de couper la radio
        ESP.restart();
    }
}

void OtaSlave::processAbort(const OtaAbortPayload& p) {
    if (_state != RECEIVING || p.sessionId != _sessionId) return;
    Serial.printf("OTA: abort recu (raison %u) au chunk %u\n", p.reason, _expectedChunkIndex);
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

    // Comparaison SIGNEE obligatoire : _lastActivityMs vient d'un millis() frais
    // pris pendant le traitement ci-dessus, donc potentiellement POSTERIEUR au
    // nowMs capture en debut de loop() — en non signe, la difference negative
    // deborde (~4 milliards) et le timeout de 20 s saute instantanement
    // (observe en test : session abandonnee 5 ms apres son demarrage).
    if (_state == RECEIVING && (int32_t)(nowMs - _lastActivityMs) > (int32_t)OTA_SESSION_TIMEOUT_MS) {
        Serial.printf("OTA: inactivite, session %u abandonnee au chunk %u\n",
                      _sessionId, _expectedChunkIndex);
        Update.abort();
        _state = IDLE;
    }
}
#endif
