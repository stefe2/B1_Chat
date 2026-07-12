#include <Arduino.h>
#include "config.h"
#include "servo_engine.h"
#include "animation.h"
#include "mesh_comm.h"
#include "mesh_topology.h"
#include "registry.h"
#include "config_store.h"
#include "serial_console.h"
#include "sequence_store.h"
#include "audio.h"

// NOTE: banc de test temporaire (étapes 2-8). Sera remplacé par la machine
// à états du droïde à l'étape 6.

// Journalisation : JSON via la console sur le maître, texte brut sur l'esclave.
#if IS_MASTER
  #define LOGF(fmt, ...) Console.log(fmt, ##__VA_ARGS__)
#else
  #define LOGF(fmt, ...) do { Serial.printf(fmt, ##__VA_ARGS__); Serial.print('\n'); } while (0)
#endif

static ServoEngine head;
static AnimationPlayer anim;
static uint32_t nextMove = 0;

// État runtime des servos de CE droïde (pilotable depuis la console web).
static bool gServos = true;

// Anims spontanées au repos de CE droïde (pilotable depuis la console web).
// N'affecte pas Jouer (anim) ni le Séquenceur : uniquement le tirage aléatoire au repos.
static bool gAutoAnim = true;

// LED de vie (témoin d'exécution) — clignotement non bloquant.
static uint32_t lastBlink = 0;
static bool ledOn = false;

// Test mesh / timers
static uint32_t nextMeshSend = 0;
static uint32_t nextHeartbeat = 0;
static uint32_t nextPresenceScan = 0;
static uint32_t nextDroidsPush = 0;
static uint32_t nextNeighborReport = 0;

// Lecteur de séquence stockée (maître) : exécution autonome sans PC.
#if IS_MASTER
static StoredSequence gSeq;
static bool gSeqPlaying = false;
static uint8_t gSeqIndex = 0;
static uint32_t gSeqNextAt = 0;
static uint8_t gSeqSlot = 0;
// Vrai si l'étape en cours cible ce maître : on attend alors la fin réelle de
// l'animation locale (anim.isPlaying()) en plus du délai avant d'avancer. Pour
// une étape ciblant uniquement un esclave distant, cette donnée n'existe pas
// côté maître (pas d'accusé de réception mesh) : on reste sur le délai seul.
static bool gSeqWaitLocal = false;
#endif

// Suivi hors-ligne (maître) : mémorise l'état en ligne pour signaler les pertes.
static const uint32_t DROID_TIMEOUT_MS = 7000;
#if IS_MASTER
static bool wasOnline[Registry::MAX];
#endif

// Active/coupe les servos de ce droïde (protection matérielle).
static void applyServos(bool en) {
    gServos = en;
    head.setEnabled(en);
    if (!en) anim.stop();
#if IS_MASTER
    Console.setMasterServos(en);
#endif
    LOGF("servos %s", en ? "ACTIFS" : "COUPÉS");
}

// Met en pause/reprend l'animation spontanée au repos de CE droïde.
static void applyAutoAnim(bool en) {
    gAutoAnim = en;
#if IS_MASTER
    Console.setMasterAutoAnim(en);
#endif
    LOGF("anims auto %s", en ? "ACTIVES" : "EN PAUSE");
}

// Persiste et applique une calibration reçue pour CE droïde (maître ou esclave).
static void applyCalib(const CalibPayload& p) {
    const ServoCalib c{p.panMin, p.panCenter, p.panMax, p.tiltMin, p.tiltCenter, p.tiltMax};
    Config.setCalib(Mesh.myId(), c);
    head.setLimits(c.panMin, c.panCenter, c.panMax, c.tiltMin, c.tiltCenter, c.tiltMax);
    head.center();
    LOGF("calibration appliquee (pan %u/%u/%u, tilt %u/%u/%u)",
         p.panMin, p.panCenter, p.panMax, p.tiltMin, p.tiltCenter, p.tiltMax);
}

// Hook console : jouer une animation localement (maître).
#if IS_MASTER
static void playLocalAnim(uint8_t animId, uint32_t seed) {
    if (gServos) {
        anim.play(animId, seed);
        Audio.playForAnim(animId, seed);
    }
}

