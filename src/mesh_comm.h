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
    MSG_ANIM_EXEC = 17,  // droid -> master: tracked animation lifecycle report
    MSG_ANIM_LEASED = 18, // safe fail-closed infinite animation with an initial lease
    MSG_ANIM_LEASE_RENEW = 19, // renews only the matching active leased animation
};

// Lifecycle phases reported for console-originated animation commands.
enum AnimExecPhase : uint8_t {
    ANIM_EXEC_STARTED     = 1,
    ANIM_EXEC_COMPLETED   = 2,
    ANIM_EXEC_INTERRUPTED = 3,
    ANIM_EXEC_REJECTED    = 4,
};

enum AnimExecReason : uint8_t {
    ANIM_EXEC_REASON_NONE       = 0,
    ANIM_EXEC_REASON_SERVOS_OFF = 1,
    ANIM_EXEC_REASON_LEASE_EXPIRED = 2,
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

// Maximum application payload carried by one authenticated ESP-NOW frame.
// Public because main.cpp's callback-to-loop inbox must reserve enough room
// to copy any message without processing it on the Wi-Fi task.
static const uint8_t MESH_MAX_PAYLOAD = 200;

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
    uint16_t syncDelayMs;  // lower 15 bits: delay; high bit: execution report requested
    uint32_t seed;         // random variation seed
};

static const uint16_t ANIM_EXEC_TRACKED_FLAG = 0x8000;
static const uint16_t ANIM_SYNC_DELAY_MASK = 0x7FFF;
static const uint16_t ANIM_LEASE_MIN_MS = 1000;
static const uint16_t ANIM_LEASE_MAX_MS = 30000;

// Separate type rather than changing AnimPayload: old nodes safely ignore the
// unknown command instead of starting an infinite gesture without its lease.
struct LeasedAnimPayload {
    uint16_t targetId;
    uint8_t  animId;      // POWER_DOWN or TALK only
    uint16_t leaseMs;
    uint32_t seed;
};

struct AnimLeaseRenewPayload {
    uint16_t targetId;
    uint16_t originSeq;   // sequence of MSG_ANIM_LEASED; rejects stale renewals
    uint16_t leaseMs;
};

// A droid echoes the originating MSG_ANIM header sequence. The master maps
// that wire-level correlation back to the console's requestId without
// changing AnimPayload, so pre-report firmware remains able to execute it.
struct AnimExecPayload {
    uint16_t originSeq;
    uint8_t  animId;
    uint8_t  phase;       // AnimExecPhase
    uint8_t  reason;      // AnimExecReason
    uint32_t atMs;        // reporting droid's local uptime
};

static_assert(sizeof(LeasedAnimPayload) == 9,
              "Leased animation wire format must remain exactly 9 bytes");
static_assert(sizeof(AnimLeaseRenewPayload) == 6,
              "Animation lease renewal wire format must remain exactly 6 bytes");

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
    uint32_t buildId;    // content-derived firmware identity (FW_BUILD_ID)
};

// Accepted by a new master while rolling out Build IDs to an older fleet.
// Keep this exact layout: pre-Build-ID nodes send these 8-byte heartbeats.
struct LegacyHeartbeatPayload {
    uint32_t uptimeMs;
    uint8_t  state;
    uint8_t  fwMajor;
    uint8_t  fwMinor;
    uint8_t  fwPatch;
};

static_assert(sizeof(LegacyHeartbeatPayload) == 8,
              "Legacy heartbeat wire format must remain exactly 8 bytes");
static_assert(sizeof(HeartbeatPayload) == 12,
              "Build-ID heartbeat wire format must remain exactly 12 bytes");

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

// Payload data size per OTA fragment (margin under MESH_MAX_PAYLOAD).
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

// Callback invoked on the ESP-NOW Wi-Fi task for every valid, non-duplicate
// message. It must return quickly and must not perform flash or actuator work.
typedef void (*MeshReceiveHandler)(uint8_t type, const uint8_t* payload,
                                   uint8_t len, uint16_t srcId, uint16_t seq,
                                   int rssi);

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
    bool send(uint8_t type, const void* payload, uint8_t len,
              uint8_t ttl = MESH_TTL, uint16_t* outSeq = nullptr);

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

    // send()/copyNeighbors() run from loop(), while handleRaw() runs on the
    // ESP-NOW Wi-Fi task. Protects _seq, _seen*, and _neighbors* only; HMAC
    // calculation and esp_now_send() deliberately stay outside the lock.
    mutable portMUX_TYPE _mux = portMUX_INITIALIZER_UNLOCKED;

    // The helpers below are called only while _mux is held.
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
