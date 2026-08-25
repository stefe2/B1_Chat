using System.Collections.ObjectModel;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using CommunityToolkit.Mvvm.ComponentModel;
using b1_chat_console.Models;

namespace b1_chat_console.Services;

/// <summary>
/// Core of the firmware's JSON serial protocol (cmd/evt) — new code (only existed as JS in
/// index.html before this port). Parses incoming evt messages, builds outgoing cmd messages via
/// SendCmd (same guard rails as sendCmd() in JS: always logs, refuses if the port is closed,
/// refuses everything except hello/ping before the handshake completes), and holds the central
/// state (droids, topology, caps, sequence catalog) the ViewModels depend on.
/// </summary>
public partial class ProtocolClient : ObservableObject, ISequencerProtocol
{
    private readonly SerialLinkService _link;
    private System.Threading.Timer? _keepalive;
    private readonly HashSet<string> _caps = new();
    private readonly Dictionary<ushort, Droid> _droidsById = new();
    private ushort? _masterId;
    private int _nextAnimRequestId;
    private bool _gestureCatalogCompatible;
    private const string RequiredGestureCatalogId = "b1.core";
    private const string RequiredGestureCatalogRevision = "v2";
    private const string RequiredGestureCatalogHash = "sha256:e659b6a7a868732a53b9038912431f25b0e99f3fef6c9d240d35ed4afec25b7e";

    public ObservableCollection<Droid> Droids { get; } = new();
    public ObservableCollection<MeshLink> MeshLinks { get; } = new();
    public Dictionary<int, int> AnimDurationMs { get; } = new();
    public Dictionary<int, AnimationDurationMetadata> AnimDurationMetadata { get; } = new();
    IReadOnlyDictionary<int, int> ISequencerProtocol.AnimDurationMs => AnimDurationMs;
    IReadOnlyDictionary<int, AnimationDurationMetadata> ISequencerProtocol.AnimDurationMetadata => AnimDurationMetadata;

    [ObservableProperty] private bool _portOpen;
    [ObservableProperty] private bool _sessionReady;
    [ObservableProperty] private string? _fwVersion;
    [ObservableProperty] private string? _fwBuildId;
    [ObservableProperty] private int _fwProto;
    [ObservableProperty] private int _lineMax;
    [ObservableProperty] private bool _dirty;

    public bool HasCap(string c) => _caps.Contains(c);
    public bool SupportsAnimLease => HasCap("gestureLease");
    public bool SupportsGestureStop => HasCap("gestureStop");
    public bool SupportsSafeStop => HasCap("safeStop");

    public event Action<string>? LogTx;
    public event Action<string>? LogRx;
    public event Action<string>? LogSys;
    public event Action<string>? LogErr;
    public event Action? HelloReceived;
    public event Action<JsonElement>? CalibDataReceived;
    public event Action? AnimDurationsReceived;
    public event Action? MeshTopologyChanged;
    public event Action? DroidsChanged;
    public event Action<ushort, int>? AnimSent; // target, animId — used to drive the mesh topology's broadcast ripple
    public event Action<AnimMasterReceipt>? AnimMasterAccepted;
    public event Action<AnimExecutionReport>? AnimExecutionReceived;
    public event Action<ushort, string>? PacketSent; // target, kind — every other command with a real mesh frame (see MeshTopologyViewModel's traveling-packet dots)
    public event Action<ushort, int, int, int>? OtaReadyReceived;      // target, sessionId, chunkSize, totalChunks
    public event Action<int, int, int>? OtaChunkAckReceived;           // seq, sent, total
    public event Action<ushort, int>? OtaDoneReceived;                 // target, sessionId
    public event Action<ushort, bool, string?, string?, string?>? OtaResultReceived; // target, ok, fw, build, reason
    public event Action<ushort?, int, string>? OtaErrorReceived;       // target, sessionId, reason

    // A serial write that fails (e.g. port blocked/disconnected mid-send) — until now
    // SerialLinkService.ErrorOccurred wasn't listened to anywhere, which made a write
    // failure totally silent (e.g. an OTA chunk that never goes out).
    public event Action<string>? LinkError;

    // Port closing (voluntary or not): an in-flight OTA session must be cancelled right
    // away instead of retrying chunks on a closed port.
    public event Action<bool>? LinkClosed; // true = unexpected disconnect

