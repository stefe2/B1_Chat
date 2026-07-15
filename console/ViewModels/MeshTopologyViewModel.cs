using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using b1_chat_console.Models;
using b1_chat_console.Services;

namespace b1_chat_console.ViewModels;

/// <summary>
/// Fixed-bearing polar layout: the master is pinned at the disc center and each slave sits at a
/// fixed, evenly-spaced angle around it (3 slaves = 120° apart, assigned by ascending id, first
/// at 12 o'clock). Only the radius moves, driven by RSSI — strong signal pulls a node in, weak
/// signal pushes it toward the rim — and radius changes are eased by a ~30 fps exponential lerp
/// so RSSI jitter glides instead of jumping. A second ticker animates colored dots along the
/// actual graph edges for every mesh frame the console can observe (outgoing commands, OTA
/// chunks, inbound telemetry refreshes). The console never sees raw inter-slave relay traffic
/// (only what the master reports over serial), so this is a faithful visualization of what's
/// observable, not a literal packet capture.
/// </summary>
public partial class MeshTopologyViewModel : ObservableObject
{
    private readonly ProtocolClient _protocol;

    public ObservableCollection<MeshNodeVisual> Nodes { get; } = new();
    public ObservableCollection<MeshEdgeVisual> Edges { get; } = new();
    public ObservableCollection<MeshPacketVisual> Packets { get; } = new();

    [ObservableProperty] private int _linkCount;
    [ObservableProperty] private bool _isEmpty = true;
    [ObservableProperty] private string _statsText = "";

    // Master's current canvas position (now always the canvas center, kept as a bound property
    // since MeshTopologyCardView.xaml anchors the hop-wave ring and the TALK-sync pulse here).
    [ObservableProperty] private double _masterX;
    [ObservableProperty] private double _masterY;

    // OTA travel indicator: a persistent dashed line from master to whichever droid currently has
    // an OTA session in flight (only one is ever active mesh-wide), overlaid with real per-chunk
    // traveling dots (see OnOtaChunkAck) for the actual packet cadence.
    [ObservableProperty] private bool _otaTravelActive;
    [ObservableProperty] private double _otaX1;
    [ObservableProperty] private double _otaY1;
    [ObservableProperty] private double _otaX2;
    [ObservableProperty] private double _otaY2;

    // Fired once per outgoing anim broadcast (any target) so the view can play a
    // one-shot expanding-ring "hop wave" from the master's position. TALK (animId 17)
    // additionally triggers a rhythmic pulse for the animation's known duration.
    public event Action? HopWaveRequested;
    public event Action<int>? TalkPulseRequested;

    public const double CanvasSize = 260;
    private const double Center = CanvasSize / 2;
    // The canvas now renders as a circular radar disc (MeshTopologyCardView.xaml), so positions
    // are clamped radially rather than per-axis — otherwise a node near a square corner would
    // render outside the visible green disc.
    private const double MaxNodeRadius = 122;

    // Polar layout tuning: a direct-link slave's radius interpolates MinNodeRadius (strongest
    // signal) .. MaxNodeRadius (weakest); a multi-hop slave sits at its relay parent's radius
    // plus one RSSI-scaled hop segment. RadiusLerpRate is the exponential approach speed of the
    // smoothing ticker (higher = snappier).
    private const double MinNodeRadius = 42;
    private const double HopBaseLen = 30;
    private const double HopExtraLen = 40;
    private const double RadiusLerpRate = 3.0; // s^-1

    // Radius smoothing state: fixed bearing per slave (reshuffled only when the slave set
    // changes), plus current/target radius pairs eased toward each other by _layoutTimer.
    private readonly Dictionary<ushort, double> _angle = new();
    private readonly Dictionary<ushort, double> _radiusCurrent = new();
    private readonly Dictionary<ushort, double> _radiusTarget = new();
    private List<ushort> _angleOrder = new();
    private Dictionary<ushort, MeshNodeVisual> _nodeById = new();
    private readonly DispatcherTimer _layoutTimer;
    private DateTime _lastLayoutTick;

