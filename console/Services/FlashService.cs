using System.Diagnostics;
using System.IO;

namespace b1_chat_console.Services;

/// <summary>
/// Porte de FindEspflash/StartFlash (ex-MainWindow.xaml.cs) : meme recherche de l'outil,
/// mêmes arguments espflash. Ne touche pas au port serie — l'appelant (ViewModel) doit le
/// fermer avant d'appeler Start() et le rouvrir lui-meme apres reception de Completed
/// (meme contrat que l'ancien StartFlash, qui ne rouvrait jamais le port non plus).
/// </summary>
public class FlashService
{
    public event Action<string>? LogLine;
    public event Action<bool, int?, string?>? Completed; // ok, exitCode, error

    public static string? FindEspflash()
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "tools", "espflash.exe"),
            @"C:\Program Files\KyberEditor\Tools\espflash.exe",
        };
        foreach (var c in candidates)
            if (File.Exists(c)) return c;

        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';'))
        {
            var p = Path.Combine(dir.Trim(), "espflash.exe");
            try { if (dir.Length > 0 && File.Exists(p)) return p; } catch { /* segment de PATH invalide */ }
        }
        return null;
    }

    public void Start(string binPath, string address, string portName, bool eraseFirst = false)
    {
        var tool = FindEspflash();
        if (tool == null)
        {
            Completed?.Invoke(false, null, "espflash.exe introuvable — dépose-le dans le dossier tools\\ à côté de l'application (voir CLAUDE.md).");
            return;
        }
        if (!File.Exists(binPath))
        {
            Completed?.Invoke(false, null, "fichier .bin introuvable : " + binPath);
            return;
        }
        if (string.IsNullOrWhiteSpace(portName))
        {
            Completed?.Invoke(false, null, "aucun port série choisi.");
            return;
        }

        if (eraseFirst) RunErase(tool, portName, () => RunWrite(tool, binPath, address, portName));
        else RunWrite(tool, binPath, address, portName);
    }

    private void RunErase(string tool, string portName, Action onSuccess)
    {
        var psi = new ProcessStartInfo
        {
            FileName = tool,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("erase-flash");
        psi.ArgumentList.Add("--port"); psi.ArgumentList.Add(portName);

        try
        {
            var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
            void Line(string? s) { if (s != null) LogLine?.Invoke(s); }
            proc.OutputDataReceived += (_, a) => Line(a.Data);
            proc.ErrorDataReceived += (_, a) => Line(a.Data);
            proc.Exited += (_, _) =>
            {
                if (proc.ExitCode == 0)
                {
                    LogLine?.Invoke("— Puce effacée —");
                    onSuccess();
                }
                else
                {
                    Completed?.Invoke(false, proc.ExitCode, "échec de l'effacement complet");
                }
                proc.Dispose();
            };
            proc.Start();
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();
            LogLine?.Invoke($"» espflash erase-flash --port {portName}");
        }
        catch (Exception ex)
        {
            Completed?.Invoke(false, null, ex.Message);
        }
    }

    private void RunWrite(string tool, string binPath, string address, string portName)
    {
        var psi = new ProcessStartInfo
        {
            FileName = tool,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("write-bin");
        psi.ArgumentList.Add("--port"); psi.ArgumentList.Add(portName);
        psi.ArgumentList.Add("-B"); psi.ArgumentList.Add("460800");
        psi.ArgumentList.Add(address);
        psi.ArgumentList.Add(binPath);

        try
        {
            var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
            void Line(string? s) { if (s != null) LogLine?.Invoke(s); }
            proc.OutputDataReceived += (_, a) => Line(a.Data);
            proc.ErrorDataReceived += (_, a) => Line(a.Data);
            proc.Exited += (_, _) =>
            {
                Completed?.Invoke(proc.ExitCode == 0, proc.ExitCode, null);
                proc.Dispose();
            };
            proc.Start();
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();
            LogLine?.Invoke($"» espflash write-bin --port {portName} -B 460800 {address} {Path.GetFileName(binPath)}");
        }
        catch (Exception ex)
        {
            Completed?.Invoke(false, null, ex.Message);
        }
    }
}
