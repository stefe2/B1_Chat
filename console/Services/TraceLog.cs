using System;
using System.IO;

namespace b1_chat_console.Services;

/// <summary>
/// Trace de diagnostic du lien série : chaque ligne TX/RX (tronquée) et chaque
/// événement de lien (ouverture, fermeture, erreur, mort de la boucle de
/// lecture) horodatés, dans %LOCALAPPDATA%\B1ChatConsole\serial-trace.log.
/// Toujours actif : volume modeste (quelques Mo pour un OTA complet), fichier
/// recréé à chaque lancement. Ne doit JAMAIS bloquer ni faire échouer quoi que
/// ce soit — toute erreur d'écriture de la trace est avalée.
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
        Write("SYS", "trace démarrée");
    }

    public static void Write(string tag, string message)
    {
        if (Writer == null) return;
        lock (Lock)
        {
            try { Writer.WriteLine($"{DateTime.Now:HH:mm:ss.fff} [{tag,-3}] {message}"); } catch { }
        }
    }

    /// <summary>Tronque une ligne de protocole pour la trace (les otaChunk font ~330 caractères
    /// de base64 : le début + la longueur suffisent au diagnostic).</summary>
    public static string Trunc(string s)
    {
        s = s.TrimEnd('\r', '\n');
        return s.Length <= 120 ? s : s[..120] + $"…({s.Length} car.)";
    }
}
