#include "sequence_store.h"

SequenceStore Sequences;

namespace {
const char* NVS_NS = "b1seq";

struct SequenceBlob {
    char    name[StoredSequence::NAME_LEN];
    uint8_t loop;
    uint8_t stepCount;
    SeqStep steps[StoredSequence::STEP_MAX];
    uint8_t track;   // added at the end of the blob: old blobs (without this
                     // field) stay readable, track then defaults to 0 (none)
};

// Blob size before `track` was added (accepted on read, never written).
const size_t BLOB_LEN_V1 = sizeof(SequenceBlob) - sizeof(uint8_t);

// Reads a blob, accepting both the old and new size. blob must be
// zero-initialized by the caller (track=0 for an old blob).
bool readBlob(Preferences& p, const char* key, SequenceBlob& blob) {
    const size_t got = p.getBytes(key, &blob, sizeof(blob));
    return got == sizeof(blob) || got == BLOB_LEN_V1;
}
}

void SequenceStore::begin() {
    _p.begin(NVS_NS, false);
}

void SequenceStore::slotKey(uint8_t slot, char out[8]) {
    snprintf(out, 8, "s%02u", slot);
}

bool SequenceStore::save(uint8_t slot, const StoredSequence& seq) {
    if (!validSlot(slot)) return false;

    SequenceBlob blob{};
    strncpy(blob.name, seq.name, sizeof(blob.name) - 1);
    blob.name[sizeof(blob.name) - 1] = '\0';
    blob.loop = seq.loop ? 1 : 0;
    blob.stepCount = seq.stepCount > StoredSequence::STEP_MAX ? StoredSequence::STEP_MAX : seq.stepCount;
    blob.track = seq.track;

    for (uint8_t i = 0; i < blob.stepCount; i++) {
        blob.steps[i] = seq.steps[i];
    }

    char key[8];
    slotKey(slot, key);
    return _p.putBytes(key, &blob, sizeof(blob)) == sizeof(blob);
}

bool SequenceStore::load(uint8_t slot, StoredSequence& out) {
    if (!validSlot(slot)) return false;

    char key[8];
    slotKey(slot, key);
    if (!_p.isKey(key)) return false;

    SequenceBlob blob{};
    if (!readBlob(_p, key, blob)) return false;

    memset(&out, 0, sizeof(out));
    strncpy(out.name, blob.name, sizeof(out.name) - 1);
    out.loop = blob.loop ? 1 : 0;
    out.stepCount = blob.stepCount > StoredSequence::STEP_MAX ? StoredSequence::STEP_MAX : blob.stepCount;
    out.track = blob.track;
    for (uint8_t i = 0; i < out.stepCount; i++) {
        out.steps[i] = blob.steps[i];
    }
    return true;
}

bool SequenceStore::remove(uint8_t slot) {
    if (!validSlot(slot)) return false;

    char key[8];
    slotKey(slot, key);
    if (!_p.isKey(key)) return true;
    return _p.remove(key);
}

uint8_t SequenceStore::list(StoredSequenceMeta* out, uint8_t maxOut) {
    if (!out || maxOut == 0) return 0;

    uint8_t n = 0;
    for (uint8_t slot = 0; slot < SLOT_MAX && n < maxOut; slot++) {
        char key[8];
        slotKey(slot, key);
        if (!_p.isKey(key)) continue;

        SequenceBlob blob{};
        if (!readBlob(_p, key, blob)) continue;

        StoredSequenceMeta& m = out[n++];
        m.slot = slot;
        strncpy(m.name, blob.name, sizeof(m.name) - 1);
        m.name[sizeof(m.name) - 1] = '\0';
        m.loop = blob.loop ? 1 : 0;
        m.stepCount = blob.stepCount > StoredSequence::STEP_MAX ? StoredSequence::STEP_MAX : blob.stepCount;
        m.track = blob.track;
    }

    return n;
}