    // Packet ticker: constant travel speed (not a fixed per-hop duration) so a weak, elongated
    // link visibly takes the dot longer to cross — thematically fitting since the layout itself
    // already stretches weak links out.
    private const double PacketSpeedPxPerSec = 260;
    private const double MinHopSeconds = 0.12;
    private readonly DispatcherTimer _packetTimer;
    private DateTime _lastPacketTick;

    // Last-seen (Rssi, Online) per droid: a changed value between two renders is our best available
    // proxy for "this droid's telemetry just genuinely refreshed" (evt:droids is a periodic full
    // snapshot, not a per-heartbeat push) — also used to trigger the inbound heartbeat packet.
    private readonly Dictionary<ushort, (int Rssi, bool Online)> _lastTelemetry = new();

    // Canvas positions from the most recent render, keyed by droid id — reused by the OTA travel
    // indicator and the packet ticker to build paths without waiting for a re-render.
    private readonly Dictionary<ushort, Point> _positions = new();

    // BFS parent (over direct mesh links) from the most recent render, used to reconstruct the
    // master->node path a packet dot travels along.
    private readonly Dictionary<ushort, ushort> _parent = new();
    private ushort _masterId;
    private bool _hasMaster;

    private ushort? _otaTarget;

    public MeshTopologyViewModel(ProtocolClient protocol)
    {
        _protocol = protocol;
        _protocol.MeshTopologyChanged += Render;
        _protocol.DroidsChanged += Render;
        _protocol.AnimSent += OnAnimSent;
        _protocol.PacketSent += OnPacketSent;
        _protocol.OtaReadyReceived += (target, _, _, _) => { _otaTarget = target; StartOtaTravel(target); };
        _protocol.OtaChunkAckReceived += (_, _, _) => OnOtaChunkAck();
        _protocol.OtaDoneReceived += (_, _) => { OtaTravelActive = false; _otaTarget = null; };
        _protocol.OtaResultReceived += (_, _, _, _) => { OtaTravelActive = false; _otaTarget = null; };
        _protocol.OtaErrorReceived += (_, _, _) => { OtaTravelActive = false; _otaTarget = null; };

        _packetTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(30) };
        _packetTimer.Tick += PacketTick;

