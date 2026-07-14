using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using b1_chat_console.Models;
using b1_chat_console.Services;

namespace b1_chat_console.ViewModels;

/// <summary>
/// Ports renderTopology() (index.html)'s algorithm as-is: circular layout
/// (master first then ascending id, starting at 12 o'clock, clockwise) and merges
/// directed links into undirected pairs at the worst RSSI of the two directions
/// ("the more conservative reading").
/// </summary>
public partial class MeshTopologyViewModel : ObservableObject
{
    private readonly ProtocolClient _protocol;

    public ObservableCollection<MeshNodeVisual> Nodes { get; } = new();
    public ObservableCollection<MeshEdgeVisual> Edges { get; } = new();

    [ObservableProperty] private int _linkCount;
    [ObservableProperty] private bool _isEmpty = true;
    [ObservableProperty] private string _statsText = "";

    // Master's current canvas position, used to anchor the hop-wave ripple and the
    // TALK-sync pulse rings drawn in MeshTopologyCardView.xaml (see code-behind).
    [ObservableProperty] private double _masterX;
    [ObservableProperty] private double _masterY;

    // OTA travel indicator: an animated dashed line from master to whichever droid
    // currently has an OTA session in flight (only one is ever active mesh-wide).
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
    private const double Center = CanvasSize / 2, BaseRadius = 90;

    // Spreads nodes a bit further out as the mesh grows past 6 droids, so labels
    // stay legible instead of crowding together — capped well short of the canvas
    // edge (CanvasSize/2 = 130) to leave room for node radius + label height.
    private static double RadiusFor(int nodeCount) =>
        nodeCount <= 6 ? BaseRadius : Math.Min(BaseRadius + (nodeCount - 6) * 3, 108);

    // Last-seen (Rssi, Online) per droid: a changed value between two renders is our
    // best available proxy for "this droid's telemetry just genuinely refreshed"
    // (evt:droids is a periodic full snapshot, not a per-heartbeat push).
    private readonly Dictionary<ushort, (int Rssi, bool Online)> _lastTelemetry = new();

    // Canvas positions from the most recent render, keyed by droid id — reused by
    // the OTA travel indicator to place its line without waiting for a re-render.
    private readonly Dictionary<ushort, (double X, double Y)> _positions = new();

    public MeshTopologyViewModel(ProtocolClient protocol)
    {
        _protocol = protocol;
        _protocol.MeshTopologyChanged += Render;
        _protocol.DroidsChanged += Render;
        _protocol.AnimSent += OnAnimSent;
        _protocol.OtaReadyReceived += (target, _, _, _) => StartOtaTravel(target);
        _protocol.OtaDoneReceived += (_, _) => OtaTravelActive = false;
        _protocol.OtaResultReceived += (_, _, _, _) => OtaTravelActive = false;
        _protocol.OtaErrorReceived += (_, _, _) => OtaTravelActive = false;
    }

    private static double RssiToStrength(int rssi) => Math.Clamp((rssi + 90) / 60.0, 0, 1);

    private void OnAnimSent(ushort target, int animId)
    {
        HopWaveRequested?.Invoke();
        if (animId != 17) return; // TALK
        var ms = _protocol.AnimDurationMs.TryGetValue(17, out var d) ? d : 2000;
        TalkPulseRequested?.Invoke(ms);
    }

    private void StartOtaTravel(ushort targetId)
    {
        var master = _protocol.Droids.FirstOrDefault(d => d.IsMaster);
        if (master == null || !_positions.TryGetValue(master.Id, out var mp) || !_positions.TryGetValue(targetId, out var tp)) return;
        OtaX1 = mp.X; OtaY1 = mp.Y; OtaX2 = tp.X; OtaY2 = tp.Y;
        OtaTravelActive = true;
    }

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
            return;
        }

        var radius = RadiusFor(ordered.Count);
        var pos = new Dictionary<ushort, (double X, double Y)>();
        for (var i = 0; i < ordered.Count; i++)
        {
            var angle = 2 * Math.PI * i / ordered.Count - Math.PI / 2;
            pos[ordered[i].Id] = (Center + radius * Math.Cos(angle), Center + radius * Math.Sin(angle));
        }
        _positions.Clear();
        foreach (var kv in pos) _positions[kv.Key] = kv.Value;
        if (ordered[0].IsMaster) { MasterX = pos[ordered[0].Id].X; MasterY = pos[ordered[0].Id].Y; }

        // Merge directed edges into undirected pairs, key = min-max; also build an
        // adjacency list (hop-count stats) and track each node's strongest link.
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
            Edges.Add(new MeshEdgeVisual(pa.X, pa.Y, pb.X, pb.Y, 1 + 3 * strength, 0.25 + 0.7 * strength, strength, tooltip));

            AddAdjacency(a, b);
            AddAdjacency(b, a);
            if (!bestStrengthByNode.TryGetValue(a, out var sa) || strength > sa) bestStrengthByNode[a] = strength;
            if (!bestStrengthByNode.TryGetValue(b, out var sb) || strength > sb) bestStrengthByNode[b] = strength;

            worstRssiSum += worst;
            worstRssiCount++;
            if (weakest == null || worst < weakest.Value.Rssi) weakest = (a, b, worst);
        }
        LinkCount = Edges.Count;

        // BFS hop distance from master over the direct-link graph.
        var masterId = ordered[0].Id;
        var hopDistance = new Dictionary<ushort, int> { [masterId] = 0 };
        var queue = new Queue<ushort>();
        queue.Enqueue(masterId);
        while (queue.Count > 0)
        {
            var cur = queue.Dequeue();
            if (!adjacency.TryGetValue(cur, out var neighbors)) continue;
            foreach (var n in neighbors)
                if (!hopDistance.ContainsKey(n)) { hopDistance[n] = hopDistance[cur] + 1; queue.Enqueue(n); }
        }

        var byId = ordered.ToDictionary(d => d.Id);
        for (var i = 0; i < ordered.Count; i++)
        {
            var d = ordered[i];
            var p = pos[d.Id];
            bestStrengthByNode.TryGetValue(d.Id, out var best);

            var telemetry = (d.Rssi, d.Online);
            var pulse = !d.IsMaster && (!_lastTelemetry.TryGetValue(d.Id, out var prev) || prev != telemetry);
            _lastTelemetry[d.Id] = telemetry;

            Nodes.Add(new MeshNodeVisual(p.X, p.Y, string.IsNullOrEmpty(d.Name) ? d.IdHex : d.Name, d.IsMaster)
            {
                BestLinkStrength = best,
                Pulse = pulse,
            });
        }

        StatsText = BuildStatsText(worstRssiCount, worstRssiSum, weakest, hopDistance, ordered.Count, byId);
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