// Hook console : activer/couper les servos d'une cible (maître).
static void onServoCmd(uint16_t target, bool en) {
    ServoPayload p{target, (uint8_t)(en ? 1 : 0)};
    Mesh.send(MSG_SERVO, &p, sizeof(p));
    if (target == MESH_TARGET_ALL || target == Mesh.myId()) applyServos(en);
}

// Hook console : mettre en pause/reprendre l'animation spontanée d'une cible (maître).
static void onAutoAnimCmd(uint16_t target, bool en) {
    AutoAnimPayload p{target, (uint8_t)(en ? 1 : 0)};
    Mesh.send(MSG_AUTOANIM, &p, sizeof(p));
    if (target == MESH_TARGET_ALL || target == Mesh.myId()) applyAutoAnim(en);
}

static void onVolumeCmd(uint8_t v) {
    Audio.setVolume(v);
}

static void onTrackCmd(uint8_t track) {
    if (!Audio.playTrack(track)) LOGF("audio indisponible (track %u)", track);
}

static bool onSeqSave(uint8_t slot, const StoredSequence& seq) {
    return Sequences.save(slot, seq);
}

static uint8_t onSeqList(StoredSequenceMeta* out, uint8_t maxOut) {
    return Sequences.list(out, maxOut);
}

static bool onSeqLoad(uint8_t slot, StoredSequence& out) {
    return Sequences.load(slot, out);
}

static bool onSeqDelete(uint8_t slot) {
    return Sequences.remove(slot);
}

static void onSeqRun(uint8_t slot) {
    if (!Sequences.load(slot, gSeq) || gSeq.stepCount == 0) {
        gSeqPlaying = false;
        LOGF("sequence slot=%u introuvable/vide", slot);
        Console.pushSeqState(false, slot, 0, 0);
        return;
    }
    gSeqSlot = slot;
    gSeqPlaying = true;
    gSeqIndex = 0;
    gSeqWaitLocal = false;
    gSeqNextAt = millis();
    Console.pushSeqState(true, gSeqSlot, gSeqIndex, gSeq.stepCount);
}

static void onSeqStop() {
    gSeqPlaying = false;
    Console.pushSeqState(false, gSeqSlot, gSeqIndex, gSeq.stepCount);
}

// Répond à une demande ponctuelle d'état (ex. dashboard qui se connecte en
// cours de lecture, sans attendre la prochaine transition d'étape).
static void onSeqQueryCmd() {
    Console.pushSeqState(gSeqPlaying, gSeqSlot, gSeqIndex, gSeq.stepCount);
}

// Hook console : calibration reçue (déjà filtrée sur target == ce droïde).
static void onCalibCmd(uint16_t target, uint8_t panMin, uint8_t panCenter, uint8_t panMax,
                        uint8_t tiltMin, uint8_t tiltCenter, uint8_t tiltMax) {
    (void)target;
    const CalibPayload p{target, panMin, panCenter, panMax, tiltMin, tiltCenter, tiltMax};
    applyCalib(p);
}

// Hook console : aperçu transitoire (non persisté), déjà filtré sur target.
static void onPreviewCmd(uint16_t target, uint8_t pan, uint8_t tilt) {
    (void)target;
    head.setTarget(pan, tilt, 150);
}
#endif

