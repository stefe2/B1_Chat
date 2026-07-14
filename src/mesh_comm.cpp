#include "mesh_comm.h"
#include "config.h"

#include <WiFi.h>
#include <esp_now.h>
#include <esp_wifi.h>
#include <esp_mac.h>
#include "mbedtls/md.h"
#include "mbedtls/sha256.h"

MeshComm Mesh;
MeshComm* MeshComm::_instance = nullptr;

namespace {
const uint8_t BROADCAST_ADDR[6] = {0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF};
const uint8_t HMAC_LEN = 8;                       // truncated signature
const uint8_t HDR_LEN = sizeof(MeshHeader);       // 6 bytes
const uint8_t MAX_PAYLOAD = 200;
const uint8_t TTL_OFFSET = 4;                     // position of the ttl field
}  // namespace

// --- Static ESP-NOW callback (core 3.x) ----------------------------------
static void espnowRecvCb(const esp_now_recv_info_t* info, const uint8_t* data, int len) {
    int rssi = 0;
    if (info && info->rx_ctrl) rssi = info->rx_ctrl->rssi;
    Mesh.handleRaw(info ? info->src_addr : nullptr, data, len, rssi);
}

void MeshComm::deriveKey(const char* password, uint8_t out32[32]) {
    mbedtls_sha256((const unsigned char*)password, strlen(password), out32, 0);
}

uint16_t MeshComm::idFromMac(const uint8_t* mac) {
    return mac ? (((uint16_t)mac[4] << 8) | mac[5]) : 0;
}

void MeshComm::recordNeighbor(uint16_t id, int rssi, uint32_t now) {
    for (uint8_t i = 0; i < _neighborCount; i++) {
        if (_neighbors[i].id == id) {
            _neighbors[i].rssi = (int8_t)rssi;
            _neighbors[i].lastSeenMs = now;
            return;
        }
    }
    if (_neighborCount < MAX_NEIGHBORS) {
        _neighbors[_neighborCount++] = {id, (int8_t)rssi, now};
        return;
    }
    // Table full: reuse the stalest neighbor rather than silently dropping
    // a new measurement.
    uint8_t oldest = 0;
    for (uint8_t i = 1; i < MAX_NEIGHBORS; i++)
        if (_neighbors[i].lastSeenMs < _neighbors[oldest].lastSeenMs) oldest = i;
    _neighbors[oldest] = {id, (int8_t)rssi, now};
}

uint8_t MeshComm::copyNeighbors(NeighborEntry* out, uint8_t maxOut, uint32_t staleMs) const {
    const uint32_t now = millis();
    uint8_t n = 0;
    for (uint8_t i = 0; i < _neighborCount && n < maxOut; i++) {
        if (now - _neighbors[i].lastSeenMs < staleMs) {
            out[n].id = _neighbors[i].id;
            out[n].rssi = _neighbors[i].rssi;
            n++;
        }
    }
    return n;
}

bool MeshComm::begin(const char* groupPassword) {
    _instance = this;
    deriveKey(groupPassword, _key);

    // Derives the identity from the MAC (last 2 bytes).
    uint8_t mac[6];
    WiFi.mode(WIFI_STA);
    WiFi.disconnect();
    esp_read_mac(mac, ESP_MAC_WIFI_STA);
    _myId = idFromMac(mac);

    // Fixes the radio channel shared by the group.
    esp_wifi_set_promiscuous(true);
    esp_wifi_set_channel(MESH_WIFI_CHANNEL, WIFI_SECOND_CHAN_NONE);
    esp_wifi_set_promiscuous(false);

    if (esp_now_init() != ESP_OK) return false;
    esp_now_register_recv_cb(espnowRecvCb);

    // Adds the broadcast peer.
    esp_now_peer_info_t peer = {};
    memcpy(peer.peer_addr, BROADCAST_ADDR, 6);
    peer.channel = MESH_WIFI_CHANNEL;
    peer.ifidx = WIFI_IF_STA;
    peer.encrypt = false;
    if (esp_now_add_peer(&peer) != ESP_OK) return false;

    return true;
}

void MeshComm::computeHmac(const uint8_t* frame, uint8_t frameLen, uint8_t out8[8]) {
    // Working copy with ttl neutralized (excluded from the signature).
    uint8_t tmp[HDR_LEN + MAX_PAYLOAD];
    memcpy(tmp, frame, frameLen);
    tmp[TTL_OFFSET] = 0;

    uint8_t full[32];
    const mbedtls_md_info_t* md = mbedtls_md_info_from_type(MBEDTLS_MD_SHA256);
    mbedtls_md_context_t ctx;
    mbedtls_md_init(&ctx);
    mbedtls_md_setup(&ctx, md, 1);
    mbedtls_md_hmac_starts(&ctx, _key, 32);
    mbedtls_md_hmac_update(&ctx, tmp, frameLen);
    mbedtls_md_hmac_finish(&ctx, full);
    mbedtls_md_free(&ctx);

    memcpy(out8, full, HMAC_LEN);
}

