#pragma once

// ============================================================================
//  SequenceStore — séquences persistées en NVS (maître)
//
//  Stocke jusqu'à SEQ_SLOT_MAX séquences nommées. Chaque séquence contient
//  une liste d'étapes (animId, cible, délai) et un mode boucle.
// ============================================================================

#include <Arduino.h>
#include <Preferences.h>

struct SeqStep {
    uint16_t targetId;
    uint8_t  animId;
    uint16_t delayMs;
};

struct StoredSequence {
    static const uint8_t NAME_LEN = 24;
    static const uint8_t STEP_MAX = 32;

    char    name[NAME_LEN];
    uint8_t loop;
    uint8_t stepCount;
    SeqStep steps[STEP_MAX];
};

struct StoredSequenceMeta {
    uint8_t slot;
    char    name[StoredSequence::NAME_LEN];
    uint8_t loop;
    uint8_t stepCount;
};

class SequenceStore {
public:
    static const uint8_t SLOT_MAX = 8;

    void begin();

    bool save(uint8_t slot, const StoredSequence& seq);
    bool load(uint8_t slot, StoredSequence& out);
    bool remove(uint8_t slot);

    // Liste des séquences existantes. Retourne le nombre écrit dans out.
    uint8_t list(StoredSequenceMeta* out, uint8_t maxOut);

private:
    Preferences _p;

    static bool validSlot(uint8_t slot) { return slot < SLOT_MAX; }
    static void slotKey(uint8_t slot, char out[8]);
};

extern SequenceStore Sequences;
