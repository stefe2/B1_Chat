using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using b1_chat_console.Models;
using b1_chat_console.Services;

namespace b1_chat_console.ViewModels;

public partial class AnimationViewModel : ObservableObject
{
    private readonly ProtocolClient _protocol;
    private System.Threading.Timer? _saveDebounce;
    private int _saveGeneration;
    private bool _loadingConfig;

    public static readonly string[] AnimNames =
    {
        "IDLE", "LOOK_AROUND", "NOD_YES", "SHAKE_NO", "CURIOUS_TILT", "SCAN_SLOW",
        "ALERT_SNAP", "TRACK", "GLITCH_STUTTER", "CONFUSED_TILT", "DOUBLE_TAKE",
        "SLEEPY_DROOP", "TARGET_LOCK", "WHIRR_SEARCH", "SIGNAL_GLITCH",
        "GREETING_NOD", "POWER_DOWN", "TALK",
    };

    public ObservableCollection<Droid> Targets => _protocol.Droids;
    public ObservableCollection<string> Anims { get; } = new(AnimNames.Select((n, i) => $"{i} — {n}"));

    [ObservableProperty] private Droid? _selectedTarget;
    [ObservableProperty] private int _selectedAnimIndex;

    // Fleet-wide (or per-target, via TargetId below) idle-animation tuning —
    // wired up firmware-side in fw 1.9.0 (applyAnimParamsEffect in main.cpp):
    // Freq scales the spontaneous idle-draw interval, Amp scales gesture
    // offsets, Speed scales gesture move/hold durations. 50/60/50 are the
    // historical defaults (index.html's original values), matching today's
    // untouched tuning until moved.
    [ObservableProperty] private int _freq = 50;
    [ObservableProperty] private int _amp = 60;
    [ObservableProperty] private int _speed = 50;

    public AnimationViewModel(ProtocolClient protocol)
    {
        _protocol = protocol;
        _protocol.ConfigDataReceived += OnConfigData;
    }

    private ushort TargetId => SelectedTarget?.Id ?? 0xFFFF;

    [RelayCommand]
    private void PlayAnim()
    {
        var seed = (uint)Random.Shared.Next();
        _protocol.PlayAnim(TargetId, SelectedAnimIndex, seed);
    }

    partial void OnSelectedTargetChanged(Droid? value)
    {
        CancelPendingSave();
        _protocol.RequestConfig(value?.Id ?? 0xFFFF);
    }

    partial void OnFreqChanged(int value) { if (!_loadingConfig) ScheduleSave(); }
    partial void OnAmpChanged(int value) { if (!_loadingConfig) ScheduleSave(); }
    partial void OnSpeedChanged(int value) { if (!_loadingConfig) ScheduleSave(); }

    private void OnConfigData(ushort target, int freq, int amp, int speed)
    {
        var selectedId = SelectedTarget?.Id;
        if (selectedId.HasValue && selectedId.Value != target) return;
        if (!selectedId.HasValue && Targets.FirstOrDefault(d => d.IsMaster)?.Id != target) return;

        CancelPendingSave();
        _loadingConfig = true;
        try
        {
            Freq = freq;
            Amp = amp;
            Speed = speed;
        }
        finally { _loadingConfig = false; }
    }

    private void CancelPendingSave()
    {
        _saveGeneration++;
        _saveDebounce?.Dispose();
        _saveDebounce = null;
    }

    private void ScheduleSave()
    {
        CancelPendingSave();
        var generation = _saveGeneration;
        var target = TargetId;
        var freq = Freq;
        var amp = Amp;
        var speed = Speed;
        _saveDebounce = new System.Threading.Timer(_ =>
        {
            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            void Send()
            {
                if (generation != _saveGeneration) return;
                _protocol.SetConfig(target, freq, amp, speed);
            }
            if (dispatcher == null || dispatcher.CheckAccess()) Send(); else dispatcher.Invoke(Send);
        }, null, 1200, System.Threading.Timeout.Infinite);
    }
}