        _layoutTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(30) };
        _layoutTimer.Tick += LayoutTick;
    }

    private static double RssiToStrength(int rssi) => Math.Clamp((rssi + 90) / 60.0, 0, 1);

    private void OnAnimSent(ushort target, int animId)
    {
        HopWaveRequested?.Invoke();
        SpawnPacketsTo(target, "anim");
        if (animId != 17) return; // TALK
        var ms = _protocol.AnimDurationMs.TryGetValue(17, out var d) ? d : 2000;
        TalkPulseRequested?.Invoke(ms);
    }

    private void OnPacketSent(ushort target, string kind) => SpawnPacketsTo(target, kind);

    private void OnOtaChunkAck()
    {
        if (_otaTarget is { } target) SpawnPacket(target, "ota");
    }

    private void StartOtaTravel(ushort targetId)
    {
        var master = _protocol.Droids.FirstOrDefault(d => d.IsMaster);
        if (master == null || !_positions.TryGetValue(master.Id, out var mp) || !_positions.TryGetValue(targetId, out var tp)) return;
        OtaX1 = mp.X; OtaY1 = mp.Y; OtaX2 = tp.X; OtaY2 = tp.Y;
        OtaTravelActive = true;
    }

    // ---- Packet ticker --------------------------------------------------------------------

    private void SpawnPacketsTo(ushort targetId, string kind)
    {
        if (targetId == 0xFFFF)
        {
            foreach (var id in _positions.Keys)
                if (id != _masterId) SpawnPacket(id, kind);
            return;
        }
        SpawnPacket(targetId, kind);
    }

    private void SpawnPacket(ushort targetId, string kind)
    {
        var path = PathBetween(_masterId, targetId);
        if (path.Length < 2) return;
        AddPacket(path, kind);
    }

    // Inbound telemetry refresh (heartbeat / neighbor report) is never observed per-packet by the
    // console — this animates a dot from the droid back to master as a readable stand-in.
    private void SpawnInboundPacket(ushort sourceId, string kind)
    {
        var path = PathBetween(_masterId, sourceId);
        if (path.Length < 2) return;
        Array.Reverse(path);
        AddPacket(path, kind);
    }

    private void AddPacket(Point[] path, string kind)
    {
        var packet = new MeshPacketVisual { Kind = kind, Tooltip = KindTooltip(kind), Path = path };
        packet.Left = path[0].X - MeshPacketVisual.Radius;
        packet.Top = path[0].Y - MeshPacketVisual.Radius;
        Packets.Add(packet);
        if (!_packetTimer.IsEnabled)
        {
            _lastPacketTick = DateTime.UtcNow;
            _packetTimer.Start();
        }
    }

    private Point[] PathBetween(ushort masterId, ushort targetId)
    {
        if (!_hasMaster || !_positions.ContainsKey(targetId)) return Array.Empty<Point>();
        var chain = new List<ushort> { targetId };
        var cur = targetId;
        var guard = 0;
        while (cur != masterId && _parent.TryGetValue(cur, out var p) && guard++ < 32)
        {
            cur = p;
            chain.Add(cur);
        }
        if (chain[^1] != masterId) return Array.Empty<Point>(); // no known path back to master
        chain.Reverse();
        return chain.Select(id => _positions[id]).ToArray();
    }

    private static string KindTooltip(string kind) => kind switch
    {
        "anim" => "MSG_ANIM",
        "servo" => "MSG_SERVO",
        "autoAnim" => "MSG_AUTOANIM",
        "config" => "MSG_CONFIG",
        "calib" => "MSG_CALIB",
        "preview" => "MSG_PREVIEW",
        "ota" => "MSG_OTA_CHUNK",
        "heartbeat" => "heartbeat / neighbor report (inferred, not individually observable)",
        _ => kind,
    };

    private void PacketTick(object? sender, EventArgs e)
    {
        var now = DateTime.UtcNow;
        var dt = (now - _lastPacketTick).TotalSeconds;
        _lastPacketTick = now;
        if (dt <= 0 || dt > 0.25) dt = 1.0 / 30; // guard the first tick and large gaps (e.g. window was minimized)

        for (var i = Packets.Count - 1; i >= 0; i--)
        {
            var pk = Packets[i];
            var a = pk.Path[pk.SegmentIndex];
            var b = pk.Path[pk.SegmentIndex + 1];
            var segLen = (b - a).Length;
            var segDuration = Math.Max(segLen / PacketSpeedPxPerSec, MinHopSeconds);
            pk.SegmentProgress += dt / segDuration;

            while (pk.SegmentProgress >= 1)
            {
                pk.SegmentProgress -= 1;
                pk.SegmentIndex++;
                if (pk.SegmentIndex >= pk.Path.Length - 1)
                {
                    Packets.RemoveAt(i);
                    goto next;
                }
                a = pk.Path[pk.SegmentIndex];
                b = pk.Path[pk.SegmentIndex + 1];
            }

            pk.Left = a.X + (b.X - a.X) * pk.SegmentProgress - MeshPacketVisual.Radius;
            pk.Top = a.Y + (b.Y - a.Y) * pk.SegmentProgress - MeshPacketVisual.Radius;
            next: ;
        }

        if (Packets.Count == 0) _packetTimer.Stop();
    }

    // ---- Layout + render -------------------------------------------------------------------

    private void Render()
    {
        Nodes.Clear();
        Edges.Clear();

        var ordered = _protocol.Droids
            .OrderByDescending(d => d.IsMaster)
            .ThenBy(d => d.Id)
            .ToList();
        IsEmpty = ordered.Count == 0;
        if (ordered.Count == 0)
        {
            LinkCount = 0;
            StatsText = "";
            _positions.Clear();
            _parent.Clear();
            _hasMaster = false;
            Packets.Clear();
            _packetTimer.Stop();
            _angle.Clear();
            _angleOrder = new List<ushort>();
            _radiusCurrent.Clear();
            _radiusTarget.Clear();
            _nodeById = new Dictionary<ushort, MeshNodeVisual>();
            _layoutTimer.Stop();
            return;
        }

        var masterId = ordered[0].Id;
        _masterId = masterId;
        _hasMaster = ordered[0].IsMaster;

        // Merge directed edges into undirected pairs, key = min-max.
        var merged = new Dictionary<(ushort, ushort), (int? AB, int? BA)>();
        foreach (var link in _protocol.MeshLinks)
        {
            var a = Math.Min(link.From, link.To);
            var b = Math.Max(link.From, link.To);
            var key = (a, b);
            merged.TryGetValue(key, out var cur);
            if (link.From < link.To) cur.AB = link.Rssi; else cur.BA = link.Rssi;
            merged[key] = cur;
        }

        var adjacency = new Dictionary<ushort, List<ushort>>();
        void AddAdjacency(ushort a, ushort b)
        {
            if (!adjacency.TryGetValue(a, out var list)) adjacency[a] = list = new List<ushort>();
            list.Add(b);
        }
        foreach (var (a, b) in merged.Keys) { AddAdjacency(a, b); AddAdjacency(b, a); }

        // BFS hop distance + parent from master over the direct-link graph — the parent chain is
        // how a packet's multi-hop path is reconstructed (PathBetween above).
        var hopDistance = new Dictionary<ushort, int> { [masterId] = 0 };
        _parent.Clear();
        var queue = new Queue<ushort>();
        queue.Enqueue(masterId);
        while (queue.Count > 0)
        {
            var cur = queue.Dequeue();
            if (!adjacency.TryGetValue(cur, out var neighbors)) continue;
            foreach (var n in neighbors)
                if (!hopDistance.ContainsKey(n)) { hopDistance[n] = hopDistance[cur] + 1; _parent[n] = cur; queue.Enqueue(n); }
        }

        var pos = ComputePolarLayout(ordered, masterId, merged, hopDistance);
        _positions.Clear();
        foreach (var kv in pos) _positions[kv.Key] = kv.Value;
        MasterX = pos[masterId].X;
        MasterY = pos[masterId].Y;

        var bestStrengthByNode = new Dictionary<ushort, double>();
        var worstRssiSum = 0;
        var worstRssiCount = 0;
        (ushort A, ushort B, int Rssi)? weakest = null;

        foreach (var ((a, b), (ab, ba)) in merged)
        {
            if (!pos.TryGetValue(a, out var pa) || !pos.TryGetValue(b, out var pb)) continue;
            var worst = new[] { ab, ba }.Where(v => v.HasValue).Select(v => v!.Value).DefaultIfEmpty(-90).Min();
            var strength = RssiToStrength(worst);
            var tooltip = ab.HasValue && ba.HasValue
                ? $"{ab} dBm / {ba} dBm"
                : $"{(ab ?? ba)} dBm";
            Edges.Add(new MeshEdgeVisual(a, b, pa, pb, 1 + 3 * strength, 0.25 + 0.7 * strength, strength, tooltip));

            if (!bestStrengthByNode.TryGetValue(a, out var sa) || strength > sa) bestStrengthByNode[a] = strength;
            if (!bestStrengthByNode.TryGetValue(b, out var sb) || strength > sb) bestStrengthByNode[b] = strength;

            worstRssiSum += worst;
            worstRssiCount++;
            if (weakest == null || worst < weakest.Value.Rssi) weakest = (a, b, worst);
        }
        LinkCount = Edges.Count;

        var byId = ordered.ToDictionary(d => d.Id);
        for (var i = 0; i < ordered.Count; i++)
        {
            var d = ordered[i];
            var p = pos[d.Id];
            bestStrengthByNode.TryGetValue(d.Id, out var best);

            var telemetry = (d.Rssi, d.Online);
            var pulse = !d.IsMaster && (!_lastTelemetry.TryGetValue(d.Id, out var prev) || prev != telemetry);
            _lastTelemetry[d.Id] = telemetry;
            if (pulse) SpawnInboundPacket(d.Id, "heartbeat");

            Nodes.Add(new MeshNodeVisual(d.Id, p.X, p.Y, string.IsNullOrEmpty(d.Name) ? d.IdHex : d.Name, d.IsMaster)
            {
                BestLinkStrength = best,
                Pulse = pulse,
            });
        }
        _nodeById = Nodes.ToDictionary(n => n.Id);

        StatsText = BuildStatsText(worstRssiCount, worstRssiSum, weakest, hopDistance, ordered.Count, byId);

        // Kick the radius-smoothing ticker if any node still has ground to cover.
        if (!_layoutTimer.IsEnabled &&
            _radiusTarget.Any(kv => Math.Abs(kv.Value - _radiusCurrent.GetValueOrDefault(kv.Key, kv.Value)) > 0.3))
        {
            _lastLayoutTick = DateTime.UtcNow;
            _layoutTimer.Start();
        }
    }

    // Master pinned at canvas center; each slave has a fixed, evenly-spaced bearing (assigned by
    // ascending id, first at 12 o'clock, reshuffled only when the slave set changes) and only its
    // radius moves with RSSI. Positions are computed from the SMOOTHED radius (_radiusCurrent);
    // the freshly-computed targets are eased toward by LayoutTick between renders.
    private Dictionary<ushort, Point> ComputePolarLayout(
        List<Droid> ordered, ushort masterId,
        Dictionary<(ushort A, ushort B), (int? AB, int? BA)> merged,
        Dictionary<ushort, int> hopDistance)
    {
        var slaves = ordered.Where(d => d.Id != masterId).Select(d => d.Id).OrderBy(id => id).ToList();
        if (!slaves.SequenceEqual(_angleOrder))
        {
            _angleOrder = slaves;
            _angle.Clear();
            for (var i = 0; i < slaves.Count; i++)
                _angle[slaves[i]] = -Math.PI / 2 + i * 2 * Math.PI / slaves.Count;
        }

        double WorstRssi(ushort a, ushort b)
        {
            var key = a < b ? (a, b) : (b, a);
            return merged.TryGetValue(key, out var v)
                ? new[] { v.AB, v.BA }.Where(x => x.HasValue).Select(x => x!.Value).DefaultIfEmpty(-90).Min()
                : -90;
        }

        // Radius targets, walked in hop order so a multi-hop child finds its relay parent's
        // target already computed. Direct link: RSSI interpolates MinNodeRadius..MaxNodeRadius.
        // Multi-hop: parent radius + one RSSI-scaled hop segment. No path at all: parked at rim.
        _radiusTarget.Clear();
        foreach (var id in slaves.OrderBy(id => hopDistance.TryGetValue(id, out var h) ? h : int.MaxValue))
        {
            double target;
            if (merged.ContainsKey(masterId < id ? (masterId, id) : (id, masterId)))
            {
                var strength = RssiToStrength((int)WorstRssi(masterId, id));
                target = MinNodeRadius + (1 - strength) * (MaxNodeRadius - MinNodeRadius);
            }
            else if (_parent.TryGetValue(id, out var parent) && _radiusTarget.TryGetValue(parent, out var parentR))
            {
                var strength = RssiToStrength((int)WorstRssi(parent, id));
                target = Math.Min(parentR + HopBaseLen + (1 - strength) * HopExtraLen, MaxNodeRadius);
            }
            else
            {
                target = MaxNodeRadius;
            }
            _radiusTarget[id] = target;
            if (!_radiusCurrent.ContainsKey(id)) _radiusCurrent[id] = target; // new node: no fly-in
        }
        foreach (var stale in _radiusCurrent.Keys.Where(k => !_radiusTarget.ContainsKey(k)).ToList())
            _radiusCurrent.Remove(stale);

        var pos = new Dictionary<ushort, Point> { [masterId] = new(Center, Center) };
        foreach (var id in slaves)
            pos[id] = PolarPoint(_angle[id], _radiusCurrent[id]);
        return pos;
    }

    private static Point PolarPoint(double angle, double radius) =>
        new(Center + radius * Math.Cos(angle), Center + radius * Math.Sin(angle));

    // ~30 fps exponential lerp of each node's radius toward its target, dragging the node
    // visual, its incident edges, the cached position (used for packet paths), and the OTA
    // travel line along — so RSSI changes glide instead of jumping.
    private void LayoutTick(object? sender, EventArgs e)
    {
        var now = DateTime.UtcNow;
        var dt = (now - _lastLayoutTick).TotalSeconds;
        _lastLayoutTick = now;
        if (dt <= 0 || dt > 0.25) dt = 1.0 / 30;
        var alpha = 1 - Math.Exp(-RadiusLerpRate * dt);

        var anyMoving = false;
        foreach (var (id, target) in _radiusTarget)
        {
            if (!_radiusCurrent.TryGetValue(id, out var cur) || Math.Abs(target - cur) < 0.05) continue;
            cur += (target - cur) * alpha;
            if (Math.Abs(target - cur) < 0.05) cur = target; else anyMoving = true;
            _radiusCurrent[id] = cur;

            if (!_angle.TryGetValue(id, out var angle)) continue;
            var pt = PolarPoint(angle, cur);
            _positions[id] = pt;
            if (_nodeById.TryGetValue(id, out var node)) { node.X = pt.X; node.Y = pt.Y; }
            foreach (var edge in Edges)
            {
                if (edge.A == id) { edge.X1 = pt.X; edge.Y1 = pt.Y; }
                else if (edge.B == id) { edge.X2 = pt.X; edge.Y2 = pt.Y; }
            }
            if (OtaTravelActive && _otaTarget == id) { OtaX2 = pt.X; OtaY2 = pt.Y; }
        }

        if (!anyMoving) _layoutTimer.Stop();
    }

    private static string BuildStatsText(
        int linkCount, int rssiSum, (ushort A, ushort B, int Rssi)? weakest,
        Dictionary<ushort, int> hopDistance, int droidCount, Dictionary<ushort, Droid> byId)
    {
        if (linkCount == 0) return "";

        var avg = rssiSum / (double)linkCount;
        var parts = new List<string> { $"avg {avg:F0} dBm" };

        if (weakest.HasValue)
        {
            var (a, b, rssi) = weakest.Value;
            var nameA = byId.TryGetValue(a, out var da) ? (string.IsNullOrEmpty(da.Name) ? da.IdHex : da.Name) : a.ToString("X4");
            var nameB = byId.TryGetValue(b, out var db) ? (string.IsNullOrEmpty(db.Name) ? db.IdHex : db.Name) : b.ToString("X4");
            parts.Add($"weakest {nameA}↔{nameB} ({rssi} dBm)");
        }

        var maxHop = hopDistance.Count > 0 ? hopDistance.Values.Max() : 0;
        var unreachable = droidCount - hopDistance.Count;
        parts.Add(unreachable > 0 ? $"farthest {maxHop} hop(s), {unreachable} unreachable" : $"farthest {maxHop} hop(s)");

        return string.Join("  ·  ", parts);
    }
}
