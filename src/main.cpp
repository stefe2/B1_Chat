#include <Arduino.h>
#include "config.h"
#include "servo_engine.h"
#include "animation.h"
#include "mesh_comm.h"
#include "mesh_topology.h"
#include "registry.h"
#include "config_store.h"
#include "serial_console.h"
#include "ota_guard.h"
#include "ota_master.h"
#include "ota_slave.h"
#include "esp_task_wdt.h"

// NOTE: temporary test bench (steps 2-8). Will be replaced by the droid's
// state machine at step 6.

// Logging: JSON via the console on the master, plain text on the slave.
#if IS_MASTER
  #define LOGF(fmt, ...) Console.log(fmt, ##__VA_ARGS__)
#else
  #define LOGF(fmt, ...) do { Serial.printf(fmt, ##__VA_ARGS__); Serial.print('\n'); } while (0)
#endif

static ServoEngine head;
static AnimationPlayer anim;
static uint32_t nextMove = 0;

// Runtime servo state of THIS droid (controllable from the web console).
static bool gServos = true;

// Spontaneous idle anims of THIS droid (controllable from the web console).
// Doesn't affect Play (anim) or the Sequencer: only the random idle draw.
static bool gAutoAnim = true;

// Live-tunable "frequency" param (0..100, see applyAnimParamsEffect) — scales
// the idle-draw interval below. 50 = historical default = today's untouched
// 2.5-5s (master)/3-7s (isolated slave) range.
static uint8_t gIdleFreqPct = 50;

// Multiplier applied to the idle-draw interval, mirroring AnimationPlayer's
// own amp/speed scale clamp (see setAmpSpeedPct) so "frequency" behaves
// consistently with the other two knobs.
static float idleFreqScale() {
    float s = 50.0f / (float)(gIdleFreqPct < 10 ? 10 : gIdleFreqPct);
    if (s < 0.15f) s = 0.15f;
    if (s > 4.0f) s = 4.0f;
    return s;
}

// Life LED (execution indicator) — non-blocking blink, overridden solid by "locate".
static uint32_t lastBlink = 0;
static bool ledOn = false;

// "Locate" (find-me) override of THIS droid's onboard LED — solid on while
// active, resumes the normal execution-indicator blink once cleared. Not
// persisted (console-driven, ephemeral like preview positioning).
static bool gLocateOn = false;

// Mesh test / timers
static uint32_t nextMeshSend = 0;
// Firmware version, decomposed once at startup from FW_VERSION (config.h)
// to be included (compact, 3 bytes) in every heartbeat.
static uint8_t gFwMajor = 0, gFwMinor = 0, gFwPatch = 0;

static uint32_t nextHeartbeat = 0;
static uint32_t nextPresenceScan = 0;
static uint32_t nextDroidsPush = 0;
static uint32_t nextNeighborReport = 0;

// (The master's stored-sequence player and its 8 NVS slots were retired in
// fw 1.7.0 — sequences are entirely console-driven now, see CLAUDE.md.)

// Offline tracking (master): remembers the online state to report losses.
static const uint32_t DROID_TIMEOUT_MS = 4000;
#if IS_MASTER
static bool wasOnline[Registry::MAX];
#endif

// Enables/disables this droid's servos (hardware protection). Persisted
// immediately so it survives a reboot (see ConfigStore::setServosEnabledImmediate).
static void applyServos(bool en) {
    gServos = en;
    head.setEnabled(en);
    if (!en) anim.stop();
    Config.setServosEnabledImmediate(en);
#if IS_MASTER
    Console.setMasterServos(en);
#endif
    LOGF("servos %s", en ? "ON" : "OFF");
}

// Pauses/resumes THIS droid's spontaneous idle animation. Persisted
// immediately, same reasoning as applyServos.
static void applyAutoAnim(bool en) {
    gAutoAnim = en;
    Config.setAutoAnimEnabledImmediate(en);
#if IS_MASTER
    Console.setMasterAutoAnim(en);
#endif
    LOGF("auto anims %s", en ? "ON" : "PAUSED");
}

