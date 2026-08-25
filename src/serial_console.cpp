#include "serial_console.h"
#include "config.h"
#include "mesh_comm.h"
#include "mesh_topology.h"
#include "registry.h"
#include "config_store.h"
#include "motion_engine.h"

#include <ArduinoJson.h>
#include <stdarg.h>
#include "mbedtls/base64.h"

SerialConsole Console;

namespace {
bool readIntField(JsonObjectConst obj, const char* key, int minValue, int maxValue,
                  int& out, char* why, size_t whyLen) {
    JsonVariantConst v = obj[key];
    if (v.isNull() || !v.is<int>()) {
        snprintf(why, whyLen, "%s must be an integer", key);
        return false;
    }
    const int value = v.as<int>();
    if (value < minValue || value > maxValue) {
        snprintf(why, whyLen, "%s outside %d..%d", key, minValue, maxValue);
        return false;
    }
    out = value;
    return true;
}

bool readTargetField(JsonObjectConst obj, bool allowAll, uint16_t& out,
                     char* why, size_t whyLen) {
    int value;
    if (!readIntField(obj, "target", 1, allowAll ? 65535 : 65534,
                      value, why, whyLen)) return false;
    out = (uint16_t)value;
    return true;
}

bool isMd5Hex(const char* value) {
    if (!value || strlen(value) != 32) return false;
    for (uint8_t i = 0; i < 32; i++) {
        const char c = value[i];
        if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') ||
              (c >= 'A' && c <= 'F'))) return false;
    }
    return true;
}

void putBuildId(JsonObject object, const char* key, uint32_t buildId) {
    if (buildId == 0) return;
    char value[9];
    snprintf(value, sizeof(value), "%08lX", (unsigned long)buildId);
    object[key] = value;
}

const char* animExecPhaseName(uint8_t phase) {
    switch (phase) {
    case ANIM_EXEC_STARTED: return "started";
    case ANIM_EXEC_COMPLETED: return "completed";
    case ANIM_EXEC_INTERRUPTED: return "interrupted";
    case ANIM_EXEC_REJECTED: return "rejected";
    default: return "unknown";
    }
}

const char* animExecReasonName(uint8_t reason) {
    switch (reason) {
    case ANIM_EXEC_REASON_SERVOS_OFF: return "servosOff";
    case ANIM_EXEC_REASON_LEASE_EXPIRED: return "leaseExpired";
    case ANIM_EXEC_REASON_CLIPPED: return "clipped";
    default: return "";
    }
}
}

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
    me["locate"] = _masterLocate;
    me["adopted"] = true;
    me["fw"] = FW_VERSION;
    me["servoReverse"] = true;
    putBuildId(me, "build", (uint32_t)FW_BUILD_ID);

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
        o["locate"] = e.locate;
        o["adopted"] = e.adopted;
        o["fw"] = String(e.fwMajor) + "." + String(e.fwMinor) + "." + String(e.fwPatch);
        o["servoReverse"] = (e.capabilities & DROID_CAP_SERVO_REVERSE) != 0;
        putBuildId(o, "build", e.buildId);
    }
    serializeJson(doc, Serial);
    Serial.print('\n');
}

void SerialConsole::pushAnimDurations() {
    if (!_clientReady) return;

    JsonDocument doc;
    doc["evt"] = "gestureCatalog";
    JsonArray arr = doc["list"].to<JsonArray>();
    for (uint8_t i = 0; i < GESTURE_COUNT; i++) {
        JsonObject o = arr.add<JsonObject>();
        o["gestureId"] = i;
        o["key"] = MotionEngine::key((GestureWireId)i);
        const uint32_t nominalMs = MotionEngine::normalDurationMs((GestureWireId)i);
        const bool continuous = MotionEngine::isContinuous((GestureWireId)i);
        o["kind"] = i == GESTURE_IDLE_CENTER ? "immediate" : continuous ? "continuous" : "finite";
        o["nominalMs"] = nominalMs;
        o["frameCount"] = GESTURES_V2[i].frameCount;
        if (i == GESTURE_IDLE_CENTER) o["settleMs"] = 120;
    }
    serializeJson(doc, Serial);
    Serial.print('\n');
}

