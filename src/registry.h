#pragma once

// ============================================================================
//  Registry — inventaire vivant des droïdes (maître)
//
//  Alimenté par les messages reçus (heartbeat, anim, ...). Suit pour chaque
//  droïde : srcId, RSSI, date de dernière vue. Permet de détecter les
//  nouvelles connexions et les droïdes hors ligne (timeout).
//  Voir project.md (§10).
//
//  Concurrence : les setters « réception » (seen/setServos/setAutoAnim/
//  setFwVersion) sont appelés depuis le callback ESP-NOW (tâche Wi-Fi interne)
//  alors que tout le reste (lectures de pushDroids/OtaMaster/loop, adopt/
//  forget) tourne sur la tâche loop(). Chaque méthode publique est donc
//  atomique (spinlock portMUX) et at() retourne une COPIE de l'entrée plutôt
//  qu'une référence sur un tableau mutable. Les retraits (forget) n'ayant
//  lieu que côté loop(), une itération count()/at() depuis loop() ne peut pas
//  voir d'entrée décalée sous ses pieds — au pire elle manque un droïde
//  fraîchement inséré par la tâche Wi-Fi, sans conséquence.
// ============================================================================

#include <Arduino.h>

class Registry {
public:
    static const uint8_t MAX = 32;

    struct Entry {
        uint16_t id;
        int16_t  rssi;
        uint32_t lastSeen;
        bool     servos;    // état des servos rapporté par le droïde
        bool     autoAnim;  // anims spontanées au repos actives, rapporté par le droïde
        bool     adopted;   // false = en attente d'adoption (voir config_store)
        uint8_t  fwMajor = 0, fwMinor = 0, fwPatch = 0;  // version rapportée par heartbeat
    };

    // Enregistre/actualise un droïde. Retourne true si nouvellement ajouté.
    bool seen(uint16_t id, int rssi, uint32_t now);

    // Met à jour l'état des servos d'un droïde (via heartbeat).
    void setServos(uint16_t id, bool on);

    // Met à jour l'état des anims auto d'un droïde (via heartbeat).
    void setAutoAnim(uint16_t id, bool on);

    // Met à jour la version firmware rapportée par un droïde (via heartbeat).
    void setFwVersion(uint16_t id, uint8_t major, uint8_t minor, uint8_t patch);

    // Marque un droïde comme adopté/non adopté (statut RAM, cf. config_store pour la NVS).
    void setAdopted(uint16_t id, bool v);

    // Retire un droïde du registre (Oublier / adoption refusée). Retourne
    // true s'il a été trouvé et retiré.
    bool forget(uint16_t id);

    uint8_t count() const;

    // Copie de l'entrée i (jamais une référence : le tableau sous-jacent est
    // muté par la tâche Wi-Fi).
    Entry at(uint8_t i) const;

    // Droïde considéré en ligne s'il a été vu depuis moins de `timeoutMs`.
    // Différence SIGNÉE : lastSeen (horodaté par la tâche Wi-Fi) peut être
    // postérieur à `now` capturé en début de loop() — en non signé, le droïde
    // clignoterait « hors ligne » (même famille de bug que l'age de
    // pushDroids, voir CLAUDE.md pièges).
    bool online(uint8_t i, uint32_t now, uint32_t timeoutMs) const;

private:
    Entry   _e[MAX];
    uint8_t _count = 0;
    mutable portMUX_TYPE _mux = portMUX_INITIALIZER_UNLOCKED;
};

extern Registry Droids;