// Persists THIS droid's OWN name (master or slave), received via MSG_NAME —
// bypasses the master's commit/revert draft (setNameImmediate), so a droid
// keeps its own name even if the master's own copy is ever lost or reset.
static void applyName(const char* name) {
    Config.setNameImmediate(Mesh.myId(), name);
    LOGF("name persisted locally: %s", name);
}

// Applies a "locate" request for THIS droid (master or slave) — see gLocateOn.
static void applyLocate(bool en) {
    gLocateOn = en;
    LOGF("locate %s", en ? "ON" : "OFF");
}

// Applies freq/amp/speed to THIS droid's running behavior (master or slave) —
// persistence is the CALLER's job (see the two call sites: the master's own
// draft/auto-commit for its local changes, ConfigStore::setAnimParamsImmediate
// for a value received over the mesh).
static void applyAnimParamsEffect(uint8_t freq, uint8_t amp, uint8_t speed) {
    gIdleFreqPct = freq;
    anim.setAmpSpeedPct(amp, speed);
}

// Persists and applies a received calibration for THIS droid (master or slave).
static void applyCalib(const CalibPayload& p) {
    const ServoCalib c{p.panMin, p.panCenter, p.panMax, p.tiltMin, p.tiltCenter, p.tiltMax};
    Config.setCalib(Mesh.myId(), c);
    head.setLimits(c.panMin, c.panCenter, c.panMax, c.tiltMin, c.tiltCenter, c.tiltMax);
    head.center();
    LOGF("calibration applied (pan %u/%u/%u, tilt %u/%u/%u)",
         p.panMin, p.panCenter, p.panMax, p.tiltMin, p.tiltCenter, p.tiltMax);
}

// Console hook: play an animation locally (master).
#if IS_MASTER
static void playLocalAnim(uint8_t animId, uint32_t seed) {
    if (gServos) {
        anim.play(animId, seed);
    }
}

// Console hook: enable/disable a target's servos (master).
static void onServoCmd(uint16_t target, bool en) {
    ServoPayload p{target, (uint8_t)(en ? 1 : 0)};
    Mesh.send(MSG_SERVO, &p, sizeof(p));
    if (target == MESH_TARGET_ALL || target == Mesh.myId()) applyServos(en);
}

// Console hook: pause/resume a target's spontaneous animation (master).
static void onAutoAnimCmd(uint16_t target, bool en) {
    AutoAnimPayload p{target, (uint8_t)(en ? 1 : 0)};
    Mesh.send(MSG_AUTOANIM, &p, sizeof(p));
    if (target == MESH_TARGET_ALL || target == Mesh.myId()) applyAutoAnim(en);
}

// Console hook: toggle a target's "locate" LED (master).
static void onLocateCmd(uint16_t target, bool en) {
    LocatePayload p{target, (uint8_t)(en ? 1 : 0)};
    Mesh.send(MSG_LOCATE, &p, sizeof(p));
    if (target == MESH_TARGET_ALL || target == Mesh.myId()) applyLocate(en);
}

// Console hook: calibration received (already filtered on target == this droid).
static void onCalibCmd(uint16_t target, uint8_t panMin, uint8_t panCenter, uint8_t panMax,
                        uint8_t tiltMin, uint8_t tiltCenter, uint8_t tiltMax) {
    (void)target;
    const CalibPayload p{target, panMin, panCenter, panMax, tiltMin, tiltCenter, tiltMax};
    applyCalib(p);
}

// Console hook: transient preview (not persisted), already filtered on target.
static void onPreviewCmd(uint16_t target, uint8_t pan, uint8_t tilt) {
    (void)target;
    head.setTarget(pan, tilt, 150);
}
#endif

