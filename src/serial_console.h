#pragma once

// ============================================================================
//  SerialConsole — pont JSON sur l'USB pour la console web (maître)
//
//  Protocole : une ligne = un message JSON (voir project.md §10).
//  - PC → maître : {cmd:"list"|"anim"|"config"|"volume"|"name"|"playTrack"|
//                   "getConfig"|"calib"|"preview"|"getCalib"|"getAnimDurations"|
//                   "seqState"|"servo"|"autoAnim"|"getMeshTopology", ...}
//  - maître → PC : {evt:"droids"|"log"|"state"|"meshTopology"|"err", ...}
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

    // Émet une erreur explicite au format {evt:"err","msg":...} — commande
    // inconnue/invalide, ligne tronquée, JSON invalide. La console l'affiche
    // en rouge au lieu que l'échec soit silencieux.
    void pushErr(const char* fmt, ...);

    // Émet la liste des droïdes ({evt:"droids",...}) et l'état ({evt:"state"}).
    void pushDroids();
    void pushState();

    // Émet la durée indicative (ms) de chaque geste ({evt:"animDurations",...}).
    void pushAnimDurations();

    // Émet l'état de lecture d'une séquence ({evt:"seqState",...}).
    void pushSeqState(bool playing, uint8_t slot, uint8_t index, uint8_t total);

    // Émet les liens directs du mesh détectés ({evt:"meshTopology",...}).
    void pushMeshTopology();

    // Hooks optionnels déclenchés par les commandes reçues.
    void onAnim(void (*cb)(uint8_t animId, uint32_t seed)) { _animCb = cb; }
    void onVolume(void (*cb)(uint8_t volume)) { _volCb = cb; }
    void onTrack(void (*cb)(uint8_t track)) { _trackCb = cb; }
    void onConfig(void (*cb)(uint8_t freq, uint8_t amp, uint8_t speed)) { _cfgCb = cb; }
    void onServo(void (*cb)(uint16_t target, bool enabled)) { _servoCb = cb; }
    void onAutoAnim(void (*cb)(uint16_t target, bool enabled)) { _autoAnimCb = cb; }
    void onSeqSave(bool (*cb)(uint8_t slot, const StoredSequence& seq)) { _seqSaveCb = cb; }
    void onSeqList(uint8_t (*cb)(StoredSequenceMeta* out, uint8_t maxOut)) { _seqListCb = cb; }
    void onSeqLoad(bool (*cb)(uint8_t slot, StoredSequence& out)) { _seqLoadCb = cb; }
    void onSeqRun(void (*cb)(uint8_t slot)) { _seqRunCb = cb; }
    void onSeqStop(void (*cb)()) { _seqStopCb = cb; }
    void onSeqDelete(bool (*cb)(uint8_t slot)) { _seqDeleteCb = cb; }
    void onCalib(void (*cb)(uint16_t target, uint8_t panMin, uint8_t panCenter, uint8_t panMax,
                            uint8_t tiltMin, uint8_t tiltCenter, uint8_t tiltMax)) { _calibCb = cb; }
    void onPreview(void (*cb)(uint16_t target, uint8_t pan, uint8_t tilt)) { _previewCb = cb; }
    void onSeqQuery(void (*cb)()) { _seqQueryCb = cb; }

    // État servos du maître (pour l'afficher dans la liste).
    void setMasterServos(bool on) { _masterServos = on; }

    // État anims auto du maître (pour l'afficher dans la liste).
    void setMasterAutoAnim(bool on) { _masterAutoAnim = on; }

    // Session Web Serial validée par handshake hello/ping.
    bool isClientReady() const { return _clientReady; }

private:
    // Tampon de ligne : 4 Ko pour accepter seqSave 32 étapes et setMulti.
    // (256 o auparavant : toute ligne plus longue était jetée en silence.)
    static const uint16_t SERIAL_LINE_MAX = 4096;
    char     _buf[SERIAL_LINE_MAX];
    uint16_t _len = 0;
    bool     _overflow = false;
    bool     _masterServos = true;
    bool     _masterAutoAnim = true;
    bool     _clientReady = false;
    uint32_t _lastHelloMs = 0;
    static const uint32_t CLIENT_TIMEOUT_MS = 5000;

    void (*_animCb)(uint8_t, uint32_t) = nullptr;
    void (*_volCb)(uint8_t) = nullptr;
    void (*_trackCb)(uint8_t) = nullptr;
    void (*_cfgCb)(uint8_t, uint8_t, uint8_t) = nullptr;
    void (*_servoCb)(uint16_t, bool) = nullptr;
    void (*_autoAnimCb)(uint16_t, bool) = nullptr;
    bool (*_seqSaveCb)(uint8_t, const StoredSequence&) = nullptr;
    uint8_t (*_seqListCb)(StoredSequenceMeta*, uint8_t) = nullptr;
    bool (*_seqLoadCb)(uint8_t, StoredSequence&) = nullptr;
    void (*_seqRunCb)(uint8_t) = nullptr;
    void (*_seqStopCb)() = nullptr;
    bool (*_seqDeleteCb)(uint8_t) = nullptr;
    void (*_calibCb)(uint16_t, uint8_t, uint8_t, uint8_t, uint8_t, uint8_t, uint8_t) = nullptr;
    void (*_previewCb)(uint16_t, uint8_t, uint8_t) = nullptr;
    void (*_seqQueryCb)() = nullptr;

    void handleLine(const char* line);
    void pushSeqList();
    void pushSeqData(uint8_t slot, const StoredSequence& seq);
    void pushCalibData(uint16_t target);
    void pushSeqSaved(bool ok, uint8_t slot, const char* name);
    void pushSeqDeleted(bool ok, uint8_t slot);
};

extern SerialConsole Console;
