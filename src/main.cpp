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

// Runtime servo state of THIS droid (controllable from the web console).
static bool gServos = false;

// Transient safety latch. Safe Stop and Emergency Stop block untracked or
// delayed animation traffic until an explicit tracked gesture is accepted.
// DroidController will replace this narrow temporary ownership mechanism in V2.
static bool gSafetyHold = false;

// Life LED (execution indicator) — non-blocking blink, overridden solid by "locate".
static uint32_t lastBlink = 0;
static bool ledOn = false;

// "Locate" (find-me) override of THIS droid's onboard LED — solid on while
// active, resumes the normal execution-indicator blink once cleared. Not
// persisted (console-driven, ephemeral like preview positioning).
static bool gLocateOn = false;

// Firmware version, decomposed once at startup from FW_VERSION (config.h)
// to be included (compact, 3 bytes) in every heartbeat.
static uint8_t gFwMajor = 0, gFwMinor = 0, gFwPatch = 0;

static uint32_t nextHeartbeat = 0;
static uint32_t nextPresenceScan = 0;
static uint32_t nextDroidsPush = 0;
static uint32_t nextNeighborReport = 0;

// Valid application messages arrive on the ESP-NOW Wi-Fi task. Keep that
// callback short: it only copies them here, then loop() performs all logging,
// NVS, registry, topology, animation, and servo work.
static const uint8_t MESH_INBOX_CAPACITY = 32;
static const uint8_t MESH_INBOX_MAX_PER_LOOP = 16;

struct PendingMeshMessage {
    uint8_t  type;
    uint8_t  len;
    uint16_t srcId;
    uint16_t seq;
    int16_t  rssi;
    uint8_t  payload[MESH_MAX_PAYLOAD];
};

static PendingMeshMessage gMeshInbox[MESH_INBOX_CAPACITY];
static uint8_t gMeshInboxHead = 0;
static uint8_t gMeshInboxTail = 0;
static uint8_t gMeshInboxCount = 0;
static uint32_t gMeshInboxDropped = 0;
static portMUX_TYPE gMeshInboxMux = portMUX_INITIALIZER_UNLOCKED;

struct MeshInboxGuard {
    MeshInboxGuard() { portENTER_CRITICAL(&gMeshInboxMux); }
    ~MeshInboxGuard() { portEXIT_CRITICAL(&gMeshInboxMux); }
};

static void enqueueMeshMessage(uint8_t type, const uint8_t* payload, uint8_t len,
                               uint16_t srcId, uint16_t seq, int rssi) {
    if (len > MESH_MAX_PAYLOAD || (len > 0 && payload == nullptr)) return;

    MeshInboxGuard guard;
    if (gMeshInboxCount >= MESH_INBOX_CAPACITY) {
        gMeshInboxDropped++;
        return;
    }

    PendingMeshMessage& msg = gMeshInbox[gMeshInboxTail];
    msg.type = type;
    msg.len = len;
    msg.srcId = srcId;
    msg.seq = seq;
    msg.rssi = (int16_t)rssi;
    if (len > 0) memcpy(msg.payload, payload, len);

    gMeshInboxTail = (uint8_t)((gMeshInboxTail + 1) % MESH_INBOX_CAPACITY);
    gMeshInboxCount++;
}

static bool dequeueMeshMessage(PendingMeshMessage& out) {
    MeshInboxGuard guard;
    if (gMeshInboxCount == 0) return false;

    out = gMeshInbox[gMeshInboxHead];
    gMeshInboxHead = (uint8_t)((gMeshInboxHead + 1) % MESH_INBOX_CAPACITY);
    gMeshInboxCount--;
    return true;
}

static bool validMeshTarget(uint16_t target, bool allowAll = true) {
    return target != 0 && (allowAll || target != MESH_TARGET_ALL);
}

static bool validCalibPayload(const CalibPayload& p) {
    return validMeshTarget(p.targetId) &&
           p.panMin <= 180 && p.panCenter <= 180 && p.panMax <= 180 &&
           p.tiltMin <= 180 && p.tiltCenter <= 180 && p.tiltMax <= 180 &&
           p.panMin <= p.panCenter && p.panCenter <= p.panMax &&
           p.tiltMin <= p.tiltCenter && p.tiltCenter <= p.tiltMax;
}