static void onMeshMessage(uint8_t type, const uint8_t* payload, uint8_t len,
                          uint16_t srcId, int rssi) {
#if IS_MASTER
    // Any received message proves this droid's presence.
    if (Droids.seen(srcId, rssi, millis())) {
        const String name = Config.getName(srcId);
        LOGF("new B1 %04X%s%s connected to the mesh (total %u)",
             srcId, name.length() ? " " : "", name.c_str(), Droids.count());
    }
#else
    (void)srcId; (void)rssi;
#endif

    if (type == MSG_ANIM && len == sizeof(AnimPayload)) {
        AnimPayload p;
        memcpy(&p, payload, sizeof(p));
        LOGF("ANIM from %04X (rssi %d) target=%04X anim=%u", srcId, rssi, p.targetId, p.animId);
        if (p.targetId == MESH_TARGET_ALL || p.targetId == Mesh.myId()) {
            if (gServos) anim.play(p.animId, p.seed);
        }
    } else if (type == MSG_SERVO && len == sizeof(ServoPayload)) {
        ServoPayload p;
        memcpy(&p, payload, sizeof(p));
        if (p.targetId == MESH_TARGET_ALL || p.targetId == Mesh.myId())
            applyServos(p.enabled != 0);
    } else if (type == MSG_AUTOANIM && len == sizeof(AutoAnimPayload)) {
        AutoAnimPayload p;
        memcpy(&p, payload, sizeof(p));
        if (p.targetId == MESH_TARGET_ALL || p.targetId == Mesh.myId())
            applyAutoAnim(p.enabled != 0);
    } else if (type == MSG_LOCATE && len == sizeof(LocatePayload)) {
        LocatePayload p;
        memcpy(&p, payload, sizeof(p));
        if (p.targetId == MESH_TARGET_ALL || p.targetId == Mesh.myId())
            applyLocate(p.enabled != 0);
    } else if (type == MSG_NAME && len == sizeof(NamePayload)) {
        NamePayload p;
        memcpy(&p, payload, sizeof(p));
        p.name[sizeof(p.name) - 1] = '\0'; // defensive: guarantee NUL-termination
        if (p.targetId == Mesh.myId()) applyName(p.name);
    } else if (type == MSG_CALIB && len == sizeof(CalibPayload)) {
        CalibPayload p;
        memcpy(&p, payload, sizeof(p));
        if (p.targetId == MESH_TARGET_ALL || p.targetId == Mesh.myId())
            applyCalib(p);
    } else if (type == MSG_CONFIG && len == sizeof(ConfigPayload)) {
        ConfigPayload p;
        memcpy(&p, payload, sizeof(p));
        if (p.targetId == MESH_TARGET_ALL || p.targetId == Mesh.myId()) {
            const uint8_t freq = (uint8_t)p.freq, amp = (uint8_t)p.amplitude, speed = (uint8_t)p.speed;
            // Immediate persistence: the receiving droid has no "commit" command of its
            // own to ever flush a draft with (see ConfigStore::setAnimParamsImmediate).
            Config.setAnimParamsImmediate(freq, amp, speed);
            applyAnimParamsEffect(freq, amp, speed);
        }
    } else if (type == MSG_PREVIEW && len == sizeof(PreviewPayload)) {
        PreviewPayload p;
        memcpy(&p, payload, sizeof(p));
        if (p.targetId == MESH_TARGET_ALL || p.targetId == Mesh.myId())
            head.setTarget(p.pan, p.tilt, 150);
    } else if (type == MSG_HEARTBEAT && len == sizeof(HeartbeatPayload)) {
#if IS_MASTER
        HeartbeatPayload hb;
        memcpy(&hb, payload, sizeof(hb));
        Droids.setServos(srcId, hb.state & 0x01);
        Droids.setAutoAnim(srcId, hb.state & 0x02);
        Droids.setFwVersion(srcId, hb.fwMajor, hb.fwMinor, hb.fwPatch);
#endif
    } else if (type == MSG_HEARTBEAT) {
        // old form / presence: already noted.
    } else if (type == MSG_NEIGHBORS && len == sizeof(NeighborReportPayload)) {
#if IS_MASTER
        NeighborReportPayload rep;
        memcpy(&rep, payload, sizeof(rep));
        const uint32_t now2 = millis();
        const uint8_t n = rep.count > MAX_NEIGHBORS ? MAX_NEIGHBORS : rep.count;
        for (uint8_t i = 0; i < n; i++)
            MeshTopo.seen(srcId, rep.entries[i].id, rep.entries[i].rssi, now2);
#endif
    } else if (type == MSG_OTA_ACK && len == sizeof(OtaAckPayload)) {
#if IS_MASTER
        OtaAckPayload p;
        memcpy(&p, payload, sizeof(p));
        OtaM.onAck(srcId, p);
#endif
    } else if (type == MSG_OTA_START && len == sizeof(OtaStartPayload)) {
#if !IS_MASTER
        OtaStartPayload p;
        memcpy(&p, payload, sizeof(p));
        OtaS.onStart(srcId, p);
#endif
    } else if (type == MSG_OTA_CHUNK && len == sizeof(OtaChunkPayload)) {
#if !IS_MASTER
        OtaChunkPayload p;
        memcpy(&p, payload, sizeof(p));
        OtaS.onChunk(srcId, p);
#endif
    } else if (type == MSG_OTA_END && len == sizeof(OtaEndPayload)) {
#if !IS_MASTER
        OtaEndPayload p;
        memcpy(&p, payload, sizeof(p));
        OtaS.onEnd(srcId, p);
#endif
    } else if (type == MSG_OTA_ABORT && len == sizeof(OtaAbortPayload)) {
#if !IS_MASTER
        OtaAbortPayload p;
        memcpy(&p, payload, sizeof(p));
        OtaS.onAbort(srcId, p);
#endif
    } else {
        LOGF("type=%u len=%u from %04X (rssi %d)", type, len, srcId, rssi);
    }
}

