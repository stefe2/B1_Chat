#include "ota_master.h"

#if IS_MASTER
#include "registry.h"

OtaMaster OtaM;

namespace {
// RAII : verrouille l'etat partage d'OtaMaster. Ne JAMAIS appeler Mesh.send()/
// esp_now_send() a l'interieur d'une section ainsi verrouillee (l'API ESP-NOW ne doit
// pas etre invoquee avec les interruptions desactivees) — chaque methode ci-dessous
// mute l'etat sous verrou puis envoie hors verrou, une fois celui-ci relache.
struct CriticalGuard {
    portMUX_TYPE& mux;
    explicit CriticalGuard(portMUX_TYPE& m) : mux(m) { portENTER_CRITICAL(&mux); }
    ~CriticalGuard() { portEXIT_CRITICAL(&mux); }
};
}  // namespace

void OtaMaster::sendFrame(uint8_t type, const void* payload, uint8_t len) {
    {
        CriticalGuard guard(_mux);
        memcpy(_lastSentBuf, payload, len);
        _lastSentLen = len;
        _lastSentType = type;
    }
    Mesh.send(type, payload, len, OTA_MESH_TTL);
    CriticalGuard guard(_mux);
    _lastSendMs = millis();
}

void OtaMaster::resend() {
    uint8_t type, len, buf[sizeof(OtaChunkPayload)];
    {
        CriticalGuard guard(_mux);
        type = _lastSentType;
        len = _lastSentLen;
        memcpy(buf, _lastSentBuf, len);
    }
    Mesh.send(type, buf, len, OTA_MESH_TTL);
    CriticalGuard guard(_mux);
    _lastSendMs = millis();
}

bool OtaMaster::begin(uint16_t target, uint32_t size, const char* md5Hex32) {
    OtaStartPayload p{};
    {
        CriticalGuard guard(_mux);
        if (busy()) return false;
        if (size == 0 || size > OTA_MAX_IMAGE_SIZE) return false;

        bool known = false;
        for (uint8_t i = 0; i < Droids.count(); i++) {
            const Registry::Entry& e = Droids.at(i);
            if (e.id == target) {
                known = true;
                _prevFwMajor = e.fwMajor; _prevFwMinor = e.fwMinor; _prevFwPatch = e.fwPatch;
                break;
            }
        }
        if (!known) return false;

        _target = target;
        _sessionId++;   // u8, le wrap est sans conséquence (une seule session à la fois)
        _totalSize = size;
        _totalChunks = (uint16_t)((size + OTA_CHUNK_DATA_MAX - 1) / OTA_CHUNK_DATA_MAX);
        _retryCount = 0;
        _state = OM_AWAIT_START_ACK;

        p.targetId = target;
        p.sessionId = _sessionId;
        p.totalSize = size;
        p.totalChunks = _totalChunks;
        p.chunkSize = OTA_CHUNK_DATA_MAX;
        memcpy(p.md5Hex, md5Hex32, 32);
    }
    sendFrame(MSG_OTA_START, &p, sizeof(p));
    return true;
}

void OtaMaster::onSerialChunk(uint16_t index, const uint8_t* data, uint8_t len) {
    OtaChunkPayload p{};
    bool send = false;
    {
        CriticalGuard guard(_mux);
        if (_state == OM_AWAIT_SERIAL_CHUNK && index == _nextChunkIndex) {
            p.targetId = _target;
            p.sessionId = _sessionId;
            p.chunkIndex = index;
            p.dataLen = len;
            memcpy(p.data, data, len);

            _state = OM_AWAIT_CHUNK_ACK;
            _retryCount = 0;
            send = true;
        } else if (_state == OM_AWAIT_SERIAL_CHUNK && _nextChunkIndex > 0
                   && index == _nextChunkIndex - 1) {
            // La console retente le chunk précédent : notre evt:otaChunkAck s'est
            // perdu sur le lien série. Le chunk est déjà écrit chez l'esclave —
            // on ré-émet seulement l'ack (rien à renvoyer sur le mesh), la
            // console reprendra au bon index.
            _pending = Event{};
            _pending.type = EV_CHUNK_ACK;
            _pending.chunkIndex = index;
            _pending.sent = _nextChunkIndex;
            _pending.total = _totalChunks;
        }
        // Tout autre cas (session inactive, index inattendu, chunk courant déjà
        // en vol sur le mesh) : ignoré, les mécanismes de retry existants suffisent.
    }
    if (send) sendFrame(MSG_OTA_CHUNK, &p, sizeof(p));
}

