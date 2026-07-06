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

    _panMin = SERVO_PAN_MIN; _panCenter = SERVO_PAN_CENTER; _panMax = SERVO_PAN_MAX;
    _tiltMin = SERVO_TILT_MIN; _tiltCenter = SERVO_TILT_CENTER; _tiltMax = SERVO_TILT_MAX;

    _curPan = _panCenter;
    _curTilt = _tiltCenter;
    _targetPan = _curPan;
    _targetTilt = _curTilt;
    _moving = false;
    writeServos(_curPan, _curTilt);
}

void ServoEngine::setTarget(float panDeg, float tiltDeg, uint32_t durationMs) {
    _startPan = _curPan;
    _startTilt = _curTilt;
    _targetPan = clampf(panDeg, _panMin, _panMax);
    _targetTilt = clampf(tiltDeg, _tiltMin, _tiltMax);
    _moveStart = millis();
    _moveDur = durationMs == 0 ? 1 : durationMs;
    _moving = true;
}

void ServoEngine::center(uint32_t durationMs) {
    setTarget(_panCenter, _tiltCenter, durationMs);
}

void ServoEngine::setLimits(uint8_t panMin, uint8_t panCenter, uint8_t panMax,
                            uint8_t tiltMin, uint8_t tiltCenter, uint8_t tiltMax) {
    if (panMin > panMax) { const uint8_t t = panMin; panMin = panMax; panMax = t; }
    if (tiltMin > tiltMax) { const uint8_t t = tiltMin; tiltMin = tiltMax; tiltMax = t; }
    _panMin = panMin; _panMax = panMax;
    _tiltMin = tiltMin; _tiltMax = tiltMax;
    _panCenter = (uint8_t)clampf(panCenter, panMin, panMax);
    _tiltCenter = (uint8_t)clampf(tiltCenter, tiltMin, tiltMax);
}

void ServoEngine::setIdleNoise(bool on, float panAmp, float tiltAmp) {
    _idleNoise = on;
    _panNoiseAmp = panAmp;
    _tiltNoiseAmp = tiltAmp;
}

void ServoEngine::setEnabled(bool en) {
    if (en == _enabled) return;
    _enabled = en;
    if (en) {
        ledcAttach(PIN_SERVO_PAN, SERVO_LEDC_FREQ, SERVO_LEDC_BITS);
        ledcAttach(PIN_SERVO_TILT, SERVO_LEDC_FREQ, SERVO_LEDC_BITS);
        writeServos(_curPan, _curTilt);
    } else {
        // Coupe le signal PWM : servos libres, aucun échauffement.
        ledcDetach(PIN_SERVO_PAN);
        ledcDetach(PIN_SERVO_TILT);
    }
}

// Bruit organique : somme de deux sinusoïdes à fréquences incommensurables
// -> déambulation lente non répétitive, sans à-coups.
float ServoEngine::noise(float t, float phase) const {
    return 0.6f * sinf(t * 0.70f + phase) + 0.4f * sinf(t * 1.73f + phase * 2.1f);
}

void ServoEngine::writeServos(float panDeg, float tiltDeg) {
    panDeg = clampf(panDeg, _panMin, _panMax);
    tiltDeg = clampf(tiltDeg, _tiltMin, _tiltMax);

    // Angle (0..180°) -> largeur d'impulsion (µs) -> rapport cyclique LEDC.
    const float span = SERVO_MAX_US - SERVO_MIN_US;
    const float usPan = SERVO_MIN_US + (panDeg / 180.0f) * span;
    const float usTilt = SERVO_MIN_US + (tiltDeg / 180.0f) * span;
    ledcWrite(PIN_SERVO_PAN, usToDuty(usPan));
    ledcWrite(PIN_SERVO_TILT, usToDuty(usTilt));
}

void ServoEngine::update() {
    if (!_enabled) return;
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
