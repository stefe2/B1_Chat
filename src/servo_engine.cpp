#include "servo_engine.h"
#include "config.h"

namespace {
// Easing ease-in-out (smootherstep) : dérivées nulles aux extrémités -> départ
// et arrêt doux, mouvement organique.
inline float easeInOut(float t) {
    if (t < 0) t = 0;
    if (t > 1) t = 1;
    return t * t * t * (t * (t * 6.0f - 15.0f) + 10.0f);
}

inline float clampf(float v, float lo, float hi) {
    return v < lo ? lo : (v > hi ? hi : v);
}

// PWM servo via LEDC natif : 50 Hz, résolution 16 bits (période 20 ms).
const uint32_t SERVO_LEDC_FREQ = 50;
const uint8_t  SERVO_LEDC_BITS = 16;
const uint32_t SERVO_PERIOD_US = 1000000UL / SERVO_LEDC_FREQ;  // 20000 µs
const uint32_t SERVO_MAX_DUTY  = (1UL << SERVO_LEDC_BITS) - 1;

// Convertit une largeur d'impulsion (µs) en rapport cyclique LEDC.
inline uint32_t usToDuty(float us) {
    return (uint32_t)((us * SERVO_MAX_DUTY) / SERVO_PERIOD_US);
}
}  // namespace

void ServoEngine::begin() {
    // Attache les sorties PWM (API pin-based du core ESP32 3.x).
    ledcAttach(PIN_SERVO_PAN, SERVO_LEDC_FREQ, SERVO_LEDC_BITS);
    ledcAttach(PIN_SERVO_TILT, SERVO_LEDC_FREQ, SERVO_LEDC_BITS);

    _curPan = SERVO_PAN_CENTER;
    _curTilt = SERVO_TILT_CENTER;
    _targetPan = _curPan;
    _targetTilt = _curTilt;
    _moving = false;
    writeServos(_curPan, _curTilt);
}

void ServoEngine::setTarget(float panDeg, float tiltDeg, uint32_t durationMs) {
    _startPan = _curPan;
    _startTilt = _curTilt;
    _targetPan = clampf(panDeg, SERVO_PAN_MIN, SERVO_PAN_MAX);
    _targetTilt = clampf(tiltDeg, SERVO_TILT_MIN, SERVO_TILT_MAX);
    _moveStart = millis();
    _moveDur = durationMs == 0 ? 1 : durationMs;
    _moving = true;
}

void ServoEngine::center(uint32_t durationMs) {
    setTarget(SERVO_PAN_CENTER, SERVO_TILT_CENTER, durationMs);
}

void ServoEngine::setIdleNoise(bool on, float panAmp, float tiltAmp) {
    _idleNoise = on;
    _panNoiseAmp = panAmp;
    _tiltNoiseAmp = tiltAmp;
}

// Bruit organique : somme de deux sinusoïdes à fréquences incommensurables
// -> déambulation lente non répétitive, sans à-coups.
float ServoEngine::noise(float t, float phase) const {
    return 0.6f * sinf(t * 0.70f + phase) + 0.4f * sinf(t * 1.73f + phase * 2.1f);
}

void ServoEngine::writeServos(float panDeg, float tiltDeg) {
    panDeg = clampf(panDeg, SERVO_PAN_MIN, SERVO_PAN_MAX);
    tiltDeg = clampf(tiltDeg, SERVO_TILT_MIN, SERVO_TILT_MAX);

    // Angle (0..180°) -> largeur d'impulsion (µs) -> rapport cyclique LEDC.
    const float span = SERVO_MAX_US - SERVO_MIN_US;
    const float usPan = SERVO_MIN_US + (panDeg / 180.0f) * span;
    const float usTilt = SERVO_MIN_US + (tiltDeg / 180.0f) * span;
    ledcWrite(PIN_SERVO_PAN, usToDuty(usPan));
    ledcWrite(PIN_SERVO_TILT, usToDuty(usTilt));
}

void ServoEngine::update() {
    const uint32_t now = millis();
    const uint32_t interval = 1000UL / SERVO_UPDATE_HZ;
    if (now - _lastWrite < interval) return;
    _lastWrite = now;

    // Progression de l'interpolation (position de base).
    if (_moving) {
        float t = (float)(now - _moveStart) / (float)_moveDur;
        if (t >= 1.0f) {
            t = 1.0f;
            _moving = false;
        }
        const float e = easeInOut(t);
        _curPan = _startPan + (_targetPan - _startPan) * e;
        _curTilt = _startTilt + (_targetTilt - _startTilt) * e;
    }

    // Bruit d'idle superposé (n'altère pas la position de base mémorisée).
    float outPan = _curPan;
    float outTilt = _curTilt;
    if (_idleNoise) {
        const float ts = now / 1000.0f;
        outPan += _panNoiseAmp * noise(ts, 0.0f);
        outTilt += _tiltNoiseAmp * noise(ts, 1.3f);
    }

    writeServos(outPan, outTilt);
}
