#include "serial_console.h"
#include "config.h"
#include "mesh_comm.h"
#include "mesh_topology.h"
#include "registry.h"
#include "config_store.h"
#include "animation.h"

#include <ArduinoJson.h>
#include <stdarg.h>

SerialConsole Console;

void SerialConsole::begin() {
    _len = 0;
    _clientReady = false;
    _lastHelloMs = 0;
}

void SerialConsole::log(const char* fmt, ...) {
    if (!_clientReady) return;

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

void SerialConsole::pushErr(const char* fmt, ...) {
    if (!_clientReady) return;

    char msg[200];
    va_list ap;
    va_start(ap, fmt);
    vsnprintf(msg, sizeof(msg), fmt, ap);
    va_end(ap);

    JsonDocument doc;
    doc["evt"] = "err";
    doc["msg"] = msg;
    serializeJson(doc, Serial);
    Serial.print('\n');
}

void SerialConsole::pushDroids() {
    if (!_clientReady) return;

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
    me["autoAnim"] = _masterAutoAnim;

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
        o["autoAnim"] = e.autoAnim;
    }
    serializeJson(doc, Serial);
    Serial.print('\n');
}

void SerialConsole::pushState() {
    if (!_clientReady) return;

    uint8_t f, a, s;
    Config.animParams(f, a, s);
    JsonDocument doc;
    // "config" (contrat §3) : la console peuple ses curseurs volume/freq/amp/
    // speed a la connexion. (Anciennement evt:"state", jamais interprete.)
    doc["evt"] = "config";
    doc["volume"] = Config.volume();
    doc["freq"] = f;
    doc["amp"] = a;
    doc["speed"] = s;
    serializeJson(doc, Serial);
    Serial.print('\n');
}

void SerialConsole::pushAnimDurations() {
    if (!_clientReady) return;

    JsonDocument doc;
    doc["evt"] = "animDurations";
    JsonArray arr = doc["list"].to<JsonArray>();
    for (uint8_t i = 0; i < ANIM_COUNT; i++) {
        JsonObject o = arr.add<JsonObject>();
        o["animId"] = i;
        o["ms"] = AnimationPlayer::totalDurationMs(i);
    }
    serializeJson(doc, Serial);
    Serial.print('\n');
}

void SerialConsole::pushSeqState(bool playing, uint8_t slot, uint8_t index, uint8_t total,
                                 uint8_t track, bool paused) {
    if (!_clientReady) return;

    JsonDocument doc;
    doc["evt"] = "seqState";
    doc["playing"] = playing;
    doc["slot"] = slot;
    doc["index"] = index;
    doc["total"] = total;
    if (track) doc["track"] = track; else doc["track"] = nullptr;
    doc["paused"] = paused;
    serializeJson(doc, Serial);
    Serial.print('\n');
}

void SerialConsole::pushMeshTopology() {
    if (!_clientReady) return;

    const uint32_t now = millis();
    JsonDocument doc;
    doc["evt"] = "meshTopology";
    JsonArray arr = doc["links"].to<JsonArray>();
    for (uint8_t i = 0; i < MeshTopo.count(); i++) {
        if (!MeshTopo.fresh(i, now, NEIGHBOR_STALE_MS)) continue;
        const MeshTopology::Edge& e = MeshTopo.at(i);
        JsonObject o = arr.add<JsonObject>();
        o["from"] = e.from;
        o["to"] = e.to;
        o["rssi"] = e.rssi;
    }
    serializeJson(doc, Serial);
    Serial.print('\n');
}

void SerialConsole::pushSeqSaved(bool ok, uint8_t slot, const char* name) {
    if (!_clientReady) return;

    JsonDocument doc;
    doc["evt"] = "seqSaved";
    doc["ok"] = ok;
    doc["slot"] = slot;
    doc["name"] = name;
    serializeJson(doc, Serial);
    Serial.print('\n');
}

void SerialConsole::pushSeqDeleted(bool ok, uint8_t slot) {
    if (!_clientReady) return;

    JsonDocument doc;
    doc["evt"] = "seqDeleted";
    doc["ok"] = ok;
    doc["slot"] = slot;
    serializeJson(doc, Serial);
    Serial.print('\n');
}

void SerialConsole::pushSeqList() {
    if (!_clientReady) return;

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
            if (metas[i].track) o["track"] = metas[i].track; else o["track"] = nullptr;
        }
    }

    serializeJson(doc, Serial);
    Serial.print('\n');
}