static bool validCalibV2Payload(const CalibV2Payload& p) {
    const CalibPayload limits{p.targetId, p.panMin, p.panCenter, p.panMax,
                              p.tiltMin, p.tiltCenter, p.tiltMax};
    return validCalibPayload(limits) && p.panReversed <= 1 && p.tiltReversed <= 1;
}

// One tracked console animation may produce several reports (one per target).
// The request map is master-local and keyed by the unchanged mesh header seq;
// old slaves still execute MSG_ANIM but simply never send MSG_ANIM_EXEC.
#if IS_MASTER
static const uint8_t ANIM_REQUEST_CAPACITY = 64;
struct AnimRequestRecord {
    bool used;
    uint16_t meshSeq;
    uint32_t requestId;
    uint16_t target;
    uint8_t animId;
    uint32_t createdAtMs;
};
static AnimRequestRecord gAnimRequests[ANIM_REQUEST_CAPACITY] = {};
static uint8_t gAnimRequestNext = 0;

static void rememberAnimRequest(uint16_t meshSeq, uint32_t requestId,
                                uint16_t target, uint8_t animId) {
    AnimRequestRecord& record = gAnimRequests[gAnimRequestNext];
    record = {true, meshSeq, requestId, target, animId, millis()};
    gAnimRequestNext = (uint8_t)((gAnimRequestNext + 1) % ANIM_REQUEST_CAPACITY);
}

static const AnimRequestRecord* findAnimRequest(uint16_t meshSeq, uint8_t animId) {
    const uint32_t now = millis();
    for (uint8_t offset = 0; offset < ANIM_REQUEST_CAPACITY; offset++) {
        const uint8_t index = (uint8_t)((gAnimRequestNext + ANIM_REQUEST_CAPACITY - 1 - offset) %
                                        ANIM_REQUEST_CAPACITY);
        const AnimRequestRecord& record = gAnimRequests[index];
        if (record.used && record.meshSeq == meshSeq && record.animId == animId &&
            now - record.createdAtMs < 120000UL) return &record;
    }
    return nullptr;
}
#endif

struct ActiveAnimExecution {
    bool active = false;
    uint16_t originSeq = 0;
    uint8_t animId = ANIM_IDLE;
    bool broadcast = false;
    bool leased = false;
    uint32_t leaseDeadlineMs = 0;
};
static ActiveAnimExecution gActiveAnimExec;

#if !IS_MASTER
static const uint8_t ANIM_REPORT_QUEUE_CAPACITY = 8;
struct PendingAnimExecReport {
    bool used;
    AnimExecPayload payload;
    uint32_t dueAtMs;
};
static PendingAnimExecReport gAnimReportQueue[ANIM_REPORT_QUEUE_CAPACITY] = {};
static uint8_t gAnimReportNext = 0;

static void queueAnimExecReport(const AnimExecPayload& payload, bool broadcast) {
    PendingAnimExecReport& pending = gAnimReportQueue[gAnimReportNext];
    // Deterministic 10..99 ms spreading prevents every broadcast recipient
    // from replying in the same radio slot. A full queue drops the oldest
    // telemetry report, never the animation itself.
    const uint32_t jitterMs = broadcast ? 10U + (Mesh.myId() % 90U) : 0U;
    pending = {true, payload, millis() + jitterMs};
    gAnimReportNext = (uint8_t)((gAnimReportNext + 1) % ANIM_REPORT_QUEUE_CAPACITY);
}

static void pumpAnimExecReports() {
    const uint32_t now = millis();
    uint8_t sent = 0;
    for (uint8_t i = 0; i < ANIM_REPORT_QUEUE_CAPACITY && sent < 2; i++) {
        PendingAnimExecReport& pending = gAnimReportQueue[i];
        if (!pending.used || (int32_t)(now - pending.dueAtMs) < 0) continue;
        Mesh.send(MSG_ANIM_EXEC, &pending.payload, sizeof(pending.payload));
        pending.used = false;
        sent++;
    }
}
#endif

static void publishAnimExec(uint16_t droidId, const AnimExecPayload& payload,
                            bool broadcast) {
#if IS_MASTER
    (void)broadcast;
    const AnimRequestRecord* request = findAnimRequest(payload.originSeq, payload.animId);
    if (!request) return;
    if (request->target != MESH_TARGET_ALL && request->target != droidId) return;
    Console.pushAnimExec(request->requestId, droidId, payload.originSeq,
                         payload.animId, payload.phase, payload.reason, payload.atMs);
#else
    (void)droidId;
    queueAnimExecReport(payload, broadcast);
#endif
}

