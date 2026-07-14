using System.Collections.ObjectModel;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using CommunityToolkit.Mvvm.ComponentModel;
using b1_chat_console.Models;

namespace b1_chat_console.Services;

/// <summary>
/// Coeur du protocole JSON serie firmware (cmd/evt) — nouveau code (n'existait que cote JS dans
/// index.html avant ce portage). Parse les evt entrants, construit les cmd sortants via SendCmd
/// (meme garde-fou que sendCmd() en JS : log systematique, refuse si port ferme, refuse tout sauf
/// hello/ping avant la fin du handshake), et tient l'etat central (droides, topologie, caps,
/// catalogue de sequences) dont les ViewModels dependent.
/// </summary>
public partial class ProtocolClient : ObservableObject
{
    private readonly SerialLinkService _link;
    private System.Threading.Timer? _keepalive;
    private readonly HashSet<string> _caps = new();
    private readonly Dictionary<ushort, Droid> _droidsById = new();

    public ObservableCollection<Droid> Droids { get; } = new();
    public ObservableCollection<MeshLink> MeshLinks { get; } = new();
    public ObservableCollection<SequenceSlotMeta> SeqCatalog { get; } = new();
    public Dictionary<int, int> AnimDurationMs { get; } = new();

    [ObservableProperty] private bool _portOpen;
    [ObservableProperty] private bool _sessionReady;
    [ObservableProperty] private string? _fwVersion;
    [ObservableProperty] private int _fwProto;
    [ObservableProperty] private int _lineMax;
    [ObservableProperty] private int _animCount = 18;
    [ObservableProperty] private int _seqSlotMax = 8;
    [ObservableProperty] private int _trackCount = 10;
    [ObservableProperty] private bool _dirty;

    [ObservableProperty] private int _lastVolume;
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
    public event Action<JsonElement>? SeqDataReceived;
    public event Action<JsonElement>? SeqSavedReceived;
    public event Action<int>? SeqDeletedReceived;
    public event Action<JsonElement>? SeqStateReceived;
    public event Action? MeshTopologyChanged;
    public event Action? DroidsChanged;
    public event Action<ushort, int, int, int>? OtaReadyReceived;      // target, sessionId, chunkSize, totalChunks
    public event Action<int, int, int>? OtaChunkAckReceived;           // seq, sent, total
    public event Action<ushort, int>? OtaDoneReceived;                 // target, sessionId
    public event Action<ushort, bool, string?, string?>? OtaResultReceived; // target, ok, fw, reason
    public event Action<ushort?, int, string>? OtaErrorReceived;       // target, sessionId, reason

    // Ecriture serie qui echoue (ex. port bloque/deconnecte en plein envoi) — jusqu'ici
    // SerialLinkService.ErrorOccurred n'etait ecoute nulle part, ce qui rendait un echec
    // d'ecriture totalement silencieux (ex. un chunk OTA qui ne part jamais).
    public event Action<string>? LinkError;

    // Fermeture du port (volontaire ou non) : une session OTA en cours doit etre
    // annulee tout de suite plutot que de retenter des chunks sur un port ferme.
    public event Action<bool>? LinkClosed; // true = deconnexion inattendue

