#include "motion_engine.h"

void MotionEngine::begin(ServoEngine* engine) {
    _engine = engine;
    _playing = false;
}

bool MotionEngine::isContinuous(GestureWireId gesture) {
    return gesture < GESTURE_COUNT && GESTURES_V2[gesture].execution == GESTURE_CONTINUOUS;
}

uint16_t MotionEngine::normalDurationMs(GestureWireId gesture) {
    return gesture < GESTURE_COUNT ? GESTURES_V2[gesture].normalDurationMs : 0;
}

const char* MotionEngine::key(GestureWireId gesture) {
    return gesture < GESTURE_COUNT ? GESTURES_V2[gesture].key : "";
}

bool MotionEngine::play(GestureWireId gesture, uint32_t seed) {
    if (!_engine || gesture >= GESTURE_COUNT) return false;
    _gesture = gesture;
    _frameIndex = 0;
    _moving = false;
    _holding = false;
    _clipped = false;
    _seed = seed == 0 ? 1 : seed;
    const GestureDefinitionV2& definition = GESTURES_V2[gesture];
    if (definition.execution == GESTURE_IMMEDIATE) {
        _engine->center(120);
        _playing = false;
        return true;
    }
    _playing = true;
    issueFrame();
    return true;
}

void MotionEngine::issueFrame() {
    const GestureDefinitionV2& definition = GESTURES_V2[_gesture];
    const GestureFrameV2& frame = definition.frames[_frameIndex];
    // Seed is retained for deterministic future variants. The initial catalog
    // deliberately has no pose jitter: exact authored trajectories win.
    (void)_seed;
    _clipped = _engine->setTargetNormalized(frame.panPct, frame.tiltPct, frame.moveMs) || _clipped;
    _moving = true;
    _holding = false;
}

void MotionEngine::update() {
    if (!_playing || !_engine) return;
    if (_moving) {
        if (_engine->isMoving()) return;
        _moving = false;
        _holding = true;
        _holdStartedAt = millis();
    }
    const GestureDefinitionV2& definition = GESTURES_V2[_gesture];
    const GestureFrameV2& frame = definition.frames[_frameIndex];
    if ((uint32_t)(millis() - _holdStartedAt) < frame.holdMs) return;
    _frameIndex++;
    if (_frameIndex < definition.frameCount) { issueFrame(); return; }
    if (definition.execution == GESTURE_CONTINUOUS) {
        _frameIndex = 0;
        issueFrame();
        return;
    }
    _playing = false;
}

void MotionEngine::stop(bool returnToCenter) {
    const bool center = _gesture < GESTURE_COUNT && GESTURES_V2[_gesture].returnToCenter;
    _playing = false;
    _moving = false;
    _holding = false;
    if (_engine && returnToCenter && center) _engine->center(180);
}