    public ProtocolClient(SerialLinkService link)
    {
        _link = link;
        _link.Opened += OnOpened;
        _link.Closed += OnClosed;
        _link.LineReceived += OnLineReceived;
        _link.ErrorOccurred += err =>
        {
            LogErr?.Invoke("Serial port error: " + err);
            LinkError?.Invoke(err);
        };
    }

    private void OnOpened()
    {
        PortOpen = true;
        SessionReady = false;
        StartKeepalive();
        SendCmdRaw(new JsonObject { ["cmd"] = "hello" });
    }

    private void OnClosed(bool unexpected)
    {
        PortOpen = false;
        SessionReady = false;
        StopKeepalive();
        // Cleared on ANY disconnect, expected or not: an unplugged master must refresh the
        // UI exactly like clicking Disconnect (droid list/mesh links no longer guaranteed
        // current) rather than leaving stale, misleadingly "live"-looking data on screen
        // while a background auto-reconnect is in flight.
        ClearLiveState();
        LinkClosed?.Invoke(unexpected);
    }

    private void ClearLiveState()
    {
        Droids.Clear();
        _droidsById.Clear();
        _masterId = null;
        FwVersion = null;
        FwBuildId = null;
        MeshLinks.Clear();
        DroidsChanged?.Invoke();
        MeshTopologyChanged?.Invoke();
    }

    private void StartKeepalive()
    {
        StopKeepalive();
        _keepalive = new System.Threading.Timer(_ =>
        {
            // The timer runs on a background thread; SendCmdRaw raises LogTx/LogSys which
            // feed UI-bound ObservableCollections, hence the remarshaling.
            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            void Send() => SendCmdRaw(new JsonObject { ["cmd"] = SessionReady ? "ping" : "hello" });
            if (dispatcher == null || dispatcher.CheckAccess()) Send();
            else dispatcher.Invoke(Send);
        }, null, TimeSpan.FromMilliseconds(1500), TimeSpan.FromMilliseconds(1500));
    }

    private void StopKeepalive()
    {
        _keepalive?.Dispose();
        _keepalive = null;
    }

    // --- Sending (equivalent to sendCmd() in JS) --------------------------------

    public void SendCmd(JsonObject obj) => SendCmdRaw(obj);

    private AnimDispatchState SendCmdRaw(JsonObject obj)
    {
        var cmd = obj.TryGetPropertyValue("cmd", out var cNode) ? cNode?.GetValue<string>() : null;
        var line = obj.ToJsonString();
        LogTx?.Invoke(line);
        if (!PortOpen)
        {
            LogSys?.Invoke("Not connected — command ignored.");
            return AnimDispatchState.NotConnected;
        }
        var preHandshake = cmd is "hello" or "ping";
        if (!SessionReady && !preHandshake)
        {
            LogSys?.Invoke("Handshake pending — command ignored.");
            return AnimDispatchState.HandshakePending;
        }
        return _link.Write(line + "\n")
            ? AnimDispatchState.Written
            : AnimDispatchState.WriteFailed;
    }

    // Typed helpers for the commands the ViewModels use most.
    public void RequestList() => SendCmd(new JsonObject { ["cmd"] = "list" });
    public void RequestGetAll() => SendCmd(new JsonObject { ["cmd"] = "getAll" });
    public void RequestAnimDurations() => SendCmd(new JsonObject { ["cmd"] = "getGestureCatalog" });
    public void RequestMeshTopology() => SendCmd(new JsonObject { ["cmd"] = "getMeshTopology" });
    public void RequestCalib(ushort target) => SendCmd(new JsonObject { ["cmd"] = "getCalib", ["target"] = target });

    public void SetName(ushort id, string name)
    {
        SendCmd(new JsonObject { ["cmd"] = "name", ["id"] = id, ["name"] = name });
        ScheduleAutoCommit();
    }
    public void SetServo(ushort target, bool enabled)
    {
        SendCmd(new JsonObject { ["cmd"] = "servo", ["target"] = target, ["enabled"] = enabled });
        PacketSent?.Invoke(target, "servo");
    }
    public void SetLocate(ushort target, bool enabled)
    {
        SendCmd(new JsonObject { ["cmd"] = "locate", ["target"] = target, ["enabled"] = enabled });
        PacketSent?.Invoke(target, "locate");
    }
    public void Adopt(ushort target) => SendCmd(new JsonObject { ["cmd"] = "adopt", ["target"] = target });
    public void Forget(ushort target) => SendCmd(new JsonObject { ["cmd"] = "forget", ["target"] = target });

