#pragma once

// ============================================================================
//  OtaMaster — orchestration d'une session OTA vers UN esclave (maître uniquement)
//
//  Reçoit les chunks décodés (base64 -> binaire) depuis serial_console.cpp,
//  les relaie sur le mesh en stop-and-wait (un chunk en vol, ACK requis avant
//  le suivant — Update.write() côté esclave est append-only, la reprise de
//  désordre serait de la complexité inutile). Une seule session à la fois.
//
//  Ne dépend PAS de serial_console.h : les événements à pousser vers la
//  console sont récupérés via pollEvent() et traduits en JSON par main.cpp,
//  comme le reste du firmware (seul SerialConsole parle JSON/Serial).
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

    // Démarre une session. false = occupé, cible inconnue ou taille invalide
    // (l'appelant doit alors pousser otaError lui-même — voir serial_console.cpp).
    bool begin(uint16_t target, uint32_t size, const char* md5Hex32);

    // Chunk décodé (depuis base64) reçu du série pour la session en cours.
    // Ignoré si `index` ne correspond pas au chunk attendu ou hors session.
    void onSerialChunk(uint16_t index, const uint8_t* data, uint8_t len);

    // Annulation utilisateur.
    void abort();

    // Accusé mesh reçu (appelé depuis onMeshMessage, type MSG_OTA_ACK).
    void onAck(uint16_t srcId, const OtaAckPayload& p);

    // Tick timeout/retry + surveillance post-reboot (appelé depuis loop()).
    void update(uint32_t nowMs);

    bool busy() const { return _state != OM_IDLE; }

    // Consomme l'événement en attente (type EV_NONE si rien de nouveau).
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
    // Version rapportée par la cible AVANT l'OTA (capturée dans begin()) : la
    // console ne peut pas connaître de façon fiable la version intégrée dans
    // un .bin arbitraire, donc la confirmation post-reboot compare "a changé
    // par rapport à avant" plutôt que "correspond à ce qui était annoncé".
    uint8_t  _prevFwMajor = 0, _prevFwMinor = 0, _prevFwPatch = 0;

    uint8_t  _lastSentType = 0;
    uint8_t  _lastSentBuf[sizeof(OtaChunkPayload)];
    uint8_t  _lastSentLen = 0;

    Event _pending;

    void sendFrame(uint8_t type, const void* payload, uint8_t len);
    void resend();
    void fail(const char* reason);
};

extern OtaMaster OtaM;
#endif
