#include <Arduino.h>
#include "config.h"
#include "servo_engine.h"
#include "animation.h"
#include "mesh_comm.h"

// NOTE: banc de test temporaire (étapes 2-4). Sera remplacé par la machine
// à états du droïde à l'étape 6.

static ServoEngine head;
static AnimationPlayer anim;
static uint32_t nextMove = 0;

// LED de vie (témoin d'exécution) — clignotement non bloquant.
static uint32_t lastBlink = 0;
static bool ledOn = false;

// Test mesh
static uint32_t nextMeshSend = 0;
static uint32_t nextHeartbeat = 0;

// Inventaire léger des droïdes vus (détection de nouvelle connexion, maître).
static uint16_t knownIds[32];
static uint8_t  knownCount = 0;

static bool isKnown(uint16_t id) {
    for (uint8_t i = 0; i < knownCount; i++) {
        if (knownIds[i] == id) return true;
    }
    return false;
}

static void noteDroidSeen(uint16_t srcId) {
#if IS_MASTER
    if (isKnown(srcId)) return;
    if (knownCount < 32) knownIds[knownCount++] = srcId;
    Serial.printf("[MESH] nouveau B1 %04X connecté au mesh (total %u)\n",
                  srcId, knownCount);
#else
    (void)srcId;
#endif
}

static void onMeshMessage(uint8_t type, const uint8_t* payload, uint8_t len,
                          uint16_t srcId, int rssi) {
    // Tout message reçu prouve la présence de ce droïde.
    noteDroidSeen(srcId);

    if (type == MSG_ANIM && len == sizeof(AnimPayload)) {
        AnimPayload p;
        memcpy(&p, payload, sizeof(p));
        Serial.printf("[MESH] ANIM de %04X (rssi %d) target=%04X anim=%u\n",
                      srcId, rssi, p.targetId, p.animId);
        // Joue l'animation reçue (démo).
        anim.play(p.animId, p.seed);
    } else if (type == MSG_HEARTBEAT) {
        // Présence : rien à afficher en continu (déjà noté ci-dessus).
    } else {
        Serial.printf("[MESH] type=%u len=%u de %04X (rssi %d)\n",
                      type, len, srcId, rssi);
    }
}

void setup() {
    Serial.begin(115200);
    pinMode(PIN_LED_ONBOARD, OUTPUT);

    head.begin();
    head.setIdleNoise(true);
    head.center();
    anim.begin(&head);

    if (Mesh.begin(GROUP_KEY)) {
        Mesh.onReceive(onMeshMessage);
        Serial.printf("[MESH] prêt, id=%04X\n", Mesh.myId());
    } else {
        Serial.println("[MESH] échec d'initialisation");
    }
}

void loop() {
    const uint32_t now = millis();

    head.update();
    anim.update();

    // LED de vie.
    if (now - lastBlink >= LED_BLINK_MS) {
        lastBlink = now;
        ledOn = !ledOn;
        digitalWrite(PIN_LED_ONBOARD, ledOn ? HIGH : LOW);
    }

    // Heartbeat : chaque droïde signale sa présence au mesh.
    if (now > nextHeartbeat) {
        nextHeartbeat = now + HEARTBEAT_MS;
        HeartbeatPayload hb{now, 0};
        Mesh.send(MSG_HEARTBEAT, &hb, sizeof(hb));
    }

#if IS_MASTER
    // Le maître choisit une anim au hasard, la joue et la diffuse au groupe.
    if (!anim.isPlaying() && now > nextMeshSend) {
        nextMeshSend = now + (uint32_t)random(2500, 5000);
        const uint32_t seed = (uint32_t)esp_random();
        const uint8_t animId = AnimationPlayer::randomAnimId(seed);
        AnimPayload p{MESH_TARGET_ALL, animId, 0, seed};
        Mesh.send(MSG_ANIM, &p, sizeof(p));
        anim.play(animId, seed);
        Serial.printf("[MESH] envoi+play ANIM anim=%u\n", animId);
    }
#else
    // Un esclave isolé (sans maître) s'anime aussi tout seul.
    if (!anim.isPlaying() && now > nextMove) {
        nextMove = now + (uint32_t)random(3000, 7000);
        const uint32_t seed = (uint32_t)esp_random();
        anim.play(AnimationPlayer::randomAnimId(seed), seed);
    }
#endif
}
