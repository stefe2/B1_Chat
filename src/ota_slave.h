#pragma once

// ============================================================================
//  OtaSlave — réception d'une image OTA relayée par le mesh (esclave uniquement)
//
//  Machine à états minimale : un seul transfert à la fois, réception
//  strictement séquentielle (Update.write() est append-only, aucune reprise
//  de désordre n'est tentée). Voir CLAUDE.md pour le protocole complet.
// ============================================================================

#include "config.h"

#if !IS_MASTER
#include "mesh_comm.h"

class OtaSlave {
public:
    void onStart(uint16_t srcId, const OtaStartPayload& p);
    void onChunk(uint16_t srcId, const OtaChunkPayload& p);
    void onEnd(uint16_t srcId, const OtaEndPayload& p);
    void onAbort(uint16_t srcId, const OtaAbortPayload& p);

    // Auto-abandon si plus aucune activité pendant OTA_SESSION_TIMEOUT_MS
    // (filet de sécurité si un MSG_OTA_ABORT est perdu).
    void update(uint32_t nowMs);

private:
    enum State { IDLE, RECEIVING } _state = IDLE;
    uint8_t  _sessionId = 0;
    uint16_t _expectedChunkIndex = 0;
    uint16_t _totalChunks = 0;
    uint32_t _lastActivityMs = 0;

    // Protege l'etat ci-dessus : onStart/onChunk/onEnd/onAbort s'executent depuis le
    // callback ESP-NOW (tache Wi-Fi interne) alors que update() (timeout d'inactivite)
    // s'execute depuis loop() — meme risque de course qu'OtaMaster, voir ota_master.h.
    portMUX_TYPE _mux = portMUX_INITIALIZER_UNLOCKED;

    void sendAck(uint8_t kind, uint16_t chunkIndex, uint8_t status);
};

extern OtaSlave OtaS;
#endif
