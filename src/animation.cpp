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

// L'ordre doit suivre l'enum AnimId. IDLE = pas de keyframes (bruit d'idle seul).
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
    {POWER_DOWN,       sizeof(POWER_DOWN) / sizeof(KeyFrame),       true},   // boucle
    {TALK,             sizeof(TALK) / sizeof(KeyFrame),             true},   // boucle
};

// Valeur indicative (ms) utilisée pour les gestes sans durée finie naturelle
// (IDLE : pas de keyframes ; POWER_DOWN/TALK : bouclent indéfiniment).
const uint32_t LOOPING_ANIM_DEFAULT_MS = 2000;

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
    // Anims « actives » tirables au hasard : 1..ANIM_POWER_DOWN-1 (exclut IDLE, et
    // exclut POWER_DOWN/TALK qui sont des gestes à déclenchement manuel uniquement).
    uint32_t r = seed * 1103515245u + 12345u;
    return 1 + (uint8_t)((r >> 16) % (ANIM_POWER_DOWN - 1));
}

uint32_t AnimationPlayer::totalDurationMs(uint8_t animId) {
    if (animId >= ANIM_COUNT) return 0;
    const AnimDef& a = ANIMS[animId];
    if (a.count == 0 || a.loop) return LOOPING_ANIM_DEFAULT_MS;
    uint32_t total = 0;
    for (uint8_t i = 0; i < a.count; i++) {
        total += a.frames[i].moveMs + a.frames[i].holdMs;
    }
    return total;
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
