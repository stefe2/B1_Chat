#pragma once

// ============================================================================
//  MeshComm — multi-hop ESP-NOW mesh with HMAC authentication
//
//  - Transport : ESP-NOW broadcast (fixed channel), no pairing required.
//  - Identity  : 16-bit srcId derived from the MAC (unique per board).
//  - Relay     : header {srcId, seq, ttl, type}; dedup (srcId,seq); TTL.
//  - Security  : every frame is HMAC-SHA256-signed (truncated) with the group
//                key; messages from another group / tampered ones are
//                rejected. The TTL is excluded from the signature (mutated on relay).
//  See project.md (sections 5 and 9).
// ============================================================================

#include <Arduino.h>
#include "config.h"

// ------- Message types (`type` field of the header) ---------------------
enum MeshMsgType : uint8_t {
    MSG_ANIM      = 1,
    MSG_CONFIG    = 2,
    MSG_HEARTBEAT = 4,
    MSG_SERVO     = 5,
    MSG_CALIB     = 6,
    MSG_PREVIEW   = 7,
    MSG_AUTOANIM  = 8,
    MSG_NEIGHBORS = 9,
    MSG_OTA_START = 10,  // master -> targeted slave: starts an OTA session
    MSG_OTA_CHUNK = 11,  // master -> targeted slave: one fragment of the image
    MSG_OTA_ACK   = 12,  // slave -> master: acknowledgment (start/chunk/end)
    MSG_OTA_END   = 13,  // master -> targeted slave: end of transfer, finalizes
    MSG_OTA_ABORT = 14,  // master -> targeted slave: cancels the ongoing session
    MSG_LOCATE    = 15,  // toggles the targeted droid's onboard LED solid (physical "find me")
    MSG_NAME      = 16,  // persists the targeted droid's own name in its own NVS
};

// Status/reason codes for OTA messages (OtaAckPayload.status, OtaAbortPayload.reason).
enum OtaStatus : uint8_t {
    OTA_OK           = 0,
    OTA_ERR_SESSION  = 1,  // unexpected sessionId/chunkIndex (out of sync)
    OTA_ERR_WRITE    = 2,  // Update.write() failed (fatal, no retry)
    OTA_ERR_SIZE     = 3,  // Update.begin() failed (size/space)
    OTA_ERR_MD5      = 4,  // Update.end() failed (integrity/format)
    OTA_ERR_BUSY     = 5,  // another OTA session is already in progress
    OTA_ABORT_USER   = 10, // cancellation requested from the console
    OTA_ABORT_TIMEOUT = 11, // inactivity detected (serial or mesh)
};

// "All droids" address for targeted payloads.
static const uint16_t MESH_TARGET_ALL = 0xFFFF;

#pragma pack(push, 1)
struct MeshHeader {
    uint16_t srcId;   // originating droid (derived from the MAC)
    uint16_t seq;     // per-node incrementing counter
    uint8_t  ttl;     // hops remaining (excluded from the signature)
    uint8_t  type;    // MeshMsgType
};

// Application payloads.
struct AnimPayload {
    uint16_t targetId;     // MESH_TARGET_ALL or a specific srcId
    uint8_t  animId;
    uint16_t syncDelayMs;  // delay before execution (sync/offset)
    uint32_t seed;         // random variation seed
};

struct ConfigPayload {
    uint16_t targetId;
    float    freq;
    float    amplitude;
    float    speed;
};

struct HeartbeatPayload {
    uint32_t uptimeMs;
    uint8_t  state;      // bit0 = servos active, bit1 = auto anims
    uint8_t  fwMajor;
    uint8_t  fwMinor;
    uint8_t  fwPatch;
};

struct ServoPayload {
    uint16_t targetId;   // MESH_TARGET_ALL or a specific srcId
    uint8_t  enabled;    // 1 = servos active, 0 = off
};

// Pause/resume of the spontaneous idle animation (doesn't affect Play/Sequencer).
struct AutoAnimPayload {
    uint16_t targetId;   // MESH_TARGET_ALL or a specific srcId
    uint8_t  enabled;    // 1 = auto anims active, 0 = paused
};

// Overrides the onboard LED's normal execution-indicator blink with a solid
// on/off, so the droid can be found physically. Not persisted.
struct LocatePayload {
    uint16_t targetId;   // MESH_TARGET_ALL or a specific srcId
    uint8_t  enabled;    // 1 = LED solid on, 0 = resume the normal blink
};

// Persists the targeted droid's OWN name in its own NVS — mirrors MSG_CALIB
// (mesh-pushed, immediately/directly persisted on receipt, no commit/revert)
// so a droid keeps its name even if the master's own copy is ever lost or
// reset. Never MESH_TARGET_ALL (renaming every droid identically makes no
// sense). Zero-initialized + strncpy'd on the sender side so `name` is
// always NUL-terminated; the receiver re-enforces this defensively anyway.
struct NamePayload {
    uint16_t targetId;
    char     name[24];
};

// Mechanical limits (degrees) persisted by the targeted droid.
struct CalibPayload {
    uint16_t targetId;
    uint8_t  panMin, panCenter, panMax;
    uint8_t  tiltMin, tiltCenter, tiltMax;
};

// Transient positioning (preview), not persisted.
struct PreviewPayload {
    uint16_t targetId;
    uint8_t  pan;
    uint8_t  tilt;
};

