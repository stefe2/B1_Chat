#pragma once

// ============================================================================
//  MeshComm — ESP-NOW mesh multi-sauts avec authentification HMAC
//
//  - Transport : broadcast ESP-NOW (canal fixe), aucun appairage requis.
//  - Identité  : srcId 16 bits dérivé de la MAC (unique par carte).
//  - Relais    : en-tête {srcId, seq, ttl, type} ; dédup (srcId,seq) ; TTL.
//  - Sécurité  : chaque trame signée HMAC-SHA256 (tronqué) avec la clé de
//                groupe ; les messages d'un autre groupe / falsifiés sont
//                rejetés. Le TTL est exclu de la signature (muté au relais).
//  Voir project.md (sections 5 et 9).
// ============================================================================

#include <Arduino.h>
#include "config.h"

// ------- Types de messages (champ `type` de l'en-tête) ---------------------
enum MeshMsgType : uint8_t {
    MSG_ANIM      = 1,
    MSG_CONFIG    = 2,
    MSG_HEARTBEAT = 4,
    MSG_SERVO     = 5,
    MSG_CALIB     = 6,
    MSG_PREVIEW   = 7,
    MSG_AUTOANIM  = 8,
    MSG_NEIGHBORS = 9,
    MSG_OTA_START = 10,  // maître -> esclave ciblé : démarre une session OTA
    MSG_OTA_CHUNK = 11,  // maître -> esclave ciblé : un fragment de l'image
    MSG_OTA_ACK   = 12,  // esclave -> maître : accusé (start/chunk/end)
    MSG_OTA_END   = 13,  // maître -> esclave ciblé : fin de transfert, finalise
    MSG_OTA_ABORT = 14,  // maître -> esclave ciblé : annule la session en cours
};

// Codes de statut/raison des messages OTA (OtaAckPayload.status, OtaAbortPayload.reason).
enum OtaStatus : uint8_t {
    OTA_OK           = 0,
    OTA_ERR_SESSION  = 1,  // sessionId/chunkIndex inattendu (désynchronisé)
    OTA_ERR_WRITE    = 2,  // Update.write() a échoué (fatal, pas de retry)
    OTA_ERR_SIZE     = 3,  // Update.begin() a échoué (taille/espace)
    OTA_ERR_MD5      = 4,  // Update.end() a échoué (intégrité/format)
    OTA_ERR_BUSY     = 5,  // une autre session OTA est déjà en cours
    OTA_ABORT_USER   = 10, // annulation demandée depuis la console
    OTA_ABORT_TIMEOUT = 11, // inactivité détectée (série ou mesh)
};

// Adresse « tous les droïdes » pour les charges utiles ciblées.
static const uint16_t MESH_TARGET_ALL = 0xFFFF;

#pragma pack(push, 1)
struct MeshHeader {
    uint16_t srcId;   // droïde émetteur d'origine (dérivé de la MAC)
    uint16_t seq;     // compteur incrémental par nœud
    uint8_t  ttl;     // sauts restants (exclu de la signature)
    uint8_t  type;    // MeshMsgType
};

// Charges utiles applicatives.
struct AnimPayload {
    uint16_t targetId;     // MESH_TARGET_ALL ou un srcId précis
    uint8_t  animId;
    uint16_t syncDelayMs;  // délai avant exécution (sync/décalage)
    uint32_t seed;         // graine de variation aléatoire
};

struct ConfigPayload {
    uint16_t targetId;
    float    freq;
    float    amplitude;
    float    speed;
};

struct HeartbeatPayload {
    uint32_t uptimeMs;
    uint8_t  state;      // bit0 = servos actifs, bit1 = anims auto
    uint8_t  fwMajor;
    uint8_t  fwMinor;
    uint8_t  fwPatch;
};

struct ServoPayload {
    uint16_t targetId;   // MESH_TARGET_ALL ou un srcId précis
    uint8_t  enabled;    // 1 = servos actifs, 0 = coupés
};

// Pause/reprise de l'animation spontanée au repos (n'affecte pas Jouer/Séquenceur).
struct AutoAnimPayload {
    uint16_t targetId;   // MESH_TARGET_ALL ou un srcId précis
    uint8_t  enabled;    // 1 = anims auto actives, 0 = en pause
};

// Bornes mécaniques (degrés) persistées par le droïde ciblé.
struct CalibPayload {
    uint16_t targetId;
    uint8_t  panMin, panCenter, panMax;
    uint8_t  tiltMin, tiltCenter, tiltMax;
};

// Positionnement transitoire (aperçu), non persisté.
struct PreviewPayload {
    uint16_t targetId;
    uint8_t  pan;
    uint8_t  tilt;
};

// Un voisin radio direct entendu par l'émetteur de CE rapport.
struct NeighborEntry {
    uint16_t id;     // srcId du voisin entendu directement
    int8_t   rssi;   // RSSI mesuré par l'émetteur du rapport (pas par un relais)
};

// Rapport périodique de voisinage direct (topologie). Diffusé par le maître
// ET les esclaves. hdr.srcId identifie qui a mesuré ces RSSI, même si ce
// message est ensuite relayé par d'autres nœuds pour atteindre le maître —
// seul le TRANSPORT du rapport est multi-sauts, les mesures qu'il contient
// restent des mesures directes de l'émetteur d'origine.
struct NeighborReportPayload {
    uint8_t       count;
    NeighborEntry entries[MAX_NEIGHBORS];
};