void SerialConsole::pushCalibData(uint16_t target) {
    if (!_clientReady) return;

    const uint16_t t = target == MESH_TARGET_ALL ? Mesh.myId() : target;
    const ServoCalib c = Config.getCalib(t);

    JsonDocument doc;
    doc["evt"] = "calibData";
    doc["target"] = t;
    doc["panMin"] = c.panMin;
    doc["panCenter"] = c.panCenter;
    doc["panMax"] = c.panMax;
    doc["tiltMin"] = c.tiltMin;
    doc["tiltCenter"] = c.tiltCenter;
    doc["tiltMax"] = c.tiltMax;
    serializeJson(doc, Serial);
    Serial.print('\n');
}

void SerialConsole::pushSeqData(uint8_t slot, const StoredSequence& seq) {
    if (!_clientReady) return;

    JsonDocument doc;
    doc["evt"] = "seqData";
    doc["slot"] = slot;
    doc["name"] = seq.name;
    doc["loop"] = seq.loop;
    if (seq.track) doc["track"] = seq.track; else doc["track"] = nullptr;

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
            if (_overflow) {
                // Ligne trop longue : jetée en entier, mais signalée (avant,
                // l'échec était silencieux et la fin de ligne parasitait la
                // ligne suivante).
                pushErr("ligne trop longue (max %u), commande ignoree", SERIAL_LINE_MAX - 1);
                _overflow = false;
                _len = 0;
            } else if (_len > 0) {
                _buf[_len] = '\0';
                handleLine(_buf);
                _len = 0;
            }
        } else if (_overflow) {
            // on avale le reste de la ligne fautive
        } else if (_len < sizeof(_buf) - 1) {
            _buf[_len++] = c;
        } else {
            _overflow = true;
        }
    }

    // Perte de session Web Serial si plus de keepalive.
    if (_clientReady && (millis() - _lastHelloMs > CLIENT_TIMEOUT_MS)) {
        _clientReady = false;
    }
}

// --- setMulti ----------------------------------------------------------------
// Périmètre : les ops d'état persisté utilisées par la restauration de
// sauvegarde. L'atomicité est obtenue par validation complète AVANT toute
// application : un lot refusé ne modifie rien. (Un échec d'écriture NVS en
// cours d'application — rarissime — est signalé par failedAt sans rollback.)

bool SerialConsole::validateOp(JsonObjectConst op, char* why, size_t whyLen) {
    const char* c = op["cmd"] | "";
    if (!strcmp(c, "name") || !strcmp(c, "calib") || !strcmp(c, "volume") ||
        !strcmp(c, "config")) {
        return true;   // champs bornés par nature (uint8/clamp)
    }
    if (!strcmp(c, "seqSave")) {
        const uint8_t slot = op["slot"] | 0;
        const uint8_t track = op["track"] | 0;
        if (slot >= SequenceStore::SLOT_MAX) {
            snprintf(why, whyLen, "seqSave: slot invalide %u", slot);
            return false;
        }
        if (track > AUDIO_TRACK_COUNT) {
            snprintf(why, whyLen, "seqSave: piste invalide %u", track);
            return false;
        }
        return true;
    }
    if (!strcmp(c, "seqDelete")) {
        const uint8_t slot = op["slot"] | 0;
        if (slot >= SequenceStore::SLOT_MAX) {
            snprintf(why, whyLen, "seqDelete: slot invalide %u", slot);
            return false;
        }
        return true;
    }
    snprintf(why, whyLen, "op non supportee: %s", c[0] ? c : "(sans cmd)");
    return false;
}

