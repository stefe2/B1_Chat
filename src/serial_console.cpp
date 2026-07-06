#include "serial_console.h"
#include "config.h"
#include "mesh_comm.h"
#include "registry.h"
#include "config_store.h"

#include <ArduinoJson.h>
#include <stdarg.h>

SerialConsole Console;

void SerialConsole::begin() {
    _len = 0;
}

void SerialConsole::log(const char* fmt, ...) {
    char msg[200];
    va_list ap;
    va_start(ap, fmt);
    vsnprintf(msg, sizeof(msg), fmt, ap);
    va_end(ap);

    JsonDocument doc;
    doc["evt"] = "log";
    doc["msg"] = msg;
    serializeJson(doc, Serial);
    Serial.print('\n');
}

void SerialConsole::pushDroids() {
    const uint32_t now = millis();
    JsonDocument doc;
    doc["evt"] = "droids";
    JsonArray arr = doc["list"].to<JsonArray>();

    // Le maître lui-même (absent du registry car il ignore ses propres messages).
    JsonObject me = arr.add<JsonObject>();
    me["id"] = Mesh.myId();
    me["name"] = Config.getName(Mesh.myId());
    me["rssi"] = 0;
    me["age"] = 0;
    me["role"] = "master";
    me["servos"] = _masterServos;

    // Les autres droïdes (esclaves).
    for (uint8_t i = 0; i < Droids.count(); i++) {
        const Registry::Entry& e = Droids.at(i);
        JsonObject o = arr.add<JsonObject>();
        o["id"] = e.id;
        o["name"] = Config.getName(e.id);
        o["rssi"] = e.rssi;
        o["age"] = now - e.lastSeen;   // ms depuis la dernière vue
        o["role"] = "slave";
        o["servos"] = e.servos;
    }
    serializeJson(doc, Serial);
    Serial.print('\n');
}

void SerialConsole::pushState() {
    uint8_t f, a, s;
    Config.animParams(f, a, s);
    JsonDocument doc;
    doc["evt"] = "state";
    doc["volume"] = Config.volume();
    doc["freq"] = f;
    doc["amp"] = a;
    doc["speed"] = s;
    serializeJson(doc, Serial);
    Serial.print('\n');
}

void SerialConsole::pushSeqList() {
    JsonDocument doc;
    doc["evt"] = "seqList";
    JsonArray arr = doc["list"].to<JsonArray>();

    if (_seqListCb) {
        StoredSequenceMeta metas[SequenceStore::SLOT_MAX];
        const uint8_t n = _seqListCb(metas, SequenceStore::SLOT_MAX);
        for (uint8_t i = 0; i < n; i++) {
            JsonObject o = arr.add<JsonObject>();
            o["slot"] = metas[i].slot;
            o["name"] = metas[i].name;
            o["loop"] = metas[i].loop;
            o["stepCount"] = metas[i].stepCount;
        }
    }

    serializeJson(doc, Serial);
    Serial.print('\n');
}

void SerialConsole::pushSeqData(uint8_t slot, const StoredSequence& seq) {
    JsonDocument doc;
    doc["evt"] = "seqData";
    doc["slot"] = slot;
    doc["name"] = seq.name;
    doc["loop"] = seq.loop;

    JsonArray steps = doc["steps"].to<JsonArray>();
    for (uint8_t i = 0; i < seq.stepCount; i++) {
        JsonObject s = steps.add<JsonObject>();
        s["animId"] = seq.steps[i].animId;
        s["target"] = seq.steps[i].targetId;
        s["delay"] = seq.steps[i].delayMs;
    }

    serializeJson(doc, Serial);
    Serial.print('\n');
}

void SerialConsole::update() {
    while (Serial.available()) {
        const char c = (char)Serial.read();
        if (c == '\n' || c == '\r') {
            if (_len > 0) {
                _buf[_len] = '\0';
                handleLine(_buf);
                _len = 0;
            }
        } else if (_len < sizeof(_buf) - 1) {
            _buf[_len++] = c;
        } else {
            _len = 0;  // ligne trop longue : on jette
        }
    }
}

