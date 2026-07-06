#pragma once

// ============================================================================
//  SerialConsole — pont JSON sur l'USB pour la console web (maître)
//
//  Protocole : une ligne = un message JSON (voir project.md §10).
//  - PC → maître : {cmd:"list"|"anim"|"config"|"volume"|"name"|"playTrack"|
//                   "getConfig", ...}
//  - maître → PC : {evt:"droids"|"log"|"state", ...}
//
//  Les logs applicatifs passent par log() pour rester au format JSON et ne pas
//  polluer le protocole. Des hooks permettent au firmware d'agir sur les
//  commandes (jouer une anim, régler le volume, etc.).
// ============================================================================

#include <Arduino.h>
#include "sequence_store.h"

class SerialConsole {
public:
    void begin();

    // Lit et traite les commandes entrantes (à appeler dans loop()).
    void update();

    // Émet un log au format {evt:"log","msg":...}.
    void log(const char* fmt, ...);

    // Émet la liste des droïdes ({evt:"droids",...}) et l'état ({evt:"state"}).
    void pushDroids();
    void pushState();

    // Hooks optionnels déclenchés par les commandes reçues.
    void onAnim(void (*cb)(uint8_t animId, uint32_t seed)) { _animCb = cb; }
    void onVolume(void (*cb)(uint8_t volume)) { _volCb = cb; }
    void onTrack(void (*cb)(uint8_t track)) { _trackCb = cb; }
    void onConfig(void (*cb)(uint8_t freq, uint8_t amp, uint8_t speed)) { _cfgCb = cb; }
    void onServo(void (*cb)(uint16_t target, bool enabled)) { _servoCb = cb; }
    void onSeqSave(bool (*cb)(uint8_t slot, const StoredSequence& seq)) { _seqSaveCb = cb; }
    void onSeqList(uint8_t (*cb)(StoredSequenceMeta* out, uint8_t maxOut)) { _seqListCb = cb; }
    void onSeqLoad(bool (*cb)(uint8_t slot, StoredSequence& out)) { _seqLoadCb = cb; }
    void onSeqRun(void (*cb)(uint8_t slot)) { _seqRunCb = cb; }
    void onSeqStop(void (*cb)()) { _seqStopCb = cb; }
    void onSeqDelete(bool (*cb)(uint8_t slot)) { _seqDeleteCb = cb; }

    // État servos du maître (pour l'afficher dans la liste).
    void setMasterServos(bool on) { _masterServos = on; }

private:
    char     _buf[256];
    uint16_t _len = 0;
    bool     _masterServos = true;

    void (*_animCb)(uint8_t, uint32_t) = nullptr;
    void (*_volCb)(uint8_t) = nullptr;
    void (*_trackCb)(uint8_t) = nullptr;
    void (*_cfgCb)(uint8_t, uint8_t, uint8_t) = nullptr;
    void (*_servoCb)(uint16_t, bool) = nullptr;
    bool (*_seqSaveCb)(uint8_t, const StoredSequence&) = nullptr;
    uint8_t (*_seqListCb)(StoredSequenceMeta*, uint8_t) = nullptr;
    bool (*_seqLoadCb)(uint8_t, StoredSequence&) = nullptr;
    void (*_seqRunCb)(uint8_t) = nullptr;
    void (*_seqStopCb)() = nullptr;
    bool (*_seqDeleteCb)(uint8_t) = nullptr;

    void handleLine(const char* line);
    void pushSeqList();
    void pushSeqData(uint8_t slot, const StoredSequence& seq);
};

extern SerialConsole Console;
