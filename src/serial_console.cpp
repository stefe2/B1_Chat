#include "serial_console.h"
#include "config.h"
#include "mesh_comm.h"
#include "mesh_topology.h"
#include "registry.h"
#include "config_store.h"
#include "animation.h"

#include <ArduinoJson.h>
#include <stdarg.h>
#include "mbedtls/base64.h"

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

void SerialConsole::syncDirty() {
    if (!_clientReady) return;
    const bool d = Config.dirty();
    if (d == _lastDirtySent) return;
    _lastDirtySent = d;

    JsonDocument doc;
    doc["evt"] = "dirty";
    doc["dirty"] = d;
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

    // The master itself (absent from the registry since it ignores its own messages).
    JsonObject me = arr.add<JsonObject>();
    me["id"] = Mesh.myId();
    me["name"] = Config.getName(Mesh.myId());
    me["rssi"] = 0;
    me["age"] = 0;
    me["role"] = "master";
    me["servos"] = _masterServos;
    me["autoAnim"] = _masterAutoAnim;
    me["adopted"] = true;
    me["fw"] = FW_VERSION;

    // The other droids (slaves).
    for (uint8_t i = 0; i < Droids.count(); i++) {
        const Registry::Entry& e = Droids.at(i);
        JsonObject o = arr.add<JsonObject>();
        o["id"] = e.id;
        o["name"] = Config.getName(e.id);
        o["rssi"] = e.rssi;
        // lastSeen is timestamped by the ESP-NOW callback (Wi-Fi task) with a
        // fresh millis(): it can be LATER than now. Without clamping, the
        // negative age overflows to ~4e9 (same bug family as the OTA
        // timeouts, see ota_master.cpp) — and that number doesn't fit in the
        // console's GetInt32() (a HandleDroids crash was observed mid-OTA
        // transfer, where the callback fires ~23 times/s, making the
        // collision nearly certain).
        const uint32_t last = e.lastSeen;
        o["age"] = ((int32_t)(now - last) > 0) ? (now - last) : 0; // ms since last seen
        o["role"] = "slave";
        o["servos"] = e.servos;
        o["autoAnim"] = e.autoAnim;
        o["adopted"] = e.adopted;
        o["fw"] = String(e.fwMajor) + "." + String(e.fwMinor) + "." + String(e.fwPatch);
    }
    serializeJson(doc, Serial);
    Serial.print('\n');
}