static void reportAnimExec(uint16_t originSeq, uint8_t animId, uint8_t phase,
                           uint8_t reason, bool broadcast) {
    const AnimExecPayload report{originSeq, animId, phase, reason, millis()};
    publishAnimExec(Mesh.myId(), report, broadcast);
}

static void interruptTrackedAnimation() {
    if (!gActiveAnimExec.active) return;
    reportAnimExec(gActiveAnimExec.originSeq, gActiveAnimExec.animId,
                   ANIM_EXEC_INTERRUPTED, ANIM_EXEC_REASON_NONE,
                   gActiveAnimExec.broadcast);
    gActiveAnimExec.active = false;
}

static void startAnimationCommand(uint16_t targetId, uint8_t animId, uint32_t seed,
                                  uint16_t originSeq, bool tracked,
                                  uint16_t leaseMs = 0) {
    const bool broadcast = targetId == MESH_TARGET_ALL;
    // Ignore stale or external untracked traffic while held so Safe Stop cannot
    // be undone before a tracked operator/Sequencer gesture deliberately releases it.
    if (gSafetyHold && !tracked) return;
    interruptTrackedAnimation();

    if (!gServos) {
        if (tracked) {
            reportAnimExec(originSeq, animId, ANIM_EXEC_REJECTED,
                           ANIM_EXEC_REASON_SERVOS_OFF, broadcast);
        }
        return;
    }

    // A tracked operator/Sequencer gesture is the deliberate release action.
    gSafetyHold = false;
    anim.play(animId, seed);
    if (!tracked) return;

    reportAnimExec(originSeq, animId, ANIM_EXEC_STARTED,
                   ANIM_EXEC_REASON_NONE, broadcast);
    if (anim.isPlaying()) {
        gActiveAnimExec.active = true;
        gActiveAnimExec.originSeq = originSeq;
        gActiveAnimExec.animId = animId;
        gActiveAnimExec.broadcast = broadcast;
        gActiveAnimExec.leased = leaseMs > 0;
        gActiveAnimExec.leaseDeadlineMs = millis() + leaseMs;
    } else {
        // IDLE has no keyframes: it centers immediately and is complete.
        reportAnimExec(originSeq, animId, ANIM_EXEC_COMPLETED,
                       ANIM_EXEC_REASON_NONE, broadcast);
    }
}

static void startAnimationCommand(const AnimPayload& payload, uint16_t originSeq) {
    startAnimationCommand(payload.targetId, payload.animId, payload.seed, originSeq,
                          (payload.syncDelayMs & ANIM_EXEC_TRACKED_FLAG) != 0);
}

static bool validLeasedAnimPayload(const LeasedAnimPayload& payload) {
    return validMeshTarget(payload.targetId) &&
           (payload.animId == ANIM_POWER_DOWN || payload.animId == ANIM_TALK) &&
           payload.leaseMs >= ANIM_LEASE_MIN_MS &&
           payload.leaseMs <= ANIM_LEASE_MAX_MS;
}

static void startLeasedAnimationCommand(const LeasedAnimPayload& payload,
                                        uint16_t originSeq) {
    startAnimationCommand(payload.targetId, payload.animId, payload.seed, originSeq,
                          true, payload.leaseMs);
}

static void renewAnimationLease(const AnimLeaseRenewPayload& payload) {
    if (payload.targetId != MESH_TARGET_ALL && payload.targetId != Mesh.myId()) return;
    if (!gActiveAnimExec.active || !gActiveAnimExec.leased ||
        gActiveAnimExec.originSeq != payload.originSeq) return;
    gActiveAnimExec.leaseDeadlineMs = millis() + payload.leaseMs;
}

static void finishTrackedAnimationIfNeeded() {
    if (!gActiveAnimExec.active) return;
    if (gActiveAnimExec.leased &&
        (int32_t)(millis() - gActiveAnimExec.leaseDeadlineMs) >= 0) {
        reportAnimExec(gActiveAnimExec.originSeq, gActiveAnimExec.animId,
                       ANIM_EXEC_INTERRUPTED, ANIM_EXEC_REASON_LEASE_EXPIRED,
                       gActiveAnimExec.broadcast);
        gActiveAnimExec.active = false;
        anim.play(ANIM_IDLE, esp_random());
        return;
    }
    if (anim.isPlaying()) return;
    reportAnimExec(gActiveAnimExec.originSeq, gActiveAnimExec.animId,
                   ANIM_EXEC_COMPLETED, ANIM_EXEC_REASON_NONE,
                   gActiveAnimExec.broadcast);
    gActiveAnimExec.active = false;
}

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
    if (!en) {
        gSafetyHold = true;
        interruptTrackedAnimation();
        anim.stop();
    }
    Config.setServosEnabledImmediate(en);
