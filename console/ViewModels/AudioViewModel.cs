using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using b1_chat_console.Services;

namespace b1_chat_console.ViewModels;

public partial class AudioViewModel : ObservableObject
{
    private readonly ProtocolClient _protocol;
    private System.Threading.Timer? _saveDebounce;

    [ObservableProperty] private int _volume = 15;
    [ObservableProperty] private int _track = 1;

    public AudioViewModel(ProtocolClient protocol)
    {
        _protocol = protocol;
        _protocol.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ProtocolClient.LastVolume)) Volume = _protocol.LastVolume;
        };
    }

    partial void OnVolumeChanged(int value)
    {
        _saveDebounce?.Dispose();
        _saveDebounce = new System.Threading.Timer(_ =>
        {
            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            void Send() => _protocol.SetVolume(Volume);
            if (dispatcher == null || dispatcher.CheckAccess()) Send(); else dispatcher.Invoke(Send);
        }, null, 1200, System.Threading.Timeout.Infinite);
    }

    [RelayCommand]
    private void PlayTrack() => _protocol.PlayTrack(Track);
}
