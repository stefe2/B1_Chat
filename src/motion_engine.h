#pragma once

#include <Arduino.h>
#include "generated/gesture_catalog_v2.h"
#include "servo_engine.h"

// Composes one persistent base pose with one expression overlay per axis.
// A look gesture therefore owns its base axis while Talk/Nod animate an offset
// on the other axis. The ServoEngine still receives one bounded final target.
class MotionEngine {
public:
    void begin(ServoEngine* engine);
    bool play(GestureWireId gesture, uint32_t seed = 0,
              GestureWireId* interrupted = nullptr);
    void update();
    void stop(bool resetAll = true);
    bool stopGesture(GestureWireId gesture);
    bool isPlaying() const;
    bool isPlaying(GestureWireId gesture) const;
    bool wasClipped() const;
    bool wasClipped(GestureWireId gesture) const;
    GestureWireId activeGesture() const;

    static bool isContinuous(GestureWireId gesture);
    static uint16_t normalDurationMs(GestureWireId gesture);
    static const char* key(GestureWireId gesture);

private:
    struct ChannelPlayback {
        GestureWireId gesture = GESTURE_COUNT;
        uint8_t frameIndex = 0;
        bool active = false;
        bool clipped = false;
        uint32_t nextFrameAt = 0;
    };

    ServoEngine* _engine = nullptr;
    ChannelPlayback _panBase;
    ChannelPlayback _tiltBase;
    ChannelPlayback _panOverlay;
    ChannelPlayback _tiltOverlay;
    int8_t _panBasePct = 0;
    int8_t _tiltBasePct = 0;
    int8_t _panOverlayPct = 0;
    int8_t _tiltOverlayPct = 0;
    bool _clipped[GESTURE_COUNT] = {};

    ChannelPlayback* channelFor(const GestureDefinitionV2& definition);
    int8_t* valueFor(const GestureDefinitionV2& definition);
    void startFrame(ChannelPlayback& channel, const GestureDefinitionV2& definition);
    void updateChannel(ChannelPlayback& channel);
    void applyComposedTarget(uint16_t durationMs);
    void clearChannel(ChannelPlayback& channel);
};