bool SerialConsole::applyOp(JsonObjectConst op) {
    const char* c = op["cmd"] | "";

    if (!strcmp(c, "name")) {
        Config.setName(op["id"] | 0, op["name"] | "");
        return true;
    }
    if (!strcmp(c, "volume")) {
        const uint8_t v = op["value"] | AUDIO_VOLUME_DEFAULT;
        Config.setVolume(v);
        if (_volCb) _volCb(v);
        return true;
    }
    if (!strcmp(c, "config")) {
        const uint16_t target = op["target"] | (uint16_t)MESH_TARGET_ALL;
        const uint8_t freq  = op["freq"] | 50;
        const uint8_t amp   = op["amp"]  | 60;
        const uint8_t speed = op["speed"] | 50;
        Config.setAnimParams(freq, amp, speed);
        ConfigPayload p{target, (float)freq, (float)amp, (float)speed};
        Mesh.send(MSG_CONFIG, &p, sizeof(p));
        if (_cfgCb) _cfgCb(freq, amp, speed);
        return true;
    }
    if (!strcmp(c, "calib")) {
        const uint16_t target = op["target"] | (uint16_t)MESH_TARGET_ALL;
        const uint8_t panMin     = op["panMin"]     | SERVO_PAN_MIN;
        const uint8_t panCenter  = op["panCenter"]  | SERVO_PAN_CENTER;
        const uint8_t panMax     = op["panMax"]     | SERVO_PAN_MAX;
        const uint8_t tiltMin    = op["tiltMin"]    | SERVO_TILT_MIN;
        const uint8_t tiltCenter = op["tiltCenter"] | SERVO_TILT_CENTER;
        const uint8_t tiltMax    = op["tiltMax"]    | SERVO_TILT_MAX;
        const uint16_t cacheId = target == MESH_TARGET_ALL ? Mesh.myId() : target;
        Config.setCalib(cacheId, ServoCalib{panMin, panCenter, panMax, tiltMin, tiltCenter, tiltMax});
        CalibPayload p{target, panMin, panCenter, panMax, tiltMin, tiltCenter, tiltMax};
        Mesh.send(MSG_CALIB, &p, sizeof(p));
        if ((target == MESH_TARGET_ALL || target == Mesh.myId()) && _calibCb)
            _calibCb(target, panMin, panCenter, panMax, tiltMin, tiltCenter, tiltMax);
        return true;
    }
    if (!strcmp(c, "seqSave")) {
        StoredSequence seq{};
        strncpy(seq.name, op["name"] | "Sequence", sizeof(seq.name) - 1);
        seq.name[sizeof(seq.name) - 1] = '\0';
        seq.loop = (op["loop"] | false) ? 1 : 0;
        seq.track = op["track"] | 0;
        JsonArrayConst steps = op["steps"].as<JsonArrayConst>();
        uint8_t idx = 0;
        for (JsonObjectConst s : steps) {
            if (idx >= StoredSequence::STEP_MAX) break;
            seq.steps[idx].animId = s["animId"] | 0;
            seq.steps[idx].targetId = s["target"] | (uint16_t)MESH_TARGET_ALL;
            seq.steps[idx].delayMs = s["delay"] | 0;
            idx++;
        }
        seq.stepCount = idx;
        return _seqSaveCb ? _seqSaveCb(op["slot"] | 0, seq) : false;
    }
    if (!strcmp(c, "seqDelete")) {
        return _seqDeleteCb ? _seqDeleteCb(op["slot"] | 0) : false;
    }
    return false;   // impossible après validateOp
}