#if IS_MASTER
    Console.setMasterServos(en);
#endif
    LOGF("servos %s", en ? "ON" : "OFF");
}

static void applySafeStop() {
    interruptTrackedAnimation();
    anim.stop();
    if (gServos) anim.play(ANIM_IDLE, esp_random());
    gSafetyHold = true;
    LOGF("safe stop: centered and holding");
}

// Pauses/resumes THIS droid's spontaneous idle animation. Persisted
// immediately, same reasoning as applyServos.
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
#if IS_MASTER
    Console.setMasterLocate(en);
#endif
    LOGF("locate %s", en ? "ON" : "OFF");
}

// Persists and applies a received calibration for THIS droid (master or slave).
static void applyCalib(const ServoCalib& c) {
    Config.setCalib(Mesh.myId(), c);
    head.setLimits(c.panMin, c.panCenter, c.panMax, c.tiltMin, c.tiltCenter, c.tiltMax);
    head.setReversed(c.panReversed != 0, c.tiltReversed != 0);
    head.center();
    LOGF("calibration applied (pan %u/%u/%u%s, tilt %u/%u/%u%s)",
         c.panMin, c.panCenter, c.panMax, c.panReversed ? " reversed" : "",
         c.tiltMin, c.tiltCenter, c.tiltMax, c.tiltReversed ? " reversed" : "");
}

// Console hook: dispatch a tracked command and play it locally when targeted.
#if IS_MASTER
static void onAnimCmd(uint16_t target, uint8_t animId, uint32_t seed,
                      uint32_t requestId, uint16_t leaseMs) {
    uint16_t meshSeq = 0;
    bool meshQueued;
    if (leaseMs > 0) {
        const LeasedAnimPayload payload{target, animId, leaseMs, seed};
        meshQueued = Mesh.send(MSG_ANIM_LEASED, &payload, sizeof(payload), MESH_TTL, &meshSeq);
    } else {
        const AnimPayload payload{target, animId, ANIM_EXEC_TRACKED_FLAG, seed};
        meshQueued = Mesh.send(MSG_ANIM, &payload, sizeof(payload), MESH_TTL, &meshSeq);
    }
    const bool localHandled = target == MESH_TARGET_ALL || target == Mesh.myId();
    rememberAnimRequest(meshSeq, requestId, target, animId);
    Console.pushAnimAccepted(requestId, target, animId, meshSeq, meshQueued,
                             localHandled, leaseMs);
    if (localHandled) {
        if (leaseMs > 0) {
            const LeasedAnimPayload payload{target, animId, leaseMs, seed};
            startLeasedAnimationCommand(payload, meshSeq);
        } else {
            const AnimPayload payload{target, animId, ANIM_EXEC_TRACKED_FLAG, seed};
            startAnimationCommand(payload, meshSeq);
        }
    }
}

static void onAnimLeaseRenewCmd(uint16_t target, uint16_t originSeq,
                                uint16_t leaseMs) {
    const AnimLeaseRenewPayload payload{target, originSeq, leaseMs};
    Mesh.send(MSG_ANIM_LEASE_RENEW, &payload, sizeof(payload));
    renewAnimationLease(payload);
}

static void onSafeStopCmd(uint16_t target) {
    const SafeStopPayload payload{target};
    Mesh.send(MSG_SAFE_STOP, &payload, sizeof(payload));
    if (target == MESH_TARGET_ALL || target == Mesh.myId()) applySafeStop();
}

// Console hook: enable/disable a target's servos (master).
static void onServoCmd(uint16_t target, bool en) {
    ServoPayload p{target, (uint8_t)(en ? 1 : 0)};
    Mesh.send(MSG_SERVO, &p, sizeof(p));
    if (target == MESH_TARGET_ALL || target == Mesh.myId()) applyServos(en);
}

// Console hook: toggle a target's "locate" LED (master).
static void onLocateCmd(uint16_t target, bool en) {
    LocatePayload p{target, (uint8_t)(en ? 1 : 0)};
    Mesh.send(MSG_LOCATE, &p, sizeof(p));
    if (target == MESH_TARGET_ALL || target == Mesh.myId()) applyLocate(en);
}

