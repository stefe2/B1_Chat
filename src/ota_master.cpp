#include "ota_master.h"

#if IS_MASTER
#include "registry.h"

OtaMaster OtaM;

void OtaMaster::sendFrame(uint8_t type, const void* payload, uint8_t len) {
    memcpy(_lastSentBuf, payload, len);
    _lastSentLen = len;
    _lastSentType = type;
    Mesh.send(type, payload, len, OTA_MESH_TTL);
    _lastSendMs = millis();
}

void OtaMaster::resend() {
    Mesh.send(_lastSentType, _lastSentBuf, _lastSentLen, OTA_MESH_TTL);
    _lastSendMs = millis();
}

bool OtaMaster::begin(uint16_t target, uint32_t size, const char* md5Hex32) {
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

    OtaStartPayload p{};
    p.targetId = target;
    p.sessionId = _sessionId;
    p.totalSize = size;
    p.totalChunks = _totalChunks;
    p.chunkSize = OTA_CHUNK_DATA_MAX;
    memcpy(p.md5Hex, md5Hex32, 32);

    sendFrame(MSG_OTA_START, &p, sizeof(p));
    _state = OM_AWAIT_START_ACK;
    return true;
}

void OtaMaster::onSerialChunk(uint16_t index, const uint8_t* data, uint8_t len) {
    if (_state != OM_AWAIT_SERIAL_CHUNK || index != _nextChunkIndex) return;

    OtaChunkPayload p{};
    p.targetId = _target;
    p.sessionId = _sessionId;
    p.chunkIndex = index;
    p.dataLen = len;
    memcpy(p.data, data, len);

    sendFrame(MSG_OTA_CHUNK, &p, sizeof(p));
    _state = OM_AWAIT_CHUNK_ACK;
    _retryCount = 0;
}

void OtaMaster::abort() {
    if (_state == OM_IDLE) return;
    OtaAbortPayload ab{_target, _sessionId, OTA_ABORT_USER};
    Mesh.send(MSG_OTA_ABORT, &ab, sizeof(ab), OTA_MESH_TTL);
    _pending = Event{};
    _pending.type = EV_ERROR;
    _pending.target = _target;
    _pending.sessionId = _sessionId;
    snprintf(_pending.reason, sizeof(_pending.reason), "annule");
    _state = OM_IDLE;
}

void OtaMaster::fail(const char* reason) {
    if (_state != OM_IDLE) {
        OtaAbortPayload ab{_target, _sessionId, OTA_ABORT_TIMEOUT};
        Mesh.send(MSG_OTA_ABORT, &ab, sizeof(ab), OTA_MESH_TTL);
    }
    _pending = Event{};
    _pending.type = EV_ERROR;
    _pending.target = _target;
    _pending.sessionId = _sessionId;
    snprintf(_pending.reason, sizeof(_pending.reason), "%s", reason);
    _state = OM_IDLE;
}

void OtaMaster::onAck(uint16_t srcId, const OtaAckPayload& p) {
    if (srcId != _target || p.sessionId != _sessionId) return; // session étrangère/périmée

    if (p.status != OTA_OK) {
        fail(p.kind == 0 ? "start" : p.kind == 1 ? "chunk" : "end");
        return;
    }

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
            OtaEndPayload e{_target, _sessionId, _totalChunks};
            sendFrame(MSG_OTA_END, &e, sizeof(e));
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

void OtaMaster::update(uint32_t nowMs) {
    switch (_state) {
    case OM_AWAIT_START_ACK:
    case OM_AWAIT_CHUNK_ACK:
    case OM_AWAIT_END_ACK:
        if (nowMs - _lastSendMs > OTA_ACK_TIMEOUT_MS) {
            if (++_retryCount > OTA_MAX_RETRIES) fail("timeout");
            else resend();
        }
        break;

    case OM_AWAIT_SERIAL_CHUNK:
        if (nowMs - _serialWaitSince > OTA_SERIAL_IDLE_TIMEOUT_MS) fail("console injoignable");
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
                _pending = Event{};
                _pending.type = EV_RESULT;
                _pending.target = _target;
                _pending.ok = changed;
                snprintf(_pending.fw, sizeof(_pending.fw), "%u.%u.%u", e.fwMajor, e.fwMinor, e.fwPatch);
                if (!changed) snprintf(_pending.reason, sizeof(_pending.reason), "rolledBack");
                _state = OM_IDLE;
            }
            break;
        }
        (void)found;
        if (_state == OM_AWAIT_REBOOT && nowMs - _rebootStartMs > OTA_REBOOT_WAIT_MS) {
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

OtaMaster::Event OtaMaster::pollEvent() {
    Event e = _pending;
    _pending = Event{};
    return e;
}
#endif
