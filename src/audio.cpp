#include "audio.h"

#include "config.h"
#include <DFRobotDFPlayerMini.h>

AudioPlayer Audio;

namespace {
struct TrackRange {
    uint8_t first;
    uint8_t last;
};

// Index = animId (0..17, voir animation.h). Les plages restent dans 1..AUDIO_TRACK_COUNT.
// Les nouveaux gestes (8..17) réutilisent les pistes existantes par proximité
// thématique — le projet n'a que 10 pistes fixes, à ajuster à l'oreille.
const TrackRange ANIM_TRACKS[] = {
    {1, 2},   // IDLE
    {1, 2},   // LOOK_AROUND
    {3, 4},   // NOD_YES
    {5, 6},   // SHAKE_NO
    {7, 7},   // CURIOUS_TILT
    {8, 8},   // SCAN_SLOW
    {9, 9},   // ALERT_SNAP
    {10, 10}, // TRACK
    {9, 9},   // GLITCH_STUTTER
    {7, 7},   // CONFUSED_TILT
    {7, 7},   // DOUBLE_TAKE
    {1, 2},   // SLEEPY_DROOP
    {9, 9},   // TARGET_LOCK
    {8, 8},   // WHIRR_SEARCH
    {9, 9},   // SIGNAL_GLITCH
    {3, 4},   // GREETING_NOD
    {1, 2},   // POWER_DOWN
    {1, 10},  // TALK (n'importe quelle piste — accompagne une réplique quelconque)
};
}

uint8_t AudioPlayer::clampTrack(uint8_t track) {
    if (track < 1) return 1;
    if (track > AUDIO_TRACK_COUNT) return AUDIO_TRACK_COUNT;
    return track;
}

static uint8_t clampVolume(uint8_t volume) {
    return volume > 30 ? 30 : volume;
}

uint8_t AudioPlayer::pickTrackForAnim(uint8_t animId, uint32_t seed) {
    if (animId >= (sizeof(ANIM_TRACKS) / sizeof(ANIM_TRACKS[0]))) return 1;

    TrackRange r = ANIM_TRACKS[animId];
    r.first = clampTrack(r.first);
    r.last = clampTrack(r.last);
    if (r.last < r.first) r.last = r.first;

    const uint8_t span = (uint8_t)(r.last - r.first + 1);
    const uint32_t x = seed * 1103515245u + 12345u;
    const uint8_t offset = span > 0 ? (uint8_t)((x >> 16) % span) : 0;
    return (uint8_t)(r.first + offset);
}

void AudioPlayer::begin() {
    _serial.begin(9600, SERIAL_8N1, PIN_DFPLAYER_RX, PIN_DFPLAYER_TX);

    _df = new DFRobotDFPlayerMini();
    _ready = _df->begin(_serial, true, true);
    if (!_ready) return;

    _df->setTimeOut(300);
    _df->volume(clampVolume(_volume));
}

void AudioPlayer::setVolume(uint8_t volume) {
    _volume = clampVolume(volume);
    if (_ready) _df->volume(_volume);
}

bool AudioPlayer::playTrack(uint8_t track) {
    if (!_ready) return false;
    const uint8_t t = clampTrack(track);
    _df->playMp3Folder(t);
    return true;
}

bool AudioPlayer::playForAnim(uint8_t animId, uint32_t seed) {
    const uint8_t track = pickTrackForAnim(animId, seed);
    return playTrack(track);
}

void AudioPlayer::pause() {
    if (_ready) _df->pause();
}

void AudioPlayer::resume() {
    if (_ready) _df->start();
}
