#include "motion_engine.h"

namespace {
int8_t clampPct(int value) {
    return (int8_t)constrain(value, -100, 100);
}
}

void MotionEngine::begin(ServoEngine* engine) {
    _engine = engine;
    stop(false);
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

MotionEngine::ChannelPlayback* MotionEngine::channelFor(const GestureDefinitionV2& definition) {
    const bool pan = definition.axes == GESTURE_AXIS_PAN;
    if (definition.layer == GESTURE_LAYER_BASE) return pan ? &_panBase : &_tiltBase;
    if (definition.layer == GESTURE_LAYER_OVERLAY) return pan ? &_panOverlay : &_tiltOverlay;
    return nullptr;
}

int8_t* MotionEngine::valueFor(const GestureDefinitionV2& definition) {
    const bool pan = definition.axes == GESTURE_AXIS_PAN;
    if (definition.layer == GESTURE_LAYER_BASE) return pan ? &_panBasePct : &_tiltBasePct;
    if (definition.layer == GESTURE_LAYER_OVERLAY) return pan ? &_panOverlayPct : &_tiltOverlayPct;
    return nullptr;
}

bool MotionEngine::play(GestureWireId gesture, uint32_t seed, GestureWireId* interrupted) {
    (void)seed; // Retained for deterministic catalog variants.
    if (!_engine || gesture >= GESTURE_COUNT) return false;
    if (interrupted) *interrupted = GESTURE_COUNT;
    const GestureDefinitionV2& definition = GESTURES_V2[gesture];
    if (definition.layer == GESTURE_LAYER_RESET) {
        stop(true);
        return true;
    }

    ChannelPlayback* channel = channelFor(definition);
    int8_t* value = valueFor(definition);
    if (!channel || !value || definition.axes == GESTURE_AXIS_BOTH) return false;
    if (channel->active && interrupted) *interrupted = channel->gesture;
    clearChannel(*channel);
    channel->gesture = gesture;
    channel->active = true;
    channel->clipped = false;
    _clipped[gesture] = false;
    startFrame(*channel, definition);
    return true;
}

void MotionEngine::startFrame(ChannelPlayback& channel, const GestureDefinitionV2& definition) {
    const GestureFrameV2& frame = definition.frames[channel.frameIndex];
    int8_t* value = valueFor(definition);
    *value = definition.axes == GESTURE_AXIS_PAN ? frame.panPct : frame.tiltPct;
    channel.clipped = _engine->setTargetNormalized(
        clampPct(_panBasePct + _panOverlayPct),
        clampPct(_tiltBasePct + _tiltOverlayPct), frame.moveMs) || channel.clipped;
    _clipped[channel.gesture] = channel.clipped;
    channel.nextFrameAt = millis() + (uint32_t)frame.moveMs + frame.holdMs;
}

void MotionEngine::applyComposedTarget(uint16_t durationMs) {
    if (!_engine) return;
    _engine->setTargetNormalized(clampPct(_panBasePct + _panOverlayPct),
                                 clampPct(_tiltBasePct + _tiltOverlayPct), durationMs);
}

void MotionEngine::updateChannel(ChannelPlayback& channel) {
    if (!channel.active || (int32_t)(millis() - channel.nextFrameAt) < 0) return;
    const GestureDefinitionV2& definition = GESTURES_V2[channel.gesture];
    channel.frameIndex++;
    if (channel.frameIndex < definition.frameCount) {
        startFrame(channel, definition);
        return;
    }
    if (definition.execution == GESTURE_CONTINUOUS) {
        channel.frameIndex = 0;
        startFrame(channel, definition);
        return;
    }
    if (definition.endBehavior == GESTURE_END_CLEAR_LAYER) {
        *valueFor(definition) = 0;
        applyComposedTarget(120);
    }
    channel.active = false;
}

void MotionEngine::update() {
    updateChannel(_panBase);
    updateChannel(_tiltBase);
    updateChannel(_panOverlay);
    updateChannel(_tiltOverlay);
}

void MotionEngine::clearChannel(ChannelPlayback& channel) {
    channel = ChannelPlayback{};
}

void MotionEngine::stop(bool resetAll) {
    clearChannel(_panBase); clearChannel(_tiltBase);
    clearChannel(_panOverlay); clearChannel(_tiltOverlay);
    _panOverlayPct = 0; _tiltOverlayPct = 0;
    if (resetAll) _panBasePct = _tiltBasePct = 0;
    applyComposedTarget(550);
}

bool MotionEngine::stopGesture(GestureWireId gesture) {
    if (gesture >= GESTURE_COUNT) return false;
    ChannelPlayback* channels[] = { &_panBase, &_tiltBase, &_panOverlay, &_tiltOverlay };
    bool stopped = false;
    for (ChannelPlayback* channel : channels) {
        if (channel->active && channel->gesture == gesture) {
            const GestureDefinitionV2& definition = GESTURES_V2[gesture];
            if (definition.endBehavior == GESTURE_END_CLEAR_LAYER) *valueFor(definition) = 0;
            clearChannel(*channel);
            stopped = true;
        }
    }
    if (stopped) applyComposedTarget(120);
    return stopped;
}

bool MotionEngine::isPlaying() const {
    return _panBase.active || _tiltBase.active || _panOverlay.active || _tiltOverlay.active;
}

bool MotionEngine::isPlaying(GestureWireId gesture) const {
    const ChannelPlayback* channels[] = { &_panBase, &_tiltBase, &_panOverlay, &_tiltOverlay };
    for (const ChannelPlayback* channel : channels)
        if (channel->active && channel->gesture == gesture) return true;
    return false;
}

bool MotionEngine::wasClipped() const {
    for (uint8_t i = 0; i < GESTURE_COUNT; ++i) if (_clipped[i]) return true;
    return false;
}

bool MotionEngine::wasClipped(GestureWireId gesture) const {
    return gesture < GESTURE_COUNT && _clipped[gesture];
}

GestureWireId MotionEngine::activeGesture() const {
    if (_panOverlay.active) return _panOverlay.gesture;
    if (_tiltOverlay.active) return _tiltOverlay.gesture;
    if (_panBase.active) return _panBase.gesture;
    return _tiltBase.active ? _tiltBase.gesture : GESTURE_IDLE_CENTER;
}
