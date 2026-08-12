using System.Collections.ObjectModel;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using b1_chat_console.Models;
using b1_chat_console.Services;

namespace b1_chat_console.ViewModels;

public partial class CalibrationViewModel : ObservableObject
{
    private readonly ProtocolClient _protocol;
    private System.Threading.Timer? _saveDebounce;
    private ushort? _loadedFor;
    private int _saveGeneration;
    private bool _loadingCalibration;

    public ObservableCollection<Droid> Targets => _protocol.Droids;

    [ObservableProperty] private Droid? _selectedTarget;

    [ObservableProperty] private int _panMin = 0;
    [ObservableProperty] private int _panCenter = 90;
    [ObservableProperty] private int _panMax = 180;
    [ObservableProperty] private int _tiltMin = 0;
    [ObservableProperty] private int _tiltCenter = 90;
    [ObservableProperty] private int _tiltMax = 180;
    [ObservableProperty] private bool _panReversed;
    [ObservableProperty] private bool _tiltReversed;
    [ObservableProperty] private bool _supportsServoReverse;

    public CalibrationViewModel(ProtocolClient protocol)
    {
        _protocol = protocol;
        _protocol.CalibDataReceived += OnCalibData;
        _protocol.DroidsChanged += RefreshCapabilities;
    }

    partial void OnSelectedTargetChanged(Droid? value)
    {
        CancelPendingSave();
        if (value == null) { _loadedFor = null; return; }
        _loadedFor = value.Id;
        RefreshCapabilities();
        _protocol.RequestCalib(value.Id);
    }

    private void RefreshCapabilities()
    {
        SupportsServoReverse = SelectedTarget?.SupportsServoReverse == true;
    }

    private void OnCalibData(JsonElement root)
    {
        var target = root.TryGetProperty("target", out var t) ? (ushort)t.GetInt32() : (ushort)0;
        if (_loadedFor != target) return; // stale response (target changed in the meantime)

        CancelPendingSave();
        _loadingCalibration = true;
        try
        {
            if (root.TryGetProperty("panMin", out var pn)) PanMin = pn.GetInt32();
            if (root.TryGetProperty("panCenter", out var pc)) PanCenter = pc.GetInt32();
            if (root.TryGetProperty("panMax", out var pm)) PanMax = pm.GetInt32();
            if (root.TryGetProperty("tiltMin", out var tn)) TiltMin = tn.GetInt32();
            if (root.TryGetProperty("tiltCenter", out var tc)) TiltCenter = tc.GetInt32();
            if (root.TryGetProperty("tiltMax", out var tm)) TiltMax = tm.GetInt32();
            PanReversed = root.TryGetProperty("panReversed", out var pr) && pr.GetBoolean();
            TiltReversed = root.TryGetProperty("tiltReversed", out var tr) && tr.GetBoolean();
        }
        finally { _loadingCalibration = false; }
    }

    private void OnAxisChanged(int pan, int tilt)
    {
        if (_loadingCalibration || SelectedTarget == null) return;
        _protocol.Preview(SelectedTarget.Id, pan, tilt);
        ScheduleSave();
    }

    partial void OnPanMinChanged(int value) => OnAxisChanged(value, TiltCenter);
    partial void OnPanCenterChanged(int value) => OnAxisChanged(value, TiltCenter);
    partial void OnPanMaxChanged(int value) => OnAxisChanged(value, TiltCenter);
    partial void OnTiltMinChanged(int value) => OnAxisChanged(PanCenter, value);
    partial void OnTiltCenterChanged(int value) => OnAxisChanged(PanCenter, value);
    partial void OnTiltMaxChanged(int value) => OnAxisChanged(PanCenter, value);
    partial void OnPanReversedChanged(bool value) => ScheduleDirectionSave();
    partial void OnTiltReversedChanged(bool value) => ScheduleDirectionSave();

    private void ScheduleDirectionSave()
    {
        if (_loadingCalibration || SelectedTarget == null || !SupportsServoReverse) return;
        ScheduleSave();
    }

    private void ScheduleSave()
    {
        if (SelectedTarget == null) return;
        CancelPendingSave();
        var generation = _saveGeneration;
        var target = SelectedTarget.Id;
        var panMin = PanMin;
        var panCenter = PanCenter;
        var panMax = PanMax;
        var tiltMin = TiltMin;
        var tiltCenter = TiltCenter;
        var tiltMax = TiltMax;
        var panReversed = PanReversed;
        var tiltReversed = TiltReversed;
        _saveDebounce = new System.Threading.Timer(_ =>
        {
            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            void Send()
            {
                if (generation != _saveGeneration) return;
                _protocol.SetCalib(target, panMin, panCenter, panMax,
                                   tiltMin, tiltCenter, tiltMax,
                                   panReversed, tiltReversed);
            }
            if (dispatcher == null || dispatcher.CheckAccess()) Send(); else dispatcher.Invoke(Send);
        }, null, 1200, System.Threading.Timeout.Infinite);
    }

    private void CancelPendingSave()
    {
        _saveGeneration++;
        _saveDebounce?.Dispose();
        _saveDebounce = null;
    }

    [RelayCommand] private void GotoPanMin() => Preview(PanMin, TiltCenter);
    [RelayCommand] private void GotoPanCenter() => Preview(PanCenter, TiltCenter);
    [RelayCommand] private void GotoPanMax() => Preview(PanMax, TiltCenter);
    [RelayCommand] private void GotoTiltMin() => Preview(PanCenter, TiltMin);
    [RelayCommand] private void GotoTiltCenter() => Preview(PanCenter, TiltCenter);
    [RelayCommand] private void GotoTiltMax() => Preview(PanCenter, TiltMax);

    private void Preview(int pan, int tilt)
    {
        if (SelectedTarget != null) _protocol.Preview(SelectedTarget.Id, pan, tilt);
    }
}