    public ProtocolClient(SerialLinkService link)
    {
        _link = link;
        _link.Opened += OnOpened;
        _link.Closed += OnClosed;
        _link.LineReceived += OnLineReceived;
        _link.ErrorOccurred += err =>
        {
            LogErr?.Invoke("Erreur port série : " + err);
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
        // Deconnexion volontaire (pas une coupure en cours de reconnexion auto) : la console ne
        // peut plus garantir que ces donnees (en ligne/RSSI/liens mesh) sont encore a jour, on les
        // vide plutot que de laisser un etat fige et potentiellement trompeur a l'ecran.
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
            // Le minuteur tourne sur un thread arriere-plan ; SendCmdRaw leve LogTx/LogSys
            // qui alimentent des ObservableCollection liees a l'UI, d'ou le remarshalage.
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

    // --- Envoi (equivalent de sendCmd() en JS) --------------------------------

    public void SendCmd(JsonObject obj) => SendCmdRaw(obj);

    private void SendCmdRaw(JsonObject obj)
    {
        var cmd = obj.TryGetPropertyValue("cmd", out var cNode) ? cNode?.GetValue<string>() : null;
        var line = obj.ToJsonString();
        LogTx?.Invoke(line);
        if (!PortOpen) { LogSys?.Invoke("Non connecté — commande ignorée."); return; }
        var preHandshake = cmd is "hello" or "ping";
        if (!SessionReady && !preHandshake) { LogSys?.Invoke("Handshake en attente — commande différée."); return; }
        _link.Write(line + "\n");
    }

    // Helpers typés pour les commandes les plus utilisées par les ViewModels.
    public void RequestList() => SendCmd(new JsonObject { ["cmd"] = "list" });
    public void RequestConfig() => SendCmd(new JsonObject { ["cmd"] = "getConfig" });
    public void RequestGetAll() => SendCmd(new JsonObject { ["cmd"] = "getAll" });
    public void RequestAnimDurations() => SendCmd(new JsonObject { ["cmd"] = "getAnimDurations" });
    public void RequestMeshTopology() => SendCmd(new JsonObject { ["cmd"] = "getMeshTopology" });
    public void RequestSeqList() => SendCmd(new JsonObject { ["cmd"] = "seqList" });
    public void RequestSeqState() => SendCmd(new JsonObject { ["cmd"] = "seqState" });
    public void RequestCalib(ushort target) => SendCmd(new JsonObject { ["cmd"] = "getCalib", ["target"] = target });

    public void SetName(ushort id, string name) => SendCmd(new JsonObject { ["cmd"] = "name", ["id"] = id, ["name"] = name });
    public void SetServo(ushort target, bool enabled) => SendCmd(new JsonObject { ["cmd"] = "servo", ["target"] = target, ["enabled"] = enabled });
    public void SetAutoAnim(ushort target, bool enabled) => SendCmd(new JsonObject { ["cmd"] = "autoAnim", ["target"] = target, ["enabled"] = enabled });
    public void Adopt(ushort target) => SendCmd(new JsonObject { ["cmd"] = "adopt", ["target"] = target });
    public void Forget(ushort target) => SendCmd(new JsonObject { ["cmd"] = "forget", ["target"] = target });

    public void OtaStart(ushort target, uint size, string md5Hex32) =>
        SendCmd(new JsonObject { ["cmd"] = "otaStart", ["target"] = target, ["size"] = size, ["md5"] = md5Hex32 });
    public void OtaChunk(int seq, string base64Data) => SendCmd(new JsonObject { ["cmd"] = "otaChunk", ["seq"] = seq, ["data"] = base64Data });
    public void OtaAbort() => SendCmd(new JsonObject { ["cmd"] = "otaAbort" });
    public void PlayAnim(ushort target, int animId, uint seed) => SendCmd(new JsonObject { ["cmd"] = "anim", ["target"] = target, ["animId"] = animId, ["seed"] = seed });
    public void Preview(ushort target, int pan, int tilt) => SendCmd(new JsonObject { ["cmd"] = "preview", ["target"] = target, ["pan"] = pan, ["tilt"] = tilt });
    public void SetCalib(ushort target, int panMin, int panCenter, int panMax, int tiltMin, int tiltCenter, int tiltMax) =>
        SendCmd(new JsonObject
        {
            ["cmd"] = "calib", ["target"] = target,
            ["panMin"] = panMin, ["panCenter"] = panCenter, ["panMax"] = panMax,
            ["tiltMin"] = tiltMin, ["tiltCenter"] = tiltCenter, ["tiltMax"] = tiltMax,
        });
    public void SetConfig(ushort target, int freq, int amp, int speed) =>
        SendCmd(new JsonObject { ["cmd"] = "config", ["target"] = target, ["freq"] = freq, ["amp"] = amp, ["speed"] = speed });
    public void SetVolume(int value) => SendCmd(new JsonObject { ["cmd"] = "volume", ["value"] = value });
    public void PlayTrack(int track) => SendCmd(new JsonObject { ["cmd"] = "playTrack", ["track"] = track });
    public void Commit() => SendCmd(new JsonObject { ["cmd"] = "commit" });
    public void Revert() => SendCmd(new JsonObject { ["cmd"] = "revert" });

    public void SeqLoad(int slot) => SendCmd(new JsonObject { ["cmd"] = "seqLoad", ["slot"] = slot });
    public void SeqDelete(int slot) => SendCmd(new JsonObject { ["cmd"] = "seqDelete", ["slot"] = slot });
    public void SeqRun(int slot, int? from = null)
    {
        var o = new JsonObject { ["cmd"] = "seqRun", ["slot"] = slot };
        if (from.HasValue) o["from"] = from.Value;
        SendCmd(o);
    }
    public void SeqStop() => SendCmd(new JsonObject { ["cmd"] = "seqStop" });
    public void SeqPause() => SendCmd(new JsonObject { ["cmd"] = "seqPause" });
    public void SeqResume() => SendCmd(new JsonObject { ["cmd"] = "seqResume" });

    public void SeqSave(int slot, string name, bool loop, int track, IEnumerable<SequenceStep> steps)
    {
        var stepsArr = new JsonArray();
        foreach (var s in steps)
            stepsArr.Add(new JsonObject { ["target"] = s.Target, ["animId"] = s.AnimId, ["delay"] = s.DelayMs });
        SendCmd(new JsonObject
        {
            ["cmd"] = "seqSave", ["slot"] = slot, ["name"] = name, ["loop"] = loop, ["track"] = track, ["steps"] = stepsArr,
        });
    }

    public void SetMulti(JsonArray ops) => SendCmd(new JsonObject { ["cmd"] = "setMulti", ["ops"] = ops });

    // --- Reception (equivalent de handleEvent() en JS) ------------------------

    private void OnLineReceived(string line)
    {
        // Aucune ligne, si malformee soit-elle, ne doit pouvoir tuer la reception :
        // une exception qui s'echappait d'ici a deja tue la boucle de lecture en
        // silence (fw <= 1.3.8, age deborde -> FormatException dans HandleDroids)
        // puis, apres le passage a BeginInvoke, l'application entiere.
        try { HandleLine(line); }
        catch (Exception ex)
        {
            TraceLog.Write("ERR", $"ligne indigeste ({ex.GetType().Name} : {ex.Message}) — {TraceLog.Trunc(line)}");
            LogErr?.Invoke("Ligne série illisible : " + ex.Message);
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
            case "seqList": HandleSeqList(root); break;
            case "seqData": SeqDataReceived?.Invoke(root); break;
            case "seqSaved": SeqSavedReceived?.Invoke(root); break;
            case "seqDeleted":
                if (root.TryGetProperty("slot", out var sd)) SeqDeletedReceived?.Invoke(sd.GetInt32());
                break;
            case "animDurations": HandleAnimDurations(root); break;
            case "seqState": SeqStateReceived?.Invoke(root); break;
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
        SeqSlotMax = root.TryGetProperty("seqSlots", out var ss) ? ss.GetInt32() : SeqSlotMax;
        TrackCount = root.TryGetProperty("trackCount", out var tc) ? tc.GetInt32() : TrackCount;
        Dirty = root.TryGetProperty("dirty", out var d) && d.GetBoolean();

        _caps.Clear();
        if (root.TryGetProperty("caps", out var caps) && caps.ValueKind == JsonValueKind.Array)
            foreach (var c in caps.EnumerateArray())
                if (c.GetString() is { } s) _caps.Add(s);

        if (SessionReady)
        {
            RequestList();
            RequestConfig();
            RequestSeqList();
            RequestAnimDurations();
            RequestSeqState();
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
            // TryGetInt32 (pas GetInt32) : un firmware pré-1.3.10 pouvait émettre un âge
            // débordé (~4e9, voir serial_console.cpp) — GetInt32 levait un FormatException
            // qui tuait la boucle de lecture (fw ≤ 1.3.8) puis l'application entière.
            // Un âge illisible = valeur énorme = droïde considéré hors ligne.
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
        if (root.TryGetProperty("volume", out var v)) LastVolume = v.GetInt32();
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

    private void HandleSeqList(JsonElement root)
    {
        SeqCatalog.Clear();
        if (root.TryGetProperty("list", out var list) && list.ValueKind == JsonValueKind.Array)
            foreach (var item in list.EnumerateArray())
                SeqCatalog.Add(new SequenceSlotMeta(
                    item.GetProperty("slot").GetInt32(),
                    item.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "",
                    item.TryGetProperty("stepCount", out var sc) ? sc.GetInt32() : 0,
                    item.TryGetProperty("loop", out var lp) && lp.GetBoolean(),
                    item.TryGetProperty("track", out var tr) ? tr.GetInt32() : 0));
    }

    private void HandleAnimDurations(JsonElement root)
    {
        AnimDurationMs.Clear();
        if (root.TryGetProperty("list", out var list) && list.ValueKind == JsonValueKind.Array)
            foreach (var item in list.EnumerateArray())
                AnimDurationMs[item.GetProperty("animId").GetInt32()] = item.GetProperty("ms").GetInt32();
    }
}
