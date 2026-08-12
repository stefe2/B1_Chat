#include "animation.h"
#include "config.h"

// ---------------------------------------------------------------------------
//  Keyframes: offsets (degrees) from center. moveMs = movement duration
//  (easing), holdMs = hold time before the next keyframe.
// ---------------------------------------------------------------------------
struct KeyFrame {
    int8_t   panOff;
    int8_t   tiltOff;
    uint16_t moveMs;
    uint16_t holdMs;
};

namespace {

// Keep randomized movement durations inside the domain accepted by the servo
// engine. In particular, a negative jitter must be clamped while it is still
// signed: converting it to uint16_t first used to wrap a short movement to
// roughly 65 seconds.
constexpr uint16_t clampMoveDurationMs(int32_t durationMs) {
    return durationMs < 1 ? 1
         : durationMs > UINT16_MAX ? UINT16_MAX
         : (uint16_t)durationMs;
}

static_assert(clampMoveDurationMs(-10) == 1, "negative move duration must not wrap");
static_assert(clampMoveDurationMs(250) == 250, "valid move duration must be preserved");

// -- Animation definitions (offsets from center) ------------------
const KeyFrame LOOK_AROUND[] = {
    {-40,   5, 900, 500}, { 40,   5, 1200, 500}, {  0,   0, 800, 300},
};
const KeyFrame NOD_YES[] = {
    {  0,  15, 300, 120}, {  0, -15, 300, 120}, {  0,  15, 300, 120},
    {  0, -12, 300, 120}, {  0,   0, 300, 100},
};
const KeyFrame SHAKE_NO[] = {
    {-30,   0, 260, 100}, { 30,   0, 320, 100}, {-25,   0, 300, 100},
    { 25,   0, 300, 100}, {  0,   0, 260, 100},
};
const KeyFrame CURIOUS_TILT[] = {
    { 15,  22, 700, 900}, {-10,  20, 800, 900}, {  0,   0, 700, 300},
};
const KeyFrame SCAN_SLOW[] = {
    {-60,   8, 1600, 250}, {-20,   0, 1000, 250}, { 20,   0, 1000, 250},
    { 60,   8, 1600, 250}, {  0,   0, 1400, 300},
};
const KeyFrame ALERT_SNAP[] = {
    {  0, -20, 140, 900}, {  0,  -5, 250, 400}, {  0,   0, 300, 200},
};
const KeyFrame TRACK[] = {
    { 20,  10, 500, 300}, { 35,  -5, 450, 400}, {  5,  12, 550, 300},
    {-15,  -8, 500, 400}, {  0,   0, 500, 200},
};
const KeyFrame GLITCH_STUTTER[] = {
    { -8,   3,  80,  60}, {  6,  -4,  70,  60}, { -5,   5,  60,  50},
    {  4,  -2,  70,  60}, {  0,   0,  90, 100},
};
const KeyFrame CONFUSED_TILT[] = {
    {-20,  10, 900, 900}, { 20,   8, 1000, 900}, {  0,   0, 800, 400},
};
const KeyFrame DOUBLE_TAKE[] = {
    { 25,   0, 150, 150}, {-30,   0, 120, 250}, {  5,   0, 200, 150},
    {  0,   0, 200, 100},
};
const KeyFrame SLEEPY_DROOP[] = {
    {  0, -25, 1400, 1200}, {  0,   5, 150, 150}, {  0,   0, 400, 200},
};
const KeyFrame TARGET_LOCK[] = {
    { 45,  -5, 180, 1400}, {  0,   0, 400, 200},
};
const KeyFrame WHIRR_SEARCH[] = {
    {-50,   5, 500, 150}, { 30,  -5, 450, 150}, {-40,   8, 500, 150},
    { 50,   0, 550, 150}, {  0,   0, 500, 200},
};
const KeyFrame SIGNAL_GLITCH[] = {
    {-10,   6,  50,  40}, {  8,  -6,  50,  40}, { -6,   4,  50,  40},
    {  5,  -3,  50,  40}, { -3,   2,  50,  40}, {  0,   0, 150, 150},
};
const KeyFrame GREETING_NOD[] = {
    {  0,  20, 700, 500}, {  0,   0, 700, 300},
};
const KeyFrame POWER_DOWN[] = {
    {  0, -30, 1600, 2000},
};
const KeyFrame TALK[] = {
    {  0, -10,  90,  60}, {  0,   6,  90,  60},
};

struct AnimDef {
    const KeyFrame* frames;
    uint8_t         count;
    bool            loop;
};

// The order must follow the AnimId enum. IDLE = no keyframes (idle noise only).
const AnimDef ANIMS[ANIM_COUNT] = {
    {nullptr,          0,                                     false},  // ANIM_IDLE
    {LOOK_AROUND,      sizeof(LOOK_AROUND) / sizeof(KeyFrame),      false},
    {NOD_YES,          sizeof(NOD_YES) / sizeof(KeyFrame),          false},
    {SHAKE_NO,         sizeof(SHAKE_NO) / sizeof(KeyFrame),         false},
    {CURIOUS_TILT,     sizeof(CURIOUS_TILT) / sizeof(KeyFrame),     false},
    {SCAN_SLOW,        sizeof(SCAN_SLOW) / sizeof(KeyFrame),        false},
    {ALERT_SNAP,       sizeof(ALERT_SNAP) / sizeof(KeyFrame),       false},
    {TRACK,            sizeof(TRACK) / sizeof(KeyFrame),            false},
    {GLITCH_STUTTER,   sizeof(GLITCH_STUTTER) / sizeof(KeyFrame),   false},
    {CONFUSED_TILT,    sizeof(CONFUSED_TILT) / sizeof(KeyFrame),    false},
    {DOUBLE_TAKE,      sizeof(DOUBLE_TAKE) / sizeof(KeyFrame),      false},
    {SLEEPY_DROOP,     sizeof(SLEEPY_DROOP) / sizeof(KeyFrame),     false},
    {TARGET_LOCK,      sizeof(TARGET_LOCK) / sizeof(KeyFrame),      false},
    {WHIRR_SEARCH,     sizeof(WHIRR_SEARCH) / sizeof(KeyFrame),     false},
    {SIGNAL_GLITCH,    sizeof(SIGNAL_GLITCH) / sizeof(KeyFrame),    false},
    {GREETING_NOD,     sizeof(GREETING_NOD) / sizeof(KeyFrame),     false},
    {POWER_DOWN,       sizeof(POWER_DOWN) / sizeof(KeyFrame),       true},   // loops
    {TALK,             sizeof(TALK) / sizeof(KeyFrame),             true},   // loops
};

}  // namespace