void SerialConsole::pushAnimExec(uint32_t requestId, uint16_t droidId,
                                 uint16_t meshSeq, uint8_t animId,
                                 uint8_t phase, uint8_t reason, uint32_t atMs) {
    if (!_clientReady) return;
    JsonDocument doc;
    doc["evt"] = "animExec";
    doc["requestId"] = requestId;
    doc["droid"] = droidId;
    doc["meshSeq"] = meshSeq;
    doc["gestureId"] = animId;
    doc["gestureKey"] = MotionEngine::key((GestureWireId)animId);
    doc["phase"] = animExecPhaseName(phase);
    const char* reasonName = animExecReasonName(reason);
    if (reasonName[0]) doc["reason"] = reasonName;
    doc["atMs"] = atMs;
    serializeJson(doc, Serial);
    Serial.print('\n');
}

void SerialConsole::pushAnimAccepted(uint32_t requestId, uint16_t target,
                                     uint8_t animId, uint16_t meshSeq,
                                     bool meshQueued, bool localHandled,
                                     uint16_t leaseMs) {
    if (!_clientReady || requestId == 0) return;
    JsonDocument doc;
    doc["evt"] = "animAccepted";
    doc["requestId"] = requestId;
    doc["target"] = target;
    doc["gestureId"] = animId;
    doc["gestureKey"] = MotionEngine::key((GestureWireId)animId);
    doc["meshSeq"] = meshSeq;
    doc["meshQueued"] = meshQueued;
    doc["local"] = localHandled;
    if (leaseMs > 0) doc["leaseMs"] = leaseMs;
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

void SerialConsole::pushOtaResult(uint16_t target, bool ok, const char* fw,
                                  uint32_t buildId, const char* reason) {
    if (!_clientReady) return;
    JsonDocument doc;
    doc["evt"] = "otaResult";
    doc["target"] = target;
    doc["ok"] = ok;
    if (fw && fw[0]) doc["fw"] = fw;
    putBuildId(doc.as<JsonObject>(), "build", buildId);
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
    doc["panReversed"] = c.panReversed != 0;
    doc["tiltReversed"] = c.tiltReversed != 0;
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
        putBuildId(ack.as<JsonObject>(), "build", (uint32_t)FW_BUILD_ID);
        ack["proto"] = FW_PROTO;
        ack["lineMax"] = SERIAL_LINE_MAX;
        ack["gestures"] = GESTURE_COUNT;
        ack["catalogId"] = GESTURE_CATALOG_ID;
        ack["catalogRevision"] = GESTURE_CATALOG_REVISION;
        ack["catalogHash"] = GESTURE_CATALOG_HASH;
        JsonArray caps = ack["caps"].to<JsonArray>();
        caps.add("err");
        caps.add("getAll");
        caps.add("commit");
        caps.add("gestureV2");
        caps.add("gestureExec");
        caps.add("gestureLease");
        caps.add("gestureCompose");
        caps.add("gestureStop");
        caps.add("safeStop");
        caps.add("servoReverse");
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

    JsonObjectConst command = doc.as<JsonObjectConst>();
    char validationWhy[96] = {0};

    if (!strcmp(cmd, "list")) {
        pushDroids();

    } else if (!strcmp(cmd, "getGestureCatalog")) {
        pushAnimDurations();

    } else if (!strcmp(cmd, "getMeshTopology")) {
        pushMeshTopology();

    } else if (!strcmp(cmd, "getAll")) {
        // Full dump: current roster, calibrations and topology.
        pushDroids();
        pushCalibData(Mesh.myId());
        for (uint8_t i = 0; i < Droids.count(); i++) {
            pushCalibData(Droids.at(i).id);
        }
        pushMeshTopology();
        JsonDocument done;
        done["evt"] = "allDone";
        serializeJson(done, Serial);
        Serial.print('\n');

    } else if (!strcmp(cmd, "gesture")) {
        uint16_t target;
        const char* gestureKey = command["key"] | "";
        int gestureIdValue = -1;
        for (uint8_t i = 0; i < GESTURE_COUNT; i++) {
            if (!strcmp(gestureKey, MotionEngine::key((GestureWireId)i))) {
                gestureIdValue = i;
                break;
            }
        }
        int requestIdValue = 0;
        int leaseMsValue = 0;
        if (!readTargetField(command, true, target, validationWhy, sizeof(validationWhy)) ||
            gestureIdValue < 0) {
            pushErr("invalid gesture: target or key is invalid");
            return;
        }
        if (!command["requestId"].isNull() &&
            !readIntField(command, "requestId", 1, 0x7FFFFFFF, requestIdValue,
                          validationWhy, sizeof(validationWhy))) {
            pushErr("invalid gesture: %s", validationWhy);
            return;
        }
        if (!command["leaseMs"].isNull() &&
            !readIntField(command, "leaseMs", 0, ANIM_LEASE_MAX_MS, leaseMsValue,
                          validationWhy, sizeof(validationWhy))) {
            pushErr("invalid gesture: %s", validationWhy);
            return;
        }
        const uint8_t animId = (uint8_t)gestureIdValue;
        if (leaseMsValue > 0 &&
            (leaseMsValue < ANIM_LEASE_MIN_MS ||
             !MotionEngine::isContinuous((GestureWireId)animId))) {
            pushErr("invalid gesture: lease requires a continuous gesture and %u..%u ms",
                    ANIM_LEASE_MIN_MS, ANIM_LEASE_MAX_MS);
            return;
        }
        const uint32_t seed   = doc["seed"] | (uint32_t)esp_random();
        if (_animCb) _animCb(target, animId, seed, (uint32_t)requestIdValue,
                             (uint16_t)leaseMsValue);
        log("gesture %s -> %04X", gestureKey, target);

    } else if (!strcmp(cmd, "stopGesture")) {
        uint16_t target;
        const char* gestureKey = command["key"] | "";
        int gestureIdValue = -1;
        for (uint8_t i = 0; i < GESTURE_COUNT; i++) {
            if (!strcmp(gestureKey, MotionEngine::key((GestureWireId)i))) {
                gestureIdValue = i;
                break;
            }
        }
        if (!readTargetField(command, true, target, validationWhy, sizeof(validationWhy)) ||
            gestureIdValue < 0 || gestureIdValue == GESTURE_IDLE_CENTER) {
            pushErr("invalid stopGesture: target or key is invalid");
            return;
        }
        if (_gestureStopCb) _gestureStopCb(target, (uint8_t)gestureIdValue);
        log("stopGesture %s -> %04X", gestureKey, target);

    } else if (!strcmp(cmd, "animLease")) {
        uint16_t target;
        int originSeqValue;
        int leaseMsValue;
        if (!readTargetField(command, true, target, validationWhy, sizeof(validationWhy)) ||
            !readIntField(command, "meshSeq", 0, 65535, originSeqValue,
                          validationWhy, sizeof(validationWhy)) ||
            !readIntField(command, "leaseMs", ANIM_LEASE_MIN_MS, ANIM_LEASE_MAX_MS,
                          leaseMsValue, validationWhy, sizeof(validationWhy))) {
            pushErr("invalid animLease: %s", validationWhy);
            return;
        }
        if (_animLeaseRenewCb)
            _animLeaseRenewCb(target, (uint16_t)originSeqValue, (uint16_t)leaseMsValue);

    } else if (!strcmp(cmd, "safeStop")) {
        uint16_t target;
        if (!readTargetField(command, true, target, validationWhy, sizeof(validationWhy))) {
            pushErr("invalid safeStop: %s", validationWhy);
            return;
        }
        if (_safeStopCb) _safeStopCb(target);

    } else if (!strcmp(cmd, "name")) {
        int idValue;
        JsonVariantConst nameValue = command["name"];
        if (!readIntField(command, "id", 1, 65534, idValue,
                          validationWhy, sizeof(validationWhy)) ||
            !nameValue.is<const char*>()) {
            pushErr("invalid name: %s", validationWhy[0] ? validationWhy : "name must be a string");
            return;
        }
        const char* name = nameValue.as<const char*>();
        if (strlen(name) >= sizeof(((NamePayload*)nullptr)->name)) {
            pushErr("invalid name: name too long (max %u)",
                    (unsigned)(sizeof(((NamePayload*)nullptr)->name) - 1));
            return;
        }
        const uint16_t id = (uint16_t)idValue;
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
        uint16_t target;
        if (!readTargetField(command, true, target, validationWhy, sizeof(validationWhy)) ||
            !command["enabled"].is<bool>()) {
            pushErr("invalid servo: %s", validationWhy[0] ? validationWhy : "enabled must be boolean");
            return;
        }
        const bool en = doc["enabled"] | false;
        if (_servoCb) _servoCb(target, en);
        log("servos %s -> %04X", en ? "ON" : "OFF", target);

    } else if (!strcmp(cmd, "locate")) {
        uint16_t target;
        if (!readTargetField(command, true, target, validationWhy, sizeof(validationWhy)) ||
            !command["enabled"].is<bool>()) {
            pushErr("invalid locate: %s", validationWhy[0] ? validationWhy : "enabled must be boolean");
            return;
        }
        const bool en = doc["enabled"] | false;
        if (_locateCb) _locateCb(target, en);
        log("locate %s -> %04X", en ? "ON" : "OFF", target);

    } else if (!strcmp(cmd, "adopt")) {
        uint16_t target;
        if (!readTargetField(command, false, target, validationWhy, sizeof(validationWhy))) {
            pushErr("invalid adopt: %s", validationWhy);
            return;
        }
        Droids.setAdopted(target, true);
        Config.setAdopted(target, true);
        log("droid %04X adopted", target);
        pushDroids();

    } else if (!strcmp(cmd, "forget")) {
        uint16_t target;
        if (!readTargetField(command, false, target, validationWhy, sizeof(validationWhy))) {
            pushErr("invalid forget: %s", validationWhy);
            return;
        }
        Config.setAdopted(target, false);
        if (Droids.forget(target)) log("droid %04X forgotten/ignored", target);
        else pushErr("unknown droid: %04X", target);
        pushDroids();

    } else if (!strcmp(cmd, "otaStart")) {
        uint16_t target;
        int sizeValue;
        if (!readTargetField(command, false, target, validationWhy, sizeof(validationWhy)) ||
            !readIntField(command, "size", 1, (int)OTA_MAX_IMAGE_SIZE, sizeValue,
                          validationWhy, sizeof(validationWhy))) {
            pushOtaError(0, 0, validationWhy);
            return;
        }
        const uint32_t size = (uint32_t)sizeValue;
        const char* md5 = doc["md5"] | "";
        if (!isMd5Hex(md5)) {
            pushOtaError(target, 0, "invalid md5");
        } else if (!_otaStartCb || !_otaStartCb(target, size, md5)) {
            pushOtaError(target, 0, "busy or invalid target");
        }
        // Success: nothing to push here — evt:"otaReady" will come once the
        // mesh ack for START is received (see OtaMaster::pollEvent, wired in main.cpp).

    } else if (!strcmp(cmd, "otaChunk")) {
        int seqValue;
        if (!readIntField(command, "seq", 0, 65535, seqValue,
                          validationWhy, sizeof(validationWhy)) ||
            !command["data"].is<const char*>()) {
            pushErr("invalid otaChunk: %s", validationWhy[0] ? validationWhy : "data must be a string");
            return;
        }
        const uint16_t seq = (uint16_t)seqValue;
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
        uint16_t target;
        int panMinValue, panCenterValue, panMaxValue;
        int tiltMinValue, tiltCenterValue, tiltMaxValue;
        if (!readTargetField(command, true, target, validationWhy, sizeof(validationWhy)) ||
            !readIntField(command, "panMin", 0, 180, panMinValue, validationWhy, sizeof(validationWhy)) ||
            !readIntField(command, "panCenter", 0, 180, panCenterValue, validationWhy, sizeof(validationWhy)) ||
            !readIntField(command, "panMax", 0, 180, panMaxValue, validationWhy, sizeof(validationWhy)) ||
            !readIntField(command, "tiltMin", 0, 180, tiltMinValue, validationWhy, sizeof(validationWhy)) ||
            !readIntField(command, "tiltCenter", 0, 180, tiltCenterValue, validationWhy, sizeof(validationWhy)) ||
            !readIntField(command, "tiltMax", 0, 180, tiltMaxValue, validationWhy, sizeof(validationWhy))) {
            pushErr("invalid calib: %s", validationWhy);
            return;
        }
        if (panMinValue > panCenterValue || panCenterValue > panMaxValue ||
            tiltMinValue > tiltCenterValue || tiltCenterValue > tiltMaxValue) {
            pushErr("invalid calib: min <= center <= max required");
            return;
        }
        if ((!command["panReversed"].isNull() && !command["panReversed"].is<bool>()) ||
            (!command["tiltReversed"].isNull() && !command["tiltReversed"].is<bool>())) {
            pushErr("invalid calib: reverse flags must be boolean");
            return;
        }
        const uint8_t panMin = (uint8_t)panMinValue;
        const uint8_t panCenter = (uint8_t)panCenterValue;
        const uint8_t panMax = (uint8_t)panMaxValue;
        const uint8_t tiltMin = (uint8_t)tiltMinValue;
        const uint8_t tiltCenter = (uint8_t)tiltCenterValue;
        const uint8_t tiltMax = (uint8_t)tiltMaxValue;

        // Central cache (like the names): lets getCalib answer without
        // depending on a mesh round-trip to a remote slave.
        const uint16_t cacheId = target == MESH_TARGET_ALL ? Mesh.myId() : target;
        const ServoCalib previous = Config.getCalib(cacheId);
        const bool panReversed = doc["panReversed"] | (previous.panReversed != 0);
        const bool tiltReversed = doc["tiltReversed"] | (previous.tiltReversed != 0);
        Config.setCalib(cacheId, ServoCalib{panMin, panCenter, panMax,
                                            tiltMin, tiltCenter, tiltMax,
                                            (uint8_t)panReversed, (uint8_t)tiltReversed});

        CalibPayload p{target, panMin, panCenter, panMax, tiltMin, tiltCenter, tiltMax};
        Mesh.send(MSG_CALIB, &p, sizeof(p));
        CalibV2Payload p2{target, panMin, panCenter, panMax, tiltMin, tiltCenter, tiltMax,
                          (uint8_t)panReversed, (uint8_t)tiltReversed};
        Mesh.send(MSG_CALIB_V2, &p2, sizeof(p2));
        if ((target == MESH_TARGET_ALL || target == Mesh.myId()) && _calibCb)
            _calibCb(target, panMin, panCenter, panMax, tiltMin, tiltCenter, tiltMax,
                     panReversed, tiltReversed);
        log("calib -> %04X", target);

    } else if (!strcmp(cmd, "getCalib")) {
        uint16_t target;
        if (!readTargetField(command, true, target, validationWhy, sizeof(validationWhy))) {
            pushErr("invalid getCalib: %s", validationWhy);
            return;
        }
        pushCalibData(target);

    } else if (!strcmp(cmd, "preview")) {
        uint16_t target;
        int panValue, tiltValue;
        if (!readTargetField(command, true, target, validationWhy, sizeof(validationWhy)) ||
            !readIntField(command, "pan", 0, 180, panValue, validationWhy, sizeof(validationWhy)) ||
            !readIntField(command, "tilt", 0, 180, tiltValue, validationWhy, sizeof(validationWhy))) {
            pushErr("invalid preview: %s", validationWhy);
            return;
        }
        const uint8_t pan = (uint8_t)panValue;
        const uint8_t tilt = (uint8_t)tiltValue;
        PreviewPayload p{target, pan, tilt};
        Mesh.send(MSG_PREVIEW, &p, sizeof(p));
        if ((target == MESH_TARGET_ALL || target == Mesh.myId()) && _previewCb)
            _previewCb(target, pan, tilt);

    } else if (!strcmp(cmd, "commit")) {
        // Commits the pending name edits to NVS.
        Config.commitPending();
        log("configuration committed (NVS)");
        syncDirty();

    } else if (cmd[0] == '\0') {
        pushErr("command missing cmd field");
    } else {
        pushErr("unknown command: %s", cmd);
    }
}
