using System.IO.Ports;
using System.Windows;
using System.Windows.Threading;

namespace b1_chat_console.Services;

/// <summary>
/// Port of OpenPort/ClosePort/ReadLoop (formerly MainWindow.xaml.cs): same behavior (blocking
/// loop on a Task, 500ms ReadTimeout treated as a normal poll), plus the automatic reconnect
/// logic that used to live only in JS in index.html (absent on the C# side before this port):
/// re-scans every 3s after an unexpected disconnect, until the port reappears or a voluntary
/// close (Close()/PrepareForExternalClose()) stops it.
/// Events are raised from the read loop (background thread) or a timer: it's up to the caller
/// (ViewModel) to remarshal onto the UI thread if needed.
/// </summary>
public class SerialLinkService : IDisposable
{
    public event Action<string>? LineReceived;
    public event Action? Opened;
    public event Action<string>? OpenFailed;
    public event Action<bool>? Closed; // true = unexpected disconnect
    public event Action<string>? ErrorOccurred;

    public bool AutoReconnect { get; set; } = true;
    public bool IsOpen => _port != null;
    public string? PortName { get; private set; }

    private SerialPort? _port;
    private CancellationTokenSource? _readCts;
    private Task? _readTask;
    private System.Threading.Timer? _reconnectTimer;
    private bool _manualClose;

    public static string[] GetPortNames() => SerialPort.GetPortNames();

