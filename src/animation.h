#pragma once

// ============================================================================
//  AnimationPlayer — animations de tête par keyframes (pan/tilt)
//
//  - Chaque animation est une suite de keyframes exprimées en OFFSET (degrés)
//    par rapport au centre, jouées via ServoEngine (easing intégré).
//  - Lecture non bloquante : on avance à la keyframe suivante quand le
//    mouvement est terminé et le temps de maintien écoulé.
//  - Variation par `seed` : léger jitter déterministe sur cibles et durées,
//    pour que la même animation paraisse vivante et non répétitive.
//  Voir project.md (section 6).
// ============================================================================

#include <Arduino.h>
#include "servo_engine.h"

// Identifiants d'animation (doivent rester alignés avec project.md §6).
enum AnimId : uint8_t {
    ANIM_IDLE        = 0,
    ANIM_LOOK_AROUND = 1,
    ANIM_NOD_YES     = 2,
    ANIM_SHAKE_NO    = 3,
    ANIM_CURIOUS_TILT= 4,
    ANIM_SCAN_SLOW   = 5,
    ANIM_ALERT_SNAP  = 6,
    ANIM_TRACK       = 7,
    ANIM_COUNT       = 8,
};

class AnimationPlayer {
public:
    void begin(ServoEngine* engine);

    // Démarre une animation. `seed` fait varier le rendu (0 = valeur par défaut).
    void play(uint8_t animId, uint32_t seed = 0);

    // À appeler régulièrement (dans loop()).
    void update();

    // Arrête l'animation en cours.
    void stop() { _playing = false; }

    bool isPlaying() const { return _playing; }
    uint8_t current() const { return _animId; }

    // Tire un identifiant d'animation « active » au hasard (hors IDLE).
    static uint8_t randomAnimId(uint32_t seed);

private:
    ServoEngine* _engine = nullptr;
    uint8_t  _animId = ANIM_IDLE;
    uint8_t  _idx = 0;
    bool     _playing = false;
    bool     _needMove = false;
    bool     _holding = false;
    uint32_t _holdStart = 0;
    uint16_t _holdDur = 0;

    // RNG déterministe (LCG) pour le jitter.
    uint32_t _rng = 1;
    uint8_t  rnd(uint8_t n);
    int      jitter(uint8_t amp);

    void issueCurrentFrame();
};
