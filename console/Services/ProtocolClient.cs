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
public partial class ProtocolClient : ObservableObject
{
    private readonly SerialLinkService _link;
    private System.Threading.Timer? _keepalive;
    private readonly HashSet<string> _caps = new();
    private readonly Dictionary<ushort, Droid> _droidsById = new();

    public ObservableCollection<Droid> Droids { get; } = new();
    public ObservableCollection<MeshLink> MeshLinks { get; } = new();
    public Dictionary<int, int> AnimDurationMs { get; } = new();

    [ObservableProperty] private bool _portOpen;
    [ObservableProperty] private bool _sessionReady;
    [ObservableProperty] private string? _fwVersion;
    [ObservableProperty] private int _fwProto;
    [ObservableProperty] private int _lineMax;
    [ObservableProperty] private int _animCount = 18;
    [ObservableProperty] private int _seqSlotMax = 8;
    [ObservableProperty] private bool _dirty;

    [ObservableProperty] private int _lastFreq;
    [ObservableProperty] private int _lastAmp;
    [ObservableProperty] private int _lastSpeed;

    public bool HasCap(string c) => _caps.Contains(c);

    public event Action<string>? LogTx;
    public event Action<string>? LogRx;
    public event Action<string>? LogSys;
    public event Action<string>? LogErr;
    public event Action? HelloReceived;
    public event Action? AllDoneReceived;
    public event Action<JsonElement>? CalibDataReceived;
    public event Action? AnimDurationsReceived;
    public event Action? MeshTopologyChanged;
    public event Action? DroidsChanged;
    public event Action<ushort, int>? AnimSent; // target, animId — used to drive the mesh topology's broadcast ripple
    public event Action<ushort, string>? PacketSent; // target, kind — every other command with a real mesh frame (see MeshTopologyViewModel's traveling-packet dots)
    public event Action<ushort, int, int, int>? OtaReadyReceived;      // target, sessionId, chunkSize, totalChunks
    public event Action<int, int, int>? OtaChunkAckReceived;           // seq, sent, total
    public event Action<ushort, int>? OtaDoneReceived;                 // target, sessionId
    public event Action<ushort, bool, string?, string?>? OtaResultReceived; // target, ok, fw, reason
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
        // Voluntary disconnect (not a drop mid-auto-reconnect): the console can no longer
        // guarantee this data (online/RSSI/mesh links) is still current, so it's cleared
        // instead of leaving a frozen, potentially misleading state on screen.
        if (!unexpected) ClearLiveState();
        LinkClosed?.Invoke(unexpected);
    }

    private void ClearLiveState()
    {
        Droids.Clear();
        _droidsById.Clear();
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

    private void SendCmdRaw(JsonObject obj)
    {
        var cmd = obj.TryGetPropertyValue("cmd", out var cNode) ? cNode?.GetValue<string>() : null;
        var line = obj.ToJsonString();
        LogTx?.Invoke(line);
        if (!PortOpen) { LogSys?.Invoke("Not connected — command ignored."); return; }
        var preHandshake = cmd is "hello" or "ping";
        if (!SessionReady && !preHandshake) { LogSys?.Invoke("Handshake pending — command deferred."); return; }
        _link.Write(line + "\n");
    }

    // Typed helpers for the commands the ViewModels use most.
    public void RequestList() => SendCmd(new JsonObject { ["cmd"] = "list" });
    public void RequestConfig() => SendCmd(new JsonObject { ["cmd"] = "getConfig" });
    public void RequestGetAll() => SendCmd(new JsonObject { ["cmd"] = "getAll" });
    public void RequestAnimDurations() => SendCmd(new JsonObject { ["cmd"] = "getAnimDurations" });
    public void RequestMeshTopology() => SendCmd(new JsonObject { ["cmd"] = "getMeshTopology" });
    public void RequestCalib(ushort target) => SendCmd(new JsonObject { ["cmd"] = "getCalib", ["target"] = target });

    public void SetName(ushort id, string name) => SendCmd(new JsonObject { ["cmd"] = "name", ["id"] = id, ["name"] = name });
    public void SetServo(ushort target, bool enabled)
    {
        SendCmd(new JsonObject { ["cmd"] = "servo", ["target"] = target, ["enabled"] = enabled });
        PacketSent?.Invoke(target, "servo");
    }
    public void SetAutoAnim(ushort target, bool enabled)
    {
        SendCmd(new JsonObject { ["cmd"] = "autoAnim", ["target"] = target, ["enabled"] = enabled });
        PacketSent?.Invoke(target, "autoAnim");
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
    public void PlayAnim(ushort target, int animId, uint seed)
    {
        SendCmd(new JsonObject { ["cmd"] = "anim", ["target"] = target, ["animId"] = animId, ["seed"] = seed });
        AnimSent?.Invoke(target, animId);
    }
    public void Preview(ushort target, int pan, int tilt)
    {
        SendCmd(new JsonObject { ["cmd"] = "preview", ["target"] = target, ["pan"] = pan, ["tilt"] = tilt });
        PacketSent?.Invoke(target, "preview");
    }
    public void SetCalib(ushort target, int panMin, int panCenter, int panMax, int tiltMin, int tiltCenter, int tiltMax)
    {
        SendCmd(new JsonObject
        {
            ["cmd"] = "calib", ["target"] = target,
            ["panMin"] = panMin, ["panCenter"] = panCenter, ["panMax"] = panMax,
            ["tiltMin"] = tiltMin, ["tiltCenter"] = tiltCenter, ["tiltMax"] = tiltMax,
        });
        PacketSent?.Invoke(target, "calib");
    }
    public void SetConfig(ushort target, int freq, int amp, int speed)
    {
        SendCmd(new JsonObject { ["cmd"] = "config", ["target"] = target, ["freq"] = freq, ["amp"] = amp, ["speed"] = speed });
        PacketSent?.Invoke(target, "config");
    }
    public void Commit() => SendCmd(new JsonObject { ["cmd"] = "commit" });
    public void Revert() => SendCmd(new JsonObject { ["cmd"] = "revert" });

    // (seqLoad/seqSave/seqRun/... helpers removed 2026-07-16 with the rest of the slot
    // machinery — sequences are console-only now, fw 1.7.0 dropped the commands too.)

    public void SetMulti(JsonArray ops) => SendCmd(new JsonObject { ["cmd"] = "setMulti", ["ops"] = ops });

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
            case "config": HandleConfig(root); break;
            case "dirty": Dirty = root.TryGetProperty("dirty", out var dv) && dv.GetBoolean(); break;
            case "calibData": CalibDataReceived?.Invoke(root); break;
            case "allDone": AllDoneReceived?.Invoke(); break;
            case "meshTopology": HandleMeshTopology(root); break;
            case "animDurations": HandleAnimDurations(root); break;
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
        FwProto = root.TryGetProperty("proto", out var proto) ? proto.GetInt32() : 0;
        LineMax = root.TryGetProperty("lineMax", out var lm) ? lm.GetInt32() : 0;
        AnimCount = root.TryGetProperty("anims", out var an) ? an.GetInt32() : AnimCount;
        Dirty = root.TryGetProperty("dirty", out var d) && d.GetBoolean();

        _caps.Clear();
        if (root.TryGetProperty("caps", out var caps) && caps.ValueKind == JsonValueKind.Array)
            foreach (var c in caps.EnumerateArray())
                if (c.GetString() is { } s) _caps.Add(s);

        if (SessionReady)
        {
            RequestList();
            RequestConfig();
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
            if (droid.IsMaster) droid.PortName = _link.PortName;
            if (item.TryGetProperty("servos", out var sv)) droid.ServosOn = sv.GetBoolean();
            if (item.TryGetProperty("autoAnim", out var aa)) droid.AutoAnimOn = aa.GetBoolean();
            if (item.TryGetProperty("adopted", out var ad)) droid.Adopted = ad.GetBoolean();
            if (item.TryGetProperty("fw", out var fw)) droid.FwVersion = fw.GetString() ?? "";
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

    private void HandleConfig(JsonElement root)
    {
        if (root.TryGetProperty("freq", out var f)) LastFreq = f.GetInt32();
        if (root.TryGetProperty("amp", out var a)) LastAmp = a.GetInt32();
        if (root.TryGetProperty("speed", out var s)) LastSpeed = s.GetInt32();
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
        if (root.TryGetProperty("list", out var list) && list.ValueKind == JsonValueKind.Array)
            foreach (var item in list.EnumerateArray())
                AnimDurationMs[item.GetProperty("animId").GetInt32()] = item.GetProperty("ms").GetInt32();
        AnimDurationsReceived?.Invoke();
    }
}
