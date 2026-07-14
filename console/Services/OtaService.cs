using System;
using System.IO;
using System.Security.Cryptography;
using System.Windows.Threading;

namespace b1_chat_console.Services;

/// <summary>
/// Pilote une session OTA (un esclave à la fois) : lit le .bin, calcule son MD5,
/// puis envoie un chunk par evt:otaChunkAck reçu (stop-and-wait, piloté par le
/// firmware — voir ota_master.cpp/CLAUDE.md). Ne connaît pas le mesh : tout
/// transite par ProtocolClient, comme le reste de la console.
///
/// Le segment série console↔maître n'a aucun retry côté firmware (contrairement
/// au segment mesh) : un chien de garde retente donc le chunk courant après
/// WatchdogTimeout de silence (le maître ré-émet l'ack si le chunk était déjà
/// passé — voir onSerialChunk), et déclare l'échec après MaxRetries.
/// </summary>
public class OtaService
{
    private static readonly TimeSpan WatchdogTimeout = TimeSpan.FromSeconds(3);
    private const int MaxRetries = 5;

    private readonly ProtocolClient _protocol;
    private readonly DispatcherTimer _watchdog;
    private byte[] _image = Array.Empty<byte>();
    private int _chunkSize;
    private int _totalChunks;
    private ushort _target;
    private bool _active;
    private int _lastSentIndex = -1;
    private int _retryCount;
    private DateTime _lastActivity;

    public event Action<int, int>? Progress;   // sent, total
    public event Action<bool, string>? Completed; // ok, message
    public event Action<int, int>? Retrying;   // index, tentative (info UI)

    public OtaService(ProtocolClient protocol)
    {
        _protocol = protocol;
        _protocol.OtaReadyReceived += OnReady;
        _protocol.OtaChunkAckReceived += OnChunkAck;
        _protocol.OtaResultReceived += OnResult;
        _protocol.OtaErrorReceived += OnError;
        _watchdog = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _watchdog.Tick += OnWatchdogTick;
    }

    public bool Active => _active;

    public bool Start(ushort target, string binPath, out string error)
    {
        error = "";
        if (_active) { error = "Une mise à jour OTA est déjà en cours."; return false; }

        try { _image = File.ReadAllBytes(binPath); }
        catch (Exception ex) { error = "Fichier illisible : " + ex.Message; return false; }
        if (_image.Length == 0) { error = "Fichier vide."; return false; }

        _target = target;
        _active = true;
        _lastSentIndex = -1;
        _retryCount = 0;

        using var md5 = MD5.Create();
        var hex = Convert.ToHexString(md5.ComputeHash(_image)).ToLowerInvariant();
        _protocol.OtaStart(target, (uint)_image.Length, hex);
        return true;
    }

    public void Abort()
    {
        if (!_active) return;
        _watchdog.Stop();
        _protocol.OtaAbort();
        _active = false;
    }

    private void SendChunk(int index)
    {
        var offset = index * _chunkSize;
        var len = Math.Min(_chunkSize, _image.Length - offset);
        var b64 = Convert.ToBase64String(_image, offset, len);
        _lastSentIndex = index;
        _lastActivity = DateTime.UtcNow;
        _protocol.OtaChunk(index, b64);
    }

    private void OnWatchdogTick(object? sender, EventArgs e)
    {
        if (!_active || _lastSentIndex < 0) return;
        if (DateTime.UtcNow - _lastActivity < WatchdogTimeout) return;

        if (_retryCount >= MaxRetries)
        {
            _watchdog.Stop();
            _active = false;
            Completed?.Invoke(false, $"Liaison série silencieuse (chunk {_lastSentIndex} sans réponse après {MaxRetries} tentatives).");
            return;
        }

        _retryCount++;
        Retrying?.Invoke(_lastSentIndex, _retryCount);
        SendChunk(_lastSentIndex);
    }

    private void OnReady(ushort target, int sessionId, int chunkSize, int totalChunks)
    {
        if (!_active || target != _target) return;
        _chunkSize = chunkSize;
        _totalChunks = totalChunks;
        Progress?.Invoke(0, _totalChunks);
        _watchdog.Start();
        SendChunk(0);
    }

    private void OnChunkAck(int seq, int sent, int total)
    {
        if (!_active) return;
        _retryCount = 0;
        _lastActivity = DateTime.UtcNow;
        Progress?.Invoke(sent, total);
        if (sent < total)
        {
            SendChunk(sent);
        }
        else
        {
            // Dernier chunk accusé : la suite (END mesh, reboot de l'esclave,
            // confirmation ~90 s) se joue entre maître et esclave sans nous —
            // le chien de garde n'a plus rien à surveiller.
            _watchdog.Stop();
        }
    }

    private void OnResult(ushort target, bool ok, string? fw, string? reason)
    {
        if (!_active || target != _target) return;
        _watchdog.Stop();
        _active = false;
        Completed?.Invoke(ok, ok ? $"Mise à jour réussie (fw {fw})." : $"Échec après redémarrage : {reason}.");
    }

    private void OnError(ushort? target, int sessionId, string reason)
    {
        if (!_active || (target.HasValue && target.Value != _target)) return;
        _watchdog.Stop();
        _active = false;
        Completed?.Invoke(false, "Échec du transfert : " + reason);
    }
}
