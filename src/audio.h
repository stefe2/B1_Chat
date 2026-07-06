#pragma once

// ============================================================================
//  AudioPlayer — pilotage DFPlayer Mini (maître)
//
//  - Lecture des pistes /mp3/0001.mp3 ... /mp3/00NN.mp3
//  - Volume persistant (0..30)
//  - Mapping animation -> plage de pistes avec variation pseudo-aléatoire
// ============================================================================

#include <Arduino.h>
#include <HardwareSerial.h>

class DFRobotDFPlayerMini;

class AudioPlayer {
public:
    void begin();

    bool ready() const { return _ready; }

    void setVolume(uint8_t volume);
    uint8_t volume() const { return _volume; }

    bool playTrack(uint8_t track);
    bool playForAnim(uint8_t animId, uint32_t seed);

private:
    HardwareSerial _serial{2};
    DFRobotDFPlayerMini* _df = nullptr;
    bool _ready = false;
    uint8_t _volume = 20;

    static uint8_t clampTrack(uint8_t track);
    static uint8_t pickTrackForAnim(uint8_t animId, uint32_t seed);
};

extern AudioPlayer Audio;