static void onMeshMessage(uint8_t type, const uint8_t* payload, uint8_t len,
                          uint16_t srcId, int rssi) {
#if IS_MASTER
    // Tout message reçu prouve la présence de ce droïde.
    if (Droids.seen(srcId, rssi, millis())) {
        const String name = Config.getName(srcId);
        LOGF("nouveau B1 %04X%s%s connecté au mesh (total %u)",
             srcId, name.length() ? " " : "", name.c_str(), Droids.count());
    }
#else
    (void)srcId; (void)rssi;
#endif

    if (type == MSG_ANIM && len == sizeof(AnimPayload)) {
        AnimPayload p;
        memcpy(&p, payload, sizeof(p));
        LOGF("ANIM de %04X (rssi %d) target=%04X anim=%u", srcId, rssi, p.targetId, p.animId);
        if (p.targetId == MESH_TARGET_ALL || p.targetId == Mesh.myId()) {
            if (gServos) anim.play(p.animId, p.seed);
#if IS_MASTER
            Audio.playForAnim(p.animId, p.seed);
#endif
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
    } else if (type == MSG_CALIB && len == sizeof(CalibPayload)) {
        CalibPayload p;
        memcpy(&p, payload, sizeof(p));
        if (p.targetId == MESH_TARGET_ALL || p.targetId == Mesh.myId())
            applyCalib(p);
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
#endif
    } else if (type == MSG_HEARTBEAT) {
        // ancienne forme / présence : déjà notée.
    } else if (type == MSG_NEIGHBORS && len == sizeof(NeighborReportPayload)) {
#if IS_MASTER
        NeighborReportPayload rep;
        memcpy(&rep, payload, sizeof(rep));
        const uint32_t now2 = millis();
        const uint8_t n = rep.count > MAX_NEIGHBORS ? MAX_NEIGHBORS : rep.count;
        for (uint8_t i = 0; i < n; i++)
            MeshTopo.seen(srcId, rep.entries[i].id, rep.entries[i].rssi, now2);
#endif
    } else {
        LOGF("type=%u len=%u de %04X (rssi %d)", type, len, srcId, rssi);
    }
}

void setup() {
    Serial.begin(115200);
    pinMode(PIN_LED_ONBOARD, OUTPUT);

    Config.begin();
#if IS_MASTER
    Sequences.begin();
#endif

    head.begin();
    head.setIdleNoise(true);
    anim.begin(&head);

    // État initial des servos : maître en pause si MASTER_ANIM_PAUSED.
#if IS_MASTER && MASTER_ANIM_PAUSED
    gServos = false;
#else
    gServos = true;
#endif
    head.setEnabled(gServos);

#if IS_MASTER
    Console.begin();
    Console.onAnim(playLocalAnim);
    Console.onVolume(onVolumeCmd);
    Console.onTrack(onTrackCmd);
    Console.onServo(onServoCmd);
    Console.onAutoAnim(onAutoAnimCmd);
    Console.onSeqSave(onSeqSave);
    Console.onSeqList(onSeqList);
    Console.onSeqLoad(onSeqLoad);
    Console.onSeqRun(onSeqRun);
    Console.onSeqStop(onSeqStop);
    Console.onSeqDelete(onSeqDelete);
    Console.onSeqQuery(onSeqQueryCmd);
    Console.onCalib(onCalibCmd);
    Console.onPreview(onPreviewCmd);
    Console.setMasterServos(gServos);
    Console.setMasterAutoAnim(gAutoAnim);

    Audio.begin();
    Audio.setVolume(Config.volume());
    LOGF("audio %s (vol %u)", Audio.ready() ? "OK" : "OFF", Audio.volume());
#endif

    if (Mesh.begin(GROUP_KEY)) {
        Mesh.onReceive(onMeshMessage);
        // Calibration persistée de CE droïde (bornes par défaut si jamais réglée).
        const ServoCalib c = Config.getCalib(Mesh.myId());
        head.setLimits(c.panMin, c.panCenter, c.panMax, c.tiltMin, c.tiltCenter, c.tiltMax);
        LOGF("mesh prêt, id=%04X (servos %s)", Mesh.myId(), gServos ? "ACTIFS" : "COUPÉS");
    } else {
        LOGF("mesh: échec d'initialisation");
    }
    head.center();
}

void loop() {
    const uint32_t now = millis();

    head.update();
    if (gServos) anim.update();
#if IS_MASTER
    Console.update();
#endif

    // LED de vie.
    if (now - lastBlink >= LED_BLINK_MS) {
        lastBlink = now;
        ledOn = !ledOn;
        digitalWrite(PIN_LED_ONBOARD, ledOn ? HIGH : LOW);
    }

    // Heartbeat : chaque droïde signale sa présence (et l'état de ses servos).
    if (now > nextHeartbeat) {
        nextHeartbeat = now + HEARTBEAT_MS;
        HeartbeatPayload hb{now, (uint8_t)((gServos ? 1 : 0) | (gAutoAnim ? 2 : 0))};
        Mesh.send(MSG_HEARTBEAT, &hb, sizeof(hb));
    }

    // Rapport de voisinage direct (topologie) : chaque droïde diffuse
    // périodiquement les nœuds qu'il entend en direct, avec le RSSI mesuré.
    // Gigue aléatoire pour éviter que tous les droïdes n'émettent en lockstep
    // (l'ESP-NOW broadcast n'a pas d'accusé de réception : des collisions
    // répétées feraient perdre systématiquement ces rapports).
    if (now > nextNeighborReport) {
        nextNeighborReport = now + NEIGHBOR_REPORT_MS + (uint32_t)random(0, 500);
        NeighborReportPayload rep{};
        rep.count = Mesh.copyNeighbors(rep.entries, MAX_NEIGHBORS, NEIGHBOR_STALE_MS);
        Mesh.send(MSG_NEIGHBORS, &rep, sizeof(rep));
#if IS_MASTER
        // Son propre voisinage direct est déjà connu localement, pas besoin
        // d'attendre un aller-retour réseau pour l'intégrer à la topologie.
        const uint32_t now2 = millis();
        for (uint8_t i = 0; i < rep.count; i++)
            MeshTopo.seen(Mesh.myId(), rep.entries[i].id, rep.entries[i].rssi, now2);
#endif
    }

#if IS_MASTER
    // Séquence persistée en cours (prioritaire sur le random maître). Si l'étape en
    // cours cible ce maître, on attend aussi la fin réelle de l'animation locale
    // (gSeqWaitLocal && anim.isPlaying()) en plus du délai, pour ne pas couper un
    // mouvement en cours — impossible à garantir pour les esclaves distants (pas
    // d'accusé de réception mesh), qui restent sur le délai seul.
    if (gSeqPlaying && gServos && now >= gSeqNextAt && !(gSeqWaitLocal && anim.isPlaying())) {
        if (gSeq.stepCount == 0) {
            gSeqPlaying = false;
            Console.pushSeqState(false, gSeqSlot, gSeqIndex, gSeq.stepCount);
        } else {
            if (gSeqIndex >= gSeq.stepCount) {
                if (gSeq.loop) {
                    gSeqIndex = 0;
                } else {
                    gSeqPlaying = false;
                    Console.pushSeqState(false, gSeqSlot, gSeqIndex, gSeq.stepCount);
                }
            }
            if (gSeqPlaying) {
                const SeqStep& st = gSeq.steps[gSeqIndex];
                const uint32_t seed = (uint32_t)esp_random();
                AnimPayload p{st.targetId, st.animId, 0, seed};
                Mesh.send(MSG_ANIM, &p, sizeof(p));
                gSeqWaitLocal = (st.targetId == MESH_TARGET_ALL || st.targetId == Mesh.myId());
                if (gSeqWaitLocal) {
                    anim.play(st.animId, seed);
                    Audio.playForAnim(st.animId, seed);
                }
                gSeqNextAt = now + st.delayMs;
                gSeqIndex++;
                Console.pushSeqState(true, gSeqSlot, gSeqIndex, gSeq.stepCount);
            }
        }
    }

    // Surveillance de présence : signale un B1 qui passe hors-ligne.
    if (now > nextPresenceScan) {
        nextPresenceScan = now + 1000;
        for (uint8_t i = 0; i < Droids.count(); i++) {
            const bool on = Droids.online(i, now, DROID_TIMEOUT_MS);
            if (wasOnline[i] && !on) {
                const String name = Config.getName(Droids.at(i).id);
                LOGF("B1 %04X%s%s hors-ligne", Droids.at(i).id,
                     name.length() ? " " : "", name.c_str());
            }
            wasOnline[i] = on;
        }
    }

    // Envoi périodique de la liste des droïdes à la console web.
    if (now > nextDroidsPush) {
        nextDroidsPush = now + 1500;
        Console.pushDroids();
        Console.pushMeshTopology();
    }

    // Le maître choisit une anim au hasard, la joue et la diffuse au groupe.
    if (!gSeqPlaying && gServos && gAutoAnim && !anim.isPlaying() && now > nextMeshSend) {
        nextMeshSend = now + (uint32_t)random(2500, 5000);
        const uint32_t seed = (uint32_t)esp_random();
        const uint8_t animId = AnimationPlayer::randomAnimId(seed);
        AnimPayload p{MESH_TARGET_ALL, animId, 0, seed};
        Mesh.send(MSG_ANIM, &p, sizeof(p));
        anim.play(animId, seed);
        Audio.playForAnim(animId, seed);
    }
#else
    // Un esclave isolé (sans maître) s'anime aussi tout seul.
    if (gServos && gAutoAnim && !anim.isPlaying() && now > nextMove) {
        nextMove = now + (uint32_t)random(3000, 7000);
        const uint32_t seed = (uint32_t)esp_random();
        anim.play(AnimationPlayer::randomAnimId(seed), seed);
    }
#endif
}