bool MeshComm::verify(const uint8_t* frame, uint8_t frameLen) {
    if (frameLen < HDR_LEN + HMAC_LEN) return false;
    const uint8_t signedLen = frameLen - HMAC_LEN;
    uint8_t expected[HMAC_LEN];
    computeHmac(frame, signedLen, expected);
    // Constant-time comparison.
    uint8_t diff = 0;
    const uint8_t* got = frame + signedLen;
    for (uint8_t i = 0; i < HMAC_LEN; i++) diff |= expected[i] ^ got[i];
    return diff == 0;
}

bool MeshComm::rawBroadcast(const uint8_t* frame, uint8_t frameLen) {
    return esp_now_send(BROADCAST_ADDR, frame, frameLen) == ESP_OK;
}

bool MeshComm::send(uint8_t type, const void* payload, uint8_t len, uint8_t ttl) {
    if (len > MAX_PAYLOAD) return false;

    uint8_t frame[HDR_LEN + MAX_PAYLOAD + HMAC_LEN];
    MeshHeader hdr;
    hdr.srcId = _myId;
    hdr.seq = ++_seq;
    hdr.ttl = ttl;
    hdr.type = type;

    memcpy(frame, &hdr, HDR_LEN);
    if (len && payload) memcpy(frame + HDR_LEN, payload, len);

    const uint8_t signedLen = HDR_LEN + len;
    computeHmac(frame, signedLen, frame + signedLen);

    // Our own message must not be re-processed if it comes back to us.
    remember(_myId, hdr.seq);

    return rawBroadcast(frame, signedLen + HMAC_LEN);
}

bool MeshComm::alreadySeen(uint16_t srcId, uint16_t seq) {
    const uint32_t key = ((uint32_t)srcId << 16) | seq;
    for (uint8_t i = 0; i < 32; i++) {
        if (_seen[i] == key) return true;
    }
    return false;
}

void MeshComm::remember(uint16_t srcId, uint16_t seq) {
    const uint32_t key = ((uint32_t)srcId << 16) | seq;
    _seen[_seenIdx] = key;
    _seenIdx = (_seenIdx + 1) % 32;
}

void MeshComm::handleRaw(const uint8_t* mac, const uint8_t* data, int len, int rssi) {
    if (len < HDR_LEN + HMAC_LEN || len > HDR_LEN + MAX_PAYLOAD + HMAC_LEN) return;

    // 1) Authentication: rejects other groups / tampered messages.
    if (!verify(data, (uint8_t)len)) return;

    MeshHeader hdr;
    memcpy(&hdr, data, HDR_LEN);

    // 1.5) Topology: whoever physically transmitted this frame to us (mac)
    // is a direct radio neighbor — independent of the application-level
    // srcId and of the dedup below. Must run HERE, before the
    // srcId==_myId early-return and before dedup, because:
    //  - a relayed echo of our own original message (hdr.srcId==_myId)
    //    still proves we directly hear the relay;
    //  - a duplicate/relayed copy of an already-seen (srcId,seq) still
    //    refreshes the RSSI measurement for THIS specific radio link.
    const uint16_t neighborId = idFromMac(mac);
    if (neighborId != 0 && neighborId != _myId) {
        recordNeighbor(neighborId, rssi, millis());
    }

    // 2) Ignores our own messages and duplicates (anti-loop).
    if (hdr.srcId == _myId) return;
    if (alreadySeen(hdr.srcId, hdr.seq)) return;
    remember(hdr.srcId, hdr.seq);

    const uint8_t payloadLen = (uint8_t)len - HDR_LEN - HMAC_LEN;
    const uint8_t* payload = data + HDR_LEN;

    // 3) Passes it up to the application.
    if (_handler) _handler(hdr.type, payload, payloadLen, hdr.srcId, rssi);

    // 4) Multi-hop relay: decrements the TTL and re-broadcasts (the HMAC
    //    excludes the TTL, so the signature stays valid).
    if (hdr.ttl > 0) {
        uint8_t relay[HDR_LEN + MAX_PAYLOAD + HMAC_LEN];
        memcpy(relay, data, len);
        relay[TTL_OFFSET] = hdr.ttl - 1;
        rawBroadcast(relay, (uint8_t)len);
    }
}
