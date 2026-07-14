using System;
using System.IO;
using System.Security.Cryptography;

namespace b1_chat_console.Services;

/// <summary>
/// Pilote une session OTA (un esclave à la fois) : lit le .bin, calcule son MD5,
/// puis envoie un chunk par evt:otaChunkAck reçu (stop-and-wait, piloté par le
/// firmware — voir ota_master.cpp/CLAUDE.md). Ne connaît pas le mesh : tout
/// transite par ProtocolClient, comme le reste de la console.
/// </summary>
public class OtaService
{
    private readonly ProtocolClient _protocol;
    private byte[] _image = Array.Empty<byte>();
    private int _chunkSize;
    private int _totalChunks;
    private ushort _target;
    private bool _active;

    public event Action<int, int>? Progress;   // sent, total
    public event Action<bool, string>? Completed; // ok, message

    public OtaService(ProtocolClient protocol)
    {
        _protocol = protocol;
        _protocol.OtaReadyReceived += OnReady;
        _protocol.OtaChunkAckReceived += OnChunkAck;
        _protocol.OtaResultReceived += OnResult;
        _protocol.OtaErrorReceived += OnError;
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

        using var md5 = MD5.Create();
        var hex = Convert.ToHexString(md5.ComputeHash(_image)).ToLowerInvariant();
        _protocol.OtaStart(target, (uint)_image.Length, hex);
        return true;
    }

    public void Abort()
    {
        if (!_active) return;
        _protocol.OtaAbort();
        _active = false;
    }

    private void SendChunk(int index)
    {
        var offset = index * _chunkSize;
        var len = Math.Min(_chunkSize, _image.Length - offset);
        var b64 = Convert.ToBase64String(_image, offset, len);
        _protocol.OtaChunk(index, b64);
    }

    private void OnReady(ushort target, int sessionId, int chunkSize, int totalChunks)
    {
        if (!_active || target != _target) return;
        _chunkSize = chunkSize;
        _totalChunks = totalChunks;
        Progress?.Invoke(0, _totalChunks);
        SendChunk(0);
    }

    private void OnChunkAck(int seq, int sent, int total)
    {
        if (!_active) return;
        Progress?.Invoke(sent, total);
        if (sent < total) SendChunk(sent);
    }

    private void OnResult(ushort target, bool ok, string? fw, string? reason)
    {
        if (!_active || target != _target) return;
        _active = false;
        Completed?.Invoke(ok, ok ? $"Mise à jour réussie (fw {fw})." : $"Échec après redémarrage : {reason}.");
    }

    private void OnError(ushort? target, int sessionId, string reason)
    {
        if (!_active || (target.HasValue && target.Value != _target)) return;
        _active = false;
        Completed?.Invoke(false, "Échec du transfert : " + reason);
    }
}
