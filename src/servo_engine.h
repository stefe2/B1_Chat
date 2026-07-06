#pragma once

// ============================================================================
//  ServoEngine — pilotage fluide et organique de la tête (pan + tilt)
//
//  - Interpolation non bloquante à SERVO_UPDATE_HZ (voir config.h)
//  - Easing ease-in-out entre positions cibles (pas de mouvement linéaire brut)
//  - Bruit d'idle optionnel (micro-oscillations) pour un rendu vivant
//  Les angles sont bornés aux limites mécaniques définies dans config.h.
//  PWM piloté via l'API LEDC native du core ESP32 (pas de dépendance externe).
// ============================================================================

#include <Arduino.h>

class ServoEngine {
public:
    // Attache les servos et centre la tête.
    void begin();

    // Définit une nouvelle cible atteinte en `durationMs` avec easing.
    // pan/tilt en degrés (bornés automatiquement).
    void setTarget(float panDeg, float tiltDeg, uint32_t durationMs);

    // Recentre la tête.
    void center(uint32_t durationMs = 800);

    // À appeler très régulièrement (dans loop()). Met à jour les servos
    // au rythme SERVO_UPDATE_HZ.
    void update();

    // Vrai tant qu'un mouvement d'interpolation est en cours.
    bool isMoving() const { return _moving; }

    // Active/désactive le bruit d'idle et règle son amplitude (degrés).
    void setIdleNoise(bool on, float panAmp = 3.0f, float tiltAmp = 2.0f);

    float pan() const { return _curPan; }
    float tilt() const { return _curTilt; }

private:
    // Interpolation
    float _startPan = 0, _startTilt = 0;
    float _targetPan = 0, _targetTilt = 0;
    float _curPan = 0, _curTilt = 0;   // position de base (sans bruit)
    uint32_t _moveStart = 0;
    uint32_t _moveDur = 0;
    bool _moving = false;

    // Cadence d'écriture
    uint32_t _lastWrite = 0;

    // Bruit d'idle
    bool  _idleNoise = false;
    float _panNoiseAmp = 0;
    float _tiltNoiseAmp = 0;

    void writeServos(float panDeg, float tiltDeg);
    float noise(float t, float phase) const;
};
