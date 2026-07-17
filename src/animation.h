#pragma once

// ============================================================================
//  AnimationPlayer — head animations via keyframes (pan/tilt)
//
//  - Each animation is a sequence of keyframes expressed as an OFFSET
//    (degrees) from center, played via ServoEngine (built-in easing).
//  - Non-blocking playback: advances to the next keyframe once the movement
//    is finished and the hold time has elapsed.
//  - Variation via `seed`: slight deterministic jitter on targets and
//    durations, so the same animation feels alive rather than repetitive.
//  See project.md (section 6).
// ============================================================================

#include <Arduino.h>
#include "servo_engine.h"

// Animation IDs (must stay aligned with project.md §6).
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
    ANIM_POWER_DOWN     = 16,  // loops until interrupted by another gesture
    ANIM_TALK           = 17,  // loops; meant to accompany an audio track
    ANIM_COUNT          = 18,
};

class AnimationPlayer {
public:
    void begin(ServoEngine* engine);

    // Starts an animation. `seed` varies the rendering (0 = default value).
    void play(uint8_t animId, uint32_t seed = 0);

    // To be called regularly (in loop()).
    void update();

    // Stops the current animation.
    void stop() { _playing = false; }

    bool isPlaying() const { return _playing; }

    // Picks a random "active" animation ID (excluding IDLE and gestures
    // triggered manually only, like POWER_DOWN/TALK).
    static uint8_t randomAnimId(uint32_t seed);

    // Indicative total duration (ms) of a gesture (sum of keyframes). For a
    // looping gesture (POWER_DOWN, TALK) or IDLE, returns an indicative
    // default value since there's no natural finite duration.
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

    // Deterministic RNG (LCG) for jitter.
    uint32_t _rng = 1;
    uint8_t  rnd(uint8_t n);
    int      jitter(uint8_t amp);

    void issueCurrentFrame();
};