#if IS_MASTER
// Relay between serial commands (SerialConsole) and OtaMaster — main.cpp is
// the only wiring point between the mesh/registry and the JSON protocol.
static bool onOtaStartCmd(uint16_t target, uint32_t size, const char* md5Hex32) {
    return OtaM.begin(target, size, md5Hex32);
}
static void onOtaChunkCmd(uint16_t seq, const uint8_t* data, uint8_t len) {
    OtaM.onSerialChunk(seq, data, len);
}
static void onOtaAbortCmd() {
    OtaM.abort();
}

// Translates the pending OTA event (if any) into a console JSON evt.
static void pumpOtaEvents() {
    const OtaMaster::Event ev = OtaM.pollEvent();
    switch (ev.type) {
    case OtaMaster::EV_READY:
        Console.pushOtaReady(ev.target, ev.sessionId, ev.chunkSize, ev.total);
        break;
    case OtaMaster::EV_CHUNK_ACK:
        Console.pushOtaChunkAck(ev.chunkIndex, ev.sent, ev.total);
        break;
    case OtaMaster::EV_DONE:
        Console.pushOtaDone(ev.target, ev.sessionId);
        break;
    case OtaMaster::EV_RESULT:
        Console.pushOtaResult(ev.target, ev.ok, ev.fw, ev.reason);
        break;
    case OtaMaster::EV_ERROR:
        Console.pushOtaError(ev.target, ev.sessionId, ev.reason);
        break;
    default:
        break;
    }
}
#endif