    /// <summary>Events are raised from background threads (serial read, reconnect timer); they're
    /// systematically remarshaled onto the UI thread here (same reflex as Dispatcher.Invoke in the
    /// old ReadLoop) so downstream bound ObservableCollections never blow up with a cross-thread
    /// exception.</summary>
    private static void RunOnUi(Action a)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null || dispatcher.CheckAccess()) a();
        else dispatcher.Invoke(a);
    }

    public void Open(string portName)
    {
        ClosePortOnly();
        StopReconnectLoop();
        _manualClose = false;
        try
        {
            // Explicit WriteTimeout: by default SerialPort.Write() blocks indefinitely if the
            // buffer doesn't drain fast enough (e.g. unstable link) — without this, a stuck OTA
            // chunk freezes the UI thread (Write() is called synchronously from SendCmdRaw)
            // without ever throwing an exception that could be caught.
            var port = new SerialPort(portName, 115200) { NewLine = "\n", Encoding = System.Text.Encoding.UTF8, ReadTimeout = 500, WriteTimeout = 3000 };
            port.Open();
            TraceLog.Write("SYS", "port opened: " + portName);
            _port = port;
            PortName = portName;
            _readCts = new CancellationTokenSource();
            var token = _readCts.Token;
            _readTask = Task.Run(() => ReadLoop(port, token), token);
            RunOnUi(() => Opened?.Invoke());
        }
        catch (Exception ex)
        {
            _port = null;
            PortName = null;
            RunOnUi(() => OpenFailed?.Invoke(ex.Message));
        }
    }

    /// <summary>Voluntary close requested by the user: no auto-reconnect.</summary>
    public void Close()
    {
        _manualClose = true;
        StopReconnectLoop();
        var wasOpen = _port != null;
        CancelAndWaitReadLoop();
        ClosePortOnly();
        if (wasOpen) RunOnUi(() => Closed?.Invoke(false));
    }

    /// <summary>
    /// Voluntary close for an external need (espflash flashing): doesn't attempt to auto-reconnect
    /// (the caller reopens the port itself afterward if needed, same contract as the old
    /// StartFlash) but still raises "Closed" (like Close()) so the upstream Connected state stays
    /// in sync with the port's actual state — otherwise a consumer that was never notified of the
    /// close stays stuck on "connected" while the port is already closed, with no way to restart
    /// the connection from the UI.
    /// Waits (capped at 1s) for the background read thread to actually finish before
    /// closing/disposing the SerialPort: without this wait, an external process (espflash) trying
    /// to open the same port right after can race against the Windows handle release still in
    /// progress on the read thread's side — "Error while connecting to device" on espflash's side,
    /// even though Close()/Dispose() were already called here.
    /// </summary>
    public void PrepareForExternalClose()
    {
        _manualClose = true;
        StopReconnectLoop();
        CancelAndWaitReadLoop();
        var wasOpen = _port != null;
        ClosePortOnly();
        if (wasOpen) RunOnUi(() => Closed?.Invoke(false));
    }

    private void CancelAndWaitReadLoop()
    {
        _readCts?.Cancel();
        try { _readTask?.Wait(1000); } catch { /* thread already finished/expected exception, doesn't matter here */ }
    }

    public void Write(string data)
    {
        var port = _port;
        if (port == null)
        {
            // Without this signal, writing to a closed port was a silent no-op: the OTA
            // watchdog kept retrying its chunks into the void with nothing to warn it.
            TraceLog.Write("TX!", "port closed — " + TraceLog.Trunc(data));
            RunOnUi(() => ErrorOccurred?.Invoke("port closed (write impossible)"));
            return;
        }
        TraceLog.Write("TX", TraceLog.Trunc(data));
        try { port.Write(data); }
        catch (Exception ex)
        {
            TraceLog.Write("ERR", "Write: " + ex.Message);
            RunOnUi(() => ErrorOccurred?.Invoke(ex.Message));
        }
    }

    private void ReadLoop(SerialPort port, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            string? line;
            try { line = port.ReadLine(); }
            catch (TimeoutException) { continue; }
            catch (Exception ex)
            {
                // The cause of the read loop's death used to be swallowed here: the link
                // would die in the master->console direction with no actionable clue.
                TraceLog.Write("ERR", "ReadLoop: " + ex.GetType().Name + " — " + ex.Message);
                break;
            }
            if (line != null)
            {
                var l = line;
                TraceLog.Write("RX", TraceLog.Trunc(l));
                // BeginInvoke (async, order-preserving) rather than Invoke: a synchronous
                // Invoke would suspend THIS read thread for the entire UI processing of the
                // line (which can itself do a port.Write blocking for up to 3s) — the OS's RX
                // buffer could then overflow and lose lines with no exception.
                var dispatcher = Application.Current?.Dispatcher;
                if (dispatcher == null || dispatcher.CheckAccess()) LineReceived?.Invoke(l);
                else dispatcher.BeginInvoke(() => LineReceived?.Invoke(l));
            }
        }

        if (!token.IsCancellationRequested)
        {
            // Unrequested disconnect (cable unplugged, etc.).
            var disconnectedPort = PortName;
            TraceLog.Write("SYS", "unexpected disconnect from " + (disconnectedPort ?? "?"));
            ClosePortOnly();
            RunOnUi(() => Closed?.Invoke(true));
            if (!_manualClose && AutoReconnect && disconnectedPort != null)
                StartReconnectLoop(disconnectedPort);
        }
    }

    private void StartReconnectLoop(string portName)
    {
        StopReconnectLoop();
        _reconnectTimer = new System.Threading.Timer(_ =>
        {
            if (_manualClose || _port != null) return;
            if (Array.IndexOf(SerialPort.GetPortNames(), portName) >= 0)
            {
                StopReconnectLoop();
                Open(portName);
            }
        }, null, TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(3));
    }

    private void StopReconnectLoop()
    {
        _reconnectTimer?.Dispose();
        _reconnectTimer = null;
    }

    private void ClosePortOnly()
    {
        _readCts?.Cancel();
        _readCts = null;
        _readTask = null;
        if (_port != null)
        {
            try { _port.Close(); } catch { /* already closed/disconnected */ }
            _port.Dispose();
            _port = null;
        }
        PortName = null;
    }

    public void Dispose()
    {
        StopReconnectLoop();
        ClosePortOnly();
    }
}
