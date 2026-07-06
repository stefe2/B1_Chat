#include "animation.h"
#include "config.h"

// ---------------------------------------------------------------------------
//  Keyframes : offsets (degrés) par rapport au centre. moveMs = durée du
//  déplacement (easing), holdMs = maintien avant la keyframe suivante.
// ---------------------------------------------------------------------------
struct KeyFrame {
    int8_t   panOff;
    int8_t   tiltOff;
    uint16_t moveMs;
    uint16_t holdMs;
};

namespace {

// -- Définitions des animations (offsets depuis le centre) ------------------
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

struct AnimDef {
    const KeyFrame* frames;
    uint8_t         count;
    bool            loop;
};

// L'ordre doit suivre l'enum AnimId. IDLE = pas de keyframes (bruit d'idle seul).
const AnimDef ANIMS[ANIM_COUNT] = {
    {nullptr,       0,                              false},  // ANIM_IDLE
    {LOOK_AROUND,   sizeof(LOOK_AROUND) / sizeof(KeyFrame),  false},
    {NOD_YES,       sizeof(NOD_YES) / sizeof(KeyFrame),      false},
    {SHAKE_NO,      sizeof(SHAKE_NO) / sizeof(KeyFrame),     false},
    {CURIOUS_TILT,  sizeof(CURIOUS_TILT) / sizeof(KeyFrame), false},
    {SCAN_SLOW,     sizeof(SCAN_SLOW) / sizeof(KeyFrame),    false},
    {ALERT_SNAP,    sizeof(ALERT_SNAP) / sizeof(KeyFrame),   false},
    {TRACK,         sizeof(TRACK) / sizeof(KeyFrame),        false},
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

uint8_t AnimationPlayer::randomAnimId(uint32_t seed) {
    // Anims « actives » : 1..ANIM_COUNT-1 (on exclut IDLE).
    uint32_t r = seed * 1103515245u + 12345u;
    return 1 + (uint8_t)((r >> 16) % (ANIM_COUNT - 1));
}

void AnimationPlayer::play(uint8_t animId, uint32_t seed) {
    if (!_engine || animId >= ANIM_COUNT) return;
    _animId = animId;
    _rng = seed ? seed : 1;
    _idx = 0;
    _holding = false;

    // IDLE ou animation vide : on laisse la tête en bruit d'idle.
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

    // Cible absolue = centre + offset + léger jitter organique.
    const float pan  = SERVO_PAN_CENTER  + f.panOff  + jitter(4);
    const float tilt = SERVO_TILT_CENTER + f.tiltOff + jitter(3);
    const uint16_t move = f.moveMs + (uint16_t)(jitter(6) * 10);

    _engine->setTarget(pan, tilt, move);
    _holdDur = f.holdMs;
    _needMove = false;
    _holding = false;
}

void AnimationPlayer::update() {
    if (!_playing || !_engine) return;
    if (_engine->isMoving()) return;

    // Déclenche le déplacement vers la keyframe courante.
    if (_needMove) {
        issueCurrentFrame();
        return;
    }

    // Arrivé : gère le temps de maintien.
    const uint32_t now = millis();
    if (!_holding) {
        _holding = true;
        _holdStart = now;
        return;
    }
    if (now - _holdStart < _holdDur) return;

    // Passe à la keyframe suivante.
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