void SerialConsole::pushState() {
    if (!_clientReady) return;

    uint8_t f, a, s;
    Config.animParams(f, a, s);
    JsonDocument doc;
    // "config" (contract §3): the console populates its freq/amp/speed
    // sliders on connection. (Formerly evt:"state", never interpreted.)
    doc["evt"] = "config";
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

void SerialConsole::pushOtaReady(uint16_t target, uint8_t sessionId, uint8_t chunkSize, uint16_t totalChunks) {
    if (!_clientReady) return;
    JsonDocument doc;
    doc["evt"] = "otaReady";
    doc["target"] = target;
    doc["sessionId"] = sessionId;
    doc["chunkSize"] = chunkSize;
    doc["totalChunks"] = totalChunks;
    serializeJson(doc, Serial);
    Serial.print('\n');
}

void SerialConsole::pushOtaChunkAck(uint16_t seq, uint16_t sent, uint16_t total) {
    if (!_clientReady) return;
    JsonDocument doc;
    doc["evt"] = "otaChunkAck";
    doc["seq"] = seq;
    doc["sent"] = sent;
    doc["total"] = total;
    serializeJson(doc, Serial);
    Serial.print('\n');
}

void SerialConsole::pushOtaDone(uint16_t target, uint8_t sessionId) {
    if (!_clientReady) return;
    JsonDocument doc;
    doc["evt"] = "otaDone";
    doc["target"] = target;
    doc["sessionId"] = sessionId;
    serializeJson(doc, Serial);
    Serial.print('\n');
}

void SerialConsole::pushOtaResult(uint16_t target, bool ok, const char* fw, const char* reason) {
    if (!_clientReady) return;
    JsonDocument doc;
    doc["evt"] = "otaResult";
    doc["target"] = target;
    doc["ok"] = ok;
    if (fw && fw[0]) doc["fw"] = fw;
    if (reason && reason[0]) doc["reason"] = reason;
    serializeJson(doc, Serial);
    Serial.print('\n');
}

void SerialConsole::pushOtaError(uint16_t target, uint8_t sessionId, const char* reason) {
    if (!_clientReady) return;
    JsonDocument doc;
    doc["evt"] = "otaError";
    if (target) doc["target"] = target;
    doc["sessionId"] = sessionId;
    doc["reason"] = reason;
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

void SerialConsole::update() {
    while (Serial.available()) {
        const char c = (char)Serial.read();
        if (c == '\n' || c == '\r') {
            if (_overflow) {
                // Line too long: discarded entirely, but reported (before,
                // the failure was silent and the line ending would corrupt
                // the following line).
                pushErr("line too long (max %u), command ignored", SERIAL_LINE_MAX - 1);
                _overflow = false;
                _len = 0;
            } else if (_len > 0) {
                _buf[_len] = '\0';
                handleLine(_buf);
                _len = 0;
            }
        } else if (_overflow) {
            // swallows the rest of the offending line
        } else if (_len < sizeof(_buf) - 1) {
            _buf[_len++] = c;
        } else {
            _overflow = true;
        }
    }

    // Web Serial session lost if no more keepalive.
    if (_clientReady && (millis() - _lastHelloMs > CLIENT_TIMEOUT_MS)) {
        _clientReady = false;
    }
}

// --- setMulti ----------------------------------------------------------------
// Scope: the persisted-state ops used by backup restore. Atomicity is
// achieved by full validation BEFORE any application: a rejected batch
// changes nothing. (An NVS write failure mid-application — extremely rare —
// is reported via failedAt with no rollback.)

bool SerialConsole::validateOp(JsonObjectConst op, char* why, size_t whyLen) {
    const char* c = op["cmd"] | "";
    if (!strcmp(c, "name") || !strcmp(c, "calib") || !strcmp(c, "config")) {
        return true;   // fields bounded by nature (uint8/clamp)
    }
    // (seqSave/seqDelete ops removed in fw 1.7.0 — an old backup file carrying
    // them is rejected here with an explicit reason, nothing partially applies.)
    snprintf(why, whyLen, "unsupported op: %s", c[0] ? c : "(no cmd)");
    return false;
}

bool SerialConsole::applyOp(JsonObjectConst op) {
    const char* c = op["cmd"] | "";

    if (!strcmp(c, "name")) {
        const uint16_t id = op["id"] | 0;
        const char* name = op["name"] | "";
        Config.setName(id, name);
        // Same relay as the plain "name" cmd (see there) — keeps a restored
        // backup's names resilient on each droid too, not just the master.
        NamePayload np{id, {0}};
        strncpy(np.name, name, sizeof(np.name) - 1);
        Mesh.send(MSG_NAME, &np, sizeof(np));
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
    return false;   // impossible after validateOp
}

void SerialConsole::handleLine(const char* line) {
    JsonDocument doc;
    if (deserializeJson(doc, line)) {
        pushErr("invalid JSON");
        return;
    }

    const char* cmd = doc["cmd"] | "";

    if (!strcmp(cmd, "hello")) {
        _clientReady = true;
        _lastHelloMs = millis();

        // Enriched handshake: version + capabilities, so the console can
        // adapt to the connected firmware (and offer GitHub updates).
        JsonDocument ack;
        ack["evt"] = "hello";
        ack["ok"] = true;
        ack["id"] = Mesh.myId();
        ack["fw"] = FW_VERSION;
        ack["proto"] = FW_PROTO;
        ack["lineMax"] = SERIAL_LINE_MAX;
        ack["anims"] = ANIM_COUNT;
        JsonArray caps = ack["caps"].to<JsonArray>();
        caps.add("err");
        caps.add("getAll");
        caps.add("config");
        caps.add("setMulti");
        caps.add("commit");
        ack["dirty"] = Config.dirty();
        _lastDirtySent = Config.dirty();
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
        // Full dump: burst of existing events ending with allDone. Replaces
        // the dozen or so intercepted per-target requests (getCalib) the
        // console used to make for backup/restore.
        pushState();
        pushDroids();
        pushCalibData(Mesh.myId());
        for (uint8_t i = 0; i < Droids.count(); i++)
            pushCalibData(Droids.at(i).id);
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
        syncDirty();

    } else if (!strcmp(cmd, "name")) {
        const uint16_t id = doc["id"] | 0;
        const char* name = doc["name"] | "";
        Config.setName(id, name);
        // Relayed so the targeted droid persists its OWN name locally too (see
        // MSG_NAME/applyName in main.cpp) — survives a master NVS reset.
        NamePayload np{id, {0}};
        strncpy(np.name, name, sizeof(np.name) - 1);
        Mesh.send(MSG_NAME, &np, sizeof(np));
        log("name %04X = %s", id, name);
        pushDroids();
        syncDirty();

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

    } else if (!strcmp(cmd, "locate")) {
        const uint16_t target = doc["target"] | (uint16_t)MESH_TARGET_ALL;
        const bool en = doc["enabled"] | false;
        if (_locateCb) _locateCb(target, en);
        log("locate %s -> %04X", en ? "ON" : "OFF", target);

    } else if (!strcmp(cmd, "adopt")) {
        const uint16_t target = doc["target"] | 0;
        Droids.setAdopted(target, true);
        Config.setAdopted(target, true);
        log("droid %04X adopted", target);
        pushDroids();

    } else if (!strcmp(cmd, "forget")) {
        const uint16_t target = doc["target"] | 0;
        Config.setAdopted(target, false);
        if (Droids.forget(target)) log("droid %04X forgotten/ignored", target);
        else pushErr("unknown droid: %04X", target);
        pushDroids();

    } else if (!strcmp(cmd, "otaStart")) {
        const uint16_t target = doc["target"] | 0;
        const uint32_t size = doc["size"] | 0;
        const char* md5 = doc["md5"] | "";
        if (strlen(md5) != 32) {
            pushOtaError(target, 0, "invalid md5");
        } else if (!_otaStartCb || !_otaStartCb(target, size, md5)) {
            pushOtaError(target, 0, "busy or invalid target");
        }
        // Success: nothing to push here — evt:"otaReady" will come once the
        // mesh ack for START is received (see OtaMaster::pollEvent, wired in main.cpp).

    } else if (!strcmp(cmd, "otaChunk")) {
        const uint16_t seq = doc["seq"] | 0;
        const char* b64 = doc["data"] | "";
        uint8_t buf[OTA_CHUNK_DATA_MAX];
        size_t outLen = 0;
        if (mbedtls_base64_decode(buf, sizeof(buf), &outLen, (const uint8_t*)b64, strlen(b64)) != 0) {
            pushErr("chunk %u: invalid base64", seq);
        } else if (_otaChunkCb) {
            _otaChunkCb(seq, buf, (uint8_t)outLen);
        }

    } else if (!strcmp(cmd, "otaAbort")) {
        if (_otaAbortCb) _otaAbortCb();

    } else if (!strcmp(cmd, "calib")) {
        const uint16_t target = doc["target"] | (uint16_t)MESH_TARGET_ALL;
        const uint8_t panMin     = doc["panMin"]     | SERVO_PAN_MIN;
        const uint8_t panCenter  = doc["panCenter"]  | SERVO_PAN_CENTER;
        const uint8_t panMax     = doc["panMax"]     | SERVO_PAN_MAX;
        const uint8_t tiltMin    = doc["tiltMin"]    | SERVO_TILT_MIN;
        const uint8_t tiltCenter = doc["tiltCenter"] | SERVO_TILT_CENTER;
        const uint8_t tiltMax    = doc["tiltMax"]    | SERVO_TILT_MAX;

        // Central cache (like the names): lets getCalib answer without
        // depending on a mesh round-trip to a remote slave.
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
            res["error"] = "ops missing or not an array";
            serializeJson(res, Serial);
            Serial.print('\n');
            return;
        }

        // Pass 1: full validation — a rejected batch changes nothing.
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

        // Pass 2: application.
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
            res["error"] = "application failed (NVS write?)";
        }
        serializeJson(res, Serial);
        Serial.print('\n');
        log("setMulti: %u/%u ops", applied, idx);
        pushDroids();
        syncDirty();

    } else if (!strcmp(cmd, "commit")) {
        // Commits the RAM overlay (params/names) to NVS.
        Config.commitPending();
        log("configuration committed (NVS)");
        syncDirty();

    } else if (cmd[0] == '\0') {
        pushErr("command missing cmd field");
    } else {
        pushErr("unknown command: %s", cmd);
    }
}
