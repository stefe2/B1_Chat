#include <Arduino.h>
#include "config.h"
#include "servo_engine.h"
#include "animation.h"
#include "mesh_comm.h"
#include "registry.h"
#include "config_store.h"

// NOTE: banc de test temporaire (étapes 2-7). Sera remplacé par la machine
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
static uint32_t nextPresenceScan = 0;

// Suivi hors-ligne (maître) : mémorise l'état en ligne pour signaler les pertes.
static const uint32_t DROID_TIMEOUT_MS = 7000;
#if IS_MASTER
static bool wasOnline[Registry::MAX];
#endif

static void onMeshMessage(uint8_t type, const uint8_t* payload, uint8_t len,
                          uint16_t srcId, int rssi) {
#if IS_MASTER
    // Tout message reçu prouve la présence de ce droïde.
    if (Droids.seen(srcId, rssi, millis())) {
        const String name = Config.getName(srcId);
        Serial.printf("[MESH] nouveau B1 %04X%s%s connecté au mesh (total %u)\n",
                      srcId, name.length() ? " " : "", name.c_str(), Droids.count());
    }
#else
    (void)srcId; (void)rssi;
#endif

    if (type == MSG_ANIM && len == sizeof(AnimPayload)) {
        AnimPayload p;
        memcpy(&p, payload, sizeof(p));
        Serial.printf("[MESH] ANIM de %04X (rssi %d) target=%04X anim=%u\n",
                      srcId, rssi, p.targetId, p.animId);
        // Joue l'animation reçue (démo).
        anim.play(p.animId, p.seed);
    } else if (type == MSG_HEARTBEAT) {
        // Présence : déjà notée dans le registry ci-dessus.
    } else {
        Serial.printf("[MESH] type=%u len=%u de %04X (rssi %d)\n",
                      type, len, srcId, rssi);
    }
}

void setup() {
    Serial.begin(115200);
    pinMode(PIN_LED_ONBOARD, OUTPUT);

    Config.begin();

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
    // Surveillance de présence : signale un B1 qui passe hors-ligne.
    if (now > nextPresenceScan) {
        nextPresenceScan = now + 1000;
        for (uint8_t i = 0; i < Droids.count(); i++) {
            const bool on = Droids.online(i, now, DROID_TIMEOUT_MS);
            if (wasOnline[i] && !on) {
                const String name = Config.getName(Droids.at(i).id);
                Serial.printf("[MESH] B1 %04X%s%s hors-ligne\n",
                              Droids.at(i).id, name.length() ? " " : "",
                              name.c_str());
            }
            wasOnline[i] = on;
        }
    }

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
