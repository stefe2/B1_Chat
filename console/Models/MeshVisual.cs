namespace b1_chat_console.Models;

public record MeshNodeVisual(double X, double Y, string Label, bool IsMaster)
{
    public const double NodeRadius = 10;
    public double Left => X - NodeRadius;
    public double Top => Y - NodeRadius;
    public double LabelLeft => X - 30;
    public double LabelTop => Y + NodeRadius + 2;

    // Spinning dashed ring drawn around the master node only (MeshTopologyCardView.xaml).
    public const double MasterRingRadius = 15;
    public double MasterRingLeft => X - MasterRingRadius;
    public double MasterRingTop => Y - MasterRingRadius;

    // One-shot "ping" ring played when this droid's telemetry actually refreshes
    // (see MeshTopologyViewModel.Render()), distinct from the master's permanent ring above.
    public const double PulseRingRadius = 13;
    public double PulseRingLeft => X - PulseRingRadius;
    public double PulseRingTop => Y - PulseRingRadius;
    public bool Pulse { get; init; }

    // Signal-halo glow color source (see StrengthToBrushConverter, ConverterParameter="Color").
    public double BestLinkStrength { get; init; }
}

public record MeshEdgeVisual(double X1, double Y1, double X2, double Y2, double Thickness, double Opacity, double Strength, string Tooltip);
