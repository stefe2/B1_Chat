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

    public ObservableCollection<Droid> Targets => _protocol.Droids;

    [ObservableProperty] private Droid? _selectedTarget;

    [ObservableProperty] private int _panMin = 0;
    [ObservableProperty] private int _panCenter = 90;
    [ObservableProperty] private int _panMax = 180;
    [ObservableProperty] private int _tiltMin = 0;
    [ObservableProperty] private int _tiltCenter = 90;
    [ObservableProperty] private int _tiltMax = 180;

    public CalibrationViewModel(ProtocolClient protocol)
    {
        _protocol = protocol;
        _protocol.CalibDataReceived += OnCalibData;
    }

    partial void OnSelectedTargetChanged(Droid? value)
    {
        if (value == null) return;
        _loadedFor = value.Id;
        _protocol.RequestCalib(value.Id);
    }

    private void OnCalibData(JsonElement root)
    {
        var target = root.TryGetProperty("target", out var t) ? (ushort)t.GetInt32() : (ushort)0;
        if (_loadedFor != target) return; // reponse perimee (cible changee entre-temps)

        if (root.TryGetProperty("panMin", out var pn)) PanMin = pn.GetInt32();
        if (root.TryGetProperty("panCenter", out var pc)) PanCenter = pc.GetInt32();
        if (root.TryGetProperty("panMax", out var pm)) PanMax = pm.GetInt32();
        if (root.TryGetProperty("tiltMin", out var tn)) TiltMin = tn.GetInt32();
        if (root.TryGetProperty("tiltCenter", out var tc)) TiltCenter = tc.GetInt32();
        if (root.TryGetProperty("tiltMax", out var tm)) TiltMax = tm.GetInt32();
    }

    private void OnAxisChanged(int pan, int tilt)
    {
        if (SelectedTarget == null) return;
        _protocol.Preview(SelectedTarget.Id, pan, tilt);
        ScheduleSave();
    }

    partial void OnPanMinChanged(int value) => OnAxisChanged(value, TiltCenter);
    partial void OnPanCenterChanged(int value) => OnAxisChanged(value, TiltCenter);
    partial void OnPanMaxChanged(int value) => OnAxisChanged(value, TiltCenter);
    partial void OnTiltMinChanged(int value) => OnAxisChanged(PanCenter, value);
    partial void OnTiltCenterChanged(int value) => OnAxisChanged(PanCenter, value);
    partial void OnTiltMaxChanged(int value) => OnAxisChanged(PanCenter, value);

    private void ScheduleSave()
    {
        _saveDebounce?.Dispose();
        _saveDebounce = new System.Threading.Timer(_ =>
        {
            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            void Send()
            {
                if (SelectedTarget == null) return;
                _protocol.SetCalib(SelectedTarget.Id, PanMin, PanCenter, PanMax, TiltMin, TiltCenter, TiltMax);
            }
            if (dispatcher == null || dispatcher.CheckAccess()) Send(); else dispatcher.Invoke(Send);
        }, null, 1200, System.Threading.Timeout.Infinite);
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