void SerialConsole::handleLine(const char* line) {
    JsonDocument doc;
    if (deserializeJson(doc, line)) {
        pushErr("JSON invalide");
        return;
    }

    const char* cmd = doc["cmd"] | "";

    if (!strcmp(cmd, "hello")) {
        _clientReady = true;
        _lastHelloMs = millis();

        // Handshake enrichi : version + capacités, pour que la console
        // s'adapte au firmware connecté (et propose les mises à jour GitHub).
        JsonDocument ack;
        ack["evt"] = "hello";
        ack["ok"] = true;
        ack["id"] = Mesh.myId();
        ack["fw"] = FW_VERSION;
        ack["proto"] = FW_PROTO;
        ack["lineMax"] = SERIAL_LINE_MAX;
        ack["anims"] = ANIM_COUNT;
        ack["seqSlots"] = SequenceStore::SLOT_MAX;
        ack["trackCount"] = AUDIO_TRACK_COUNT;
        JsonArray caps = ack["caps"].to<JsonArray>();
        caps.add("err");
        caps.add("getAll");
        caps.add("config");
        caps.add("seqTrack");
        caps.add("seqFrom");
        caps.add("seqPause");
        caps.add("setMulti");
        serializeJson(ack, Serial);
        Serial.print('\n');
        return;
    }

    if (!strcmp(cmd, "ping")) {
        if (_clientReady) _lastHelloMs = millis();
        return;
    }

    if (!_clientReady) return;
    _lastHelloMs = millis();

    if (!strcmp(cmd, "list")) {
        pushDroids();

    } else if (!strcmp(cmd, "getConfig")) {
        pushState();

    } else if (!strcmp(cmd, "getAnimDurations")) {
        pushAnimDurations();

    } else if (!strcmp(cmd, "getMeshTopology")) {
        pushMeshTopology();

    } else if (!strcmp(cmd, "getAll")) {
        // Dump complet : rafale d'évènements existants terminée par allDone.
        // Remplace la quinzaine de requêtes interceptées (getCalib/seqLoad par
        // cible) que la console faisait pour la sauvegarde/restauration.
        pushState();
        pushDroids();
        pushCalibData(Mesh.myId());
        for (uint8_t i = 0; i < Droids.count(); i++)
            pushCalibData(Droids.at(i).id);
        if (_seqListCb && _seqLoadCb) {
            StoredSequenceMeta metas[SequenceStore::SLOT_MAX];
            const uint8_t n = _seqListCb(metas, SequenceStore::SLOT_MAX);
            for (uint8_t i = 0; i < n; i++) {
                StoredSequence seq{};
                if (_seqLoadCb(metas[i].slot, seq)) pushSeqData(metas[i].slot, seq);
            }
        }
        pushSeqList();
        if (_seqQueryCb) _seqQueryCb();
        pushMeshTopology();
        JsonDocument done;
        done["evt"] = "allDone";
        serializeJson(done, Serial);
        Serial.print('\n');

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
        if (track < 1 || track > AUDIO_TRACK_COUNT) {
            pushErr("piste invalide: %u (1-%u)", track, AUDIO_TRACK_COUNT);
            return;
        }
        if (_trackCb) _trackCb(track);
        log("piste %u", track);

    } else if (!strcmp(cmd, "servo")) {
        const uint16_t target = doc["target"] | (uint16_t)MESH_TARGET_ALL;
        const bool en = doc["enabled"] | false;
        if (_servoCb) _servoCb(target, en);
        log("servos %s -> %04X", en ? "ON" : "OFF", target);

    } else if (!strcmp(cmd, "autoAnim")) {
        const uint16_t target = doc["target"] | (uint16_t)MESH_TARGET_ALL;
        const bool en = doc["enabled"] | false;
        if (_autoAnimCb) _autoAnimCb(target, en);
        log("anims auto %s -> %04X", en ? "ON" : "OFF", target);

    } else if (!strcmp(cmd, "seqList")) {
        pushSeqList();

    } else if (!strcmp(cmd, "seqSave")) {
        const uint8_t slot = doc["slot"] | 0;
        if (slot >= SequenceStore::SLOT_MAX) {
            pushErr("slot invalide: %u (0-%u)", slot, SequenceStore::SLOT_MAX - 1);
            return;
        }
        StoredSequence seq{};
        const char* name = doc["name"] | "Sequence";
        strncpy(seq.name, name, sizeof(seq.name) - 1);
        seq.name[sizeof(seq.name) - 1] = '\0';
        seq.loop = (doc["loop"] | false) ? 1 : 0;
        const uint8_t track = doc["track"] | 0;   // absent/null = 0 = aucune
        if (track > AUDIO_TRACK_COUNT) {
            pushErr("piste invalide: %u (1-%u, ou null)", track, AUDIO_TRACK_COUNT);
            return;
        }
        seq.track = track;

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
        pushSeqSaved(ok, slot, seq.name);
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
            pushErr("seq load slot=%u: vide ou introuvable", slot);
        }

    } else if (!strcmp(cmd, "seqDelete")) {
        const uint8_t slot = doc["slot"] | 0;
        if (slot >= SequenceStore::SLOT_MAX) {
            pushErr("slot invalide: %u (0-%u)", slot, SequenceStore::SLOT_MAX - 1);
            return;
        }
        bool ok = false;
        if (_seqDeleteCb) ok = _seqDeleteCb(slot);
        log("seq del slot=%u %s", slot, ok ? "OK" : "ERR");
        pushSeqDeleted(ok, slot);
        pushSeqList();

    } else if (!strcmp(cmd, "seqRun")) {
        const uint8_t slot = doc["slot"] | 0;
        const uint8_t from = doc["from"] | 0;   // étape de départ (0 = début)
        if (slot >= SequenceStore::SLOT_MAX) {
            pushErr("slot invalide: %u (0-%u)", slot, SequenceStore::SLOT_MAX - 1);
            return;
        }
        if (from >= StoredSequence::STEP_MAX) {
            pushErr("etape de depart invalide: %u (0-%u)", from, StoredSequence::STEP_MAX - 1);
            return;
        }
        if (_seqRunCb) _seqRunCb(slot, from);
        log("seq run slot=%u from=%u", slot, from);

    } else if (!strcmp(cmd, "seqStop")) {
        if (_seqStopCb) _seqStopCb();
        log("seq stop");

    } else if (!strcmp(cmd, "seqPause")) {
        if (_seqPauseCb) _seqPauseCb(true);

    } else if (!strcmp(cmd, "seqResume")) {
        if (_seqPauseCb) _seqPauseCb(false);

    } else if (!strcmp(cmd, "seqState")) {
        if (_seqQueryCb) _seqQueryCb();

    } else if (!strcmp(cmd, "calib")) {
        const uint16_t target = doc["target"] | (uint16_t)MESH_TARGET_ALL;
        const uint8_t panMin     = doc["panMin"]     | SERVO_PAN_MIN;
        const uint8_t panCenter  = doc["panCenter"]  | SERVO_PAN_CENTER;
        const uint8_t panMax     = doc["panMax"]     | SERVO_PAN_MAX;
        const uint8_t tiltMin    = doc["tiltMin"]    | SERVO_TILT_MIN;
        const uint8_t tiltCenter = doc["tiltCenter"] | SERVO_TILT_CENTER;
        const uint8_t tiltMax    = doc["tiltMax"]    | SERVO_TILT_MAX;

        // Cache central (comme les noms) : permet à getCalib de répondre sans
        // dépendre d'un aller-retour mesh vers un esclave distant.
        const uint16_t cacheId = target == MESH_TARGET_ALL ? Mesh.myId() : target;
        Config.setCalib(cacheId, ServoCalib{panMin, panCenter, panMax, tiltMin, tiltCenter, tiltMax});

        CalibPayload p{target, panMin, panCenter, panMax, tiltMin, tiltCenter, tiltMax};
        Mesh.send(MSG_CALIB, &p, sizeof(p));
        if ((target == MESH_TARGET_ALL || target == Mesh.myId()) && _calibCb)
            _calibCb(target, panMin, panCenter, panMax, tiltMin, tiltCenter, tiltMax);
        log("calib -> %04X", target);

    } else if (!strcmp(cmd, "getCalib")) {
        const uint16_t target = doc["target"] | (uint16_t)MESH_TARGET_ALL;
        pushCalibData(target);

    } else if (!strcmp(cmd, "preview")) {
        const uint16_t target = doc["target"] | (uint16_t)MESH_TARGET_ALL;
        const uint8_t pan  = doc["pan"]  | SERVO_PAN_CENTER;
        const uint8_t tilt = doc["tilt"] | SERVO_TILT_CENTER;
        PreviewPayload p{target, pan, tilt};
        Mesh.send(MSG_PREVIEW, &p, sizeof(p));
        if ((target == MESH_TARGET_ALL || target == Mesh.myId()) && _previewCb)
            _previewCb(target, pan, tilt);

    } else if (!strcmp(cmd, "setMulti")) {
        JsonArrayConst ops = doc["ops"].as<JsonArrayConst>();
        JsonDocument res;
        res["evt"] = "setMultiDone";
        if (ops.isNull()) {
            res["ok"] = false;
            res["failedAt"] = 0;
            res["error"] = "ops manquant ou pas un tableau";
            serializeJson(res, Serial);
            Serial.print('\n');
            return;
        }

        // Passe 1 : validation complète — un lot refusé ne modifie rien.
        char why[96];
        uint16_t idx = 0;
        for (JsonObjectConst op : ops) {
            if (!validateOp(op, why, sizeof(why))) {
                res["ok"] = false;
                res["failedAt"] = idx;
                res["error"] = why;
                serializeJson(res, Serial);
                Serial.print('\n');
                return;
            }
            idx++;
        }

        // Passe 2 : application.
        uint16_t applied = 0;
        bool ok = true;
        for (JsonObjectConst op : ops) {
            if (!applyOp(op)) { ok = false; break; }
            applied++;
        }
        res["ok"] = ok;
        res["applied"] = applied;
        if (!ok) {
            res["failedAt"] = applied;
            res["error"] = "echec d'application (ecriture NVS?)";
        }
        serializeJson(res, Serial);
        Serial.print('\n');
        log("setMulti: %u/%u ops", applied, idx);
        pushDroids();
        pushSeqList();

    } else if (cmd[0] == '\0') {
        pushErr("commande sans champ cmd");
    } else {
        pushErr("commande inconnue: %s", cmd);
    }
}