void SerialConsole::handleLine(const char* line) {
    JsonDocument doc;
    if (deserializeJson(doc, line)) return;  // JSON invalide → ignoré

    const char* cmd = doc["cmd"] | "";

    if (!strcmp(cmd, "list")) {
        pushDroids();

    } else if (!strcmp(cmd, "getConfig")) {
        pushState();

    } else if (!strcmp(cmd, "anim")) {
        const uint16_t target = doc["target"] | (uint16_t)MESH_TARGET_ALL;
        const uint8_t  animId = doc["animId"] | 0;
        const uint32_t seed   = doc["seed"] | (uint32_t)esp_random();
        AnimPayload p{target, animId, 0, seed};
        Mesh.send(MSG_ANIM, &p, sizeof(p));
        if ((target == MESH_TARGET_ALL || target == Mesh.myId()) && _animCb)
            _animCb(animId, seed);
        log("anim %u -> %04X", animId, target);

    } else if (!strcmp(cmd, "config")) {
        const uint16_t target = doc["target"] | (uint16_t)MESH_TARGET_ALL;
        const uint8_t freq  = doc["freq"] | 50;
        const uint8_t amp   = doc["amp"]  | 60;
        const uint8_t speed = doc["speed"] | 50;
        Config.setAnimParams(freq, amp, speed);
        ConfigPayload p{target, (float)freq, (float)amp, (float)speed};
        Mesh.send(MSG_CONFIG, &p, sizeof(p));
        if (_cfgCb) _cfgCb(freq, amp, speed);
        log("params freq=%u amp=%u speed=%u", freq, amp, speed);

    } else if (!strcmp(cmd, "volume")) {
        const uint8_t v = doc["value"] | AUDIO_VOLUME_DEFAULT;
        Config.setVolume(v);
        if (_volCb) _volCb(v);
        log("volume=%u", v);

    } else if (!strcmp(cmd, "name")) {
        const uint16_t id = doc["id"] | 0;
        const char* name = doc["name"] | "";
        Config.setName(id, name);
        log("nom %04X = %s", id, name);
        pushDroids();

    } else if (!strcmp(cmd, "playTrack")) {
        const uint8_t track = doc["track"] | 1;
        if (_trackCb) _trackCb(track);
        log("piste %u", track);

    } else if (!strcmp(cmd, "servo")) {
        const uint16_t target = doc["target"] | (uint16_t)MESH_TARGET_ALL;
        const bool en = doc["enabled"] | false;
        if (_servoCb) _servoCb(target, en);
        log("servos %s -> %04X", en ? "ON" : "OFF", target);

    } else if (!strcmp(cmd, "seqList")) {
        pushSeqList();

    } else if (!strcmp(cmd, "seqSave")) {
        const uint8_t slot = doc["slot"] | 0;
        StoredSequence seq{};
        const char* name = doc["name"] | "Sequence";
        strncpy(seq.name, name, sizeof(seq.name) - 1);
        seq.name[sizeof(seq.name) - 1] = '\0';
        seq.loop = (doc["loop"] | false) ? 1 : 0;

        JsonArrayConst steps = doc["steps"].as<JsonArrayConst>();
        uint8_t idx = 0;
        for (JsonObjectConst s : steps) {
            if (idx >= StoredSequence::STEP_MAX) break;
            seq.steps[idx].animId = s["animId"] | 0;
            seq.steps[idx].targetId = s["target"] | (uint16_t)MESH_TARGET_ALL;
            seq.steps[idx].delayMs = s["delay"] | 0;
            idx++;
        }
        seq.stepCount = idx;

        bool ok = false;
        if (_seqSaveCb) ok = _seqSaveCb(slot, seq);
        log("seq save slot=%u %s", slot, ok ? "OK" : "ERR");
        pushSeqList();

    } else if (!strcmp(cmd, "seqLoad")) {
        const uint8_t slot = doc["slot"] | 0;
        StoredSequence seq{};
        bool ok = false;
        if (_seqLoadCb) ok = _seqLoadCb(slot, seq);
        if (ok) {
            pushSeqData(slot, seq);
            log("seq load slot=%u OK", slot);
        } else {
            log("seq load slot=%u ERR", slot);
        }

    } else if (!strcmp(cmd, "seqDelete")) {
        const uint8_t slot = doc["slot"] | 0;
        bool ok = false;
        if (_seqDeleteCb) ok = _seqDeleteCb(slot);
        log("seq del slot=%u %s", slot, ok ? "OK" : "ERR");
        pushSeqList();

    } else if (!strcmp(cmd, "seqRun")) {
        const uint8_t slot = doc["slot"] | 0;
        if (_seqRunCb) _seqRunCb(slot);
        log("seq run slot=%u", slot);

    } else if (!strcmp(cmd, "seqStop")) {
        if (_seqStopCb) _seqStopCb();
        log("seq stop");
    }
}
