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

    // Ces 3 curseurs n'ont, a ce jour, aucun effet firmware (hook onConfig non
    // branche dans main.cpp — voir CLAUDE.md) : on reproduit fidelement ce vide,
    // ce n'est pas un bug de la console.
    [ObservableProperty] private int _freq = 50;
    [ObservableProperty] private int _amp = 50;
    [ObservableProperty] private int _speed = 50;

    public AnimationViewModel(ProtocolClient protocol)
    {
        _protocol = protocol;
    }

    private ushort TargetId => SelectedTarget?.Id ?? 0xFFFF;

    [RelayCommand]
    private void PlayAnim()
    {
        var seed = (uint)Random.Shared.Next();
        _protocol.PlayAnim(TargetId, SelectedAnimIndex, seed);
    }

    partial void OnFreqChanged(int value) => ScheduleSave();
    partial void OnAmpChanged(int value) => ScheduleSave();
    partial void OnSpeedChanged(int value) => ScheduleSave();

    private void ScheduleSave()
    {
        _saveDebounce?.Dispose();
        _saveDebounce = new System.Threading.Timer(_ =>
        {
            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            void Send() => _protocol.SetConfig(TargetId, Freq, Amp, Speed);
            if (dispatcher == null || dispatcher.CheckAccess()) Send(); else dispatcher.Invoke(Send);
        }, null, 1200, System.Threading.Timeout.Infinite);
    }
}
