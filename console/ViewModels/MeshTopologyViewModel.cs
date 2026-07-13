using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using b1_chat_console.Models;
using b1_chat_console.Services;

namespace b1_chat_console.ViewModels;

/// <summary>
/// Porte l'algorithme de renderTopology() (index.html) tel quel : layout circulaire
/// (maitre d'abord puis id croissant, depart a midi, sens horaire) et fusion des liens
/// diriges en paires non dirigees au pire RSSI des deux sens ("le plus prudent").
/// </summary>
public partial class MeshTopologyViewModel : ObservableObject
{
    private readonly ProtocolClient _protocol;

    public ObservableCollection<MeshNodeVisual> Nodes { get; } = new();
    public ObservableCollection<MeshEdgeVisual> Edges { get; } = new();

    [ObservableProperty] private int _linkCount;
    [ObservableProperty] private bool _isEmpty = true;

    public const double CanvasSize = 260;
    private const double Center = CanvasSize / 2, Radius = 90;

    public MeshTopologyViewModel(ProtocolClient protocol)
    {
        _protocol = protocol;
        _protocol.MeshTopologyChanged += Render;
        _protocol.DroidsChanged += Render;
    }

    private static double RssiToStrength(int rssi) => Math.Clamp((rssi + 90) / 60.0, 0, 1);

    private void Render()
    {
        Nodes.Clear();
        Edges.Clear();

        var ordered = _protocol.Droids
            .OrderByDescending(d => d.IsMaster)
            .ThenBy(d => d.Id)
            .ToList();
        IsEmpty = ordered.Count == 0;
        if (ordered.Count == 0) { LinkCount = 0; return; }

        var pos = new Dictionary<ushort, (double X, double Y)>();
        for (var i = 0; i < ordered.Count; i++)
        {
            var angle = 2 * Math.PI * i / ordered.Count - Math.PI / 2;
            var x = Center + Radius * Math.Cos(angle);
            var y = Center + Radius * Math.Sin(angle);
            pos[ordered[i].Id] = (x, y);
            Nodes.Add(new MeshNodeVisual(x, y, string.IsNullOrEmpty(ordered[i].Name) ? ordered[i].IdHex : ordered[i].Name, ordered[i].IsMaster));
        }

        // Fusion des aretes dirigees en paires non dirigees, cle = min-max.
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

        foreach (var ((a, b), (ab, ba)) in merged)
        {
            if (!pos.TryGetValue(a, out var pa) || !pos.TryGetValue(b, out var pb)) continue;
            var worst = new[] { ab, ba }.Where(v => v.HasValue).Select(v => v!.Value).DefaultIfEmpty(-90).Min();
            var strength = RssiToStrength(worst);
            var tooltip = ab.HasValue && ba.HasValue
                ? $"{ab} dBm / {ba} dBm"
                : $"{(ab ?? ba)} dBm";
            Edges.Add(new MeshEdgeVisual(pa.X, pa.Y, pb.X, pb.Y, 1 + 3 * strength, 0.25 + 0.7 * strength, strength, tooltip));
        }
        LinkCount = Edges.Count;
    }
}