// Console hook: calibration received (already filtered on target == this droid).
static void onCalibCmd(uint16_t target, uint8_t panMin, uint8_t panCenter, uint8_t panMax,
                        uint8_t tiltMin, uint8_t tiltCenter, uint8_t tiltMax,
                        bool panReversed, bool tiltReversed) {
    (void)target;
    applyCalib(ServoCalib{panMin, panCenter, panMax, tiltMin, tiltCenter, tiltMax,
                          (uint8_t)panReversed, (uint8_t)tiltReversed});
}

// Console hook: transient preview (not persisted), already filtered on target.
static void onPreviewCmd(uint16_t target, uint8_t pan, uint8_t tilt) {
    (void)target;
    head.setTarget(pan, tilt, 150);
}
#endif

static void processMeshMessage(uint8_t type, const uint8_t* payload, uint8_t len,
                               uint16_t srcId, uint16_t seq, int rssi) {
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
        if (!validMeshTarget(p.targetId) || p.animId >= ANIM_COUNT) {
            LOGF("invalid ANIM payload from %04X", srcId);
            return;
        }
        LOGF("ANIM from %04X (rssi %d) target=%04X anim=%u", srcId, rssi, p.targetId, p.animId);
        if (p.targetId == MESH_TARGET_ALL || p.targetId == Mesh.myId()) {
            startAnimationCommand(p, seq);
        }
    } else if (type == MSG_ANIM_LEASED && len == sizeof(LeasedAnimPayload)) {
        LeasedAnimPayload p;
        memcpy(&p, payload, sizeof(p));
        if (!validLeasedAnimPayload(p)) {
            LOGF("invalid leased ANIM payload from %04X", srcId);
            return;
        }
        if (p.targetId == MESH_TARGET_ALL || p.targetId == Mesh.myId())
            startLeasedAnimationCommand(p, seq);
    } else if (type == MSG_ANIM_LEASE_RENEW && len == sizeof(AnimLeaseRenewPayload)) {
        AnimLeaseRenewPayload p;
        memcpy(&p, payload, sizeof(p));
        if (!validMeshTarget(p.targetId) || p.leaseMs < ANIM_LEASE_MIN_MS ||
            p.leaseMs > ANIM_LEASE_MAX_MS) return;
        renewAnimationLease(p);
    } else if (type == MSG_SAFE_STOP && len == sizeof(SafeStopPayload)) {
        SafeStopPayload p;
        memcpy(&p, payload, sizeof(p));
        if (!validMeshTarget(p.targetId)) return;
        if (p.targetId == MESH_TARGET_ALL || p.targetId == Mesh.myId())
            applySafeStop();
    } else if (type == MSG_ANIM_EXEC && len == sizeof(AnimExecPayload)) {
#if IS_MASTER
        AnimExecPayload p;
        memcpy(&p, payload, sizeof(p));
        if (p.animId >= ANIM_COUNT || p.phase < ANIM_EXEC_STARTED ||
            p.phase > ANIM_EXEC_REJECTED || p.reason > ANIM_EXEC_REASON_LEASE_EXPIRED) return;
        publishAnimExec(srcId, p, false);
#endif
    } else if (type == MSG_SERVO && len == sizeof(ServoPayload)) {
        ServoPayload p;
        memcpy(&p, payload, sizeof(p));
        if (!validMeshTarget(p.targetId) || p.enabled > 1) return;
        if (p.targetId == MESH_TARGET_ALL || p.targetId == Mesh.myId())
            applyServos(p.enabled != 0);
    } else if (type == MSG_LOCATE && len == sizeof(LocatePayload)) {
        LocatePayload p;
        memcpy(&p, payload, sizeof(p));
        if (!validMeshTarget(p.targetId) || p.enabled > 1) return;
        if (p.targetId == MESH_TARGET_ALL || p.targetId == Mesh.myId())
            applyLocate(p.enabled != 0);
    } else if (type == MSG_NAME && len == sizeof(NamePayload)) {
        NamePayload p;
        memcpy(&p, payload, sizeof(p));
        p.name[sizeof(p.name) - 1] = '\0'; // defensive: guarantee NUL-termination
        if (!validMeshTarget(p.targetId, false)) return;
        if (p.targetId == Mesh.myId()) applyName(p.name);
    } else if (type == MSG_CALIB && len == sizeof(CalibPayload)) {
        CalibPayload p;
        memcpy(&p, payload, sizeof(p));
        if (!validCalibPayload(p)) {
            LOGF("invalid CALIB payload from %04X", srcId);
            return;
        }
        if (p.targetId == MESH_TARGET_ALL || p.targetId == Mesh.myId()) {
            // A legacy payload carries only limits. Preserve any direction
            // flags already stored by newer firmware.
            ServoCalib c = Config.getCalib(Mesh.myId());
            c.panMin = p.panMin; c.panCenter = p.panCenter; c.panMax = p.panMax;
            c.tiltMin = p.tiltMin; c.tiltCenter = p.tiltCenter; c.tiltMax = p.tiltMax;
            applyCalib(c);
        }
    } else if (type == MSG_CALIB_V2 && len == sizeof(CalibV2Payload)) {
        CalibV2Payload p;
        memcpy(&p, payload, sizeof(p));
        if (!validCalibV2Payload(p)) {
            LOGF("invalid CALIB_V2 payload from %04X", srcId);
            return;
        }
        if (p.targetId == MESH_TARGET_ALL || p.targetId == Mesh.myId())
            applyCalib(ServoCalib{p.panMin, p.panCenter, p.panMax,
                                  p.tiltMin, p.tiltCenter, p.tiltMax,
                                  p.panReversed, p.tiltReversed});
    } else if (type == MSG_PREVIEW && len == sizeof(PreviewPayload)) {
        PreviewPayload p;
        memcpy(&p, payload, sizeof(p));
        if (!validMeshTarget(p.targetId) || p.pan > 180 || p.tilt > 180) return;
        if (p.targetId == MESH_TARGET_ALL || p.targetId == Mesh.myId())
            head.setTarget(p.pan, p.tilt, 150);
    } else if (type == MSG_HEARTBEAT && len == sizeof(HeartbeatPayload)) {
#if IS_MASTER
        HeartbeatPayload hb;
        memcpy(&hb, payload, sizeof(hb));
        Droids.setServos(srcId, hb.state & 0x01);
        Droids.setLocate(srcId, hb.state & 0x04);
        Droids.setFwIdentity(srcId, hb.fwMajor, hb.fwMinor, hb.fwPatch, hb.buildId);
#endif
    } else if (type == MSG_HEARTBEAT && len == sizeof(LegacyHeartbeatPayload)) {
#if IS_MASTER
        LegacyHeartbeatPayload hb;
        memcpy(&hb, payload, sizeof(hb));
        Droids.setServos(srcId, hb.state & 0x01);
        Droids.setLocate(srcId, false);
        Droids.setFwIdentity(srcId, hb.fwMajor, hb.fwMinor, hb.fwPatch, 0);
#endif
    } else if (type == MSG_CAPABILITIES && len == sizeof(CapabilitiesPayload)) {
#if IS_MASTER
        CapabilitiesPayload p;
        memcpy(&p, payload, sizeof(p));
        Droids.setCapabilities(srcId, p.flags);
#endif
    } else if (type == MSG_HEARTBEAT) {
        // old form / presence: already noted.
    } else if (type == MSG_NEIGHBORS && len == sizeof(NeighborReportPayload)) {
#if IS_MASTER
        NeighborReportPayload rep;
        memcpy(&rep, payload, sizeof(rep));
        if (rep.count > MAX_NEIGHBORS) return;
        const uint32_t now2 = millis();
        const uint8_t n = rep.count > MAX_NEIGHBORS ? MAX_NEIGHBORS : rep.count;
        for (uint8_t i = 0; i < n; i++)
            MeshTopo.seen(srcId, rep.entries[i].id, rep.entries[i].rssi, now2);
#endif
    } else {
        LOGF("type=%u len=%u from %04X (rssi %d)", type, len, srcId, rssi);
    }
}

