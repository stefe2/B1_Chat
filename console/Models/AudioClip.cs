using CommunityToolkit.Mvvm.ComponentModel;
using b1_chat_console.Services;

namespace b1_chat_console.Models;

/// <summary>One placed audio file on an AudioLane. Clips within a lane may overlap.</summary>
public partial class AudioClip : ObservableObject
{
    // FileName is derived from FilePath, so it must be re-read whenever the path changes —
    // without this, "Replace file…" left the previous basename on the clip (SEQ-F03).
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FileName))]
    private string _filePath = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasKnownDuration))]
    private int _durationMs;

    [ObservableProperty] private int _startMs;
    // Restarts on completion while playing — see SequencerViewModel.ScheduleTimers.
    [ObservableProperty] private bool _loop;

    // Waveform preview (WaveformService), populated asynchronously after load/add/replace — null
    // until then, or if decoding failed (missing/corrupt file), in which case no waveform renders.
    [ObservableProperty] private float[]? _peaks;

    // Transient, not persisted: outcome of the last duration probe. A failed probe used to be
    // indistinguishable from a genuinely empty file, both arriving as 0 ms (SEQ-F04/F05).
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDurationWarning))]
    [NotifyPropertyChangedFor(nameof(HasKnownDuration))]
    [NotifyPropertyChangedFor(nameof(StatusTooltip))]
    private AudioProbeStatus _probeStatus = AudioProbeStatus.Ok;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusTooltip))]
    private string? _probeMessage;

    // Transient, not persisted: true while this clip is held/dragged (dimmed "in hand", same
    // idea as SequenceStep.Dragging) — see SequenceTimelineView.xaml.cs.
    [ObservableProperty] private bool _dragging;

    // Transient view state: vertical pixel offset while dragged, so the clip itself glides
    // with the cursor across lanes; the actual lane move only settles at mouse-up. Drives a
    // TranslateTransform in the view; never serialized.
    [ObservableProperty] private double _dragOffsetY;

    /// <summary>
    /// Bumped every time the clip's source changes. A waveform decode captures the value it
    /// started with and refuses to publish if the clip has moved on since — otherwise a slow
    /// decode of the previous file overwrites the new one's envelope (SEQ-F06).
    /// </summary>
    internal int WaveformToken { get; private set; }

    internal int NextWaveformToken() => ++WaveformToken;

    public string FileName => System.IO.Path.GetFileName(FilePath);

    /// <summary>The probe failed: the clip is drawn with a warning badge and a reason.</summary>
    public bool HasDurationWarning => ProbeStatus != AudioProbeStatus.Ok;

    /// <summary>
    /// A usable, non-zero duration. False both for a failed probe and for a valid but empty file,
    /// because in either case the clip must not define the end of the sequence on its own.
    /// </summary>
    public bool HasKnownDuration => ProbeStatus == AudioProbeStatus.Ok && DurationMs > 0;

    public string StatusTooltip => ProbeStatus == AudioProbeStatus.Ok
        ? FileName
        : $"{FileName} — {ProbeMessage ?? "the duration could not be read."}";

    public AudioClip Clone() => new()
    {
        FilePath = FilePath,
        DurationMs = DurationMs,
        StartMs = StartMs,
        Loop = Loop,
        ProbeStatus = ProbeStatus,
        ProbeMessage = ProbeMessage,
    };
}
