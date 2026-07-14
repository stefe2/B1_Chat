#pragma once

// ============================================================================
//  MeshTopology — aggregates edges of the radio neighborhood graph (master)
//
//  Fed by the MSG_NEIGHBORS reports received from each droid (see
//  mesh_comm.h) and by the master's own direct neighborhood. Each edge is
//  directed: {from} heard {to} directly, at {rssi}. See project.md §5.
// ============================================================================

#include <Arduino.h>

class MeshTopology {
public:
    static const uint8_t MAX_EDGES = 64;

    struct Edge { uint16_t from, to; int8_t rssi; uint32_t lastSeen; };

    // Registers/refreshes the directed link from→to (from heard to at rssi).
    void seen(uint16_t from, uint16_t to, int8_t rssi, uint32_t now);

    uint8_t count() const { return _count; }
    const Edge& at(uint8_t i) const { return _e[i]; }
    bool fresh(uint8_t i, uint32_t now, uint32_t staleMs) const {
        return (now - _e[i].lastSeen) < staleMs;
    }

private:
    Edge    _e[MAX_EDGES];
    uint8_t _count = 0;
};

extern MeshTopology MeshTopo;
