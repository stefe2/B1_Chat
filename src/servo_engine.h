#pragma once

// ============================================================================
//  ServoEngine — smooth, organic head motion driver (pan + tilt)
//
//  - Non-blocking interpolation at SERVO_UPDATE_HZ (see config.h)
//  - Ease-in-out easing between target positions (no raw linear motion)
//  - Optional idle noise (micro-oscillations) for a lifelike feel
//  Angles are clamped to the mechanical limits defined in config.h.
//  PWM driven via the ESP32 core's native LEDC API (no external dependency).
// ============================================================================

#include <Arduino.h>

class ServoEngine {
public:
    // Attaches the servos and centers the head.
    void begin();

    // Sets a new target reached in `durationMs` with easing.
    // pan/tilt in degrees (automatically clamped).
    void setTarget(float panDeg, float tiltDeg, uint32_t durationMs);

    // Recenters the head.
    void center(uint32_t durationMs = 800);

    // To be called very regularly (in loop()). Updates the servos at the
    // SERVO_UPDATE_HZ rate.
    void update();

    // True while an interpolation move is in progress.
    bool isMoving() const { return _moving; }

    // Enables/disables idle noise and sets its amplitude (degrees).
    void setIdleNoise(bool on, float panAmp = 3.0f, float tiltAmp = 2.0f);

    // Redefines the pan/tilt mechanical limits (per-droid calibration).
    // Invalid values (min > max) are corrected; the center is clamped back
    // into the [min, max] range.
    void setLimits(uint8_t panMin, uint8_t panCenter, uint8_t panMax,
                   uint8_t tiltMin, uint8_t tiltCenter, uint8_t tiltMax);

    // Physically enables/disables the PWM outputs (servo protection).
    // Disabled: the pins are detached (no signal -> servos free to move).
    void setEnabled(bool en);
    bool isEnabled() const { return _enabled; }

    float pan() const { return _curPan; }
    float tilt() const { return _curTilt; }

private:
    bool _enabled = true;

    // Current mechanical limits (degrees); defaults set in begin(),
    // replaceable via setLimits() (per-droid persisted calibration).
    uint8_t _panMin = 0, _panCenter = 90, _panMax = 180;
    uint8_t _tiltMin = 0, _tiltCenter = 90, _tiltMax = 180;

    // Interpolation
    float _startPan = 0, _startTilt = 0;
    float _targetPan = 0, _targetTilt = 0;
    float _curPan = 0, _curTilt = 0;   // base position (without noise)
    uint32_t _moveStart = 0;
    uint32_t _moveDur = 0;
    bool _moving = false;

    // Write cadence
    uint32_t _lastWrite = 0;

    // Idle noise
    bool  _idleNoise = false;
    float _panNoiseAmp = 0;
    float _tiltNoiseAmp = 0;

    void writeServos(float panDeg, float tiltDeg);
    float noise(float t, float phase) const;
};
