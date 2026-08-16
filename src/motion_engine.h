#pragma once

#include <Arduino.h>
#include "generated/gesture_catalog_v2.h"
#include "servo_engine.h"

// Owns one safe V2 gesture at a time. Trajectories use normalized positions so
// each droid maps the same authored intent into its own calibrated limits.
class MotionEngine {
public:
    void begin(ServoEngine* engine);
    bool play(GestureWireId gesture, uint32_t seed = 0);
    void update();
    void stop(bool returnToCenter = true);
    bool isPlaying() const { return _playing; }
    bool wasClipped() const { return _clipped; }
    GestureWireId activeGesture() const { return _gesture; }

    static bool isContinuous(GestureWireId gesture);
    static uint16_t normalDurationMs(GestureWireId gesture);
    static const char* key(GestureWireId gesture);

private:
    ServoEngine* _engine = nullptr;
    GestureWireId _gesture = GESTURE_IDLE_CENTER;
    uint8_t _frameIndex = 0;
    bool _playing = false;
    bool _moving = false;
    bool _holding = false;
    bool _clipped = false;
    uint32_t _holdStartedAt = 0;
    uint32_t _seed = 1;

    void issueFrame();
};
