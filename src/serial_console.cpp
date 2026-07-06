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
    }
}
