#include <Arduino.h>
#include "config.h"
#include "servo_engine.h"
#include "animation.h"
#include "mesh_comm.h"
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

// LED de vie (témoin d'exécution) — clignotement non bloquant.
static uint32_t lastBlink = 0;
static bool ledOn = false;

// Test mesh / timers
static uint32_t nextMeshSend = 0;
static uint32_t nextHeartbeat = 0;
static uint32_t nextPresenceScan = 0;
static uint32_t nextDroidsPush = 0;

// Lecteur de séquence stockée (maître) : exécution autonome sans PC.
#if IS_MASTER
static StoredSequence gSeq;
static bool gSeqPlaying = false;
static uint8_t gSeqIndex = 0;
static uint32_t gSeqNextAt = 0;
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
        return;
    }
    gSeqPlaying = true;
    gSeqIndex = 0;
    gSeqNextAt = millis();
}

static void onSeqStop() {
    gSeqPlaying = false;
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
    } else if (type == MSG_HEARTBEAT && len == sizeof(HeartbeatPayload)) {
#if IS_MASTER
        HeartbeatPayload hb;
        memcpy(&hb, payload, sizeof(hb));
        Droids.setServos(srcId, hb.state & 0x01);
#endif
    } else if (type == MSG_HEARTBEAT) {
        // ancienne forme / présence : déjà notée.
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
    head.center();
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
    Console.onSeqSave(onSeqSave);
    Console.onSeqList(onSeqList);
    Console.onSeqLoad(onSeqLoad);
    Console.onSeqRun(onSeqRun);
    Console.onSeqStop(onSeqStop);
    Console.onSeqDelete(onSeqDelete);
    Console.setMasterServos(gServos);

    Audio.begin();
    Audio.setVolume(Config.volume());
    LOGF("audio %s (vol %u)", Audio.ready() ? "OK" : "OFF", Audio.volume());
#endif

    if (Mesh.begin(GROUP_KEY)) {
        Mesh.onReceive(onMeshMessage);
        LOGF("mesh prêt, id=%04X (servos %s)", Mesh.myId(), gServos ? "ACTIFS" : "COUPÉS");
    } else {
        LOGF("mesh: échec d'initialisation");
    }
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
        HeartbeatPayload hb{now, (uint8_t)(gServos ? 1 : 0)};
        Mesh.send(MSG_HEARTBEAT, &hb, sizeof(hb));
    }

#if IS_MASTER
    // Séquence persistée en cours (prioritaire sur le random maître).
    if (gSeqPlaying && gServos && now >= gSeqNextAt) {
        if (gSeq.stepCount == 0) {
            gSeqPlaying = false;
        } else {
            if (gSeqIndex >= gSeq.stepCount) {
                if (gSeq.loop) gSeqIndex = 0;
                else gSeqPlaying = false;
            }
            if (gSeqPlaying) {
                const SeqStep& st = gSeq.steps[gSeqIndex];
                const uint32_t seed = (uint32_t)esp_random();
                AnimPayload p{st.targetId, st.animId, 0, seed};
                Mesh.send(MSG_ANIM, &p, sizeof(p));
                if (st.targetId == MESH_TARGET_ALL || st.targetId == Mesh.myId()) {
                    anim.play(st.animId, seed);
                    Audio.playForAnim(st.animId, seed);
                }
                gSeqNextAt = now + st.delayMs;
                gSeqIndex++;
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
    }

    // Le maître choisit une anim au hasard, la joue et la diffuse au groupe.
    if (!gSeqPlaying && gServos && !anim.isPlaying() && now > nextMeshSend) {
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
    if (gServos && !anim.isPlaying() && now > nextMove) {
        nextMove = now + (uint32_t)random(3000, 7000);
        const uint32_t seed = (uint32_t)esp_random();
        anim.play(AnimationPlayer::randomAnimId(seed), seed);
    }
#endif
}