void OtaMaster::abort() {
    uint16_t target; uint8_t sessionId;
    {
        CriticalGuard guard(_mux);
        if (_state == OM_IDLE) return;
        target = _target;
        sessionId = _sessionId;
        _pending = Event{};
        _pending.type = EV_ERROR;
        _pending.target = _target;
        _pending.sessionId = _sessionId;
        snprintf(_pending.reason, sizeof(_pending.reason), "annule");
        _state = OM_IDLE;
    }
    OtaAbortPayload ab{target, sessionId, OTA_ABORT_USER};
    Mesh.send(MSG_OTA_ABORT, &ab, sizeof(ab), OTA_MESH_TTL);
}

// Appelee uniquement depuis l'interieur d'une section deja verrouillee par l'appelant
// (onAck()/update()) : ne fait que muter l'etat. Si un MSG_OTA_ABORT doit partir sur le
// mesh, l'appelant le voit via _pendingAbortNeeded et l'envoie lui-meme apres avoir
// relache le verrou.
void OtaMaster::fail(const char* reason) {
    if (_state != OM_IDLE) {
        _pendingAbortNeeded = true;
        _pendingAbortTarget = _target;
        _pendingAbortSession = _sessionId;
    }
    _pending = Event{};
    _pending.type = EV_ERROR;
    _pending.target = _target;
    _pending.sessionId = _sessionId;
    snprintf(_pending.reason, sizeof(_pending.reason), "%s", reason);
    _state = OM_IDLE;
}

void OtaMaster::onAck(uint16_t srcId, const OtaAckPayload& p) {
    bool needAbort = false; uint16_t abortTarget = 0; uint8_t abortSession = 0;
    bool needSendEnd = false; OtaEndPayload endPayload{};

    {
        CriticalGuard guard(_mux);
        if (srcId != _target || p.sessionId != _sessionId) return; // session étrangère/périmée

        if (p.status != OTA_OK) {
            fail(p.kind == 0 ? "start" : p.kind == 1 ? "chunk" : "end");
            needAbort = _pendingAbortNeeded;
            if (needAbort) { abortTarget = _pendingAbortTarget; abortSession = _pendingAbortSession; _pendingAbortNeeded = false; }
        } else {
            switch (p.kind) {
            case 0: // START
                if (_state != OM_AWAIT_START_ACK) return;
                _nextChunkIndex = 0;
                _state = OM_AWAIT_SERIAL_CHUNK;
                _serialWaitSince = millis();
                _pending = Event{};
                _pending.type = EV_READY;
                _pending.target = _target;
                _pending.sessionId = _sessionId;
                _pending.chunkSize = OTA_CHUNK_DATA_MAX;
                _pending.total = _totalChunks;
                break;

            case 1: // CHUNK
                if (_state != OM_AWAIT_CHUNK_ACK || p.chunkIndex != _nextChunkIndex) return;
                _nextChunkIndex++;
                if (_nextChunkIndex >= _totalChunks) {
                    needSendEnd = true;
                    endPayload = OtaEndPayload{_target, _sessionId, _totalChunks};
                    _state = OM_AWAIT_END_ACK;
                    _retryCount = 0;
                } else {
                    _state = OM_AWAIT_SERIAL_CHUNK;
                    _serialWaitSince = millis();
                    _pending = Event{};
                    _pending.type = EV_CHUNK_ACK;
                    _pending.chunkIndex = p.chunkIndex;
                    _pending.sent = _nextChunkIndex;
                    _pending.total = _totalChunks;
                }
                break;

            case 2: // END
                if (_state != OM_AWAIT_END_ACK) return;
                _lastSeenAtRebootStart = 0;
                for (uint8_t i = 0; i < Droids.count(); i++) {
                    if (Droids.at(i).id == _target) { _lastSeenAtRebootStart = Droids.at(i).lastSeen; break; }
                }
                _rebootStartMs = millis();
                _state = OM_AWAIT_REBOOT;
                _pending = Event{};
                _pending.type = EV_DONE;
                _pending.target = _target;
                _pending.sessionId = _sessionId;
                break;
            }
        }
    }

    if (needAbort) {
        OtaAbortPayload ab{abortTarget, abortSession, OTA_ABORT_TIMEOUT};
        Mesh.send(MSG_OTA_ABORT, &ab, sizeof(ab), OTA_MESH_TTL);
    }
    if (needSendEnd) sendFrame(MSG_OTA_END, &endPayload, sizeof(endPayload));
}

