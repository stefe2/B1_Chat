#include <Arduino.h>
#include "config.h"
#include "servo_engine.h"
#include "mesh_comm.h"

// NOTE: banc de test temporaire (étapes 2-3). Sera remplacé par la machine
// à états du droïde à l'étape 6.

static ServoEngine head;
static uint32_t nextMove = 0;

// LED de vie (témoin d'exécution) — clignotement non bloquant.
static uint32_t lastBlink = 0;
static bool ledOn = false;

// Test mesh
static uint32_t nextMeshSend = 0;

static void onMeshMessage(uint8_t type, const uint8_t* payload, uint8_t len,
                          uint16_t srcId, int rssi) {
    if (type == MSG_ANIM && len == sizeof(AnimPayload)) {
        AnimPayload p;
        memcpy(&p, payload, sizeof(p));
        Serial.printf("[MESH] ANIM de %04X (rssi %d) target=%04X anim=%u\n",
                      srcId, rssi, p.targetId, p.animId);
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

    // LED de vie.
    if (now - lastBlink >= LED_BLINK_MS) {
        lastBlink = now;
        ledOn = !ledOn;
        digitalWrite(PIN_LED_ONBOARD, ledOn ? HIGH : LOW);
    }

    // Mouvement fluide aléatoire.
    if (!head.isMoving() && now > nextMove) {
        const float pan = random(SERVO_PAN_MIN, SERVO_PAN_MAX + 1);
        const float tilt = random(SERVO_TILT_MIN, SERVO_TILT_MAX + 1);
        const uint32_t dur = random(700, 1600);
        head.setTarget(pan, tilt, dur);
        nextMove = now + dur + random(1500, 3500);
    }

#if IS_MASTER
    // Le maître émet périodiquement une anim de test à tout le groupe.
    if (now > nextMeshSend) {
        nextMeshSend = now + 3000;
        AnimPayload p{MESH_TARGET_ALL, (uint8_t)random(0, 8), 0, (uint32_t)random()};
        Mesh.send(MSG_ANIM, &p, sizeof(p));
        Serial.printf("[MESH] envoi ANIM anim=%u\n", p.animId);
    }
#endif
}