// ESP-NOW receive callback. OTA already has its own callback-safe mailbox /
// lock and remains on this low-latency path; every other application message
// is copied to the inbox and processed by loop().
static void onMeshMessage(uint8_t type, const uint8_t* payload, uint8_t len,
                          uint16_t srcId, uint16_t seq, int rssi) {
#if IS_MASTER
    if (type == MSG_OTA_ACK && len == sizeof(OtaAckPayload)) {
        OtaAckPayload p;
        memcpy(&p, payload, sizeof(p));
        OtaM.onAck(srcId, p);
        return;
    }
#else
    if (type == MSG_OTA_START && len == sizeof(OtaStartPayload)) {
        OtaStartPayload p;
        memcpy(&p, payload, sizeof(p));
        OtaS.onStart(srcId, p);
        return;
    }
    if (type == MSG_OTA_CHUNK && len == sizeof(OtaChunkPayload)) {
        OtaChunkPayload p;
        memcpy(&p, payload, sizeof(p));
        OtaS.onChunk(srcId, p);
        return;
    }
    if (type == MSG_OTA_END && len == sizeof(OtaEndPayload)) {
        OtaEndPayload p;
        memcpy(&p, payload, sizeof(p));
        OtaS.onEnd(srcId, p);
        return;
    }
    if (type == MSG_OTA_ABORT && len == sizeof(OtaAbortPayload)) {
        OtaAbortPayload p;
        memcpy(&p, payload, sizeof(p));
        OtaS.onAbort(srcId, p);
        return;
    }
#endif

    enqueueMeshMessage(type, payload, len, srcId, seq, rssi);
}

