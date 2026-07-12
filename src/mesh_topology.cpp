#include "mesh_topology.h"

MeshTopology MeshTopo;

void MeshTopology::seen(uint16_t from, uint16_t to, int8_t rssi, uint32_t now) {
    for (uint8_t i = 0; i < _count; i++) {
        if (_e[i].from == from && _e[i].to == to) {
            _e[i].rssi = rssi;
            _e[i].lastSeen = now;
            return;
        }
    }
    if (_count < MAX_EDGES) {
        _e[_count++] = {from, to, rssi, now};
        return;
    }
    // Table pleine : réutilise l'arête la plus périmée plutôt que de rejeter
    // silencieusement un nouveau lien (la topologie évolue dans le temps).
    uint8_t oldest = 0;
    for (uint8_t i = 1; i < MAX_EDGES; i++)
        if (_e[i].lastSeen < _e[oldest].lastSeen) oldest = i;
    _e[oldest] = {from, to, rssi, now};
}
