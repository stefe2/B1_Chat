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
    ANIM_IDLE           = 0,
    ANIM_LOOK_AROUND    = 1,
    ANIM_NOD_YES        = 2,
    ANIM_SHAKE_NO       = 3,
    ANIM_CURIOUS_TILT   = 4,
    ANIM_SCAN_SLOW      = 5,
    ANIM_ALERT_SNAP     = 6,
    ANIM_TRACK          = 7,
    ANIM_GLITCH_STUTTER = 8,
    ANIM_CONFUSED_TILT  = 9,
    ANIM_DOUBLE_TAKE    = 10,
    ANIM_SLEEPY_DROOP   = 11,
    ANIM_TARGET_LOCK    = 12,
    ANIM_WHIRR_SEARCH   = 13,
    ANIM_SIGNAL_GLITCH  = 14,
    ANIM_GREETING_NOD   = 15,
    ANIM_POWER_DOWN     = 16,  // boucle jusqu'à interruption par un autre geste
    ANIM_TALK           = 17,  // boucle ; pensé pour accompagner une piste audio
    ANIM_COUNT          = 18,
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

    // Tire un identifiant d'animation « active » au hasard (hors IDLE, hors gestes
    // déclenchés manuellement uniquement comme POWER_DOWN/TALK).
    static uint8_t randomAnimId(uint32_t seed);

    // Durée totale indicative (ms) d'un geste (somme des keyframes). Pour un geste en
    // boucle (POWER_DOWN, TALK) ou IDLE, retourne une valeur par défaut indicative
    // puisqu'il n'y a pas de durée finie naturelle.
    static uint32_t totalDurationMs(uint8_t animId);

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
