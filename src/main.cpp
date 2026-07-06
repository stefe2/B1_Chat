#include <Arduino.h>
#include "config.h"
#include "servo_engine.h"

// NOTE: banc de test temporaire de l'étape 2 (servo_engine).
// Sera remplacé par la machine à états du droïde à l'étape 6.

static ServoEngine head;
static uint32_t nextMove = 0;

void setup() {
    Serial.begin(115200);
    head.begin();
    head.setIdleNoise(true);   // rendu vivant même à l'arrêt
    delay(500);
    head.center();
}

void loop() {
    head.update();

    // Toutes les ~4 s, viser une position aléatoire (mouvement fluide).
    if (!head.isMoving() && millis() > nextMove) {
        const float pan = random(SERVO_PAN_MIN, SERVO_PAN_MAX + 1);
        const float tilt = random(SERVO_TILT_MIN, SERVO_TILT_MAX + 1);
        const uint32_t dur = random(700, 1600);
        head.setTarget(pan, tilt, dur);
        nextMove = millis() + dur + random(1500, 3500);
        Serial.printf("Target pan=%.0f tilt=%.0f dur=%lu\n", pan, tilt, (unsigned long)dur);
    }
}