void setup() {
    // Must remain the very first line: a crash occurring before this call
    // would never be counted by the anti-brick mechanism (see CLAUDE.md,
    // known pitfalls).
    if (Guard.earlyCheck()) return;

#ifdef OTA_TEST_FORCE_CRASH
    // Anti-brick rollback test build (never defined in release): crashes
    // intentionally right AFTER earlyCheck() — every boot therefore
    // increments OtaGuard's attempt counter, which must switch back to the
    // old partition on its own after OTA_MAX_BOOT_ATTEMPTS. To be pushed via
    // OTA to a test board ONLY (see CLAUDE.md, Verification pt 7):
    //   $env:PLATFORMIO_BUILD_FLAGS='-D OTA_TEST_FORCE_CRASH'; pio run -e b1_slave
    Serial.begin(115200);
    Serial.println("OTA_TEST_FORCE_CRASH: intentional crash");
    delay(50);
    *(volatile int*)0 = 0; // LoadStoreError -> panic -> reboot
#endif

    // Safety net: a new firmware that crashes/loops without yielding after
    // OtaGuard runs must still eventually reboot (not relying solely on
    // Arduino's default watchdog).
    esp_task_wdt_config_t wdtConfig = {10000, 0, true};
    esp_task_wdt_init(&wdtConfig);
    esp_task_wdt_add(nullptr);

    // Widened UART buffers (default: RX 256 B, TX 128 B). During an OTA, an
    // otaChunk line (~330 B) can arrive while loop() is blocked writing a
    // large pushDroids: at 256 B the RX buffer overflows and the line (so
    // the chunk) is lost — the serial stop-and-wait then freezes until the
    // timeout. Must be set BEFORE Serial.begin().
    Serial.setRxBufferSize(2048);
    Serial.setTxBufferSize(2048);
    Serial.begin(115200);
    pinMode(PIN_LED_ONBOARD, OUTPUT);

    sscanf(FW_VERSION, "%hhu.%hhu.%hhu", &gFwMajor, &gFwMinor, &gFwPatch);

    Config.begin();

    head.begin();
    head.setIdleNoise(true);
    anim.begin(&head);

    // Initial servo/auto-anim state: NVS-persisted value if this droid has
    // ever been toggled before; the compile-time default (master paused if
    // MASTER_ANIM_PAUSED) only applies on a never-configured board.
#if IS_MASTER && MASTER_ANIM_PAUSED
    gServos = Config.servosEnabled(false);
#else
    gServos = Config.servosEnabled(true);
#endif
    gAutoAnim = Config.autoAnimEnabled(true);
    head.setEnabled(gServos);

    // Restores this droid's own last freq/amp/speed (master: last committed
    // value; slave: last value immediately persisted via a received
    // MSG_CONFIG) — defaults (50/60/50) if never set, matching today's
    // untouched tuning.
    {
        uint8_t f, a, s;
        Config.animParams(f, a, s);
        applyAnimParamsEffect(f, a, s);
    }

#if IS_MASTER
    Console.begin();
    Console.onAnim(playLocalAnim);
    Console.onServo(onServoCmd);
    Console.onAutoAnim(onAutoAnimCmd);
    Console.onConfig(applyAnimParamsEffect);
    Console.onLocate(onLocateCmd);
    Console.onCalib(onCalibCmd);
    Console.onPreview(onPreviewCmd);
    Console.onOtaStart(onOtaStartCmd);
    Console.onOtaChunk(onOtaChunkCmd);
    Console.onOtaAbort(onOtaAbortCmd);
    Console.setMasterServos(gServos);
    Console.setMasterAutoAnim(gAutoAnim);
#endif

    if (Mesh.begin(GROUP_KEY)) {
        Mesh.onReceive(onMeshMessage);
        // Persisted calibration of THIS droid (default limits if never set).
        const ServoCalib c = Config.getCalib(Mesh.myId());
        head.setLimits(c.panMin, c.panCenter, c.panMax, c.tiltMin, c.tiltCenter, c.tiltMax);
        LOGF("mesh ready, id=%04X (servos %s)", Mesh.myId(), gServos ? "ON" : "OFF");
    } else {
        LOGF("mesh: initialization failed");
    }
    head.center();
}

