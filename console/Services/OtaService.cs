using System;
using System.IO;
using System.Security.Cryptography;
using System.Windows.Threading;

namespace b1_chat_console.Services;

/// <summary>
/// Drives an OTA session (one slave at a time): reads the .bin, computes its MD5,
/// then sends one chunk per evt:otaChunkAck received (stop-and-wait, driven by
/// the firmware — see ota_master.cpp/CLAUDE.md). Knows nothing about the mesh:
/// everything goes through ProtocolClient, like the rest of the console.
///
/// The console<->master serial segment has no firmware-side retry (unlike the
/// mesh segment): a watchdog therefore retries the current chunk after
/// WatchdogTimeout of silence (the master re-emits the ack if the chunk had
/// already gone through — see onSerialChunk), and declares failure after MaxRetries.
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
    public event Action<int, int>? Retrying;   // index, attempt (UI info)

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
        if (_active) { error = "An OTA update is already in progress."; return false; }

        try { _image = File.ReadAllBytes(binPath); }
        catch (Exception ex) { error = "Unreadable file: " + ex.Message; return false; }
        if (_image.Length == 0) { error = "Empty file."; return false; }

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
            Completed?.Invoke(false, $"Serial link silent (chunk {_lastSentIndex} no response after {MaxRetries} attempts).");
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
            // Last chunk acked: what follows (mesh END, slave reboot, ~90s
            // confirmation) plays out between master and slave without us —
            // the watchdog has nothing left to watch.
            _watchdog.Stop();
        }
    }

    private void OnResult(ushort target, bool ok, string? fw, string? reason)
    {
        if (!_active || target != _target) return;
        _watchdog.Stop();
        _active = false;
        Completed?.Invoke(ok, ok ? $"Update succeeded (fw {fw})." : $"Failed after reboot: {reason}.");
    }

    private void OnError(ushort? target, int sessionId, string reason)
    {
        if (!_active || (target.HasValue && target.Value != _target)) return;
        _watchdog.Stop();
        _active = false;
        Completed?.Invoke(false, "Transfer failed: " + reason);
    }
}
