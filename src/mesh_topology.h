#pragma once

// ============================================================================
//  MeshTopology — agrégateur d'arêtes du graphe de voisinage radio (maître)
//
//  Alimenté par les rapports MSG_NEIGHBORS reçus de chaque droïde (voir
//  mesh_comm.h) et par le propre voisinage direct du maître. Chaque arête est
//  dirigée : {from} a entendu {to} directement, à {rssi}. Voir project.md §5.
// ============================================================================

#include <Arduino.h>

class MeshTopology {
public:
    static const uint8_t MAX_EDGES = 64;

    struct Edge { uint16_t from, to; int8_t rssi; uint32_t lastSeen; };

    // Enregistre/actualise le lien dirigé from→to (from a entendu to à rssi).
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