// A direct radio neighbor heard by the sender of THIS report.
struct NeighborEntry {
    uint16_t id;     // srcId of the directly-heard neighbor
    int8_t   rssi;   // RSSI measured by the report's sender (not by a relay)
};

// Periodic direct-neighborhood report (topology). Broadcast by the master
// AND the slaves. hdr.srcId identifies who measured these RSSI values, even
// if this message is then relayed by other nodes to reach the master — only
// the report's TRANSPORT is multi-hop, the measurements it carries remain
// direct measurements from the original sender.
struct NeighborReportPayload {
    uint8_t       count;
    NeighborEntry entries[MAX_NEIGHBORS];
};

// Payload data size per OTA fragment (margin under mesh_comm.cpp's MAX_PAYLOAD=200).
static const uint8_t OTA_CHUNK_DATA_MAX = 190;

// Starts an OTA session toward `targetId` (never MESH_TARGET_ALL). `md5Hex`
// is the MD5 of the full image, lowercase hex, WITHOUT a null terminator.
struct OtaStartPayload {
    uint16_t targetId;
    uint8_t  sessionId;    // identifies this attempt (rejects acks from a stale session)
    uint32_t totalSize;
    uint16_t totalChunks;
    uint8_t  chunkSize;    // == OTA_CHUNK_DATA_MAX, announced so the console doesn't hardcode it
    char     md5Hex[32];
};

// One fragment of the image. Always sent at full size (padded end, ignored
// via dataLen) to keep the "len == sizeof(struct)" convention.
struct OtaChunkPayload {
    uint16_t targetId;
    uint8_t  sessionId;
    uint16_t chunkIndex;   // 0-based, STRICTLY sequential (Update.write() is append-only)
    uint8_t  dataLen;
    uint8_t  data[OTA_CHUNK_DATA_MAX];
};

// End of transfer: the slave finalizes (Update.end()) if all expected chunks were received.
struct OtaEndPayload {
    uint16_t targetId;
    uint8_t  sessionId;
    uint16_t totalChunks;
};

// Slave -> master acknowledgment (start/chunk/end). The message's hdr.srcId already identifies who's acking.
struct OtaAckPayload {
    uint8_t  sessionId;
    uint8_t  kind;         // 0=START, 1=CHUNK, 2=END
    uint16_t chunkIndex;   // only valid if kind==CHUNK
    uint8_t  status;       // OtaStatus
};

// Cancels an in-progress session (user or master-side timeout).
struct OtaAbortPayload {
    uint16_t targetId;
    uint8_t  sessionId;
    uint8_t  reason;       // OtaStatus (OTA_ABORT_*)
};
#pragma pack(pop)

// Callback invoked for every valid, non-duplicate message.
typedef void (*MeshReceiveHandler)(uint8_t type, const uint8_t* payload,
                                   uint8_t len, uint16_t srcId, int rssi);

class MeshComm {
public:
    // Initializes WiFi/ESP-NOW and derives the identity. `groupPassword` =
    // default key (usually GROUP_KEY). Returns false on failure.
    bool begin(const char* groupPassword);

    // Registers the receive handler.
    void onReceive(MeshReceiveHandler handler) { _handler = handler; }

    // Sends a (signed) message as broadcast. Default `ttl` = MESH_TTL;
    // OTA sends use a reduced TTL (OTA_MESH_TTL) so that a transfer's
    // ~5000 fragments aren't re-relayed by every node.
    bool send(uint8_t type, const void* payload, uint8_t len, uint8_t ttl = MESH_TTL);

    // Derives an HMAC key (SHA256) from a password.
    static void deriveKey(const char* password, uint8_t out32[32]);

    uint16_t myId() const { return _myId; }

    // To be called from the static ESP-NOW callback (internal use).
    void handleRaw(const uint8_t* mac, const uint8_t* data, int len, int rssi);

    // Copies up to `maxOut` "fresh" (< staleMs) direct radio neighbors into
    // `out`. Returns the number copied. Used to build the periodic
    // neighborhood report (topology), see project.md §5.
    uint8_t copyNeighbors(NeighborEntry* out, uint8_t maxOut, uint32_t staleMs) const;

private:
    static MeshComm* _instance;

    MeshReceiveHandler _handler = nullptr;
    uint16_t _myId = 0;
    uint16_t _seq = 0;
    uint8_t  _key[32];

    // Anti-duplicate cache: keys (srcId<<16 | seq).
    uint32_t _seen[32];
    uint8_t  _seenIdx = 0;

    // Direct radio neighborhood (independent of application-level relaying):
    // who physically transmitted an authenticated frame to us, and at what RSSI.
    struct Neighbor { uint16_t id; int8_t rssi; uint32_t lastSeenMs; };
    Neighbor _neighbors[MAX_NEIGHBORS];
    uint8_t  _neighborCount = 0;
    void recordNeighbor(uint16_t id, int rssi, uint32_t now);
    static uint16_t idFromMac(const uint8_t* mac);

    bool alreadySeen(uint16_t srcId, uint16_t seq);
    void remember(uint16_t srcId, uint16_t seq);

    // Truncated HMAC-SHA256 (8 bytes) over (header with ttl=0 + payload).
    void computeHmac(const uint8_t* frame, uint8_t frameLen, uint8_t out8[8]);
    bool verify(const uint8_t* frame, uint8_t frameLen);

    bool rawBroadcast(const uint8_t* frame, uint8_t frameLen);
};

extern MeshComm Mesh;