    public void OtaStart(ushort target, uint size, string md5Hex32) =>
        SendCmd(new JsonObject { ["cmd"] = "otaStart", ["target"] = target, ["size"] = size, ["md5"] = md5Hex32 });
    public void OtaChunk(int seq, string base64Data) => SendCmd(new JsonObject { ["cmd"] = "otaChunk", ["seq"] = seq, ["data"] = base64Data });
    public void OtaAbort() => SendCmd(new JsonObject { ["cmd"] = "otaAbort" });
    public AnimDispatchResult PlayAnim(ushort target, int animId, uint seed, ushort leaseMs = 0)
    {
        if (_nextAnimRequestId == int.MaxValue) _nextAnimRequestId = 0;
        var requestId = (uint)++_nextAnimRequestId;
        if (!_gestureCatalogCompatible || !GestureKeyFor(animId, out var key))
        {
            LogErr?.Invoke("Gesture playback refused: the connected firmware does not expose the required V2 gesture catalog.");
            return new AnimDispatchResult(requestId, AnimDispatchState.CatalogMismatch);
        }
        var command = new JsonObject
        {
            ["cmd"] = "gesture", ["target"] = target, ["key"] = key,
            ["seed"] = seed, ["requestId"] = requestId,
        };
        if (leaseMs > 0) command["leaseMs"] = leaseMs;
        var state = SendCmdRaw(command);
        if (state == AnimDispatchState.Written) AnimSent?.Invoke(target, animId);
        return new AnimDispatchResult(requestId, state);
    }

    private static bool GestureKeyFor(int gestureId, out string key)
    {
        var ordered = GestureSceneV2Persistence.Catalog.Ordered;
        if (gestureId >= 0 && gestureId < ordered.Count)
        {
            key = ordered[gestureId].Key;
            return true;
        }
        key = string.Empty;
        return false;
    }
    public AnimDispatchState RenewAnimLease(ushort target, int meshSeq, ushort leaseMs) =>
        SendCmdRaw(new JsonObject
        {
            ["cmd"] = "animLease", ["target"] = target,
            ["meshSeq"] = meshSeq, ["leaseMs"] = leaseMs,
        });
    public AnimDispatchState StopGesture(ushort target, int animId)
    {
        if (!_gestureCatalogCompatible || !GestureKeyFor(animId, out var key))
            return AnimDispatchState.CatalogMismatch;
        return SendCmdRaw(new JsonObject { ["cmd"] = "stopGesture", ["target"] = target, ["key"] = key });
    }
    public AnimDispatchState SafeStop(ushort target)
    {
        var state = SendCmdRaw(new JsonObject { ["cmd"] = "safeStop", ["target"] = target });
        if (state == AnimDispatchState.Written) PacketSent?.Invoke(target, "safeStop");
        return state;
    }
    public void Preview(ushort target, int pan, int tilt)
    {
        SendCmd(new JsonObject { ["cmd"] = "preview", ["target"] = target, ["pan"] = pan, ["tilt"] = tilt });
        PacketSent?.Invoke(target, "preview");
    }
    public void SetCalib(ushort target, int panMin, int panCenter, int panMax,
                         int tiltMin, int tiltCenter, int tiltMax,
                         bool panReversed, bool tiltReversed)
    {
        SendCmd(new JsonObject
        {
            ["cmd"] = "calib", ["target"] = target,
            ["panMin"] = panMin, ["panCenter"] = panCenter, ["panMax"] = panMax,
            ["tiltMin"] = tiltMin, ["tiltCenter"] = tiltCenter, ["tiltMax"] = tiltMax,
            ["panReversed"] = panReversed, ["tiltReversed"] = tiltReversed,
        });
        PacketSent?.Invoke(target, "calib");
    }
    public void Commit() => SendCmd(new JsonObject { ["cmd"] = "commit" });

    // Also catches a draft that was ALREADY dirty when this console connected (e.g. an
    // edit left uncommitted by an earlier session) — SetName alone would never
    // notice that case, since neither was called this session, and the badge would stay
    // lit forever with no way left to clear it (the manual Save button is gone).
    partial void OnDirtyChanged(bool value) { if (value) ScheduleAutoCommit(); }

