#pragma once

// ============================================================================
//  SequenceStore — sequences persisted in NVS (master)
//
//  Stores up to SEQ_SLOT_MAX named sequences. Each sequence contains a list
//  of steps (animId, target, delay) and a loop mode.
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
    uint8_t track;   // master's audio track (1-10), 0 = none
};

struct StoredSequenceMeta {
    uint8_t slot;
    char    name[StoredSequence::NAME_LEN];
    uint8_t loop;
    uint8_t stepCount;
    uint8_t track;   // 0 = none
};

class SequenceStore {
public:
    static const uint8_t SLOT_MAX = 8;

    void begin();

    bool save(uint8_t slot, const StoredSequence& seq);
    bool load(uint8_t slot, StoredSequence& out);
    bool remove(uint8_t slot);

    // List of existing sequences. Returns the count written to out.
    uint8_t list(StoredSequenceMeta* out, uint8_t maxOut);

private:
    Preferences _p;

    static bool validSlot(uint8_t slot) { return slot < SLOT_MAX; }
    static void slotKey(uint8_t slot, char out[8]);
};

extern SequenceStore Sequences;
