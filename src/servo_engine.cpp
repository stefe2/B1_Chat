#include "servo_engine.h"
#include "config.h"

namespace {
// Ease-in-out easing (smootherstep): zero derivatives at both ends -> soft
// start and stop, organic movement.
inline float easeInOut(float t) {
    if (t < 0) t = 0;
    if (t > 1) t = 1;
    return t * t * t * (t * (t * 6.0f - 15.0f) + 10.0f);
}

inline float clampf(float v, float lo, float hi) {
    return v < lo ? lo : (v > hi ? hi : v);
}

// Mirrors one calibrated half-range onto the other. Unlike a raw 180-angle
// inversion this keeps an asymmetric calibrated center physically unchanged
// and never maps outside the calibrated endpoints.
inline float reverseAroundCenter(float angle, float min, float center, float max) {
    if (angle <= center) {
        const float inputSpan = center - min;
        return inputSpan <= 0.0f
            ? center
            : center + ((center - angle) / inputSpan) * (max - center);
    }
    const float inputSpan = max - center;
    return inputSpan <= 0.0f
        ? center
        : center - ((angle - center) / inputSpan) * (center - min);
}

// Servo PWM via native LEDC: 50 Hz, 16-bit resolution (20 ms period).
const uint32_t SERVO_LEDC_FREQ = 50;
const uint8_t  SERVO_LEDC_BITS = 16;
const uint32_t SERVO_PERIOD_US = 1000000UL / SERVO_LEDC_FREQ;  // 20000 µs
const uint32_t SERVO_MAX_DUTY  = (1UL << SERVO_LEDC_BITS) - 1;

// Converts a pulse width (µs) into an LEDC duty cycle.
inline uint32_t usToDuty(float us) {
    return (uint32_t)((us * SERVO_MAX_DUTY) / SERVO_PERIOD_US);
}
}  // namespace

void ServoEngine::begin() {
    _panMin = SERVO_PAN_MIN; _panCenter = SERVO_PAN_CENTER; _panMax = SERVO_PAN_MAX;
    _tiltMin = SERVO_TILT_MIN; _tiltCenter = SERVO_TILT_CENTER; _tiltMax = SERVO_TILT_MAX;

    _curPan = _panCenter;
    _curTilt = _tiltCenter;
    _targetPan = _curPan;
    _targetTilt = _curTilt;
    _moving = false;
    // PWM remains detached until setEnabled(true). This prevents a virgin or
    // full-erased board from twitching at boot before its safe defaults load.
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

void ServoEngine::setTargetOffset(float panOffsetDeg, float tiltOffsetDeg, uint32_t durationMs) {
    setTarget(_panCenter + panOffsetDeg, _tiltCenter + tiltOffsetDeg, durationMs);
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

void ServoEngine::setReversed(bool panReversed, bool tiltReversed) {
    _panReversed = panReversed;
    _tiltReversed = tiltReversed;
}

void ServoEngine::setEnabled(bool en) {
    if (en == _enabled) return;
    _enabled = en;
    if (en) {
        ledcAttach(PIN_SERVO_PAN, SERVO_LEDC_FREQ, SERVO_LEDC_BITS);
        ledcAttach(PIN_SERVO_TILT, SERVO_LEDC_FREQ, SERVO_LEDC_BITS);
        writeServos(_curPan, _curTilt);
    } else {
        // Cuts the PWM signal: servos free to move, no heating.
        ledcDetach(PIN_SERVO_PAN);
        ledcDetach(PIN_SERVO_TILT);
    }
}

void ServoEngine::writeServos(float panDeg, float tiltDeg) {
    panDeg = clampf(panDeg, _panMin, _panMax);
    tiltDeg = clampf(tiltDeg, _tiltMin, _tiltMax);

    // Direction is an electrical-output concern. Logical positions, limits,
    // animation offsets and UI previews therefore retain the same coordinates.
    if (_panReversed) panDeg = reverseAroundCenter(panDeg, _panMin, _panCenter, _panMax);
    if (_tiltReversed) tiltDeg = reverseAroundCenter(tiltDeg, _tiltMin, _tiltCenter, _tiltMax);

    // Angle (0..180°) -> pulse width (µs) -> LEDC duty cycle.
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

    // Interpolation progress.
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

    writeServos(_curPan, _curTilt);
}