void AnimationPlayer::begin(ServoEngine* engine) {
    _engine = engine;
    _playing = false;
}

uint8_t AnimationPlayer::rnd(uint8_t n) {
    _rng = _rng * 1103515245u + 12345u;
    return n ? (uint8_t)((_rng >> 16) % n) : 0;
}

int AnimationPlayer::jitter(uint8_t amp) {
    if (amp == 0) return 0;
    return (int)rnd(2 * amp + 1) - (int)amp;
}

void AnimationPlayer::setAmpSpeedPct(uint8_t ampPct, uint8_t speedPct) {
    // 60 = the historical default (index.html's original slider value) -> scale 1.0,
    // i.e. passing back the default reproduces today's exact tuning untouched.
    _ampScale = ampPct / 60.0f;
    if (_ampScale < 0.0f) _ampScale = 0.0f;
    if (_ampScale > 1.7f) _ampScale = 1.7f;

    // 50 = the historical default -> scale 1.0. Floored at speedPct=10 (not 0) so a
    // near-zero slider can't blow the multiplier up past a merely "very slow" droid.
    float s = 50.0f / (float)(speedPct < 10 ? 10 : speedPct);
    if (s < 0.4f) s = 0.4f;
    if (s > 4.0f) s = 4.0f;
    _speedScale = s;
}

uint8_t AnimationPlayer::randomAnimId(uint32_t seed) {
    // "Active" anims eligible for random draw: 1..ANIM_POWER_DOWN-1 (excludes IDLE, and
    // excludes POWER_DOWN/TALK which are manual-trigger-only gestures).
    uint32_t r = seed * 1103515245u + 12345u;
    return 1 + (uint8_t)((r >> 16) % (ANIM_POWER_DOWN - 1));
}

uint32_t AnimationPlayer::totalDurationMs(uint8_t animId) {
    if (animId >= ANIM_COUNT) return 0;
    const AnimDef& a = ANIMS[animId];
    uint32_t total = 0;
    for (uint8_t i = 0; i < a.count; i++) {
        total += a.frames[i].moveMs + a.frames[i].holdMs;
    }
    return total;
}

uint8_t AnimationPlayer::frameCount(uint8_t animId) {
    return animId < ANIM_COUNT ? ANIMS[animId].count : 0;
}

bool AnimationPlayer::isInfinite(uint8_t animId) {
    return animId < ANIM_COUNT && ANIMS[animId].loop;
}

void AnimationPlayer::play(uint8_t animId, uint32_t seed) {
    if (!_engine || animId >= ANIM_COUNT) return;
    _animId = animId;
    _rng = seed ? seed : 1;
    _idx = 0;
    _holding = false;

    // IDLE or empty animation: leave the head in idle noise.
    if (ANIMS[animId].count == 0) {
        _playing = false;
        _engine->center(600);
        return;
    }
    _playing = true;
    _needMove = true;
}

void AnimationPlayer::issueCurrentFrame() {
    const AnimDef& a = ANIMS[_animId];
    const KeyFrame& f = a.frames[_idx];

    // Offsets are relative to THIS droid's calibrated center (ServoEngine),
    // not the compile-time 90-degree defaults. Jitter itself is not scaled:
    // it is a fixed organic-realism detail, not the gesture's amplitude.
    const float panOffset  = f.panOff  * _ampScale + jitter(4);
    const float tiltOffset = f.tiltOff * _ampScale + jitter(3);

    // Do the whole randomized calculation signed, then clamp before narrowing.
    // The shortest 50 ms frames can otherwise become negative at high speed
    // and wrap to ~65 seconds when converted to uint16_t.
    const int32_t randomizedMoveMs = (int32_t)(f.moveMs * _speedScale) + jitter(6) * 10;
    const uint16_t move = clampMoveDurationMs(randomizedMoveMs);

    _engine->setTargetOffset(panOffset, tiltOffset, move);
    _holdDur = (uint16_t)(f.holdMs * _speedScale);
    _needMove = false;
    _holding = false;
}

void AnimationPlayer::update() {
    if (!_playing || !_engine) return;
    if (_engine->isMoving()) return;

    // Triggers the move toward the current keyframe.
    if (_needMove) {
        issueCurrentFrame();
        return;
    }

    // Arrived: handles the hold time.
    const uint32_t now = millis();
    if (!_holding) {
        _holding = true;
        _holdStart = now;
        return;
    }
    if (now - _holdStart < _holdDur) return;

    // Advances to the next keyframe.
    _idx++;
    const AnimDef& a = ANIMS[_animId];
    if (_idx >= a.count) {
        if (a.loop) {
            _idx = 0;
        } else {
            _playing = false;
            return;
        }
    }
    _needMove = true;
}