    // Debounced auto-commit: SetName is the only setter that leaves the
    // master's draft "dirty" (see the header's unsaved badge, ShowCommitUi). Re-armed on
    // every such call so it fires once, 2s after the LAST one — not on every single
    // keystroke/slider tick, to avoid hammering the master's NVS.
    private System.Threading.Timer? _autoCommitTimer;
    private void ScheduleAutoCommit()
    {
        _autoCommitTimer ??= new System.Threading.Timer(_ =>
        {
            // Runs on the timer's own thread; Commit()/SendCmdRaw raise LogTx which feeds
            // UI-bound ObservableCollections, hence the remarshaling (same as StartKeepalive).
            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            void Fire() { if (Dirty && HasCap("commit")) Commit(); }
            if (dispatcher == null || dispatcher.CheckAccess()) Fire();
            else dispatcher.Invoke(Fire);
        }, null, System.Threading.Timeout.Infinite, System.Threading.Timeout.Infinite);
        _autoCommitTimer.Change(TimeSpan.FromSeconds(2), System.Threading.Timeout.InfiniteTimeSpan);
    }

    // (seqLoad/seqSave/seqRun/... helpers removed 2026-07-16 with the rest of the slot
    // machinery — sequences are console-only now, fw 1.7.0 dropped the commands too.)

    // --- Receiving (equivalent to handleEvent() in JS) ------------------------

    private void OnLineReceived(string line)
    {
        // No line, however malformed, should be able to kill reception: an exception
        // escaping from here already killed the read loop silently once (fw <= 1.3.8,
        // overflowed age -> FormatException in HandleDroids), then the whole app after
        // it was routed through BeginInvoke.
        try { HandleLine(line); }
        catch (Exception ex)
        {
            TraceLog.Write("ERR", $"unparseable line ({ex.GetType().Name}: {ex.Message}) — {TraceLog.Trunc(line)}");
            LogErr?.Invoke("Unreadable serial line: " + ex.Message);
        }
    }

    private void HandleLine(string line)
    {
        LogRx?.Invoke(line);
        JsonElement root;
        try { root = JsonDocument.Parse(line).RootElement; }
        catch { return; }

        var evt = root.TryGetProperty("evt", out var e) ? e.GetString() : null;
        switch (evt)
        {
            case "hello": HandleHello(root); break;
            case "droids": HandleDroids(root); break;
            case "log": LogRx?.Invoke(root.TryGetProperty("msg", out var m) ? m.GetString() ?? "" : ""); break;
            case "err": LogErr?.Invoke(root.TryGetProperty("msg", out var em) ? em.GetString() ?? "" : ""); break;
            case "dirty": Dirty = root.TryGetProperty("dirty", out var dv) && dv.GetBoolean(); break;
            case "calibData": CalibDataReceived?.Invoke(root); break;
            case "meshTopology": HandleMeshTopology(root); break;
            case "gestureCatalog": HandleAnimDurations(root); break;
            case "animAccepted":
                AnimMasterAccepted?.Invoke(new AnimMasterReceipt(
                    root.TryGetProperty("requestId", out var amaRequest)
                        && amaRequest.TryGetUInt32(out var acceptedRequestId) ? acceptedRequestId : 0,
                    (ushort)(root.TryGetProperty("target", out var amaTarget) ? amaTarget.GetInt32() : 0),
                    root.TryGetProperty("gestureId", out var amaAnim) ? amaAnim.GetInt32() : -1,
                    root.TryGetProperty("meshSeq", out var amaSeq) ? amaSeq.GetInt32() : 0,
                    root.TryGetProperty("meshQueued", out var amaMesh) && amaMesh.GetBoolean(),
                    root.TryGetProperty("local", out var amaLocal) && amaLocal.GetBoolean(),
                    root.TryGetProperty("leaseMs", out var amaLease) ? amaLease.GetInt32() : 0));
                break;
            case "animExec":
                AnimExecutionReceived?.Invoke(new AnimExecutionReport(
                    root.TryGetProperty("requestId", out var aer) && aer.TryGetUInt32(out var requestId) ? requestId : 0,
                    (ushort)(root.TryGetProperty("droid", out var aed) ? aed.GetInt32() : 0),
                    root.TryGetProperty("gestureId", out var aea) ? aea.GetInt32() : -1,
                    root.TryGetProperty("phase", out var aep) ? aep.GetString() ?? "unknown" : "unknown",
                    root.TryGetProperty("reason", out var aerr) ? aerr.GetString() : null,
                    root.TryGetProperty("atMs", out var aet) && aet.TryGetUInt32(out var atMs) ? atMs : 0,
                    root.TryGetProperty("meshSeq", out var aes) ? aes.GetInt32() : 0));
                break;
            case "otaReady":
                OtaReadyReceived?.Invoke(
                    (ushort)(root.TryGetProperty("target", out var ort) ? ort.GetInt32() : 0),
                    root.TryGetProperty("sessionId", out var ors) ? ors.GetInt32() : 0,
                    root.TryGetProperty("chunkSize", out var orc) ? orc.GetInt32() : 0,
                    root.TryGetProperty("totalChunks", out var ortc) ? ortc.GetInt32() : 0);
                break;
            case "otaChunkAck":
                OtaChunkAckReceived?.Invoke(
                    root.TryGetProperty("seq", out var ocs) ? ocs.GetInt32() : 0,
                    root.TryGetProperty("sent", out var ocse) ? ocse.GetInt32() : 0,
                    root.TryGetProperty("total", out var oct) ? oct.GetInt32() : 0);
                break;
            case "otaDone":
                OtaDoneReceived?.Invoke(
                    (ushort)(root.TryGetProperty("target", out var odt) ? odt.GetInt32() : 0),
                    root.TryGetProperty("sessionId", out var ods) ? ods.GetInt32() : 0);
                break;
            case "otaResult":
                OtaResultReceived?.Invoke(
                    (ushort)(root.TryGetProperty("target", out var ort2) ? ort2.GetInt32() : 0),
                    root.TryGetProperty("ok", out var ook) && ook.GetBoolean(),
                    root.TryGetProperty("fw", out var ofw) ? ofw.GetString() : null,
                    root.TryGetProperty("build", out var obuild) ? obuild.GetString() : null,
                    root.TryGetProperty("reason", out var orsn) ? orsn.GetString() : null);
                break;
            case "otaError":
                OtaErrorReceived?.Invoke(
                    root.TryGetProperty("target", out var oet) && oet.GetInt32() != 0 ? (ushort?)oet.GetInt32() : null,
                    root.TryGetProperty("sessionId", out var oes) ? oes.GetInt32() : 0,
                    root.TryGetProperty("reason", out var oer) ? oer.GetString() ?? "" : "");
                break;
        }
    }