// Taille de donnée utile par fragment OTA (marge sous MAX_PAYLOAD=200 de mesh_comm.cpp).
static const uint8_t OTA_CHUNK_DATA_MAX = 190;

// Démarre une session OTA vers `targetId` (jamais MESH_TARGET_ALL). `md5Hex`
// est le MD5 de l'image complète, hex minuscule, SANS terminateur nul.
struct OtaStartPayload {
    uint16_t targetId;
    uint8_t  sessionId;    // identifie cette tentative (rejette les acks d'une session périmée)
    uint32_t totalSize;
    uint16_t totalChunks;
    uint8_t  chunkSize;    // == OTA_CHUNK_DATA_MAX, annoncé pour que la console ne le code pas en dur
    char     md5Hex[32];
};

// Un fragment de l'image. Toujours envoyé à taille pleine (fin paddée,
// ignorée via dataLen) pour garder la convention "len == sizeof(struct)".
struct OtaChunkPayload {
    uint16_t targetId;
    uint8_t  sessionId;
    uint16_t chunkIndex;   // 0-based, séquentiel STRICT (Update.write() est append-only)
    uint8_t  dataLen;
    uint8_t  data[OTA_CHUNK_DATA_MAX];
};

// Fin de transfert : l'esclave finalise (Update.end()) si tous les chunks attendus sont reçus.
struct OtaEndPayload {
    uint16_t targetId;
    uint8_t  sessionId;
    uint16_t totalChunks;
};

// Accusé esclave -> maître (start/chunk/end). hdr.srcId du message identifie déjà qui ack.
struct OtaAckPayload {
    uint8_t  sessionId;
    uint8_t  kind;         // 0=START, 1=CHUNK, 2=END
    uint16_t chunkIndex;   // valide seulement si kind==CHUNK
    uint8_t  status;       // OtaStatus
};

// Annule une session en cours (utilisateur ou timeout côté maître).
struct OtaAbortPayload {
    uint16_t targetId;
    uint8_t  sessionId;
    uint8_t  reason;       // OtaStatus (OTA_ABORT_*)
};
#pragma pack(pop)

// Callback appelé pour chaque message valide et non dupliqué.
typedef void (*MeshReceiveHandler)(uint8_t type, const uint8_t* payload,
                                   uint8_t len, uint16_t srcId, int rssi);

class MeshComm {
public:
    // Initialise le WiFi/ESP-NOW et dérive l'identité. `groupPassword` = clé
    // par défaut (généralement GROUP_KEY). Retourne false en cas d'échec.
    bool begin(const char* groupPassword);

    // Enregistre le gestionnaire de réception.
    void onReceive(MeshReceiveHandler handler) { _handler = handler; }

    // Émet un message (signé) en broadcast. `ttl` par défaut = MESH_TTL ;
    // les envois OTA utilisent un TTL réduit (OTA_MESH_TTL) pour éviter que
    // les ~5000 fragments d'un transfert soient re-relayés par tous les nœuds.
    bool send(uint8_t type, const void* payload, uint8_t len, uint8_t ttl = MESH_TTL);

    // Dérive une clé HMAC (SHA256) à partir d'un mot de passe.
    static void deriveKey(const char* password, uint8_t out32[32]);

    uint16_t myId() const { return _myId; }

    // À appeler depuis le callback statique ESP-NOW (usage interne).
    void handleRaw(const uint8_t* mac, const uint8_t* data, int len, int rssi);

    // Copie jusqu'à `maxOut` voisins radio directs "frais" (< staleMs) dans
    // `out`. Retourne le nombre copié. Sert à construire le rapport périodique
    // de voisinage (topologie), voir project.md §5.
    uint8_t copyNeighbors(NeighborEntry* out, uint8_t maxOut, uint32_t staleMs) const;

private:
    static MeshComm* _instance;

    MeshReceiveHandler _handler = nullptr;
    uint16_t _myId = 0;
    uint16_t _seq = 0;
    uint8_t  _key[32];

    // Cache anti-doublon : clés (srcId<<16 | seq).
    uint32_t _seen[32];
    uint8_t  _seenIdx = 0;

    // Voisinage radio direct (indépendant des relais applicatifs) : qui nous
    // a physiquement transmis une trame authentifiée, et à quel RSSI.
    struct Neighbor { uint16_t id; int8_t rssi; uint32_t lastSeenMs; };
    Neighbor _neighbors[MAX_NEIGHBORS];
    uint8_t  _neighborCount = 0;
    void recordNeighbor(uint16_t id, int rssi, uint32_t now);
    static uint16_t idFromMac(const uint8_t* mac);

    bool alreadySeen(uint16_t srcId, uint16_t seq);
    void remember(uint16_t srcId, uint16_t seq);

    // HMAC-SHA256 tronqué (8 octets) sur (en-tête ttl=0 + payload).
    void computeHmac(const uint8_t* frame, uint8_t frameLen, uint8_t out8[8]);
    bool verify(const uint8_t* frame, uint8_t frameLen);

    bool rawBroadcast(const uint8_t* frame, uint8_t frameLen);
};

extern MeshComm Mesh;
