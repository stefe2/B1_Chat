namespace b1_chat_console.Models;

public record MeshNodeVisual(double X, double Y, string Label, bool IsMaster)
{
    public const double NodeRadius = 10;
    public double Left => X - NodeRadius;
    public double Top => Y - NodeRadius;
    public double LabelLeft => X - 30;
    public double LabelTop => Y + NodeRadius + 2;
}

public record MeshEdgeVisual(double X1, double Y1, double X2, double Y2, double Thickness, double Opacity, double Strength, string Tooltip);