    private void HandleHello(JsonElement root)
    {
        SessionReady = root.TryGetProperty("ok", out var ok) && ok.GetBoolean();
        FwVersion = root.TryGetProperty("fw", out var fw) ? fw.GetString() : null;
        FwBuildId = root.TryGetProperty("build", out var build) ? build.GetString() : null;
        FwProto = root.TryGetProperty("proto", out var proto) ? proto.GetInt32() : 0;
        LineMax = root.TryGetProperty("lineMax", out var lm) ? lm.GetInt32() : 0;
        Dirty = root.TryGetProperty("dirty", out var d) && d.GetBoolean();
        _gestureCatalogCompatible = root.TryGetProperty("catalogId", out var catalogId)
            && catalogId.GetString() == RequiredGestureCatalogId
            && root.TryGetProperty("catalogRevision", out var catalogRevision)
            && catalogRevision.GetString() == RequiredGestureCatalogRevision
            && root.TryGetProperty("catalogHash", out var catalogHash)
            && catalogHash.GetString() == RequiredGestureCatalogHash;

        _caps.Clear();
        if (root.TryGetProperty("caps", out var caps) && caps.ValueKind == JsonValueKind.Array)
            foreach (var c in caps.EnumerateArray())
                if (c.GetString() is { } s) _caps.Add(s);

        if (SessionReady)
        {
            RequestList();
            RequestAnimDurations();
            RequestMeshTopology();
        }
        HelloReceived?.Invoke();
    }

