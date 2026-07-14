#pragma once

// ============================================================================
//  OtaSlave — réception d'une image OTA relayée par le mesh (esclave uniquement)
//
//  Machine à états minimale : un seul transfert à la fois, réception
//  strictement séquentielle (Update.write() est append-only, aucune reprise
//  de désordre n'est tentée). Voir CLAUDE.md pour le protocole complet.
//
//  Architecture callback -> loop() : les on*() s'exécutent depuis le callback
//  ESP-NOW (tâche Wi-Fi interne) et ne touchent JAMAIS ni la flash ni la
//  machine à états — ils déposent le message brut dans une boîte aux lettres
//  d'un seul emplacement, que update() (loop()) dépile, valide et traite.
//  Raison : Update.begin/write/end font de vrais accès SPI flash (effacement
//  de secteur ~tous les 21 chunks de 190 o, MD5 sur toute l'image au end) —
//  interdits depuis la tâche Wi-Fi et a fortiori sous portENTER_CRITICAL
//  (gel/panic observé systématiquement au chunk 21, premier débordement du
//  tampon de secteur 4 Ko d'Update).
// ============================================================================

#include "config.h"

#if !IS_MASTER
#include "mesh_comm.h"

class OtaSlave {
public:
    // Appelés depuis le callback ESP-NOW : filtrage targetId + dépôt en boîte
    // aux lettres uniquement (aucun accès flash, aucun état muté).
    void onStart(uint16_t srcId, const OtaStartPayload& p);
    void onChunk(uint16_t srcId, const OtaChunkPayload& p);
    void onEnd(uint16_t srcId, const OtaEndPayload& p);
    void onAbort(uint16_t srcId, const OtaAbortPayload& p);

    // Dépile la boîte aux lettres (validation + Update.* + ack, hors verrou)
    // puis gère l'auto-abandon si plus aucune activité pendant
    // OTA_SESSION_TIMEOUT_MS (filet si un MSG_OTA_ABORT est perdu).
    // Appelé depuis loop().
    void update(uint32_t nowMs);

private:
    enum State { IDLE, RECEIVING } _state = IDLE;
    uint8_t  _sessionId = 0;
    uint16_t _expectedChunkIndex = 0;
    uint16_t _totalChunks = 0;
    uint32_t _lastActivityMs = 0;

    // Boîte aux lettres callback -> loop(). Un seul emplacement suffit
    // (stop-and-wait : un message en vol) ; un nouveau dépôt écrase l'ancien —
    // perdre un doublon est sans effet (le maître retransmet), et garder le
    // plus récent garantit qu'un ABORT n'est jamais coincé derrière un CHUNK.
    enum PendingType : uint8_t { PEND_NONE, PEND_START, PEND_CHUNK, PEND_END, PEND_ABORT };
    PendingType _pendingType = PEND_NONE;
    uint8_t _pendingBuf[sizeof(OtaChunkPayload)];   // dimensionné sur le plus gros payload

    // Protège uniquement la boîte aux lettres : le reste de l'état ci-dessus
    // n'est plus touché que par loop(), donc plus de course possible dessus.
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
