using System.IO.Ports;
using System.Windows;
using System.Windows.Threading;

namespace b1_chat_console.Services;

/// <summary>
/// Porte de OpenPort/ClosePort/ReadLoop (ex-MainWindow.xaml.cs) : meme comportement (boucle
/// bloquante sur un Task, ReadTimeout 500ms traite comme un poll normal), plus la logique de
/// reconnexion automatique qui vivait uniquement cote JS dans index.html (absente cote C# avant
/// ce portage) : re-scan toutes les 3s apres une deconnexion inattendue, jusqu'a ce que le port
/// reapparaisse ou qu'une fermeture volontaire (Close()/PrepareForExternalClose()) l'arrete.
/// Les evenements sont leves depuis la boucle de lecture (thread arriere-plan) ou un minuteur :
/// c'est a l'appelant (ViewModel) de re-marshaler sur le thread UI si necessaire.
/// </summary>
public class SerialLinkService : IDisposable
{
    public event Action<string>? LineReceived;
    public event Action? Opened;
    public event Action<string>? OpenFailed;
    public event Action<bool>? Closed; // true = deconnexion inattendue
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

    /// <summary>Les evenements sont leves depuis des threads arriere-plan (lecture serie, minuteur
    /// de reconnexion) ; on les remarshale systematiquement sur le thread UI ici (meme reflexe que
    /// Dispatcher.Invoke dans l'ancien ReadLoop) pour que les ObservableCollection liees en aval
    /// n'explosent jamais avec une exception inter-thread.</summary>
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
            var port = new SerialPort(portName, 115200) { NewLine = "\n", Encoding = System.Text.Encoding.UTF8, ReadTimeout = 500 };
            port.Open();
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

    /// <summary>Fermeture volontaire demandee par l'utilisateur : pas de reconnexion auto.</summary>
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
    /// Fermeture volontaire pour un besoin externe (flash espflash) : ferme sans lever d'evenement
    /// "closed" ni tenter de reconnecter — l'appelant sait deja pourquoi le port se ferme et
    /// rouvrira lui-meme le port apres coup si besoin (meme contrat que l'ancien StartFlash).
    /// Attend (borne a 1s) que le thread de lecture arriere-plan ait vraiment fini avant de
    /// fermer/disposer le SerialPort : sans cette attente, un process externe (espflash) qui
    /// tente d'ouvrir le meme port juste apres peut se heurter a une course avec la liberation
    /// du handle Windows encore en cours cote thread de lecture — echec "Error while connecting
    /// to device" cote espflash, meme si Close()/Dispose() ont deja ete appeles ici.
    /// </summary>
    public void PrepareForExternalClose()
    {
        _manualClose = true;
        StopReconnectLoop();
        CancelAndWaitReadLoop();
        ClosePortOnly();
    }

    private void CancelAndWaitReadLoop()
    {
        _readCts?.Cancel();
        try { _readTask?.Wait(1000); } catch { /* thread deja termine/exception attendue, sans importance ici */ }
    }

    public void Write(string data)
    {
        try { _port?.Write(data); }
        catch (Exception ex) { RunOnUi(() => ErrorOccurred?.Invoke(ex.Message)); }
    }

    private void ReadLoop(SerialPort port, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            string? line;
            try { line = port.ReadLine(); }
            catch (TimeoutException) { continue; }
            catch { break; }
            if (line != null) { var l = line; RunOnUi(() => LineReceived?.Invoke(l)); }
        }

        if (!token.IsCancellationRequested)
        {
            // Deconnexion non demandee (cable debranche, etc.).
            var disconnectedPort = PortName;
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
            try { _port.Close(); } catch { /* deja ferme/deconnecte */ }
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
