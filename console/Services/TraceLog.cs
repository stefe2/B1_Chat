using System;
using System.IO;

namespace b1_chat_console.Services;

/// <summary>
/// Serial link diagnostic trace: every TX/RX line (truncated) and every link
/// event (open, close, error, read-loop death), timestamped, written to
/// %LOCALAPPDATA%\B1ChatConsole\serial-trace.log.
/// Always active: modest volume (a few MB for a full OTA), file recreated on
/// every launch. Must NEVER block or fail anything — any trace write error is
/// swallowed.
/// </summary>
public static class TraceLog
{
    private static readonly object Lock = new();
    private static readonly StreamWriter? Writer;

    static TraceLog()
    {
        try
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "B1ChatConsole");
            Directory.CreateDirectory(dir);
            Writer = new StreamWriter(Path.Combine(dir, "serial-trace.log"), append: false) { AutoFlush = true };
        }
        catch { Writer = null; }
        Write("SYS", "trace started");
    }

    public static void Write(string tag, string message)
    {
        if (Writer == null) return;
        lock (Lock)
        {
            try { Writer.WriteLine($"{DateTime.Now:HH:mm:ss.fff} [{tag,-3}] {message}"); } catch { }
        }
    }

    /// <summary>Truncates a protocol line for the trace (otaChunk lines are ~330 base64
    /// characters: the start + the length are enough for diagnosis).</summary>
    public static string Trunc(string s)
    {
        s = s.TrimEnd('\r', '\n');
        return s.Length <= 120 ? s : s[..120] + $"…({s.Length} chars)";
    }
}