void OtaMaster::update(uint32_t nowMs) {
    bool needAbort = false; uint16_t abortTarget = 0; uint8_t abortSession = 0;
    bool needResend = false;

    {
        CriticalGuard guard(_mux);
        // Comparaisons SIGNEES obligatoires : _lastSendMs/_serialWaitSince/
        // _rebootStartMs sont horodates avec un millis() frais depuis onAck()
        // (tache Wi-Fi) ou sendFrame() (appele par Console.update() PLUS TOT
        // dans la meme iteration de loop()) — ils peuvent donc etre POSTERIEURS
        // au nowMs capture en debut de loop(). En non signe, la difference
        // negative deborde et declenche retransmissions et faux "timeout"
        // instantanes (meme bug que cote esclave, voir ota_slave.cpp).
        switch (_state) {
        case OM_AWAIT_START_ACK:
        case OM_AWAIT_CHUNK_ACK:
        case OM_AWAIT_END_ACK:
            if ((int32_t)(nowMs - _lastSendMs) > (int32_t)OTA_ACK_TIMEOUT_MS) {
                if (++_retryCount > OTA_MAX_RETRIES) {
                    fail("timeout");
                    needAbort = _pendingAbortNeeded;
                    if (needAbort) { abortTarget = _pendingAbortTarget; abortSession = _pendingAbortSession; _pendingAbortNeeded = false; }
                } else {
                    needResend = true;
                }
            }
            break;

        case OM_AWAIT_SERIAL_CHUNK:
            if ((int32_t)(nowMs - _serialWaitSince) > (int32_t)OTA_SERIAL_IDLE_TIMEOUT_MS) {
                fail("console injoignable");
                needAbort = _pendingAbortNeeded;
                if (needAbort) { abortTarget = _pendingAbortTarget; abortSession = _pendingAbortSession; _pendingAbortNeeded = false; }
            }
            break;

        case OM_AWAIT_REBOOT: {
            bool found = false;
            for (uint8_t i = 0; i < Droids.count(); i++) {
                const Registry::Entry& e = Droids.at(i);
                if (e.id != _target) continue;
                found = true;
                if (e.lastSeen != _lastSeenAtRebootStart) {
                    // Succès = la version a changé par rapport à avant l'OTA (on ne peut
                    // pas comparer à une version "annoncée" fiable, voir ota_master.h).
                    const bool changed = (e.fwMajor != _prevFwMajor || e.fwMinor != _prevFwMinor || e.fwPatch != _prevFwPatch);
                    // Version inchangée DANS la fenêtre de grâce : signe de vie émis
                    // AVANT le reboot réel (l'esclave ne redémarre que ~250 ms après
                    // son ack de END, un heartbeat de l'ancienne image peut être en
                    // vol) — attendre, ne surtout pas conclure "rolledBack". Un vrai
                    // rollback (boots ratés + bascule de partition) prend >= 10-30 s.
                    if (changed || (int32_t)(nowMs - _rebootStartMs) > (int32_t)OTA_REBOOT_GRACE_MS) {
                        _pending = Event{};
                        _pending.type = EV_RESULT;
                        _pending.target = _target;
                        _pending.ok = changed;
                        snprintf(_pending.fw, sizeof(_pending.fw), "%u.%u.%u", e.fwMajor, e.fwMinor, e.fwPatch);
                        if (!changed) snprintf(_pending.reason, sizeof(_pending.reason), "rolledBack");
                        _state = OM_IDLE;
                    }
                }
                break;
            }
            (void)found;
            if (_state == OM_AWAIT_REBOOT && (int32_t)(nowMs - _rebootStartMs) > (int32_t)OTA_REBOOT_WAIT_MS) {
                _pending = Event{};
                _pending.type = EV_RESULT;
                _pending.target = _target;
                _pending.ok = false;
                snprintf(_pending.reason, sizeof(_pending.reason), "injoignable");
                _state = OM_IDLE;
            }
            break;
        }

        default:
            break;
        }
    }

    if (needAbort) {
        OtaAbortPayload ab{abortTarget, abortSession, OTA_ABORT_TIMEOUT};
        Mesh.send(MSG_OTA_ABORT, &ab, sizeof(ab), OTA_MESH_TTL);
    }
    if (needResend) resend();
}

OtaMaster::Event OtaMaster::pollEvent() {
    CriticalGuard guard(_mux);
    Event e = _pending;
    _pending = Event{};
    return e;
}
#endif
