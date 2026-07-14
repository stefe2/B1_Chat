using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;

namespace b1_chat_console.Services;

/// <summary>
/// Port of FindEspflash/StartFlash (formerly MainWindow.xaml.cs): same tool lookup, same
/// espflash arguments. Doesn't touch the serial port — the caller (ViewModel) must close it
/// before calling Start() and reopen it itself after receiving Completed (same contract as
/// the old StartFlash, which never reopened the port either).
/// </summary>
public class FlashService
{
    public event Action<string>? LogLine;
    public event Action<int>? Progress; // 0..100
    public event Action<bool, int?, string?>? Completed; // ok, exitCode, error

    // espflash's progress bar (redrawn in place via \r with no \n) arrives as separate
    // "lines" (StreamReader.ReadLine() also splits on a lone \r): it's extracted here
    // rather than left to flood FlashLog with dozens of near-identical updates.
    private static readonly Regex ProgressPercentRegex = new(@"(\d{1,3})\s*%", RegexOptions.Compiled);
    private static readonly Regex ProgressFractionRegex = new(@"([\d.]+)\s*(K|M)?i?B\s*/\s*([\d.]+)\s*(K|M)?i?B", RegexOptions.Compiled);

    private static double UnitMultiplier(string unit) => unit switch { "K" => 1024, "M" => 1024 * 1024, _ => 1 };

    private static int? TryParseProgressPercent(string line)
    {
        var mf = ProgressFractionRegex.Match(line);
        if (mf.Success)
        {
            var cur = double.Parse(mf.Groups[1].Value, CultureInfo.InvariantCulture) * UnitMultiplier(mf.Groups[2].Value);
            var tot = double.Parse(mf.Groups[3].Value, CultureInfo.InvariantCulture) * UnitMultiplier(mf.Groups[4].Value);
            if (tot > 0) return (int)Math.Clamp(100.0 * cur / tot, 0, 100);
        }
        var mp = ProgressPercentRegex.Match(line);
        if (mp.Success && int.TryParse(mp.Groups[1].Value, out var pct)) return Math.Clamp(pct, 0, 100);
        return null;
    }

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
            try { if (dir.Length > 0 && File.Exists(p)) return p; } catch { /* invalid PATH segment */ }
        }
        return null;
    }

    public void Start(string binPath, string address, string portName, bool eraseFirst = false)
    {
        var tool = FindEspflash();
        if (tool == null)
        {
            Completed?.Invoke(false, null, "espflash.exe not found — drop it into the tools\\ folder next to the application (see CLAUDE.md).");
            return;
        }
        if (!File.Exists(binPath))
        {
            Completed?.Invoke(false, null, ".bin file not found: " + binPath);
            return;
        }
        if (string.IsNullOrWhiteSpace(portName))
        {
            Completed?.Invoke(false, null, "no serial port selected.");
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
            void Line(string? s)
            {
                if (s == null) return;
                var pct = TryParseProgressPercent(s);
                if (pct.HasValue) Progress?.Invoke(pct.Value);
                else LogLine?.Invoke(s);
            }
            proc.OutputDataReceived += (_, a) => Line(a.Data);
            proc.ErrorDataReceived += (_, a) => Line(a.Data);
            proc.Exited += (_, _) =>
            {
                if (proc.ExitCode == 0)
                {
                    LogLine?.Invoke("— Chip erased —");
                    onSuccess();
                }
                else
                {
                    Completed?.Invoke(false, proc.ExitCode, "full chip erase failed");
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
            void Line(string? s)
            {
                if (s == null) return;
                var pct = TryParseProgressPercent(s);
                if (pct.HasValue) Progress?.Invoke(pct.Value);
                else LogLine?.Invoke(s);
            }
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