static void pumpMeshInbox() {
    PendingMeshMessage msg;
    uint8_t processed = 0;
    while (processed < MESH_INBOX_MAX_PER_LOOP && dequeueMeshMessage(msg)) {
        processMeshMessage(msg.type, msg.payload, msg.len, msg.srcId, msg.seq, msg.rssi);
        processed++;
    }

    // A bounded queue is preferable to exhausting RAM under a radio flood.
    // Report cumulative losses from loop(), never from the Wi-Fi callback.
    static uint32_t reportedDropped = 0;
    static uint32_t lastDropReportMs = 0;
    uint32_t dropped;
    {
        MeshInboxGuard guard;
        dropped = gMeshInboxDropped;
    }
    const uint32_t now = millis();
    if (dropped != reportedDropped && now - lastDropReportMs >= 1000) {
        LOGF("mesh inbox full: %lu message(s) dropped", (unsigned long)dropped);
        reportedDropped = dropped;
        lastDropReportMs = now;
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
        Console.pushOtaResult(ev.target, ev.ok, ev.fw, ev.buildId, ev.reason);
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
    anim.begin(&head);

    // A virgin/full-erased ESP32 always starts inert. Once the operator has
    // explicitly changed these switches, their NVS values still survive normal
    // firmware updates and reboots.
    gServos = Config.servosEnabled(false);
    head.setEnabled(gServos);

#if IS_MASTER
    Console.begin();
    Console.onAnim(onAnimCmd);
    Console.onAnimLeaseRenew(onAnimLeaseRenewCmd);
    Console.onSafeStop(onSafeStopCmd);
    Console.onServo(onServoCmd);
    Console.onLocate(onLocateCmd);
    Console.onCalib(onCalibCmd);
    Console.onPreview(onPreviewCmd);
    Console.onOtaStart(onOtaStartCmd);
    Console.onOtaChunk(onOtaChunkCmd);
    Console.onOtaAbort(onOtaAbortCmd);
    Console.setMasterServos(gServos);
    Console.setMasterLocate(gLocateOn);
#endif

    const bool meshReady = Mesh.begin(GROUP_KEY);
    if (meshReady) {
        Mesh.onReceive(onMeshMessage);
        // Persisted calibration of THIS droid (default limits if never set).
        const ServoCalib c = Config.getCalib(Mesh.myId());
        head.setLimits(c.panMin, c.panCenter, c.panMax, c.tiltMin, c.tiltCenter, c.tiltMax);
        head.setReversed(c.panReversed != 0, c.tiltReversed != 0);
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
    pumpMeshInbox();
    finishTrackedAnimationIfNeeded();
#if IS_MASTER
    Console.update();
    OtaM.update(now);
    pumpOtaEvents();
#else
    OtaS.update(now);
    pumpAnimExecReports();
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
        HeartbeatPayload hb{now,
                            (uint8_t)((gServos ? 1 : 0) | (gLocateOn ? 4 : 0)),
                            gFwMajor, gFwMinor, gFwPatch,
                            (uint32_t)FW_BUILD_ID};
        Mesh.send(MSG_HEARTBEAT, &hb, sizeof(hb));
        const CapabilitiesPayload capabilities{DROID_CAP_SERVO_REVERSE};
        Mesh.send(MSG_CAPABILITIES, &capabilities, sizeof(capabilities));
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

#endif
}