    private void HandleDroids(JsonElement root)
    {
        if (!root.TryGetProperty("list", out var list) || list.ValueKind != JsonValueKind.Array) return;
        var seen = new HashSet<ushort>();
        foreach (var item in list.EnumerateArray())
        {
            var id = (ushort)item.GetProperty("id").GetInt32();
            seen.Add(id);
            var isNew = !_droidsById.TryGetValue(id, out var existing);
            var droid = existing ?? new Droid { Id = id };
            if (isNew)
            {
                _droidsById[id] = droid;
                Droids.Add(droid);
            }
            if (item.TryGetProperty("name", out var n))
            {
                droid.Name = n.GetString() ?? "";
                if (isNew) droid.EditingName = droid.Name;
            }
            if (item.TryGetProperty("rssi", out var r) && r.TryGetInt32(out var rssi)) droid.Rssi = rssi;
            if (item.TryGetProperty("role", out var role)) droid.IsMaster = role.GetString() == "master";
            if (droid.IsMaster) _masterId = id;
            if (droid.IsMaster) droid.PortName = _link.PortName;
            if (item.TryGetProperty("servos", out var sv)) droid.ServosOn = sv.GetBoolean();
            if (item.TryGetProperty("locate", out var lo)) droid.LocateOn = lo.GetBoolean();
            droid.SupportsServoReverse = item.TryGetProperty("servoReverse", out var sr) && sr.GetBoolean();
            if (item.TryGetProperty("adopted", out var ad)) droid.Adopted = ad.GetBoolean();
            if (item.TryGetProperty("fw", out var fw)) droid.FwVersion = fw.GetString() ?? "";
            droid.BuildId = item.TryGetProperty("build", out var build) ? build.GetString() ?? "" : "";
            // TryGetInt32 (not GetInt32): a pre-1.3.10 firmware could emit an overflowed
            // age (~4e9, see serial_console.cpp) — GetInt32 threw a FormatException that
            // killed the read loop (fw <= 1.3.8) and then the whole app.
            // An unreadable age = huge value = droid considered offline.
            var age = item.TryGetProperty("age", out var a) && a.TryGetInt32(out var ageMs) ? ageMs : int.MaxValue;
            droid.Online = droid.IsMaster || age <= 4000;
            droid.LastSeen = DateTime.UtcNow;
        }

        foreach (var staleId in _droidsById.Keys.Where(id => !seen.Contains(id)).ToList())
        {
            Droids.Remove(_droidsById[staleId]);
            _droidsById.Remove(staleId);
        }

        DroidsChanged?.Invoke();
    }

    private void HandleMeshTopology(JsonElement root)
    {
        MeshLinks.Clear();
        if (root.TryGetProperty("links", out var links) && links.ValueKind == JsonValueKind.Array)
            foreach (var l in links.EnumerateArray())
                MeshLinks.Add(new MeshLink(
                    (ushort)l.GetProperty("from").GetInt32(),
                    (ushort)l.GetProperty("to").GetInt32(),
                    l.GetProperty("rssi").GetInt32()));
        MeshTopologyChanged?.Invoke();
    }

    private void HandleAnimDurations(JsonElement root)
    {
        AnimDurationMs.Clear();
        AnimDurationMetadata.Clear();
        if (root.TryGetProperty("list", out var list) && list.ValueKind == JsonValueKind.Array)
            foreach (var item in list.EnumerateArray())
            {
                var animId = item.GetProperty("gestureId").GetInt32();
                var legacyMs = item.TryGetProperty("nominalMs", out var legacyDuration)
                    ? legacyDuration.GetInt32() : 0;
                AnimDurationMs[animId] = legacyMs;
                var hasStructuredMetadata = item.TryGetProperty("kind", out var kindElement);
                var kind = hasStructuredMetadata
                    ? kindElement.GetString() switch
                    {
                        "immediate" => AnimationDurationKind.Immediate,
                        "continuous" => AnimationDurationKind.Infinite,
                        _ => AnimationDurationKind.Finite,
                    }
                    : GestureSceneV2Persistence.ExecutionKindFor(animId) switch
                    {
                        GestureExecutionKind.Immediate => AnimationDurationKind.Immediate,
                        GestureExecutionKind.Continuous => AnimationDurationKind.Infinite,
                        GestureExecutionKind.Finite => AnimationDurationKind.Finite,
                        _ => animId == 0 ? AnimationDurationKind.Immediate : AnimationDurationKind.Finite,
                    };
                var nominalMs = item.TryGetProperty("nominalMs", out var nominal)
                    ? Math.Max(0, nominal.GetInt32())
                    : kind == AnimationDurationKind.Finite ? Math.Max(0, legacyMs) : 0;
                var frameCount = item.TryGetProperty("frameCount", out var frames)
                    ? Math.Max(0, frames.GetInt32())
                    : 0;
                var settleMs = item.TryGetProperty("settleMs", out var settle)
                    ? Math.Max(0, settle.GetInt32())
                    : animId == 0 ? 600 : 0;
                AnimDurationMetadata[animId] = new AnimationDurationMetadata(
                    animId, kind, nominalMs, frameCount, settleMs,
                    Provisional: !_gestureCatalogCompatible || !hasStructuredMetadata);
            }
        AnimDurationsReceived?.Invoke();
    }
}
