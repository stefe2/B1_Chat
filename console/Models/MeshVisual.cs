using CommunityToolkit.Mvvm.ComponentModel;

namespace b1_chat_console.Models;

// Mutable/observable (not a record) because the radius-smoothing ticker in
// MeshTopologyViewModel glides X/Y between telemetry refreshes instead of
// rebuilding the collection at 30 fps (which would replay the Pulse trigger
// and tear down tooltips mid-hover).
public partial class MeshNodeVisual : ObservableObject
{
    public const double NodeRadius = 10;
    public const double MasterRingRadius = 15;
    public const double PulseRingRadius = 13;

    public MeshNodeVisual(ushort id, double x, double y, string label, bool isMaster)
    {
        Id = id;
        _x = x;
        _y = y;
        Label = label;
        IsMaster = isMaster;
    }

    public ushort Id { get; }
    public string Label { get; }
    public bool IsMaster { get; }
    public bool Pulse { get; init; }

    // Signal-halo glow color source (see StrengthToBrushConverter, ConverterParameter="Color").
    public double BestLinkStrength { get; init; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Left), nameof(LabelLeft), nameof(MasterRingLeft), nameof(PulseRingLeft))]
    private double _x;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Top), nameof(LabelTop), nameof(MasterRingTop), nameof(PulseRingTop))]
    private double _y;

    public double Left => X - NodeRadius;
    public double Top => Y - NodeRadius;
    public double LabelLeft => X - 30;
    public double LabelTop => Y + NodeRadius + 2;

    // Spinning dashed ring drawn around the master node only (MeshTopologyCardView.xaml).
    public double MasterRingLeft => X - MasterRingRadius;
    public double MasterRingTop => Y - MasterRingRadius;

    // One-shot "ping" ring played when this droid's telemetry actually refreshes
    // (see MeshTopologyViewModel.Render()), distinct from the master's permanent ring above.
    public double PulseRingLeft => X - PulseRingRadius;
    public double PulseRingTop => Y - PulseRingRadius;
}

// Same reasoning as MeshNodeVisual: endpoints are observable so the smoothing ticker can
// drag an edge along with its moving node. A/B are the droid ids at each endpoint.
public partial class MeshEdgeVisual : ObservableObject
{
    public MeshEdgeVisual(ushort a, ushort b, System.Windows.Point pa, System.Windows.Point pb,
                          double thickness, double opacity, double strength, string tooltip)
    {
        A = a;
        B = b;
        _x1 = pa.X; _y1 = pa.Y;
        _x2 = pb.X; _y2 = pb.Y;
        Thickness = thickness;
        Opacity = opacity;
        Strength = strength;
        Tooltip = tooltip;
    }

    public ushort A { get; }
    public ushort B { get; }
    public double Thickness { get; }
    public double Opacity { get; }
    public double Strength { get; }
    public string Tooltip { get; }

    [ObservableProperty] private double _x1;
    [ObservableProperty] private double _y1;
    [ObservableProperty] private double _x2;
    [ObservableProperty] private double _y2;
}

// A single in-flight mesh frame, drawn as a small dot traveling hop-by-hop along the real
// edges of the graph (see MeshTopologyViewModel's packet ticker). Mutable/observable because
// its position is animated live rather than rebuilt per-render.
public partial class MeshPacketVisual : ObservableObject
{
    public const double Radius = 3.5;

    [ObservableProperty] private double _left;
    [ObservableProperty] private double _top;

    public required string Kind { get; init; }
    public required string Tooltip { get; init; }

    // Multi-hop path in canvas coordinates (master end first) and the ticker's progress along it.
    public required System.Windows.Point[] Path { get; init; }
    internal int SegmentIndex;
    internal double SegmentProgress;
}
