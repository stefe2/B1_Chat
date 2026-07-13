using System.Windows.Media;

namespace b1_chat_console.Models;

public enum LogKind { Tx, Rx, Sys, Err }

public record LogEntry(LogKind Kind, string Text)
{
    public Brush Brush => Kind switch
    {
        LogKind.Tx => Brushes.CornflowerBlue,
        LogKind.Rx => Brushes.MediumSeaGreen,
        LogKind.Err => Brushes.IndianRed,
        _ => Brushes.Gray,
    };
}
