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

    // Émet un message (signé) en broadcast avec le TTL par défaut.
    bool send(uint8_t type, const void* payload, uint8_t len);

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