void loop() {
    const uint32_t now = millis();
    esp_task_wdt_reset();
    Guard.confirmIfPending(now);

    head.update();
    if (gServos) anim.update();
#if IS_MASTER
    Console.update();
    OtaM.update(now);
    pumpOtaEvents();
#else
    OtaS.update(now);
#endif

    // Life LED — "locate" override: solid on instead of the normal blink.
    if (gLocateOn) {
        digitalWrite(PIN_LED_ONBOARD, HIGH);
    } else if (now - lastBlink >= LED_BLINK_MS) {
        lastBlink = now;
        ledOn = !ledOn;
        digitalWrite(PIN_LED_ONBOARD, ledOn ? HIGH : LOW);
    }

    // Heartbeat: each droid reports its presence (and its servo state).
    if (now > nextHeartbeat) {
        nextHeartbeat = now + HEARTBEAT_MS;
        HeartbeatPayload hb{now, (uint8_t)((gServos ? 1 : 0) | (gAutoAnim ? 2 : 0)), gFwMajor, gFwMinor, gFwPatch};
        Mesh.send(MSG_HEARTBEAT, &hb, sizeof(hb));
    }

    // Direct neighborhood report (topology): each droid periodically
    // broadcasts the nodes it hears directly, with the measured RSSI.
    // Random jitter to avoid every droid transmitting in lockstep (ESP-NOW
    // broadcast has no acknowledgment: repeated collisions would
    // systematically lose these reports).
    if (now > nextNeighborReport) {
        nextNeighborReport = now + NEIGHBOR_REPORT_MS + (uint32_t)random(0, 500);
        NeighborReportPayload rep{};
        rep.count = Mesh.copyNeighbors(rep.entries, MAX_NEIGHBORS, NEIGHBOR_STALE_MS);
        Mesh.send(MSG_NEIGHBORS, &rep, sizeof(rep));
#if IS_MASTER
        // Its own direct neighborhood is already known locally, no need to
        // wait for a network round-trip to fold it into the topology.
        const uint32_t now2 = millis();
        for (uint8_t i = 0; i < rep.count; i++)
            MeshTopo.seen(Mesh.myId(), rep.entries[i].id, rep.entries[i].rssi, now2);
#endif
    }

#if IS_MASTER
    // Presence monitoring: reports a B1 going offline.
    if (now > nextPresenceScan) {
        nextPresenceScan = now + 1000;
        for (uint8_t i = 0; i < Droids.count(); i++) {
            const bool on = Droids.online(i, now, DROID_TIMEOUT_MS);
            if (wasOnline[i] && !on) {
                const String name = Config.getName(Droids.at(i).id);
                LOGF("B1 %04X%s%s offline", Droids.at(i).id,
                     name.length() ? " " : "", name.c_str());
            }
            wasOnline[i] = on;
        }
    }

    // Periodically sends the droid list to the web console.
    if (now > nextDroidsPush) {
        nextDroidsPush = now + 1500;
        Console.pushDroids();
        Console.pushMeshTopology();
    }

    // The master picks a random anim, plays it, and broadcasts it to the group.
    if (gServos && gAutoAnim && !anim.isPlaying() && now > nextMeshSend) {
        nextMeshSend = now + (uint32_t)(random(2500, 5000) * idleFreqScale());
        const uint32_t seed = (uint32_t)esp_random();
        const uint8_t animId = AnimationPlayer::randomAnimId(seed);
        AnimPayload p{MESH_TARGET_ALL, animId, 0, seed};
        Mesh.send(MSG_ANIM, &p, sizeof(p));
        anim.play(animId, seed);
    }
#else
    // An isolated slave (no master) also animates itself on its own.
    if (gServos && gAutoAnim && !anim.isPlaying() && now > nextMove) {
        nextMove = now + (uint32_t)(random(3000, 7000) * idleFreqScale());
        const uint32_t seed = (uint32_t)esp_random();
        anim.play(AnimationPlayer::randomAnimId(seed), seed);
    }
#endif
}
