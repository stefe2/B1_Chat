using CommunityToolkit.Mvvm.ComponentModel;

namespace b1_chat_console.Models;

/// <summary>Target = 0xFFFF means "all droids".</summary>
public partial class SequenceStep : ObservableObject
{
    // Persistent V2 authoring values. AnimId remains a short-lived execution
    // adapter detail until the generated motion engine replaces it in Stage 4.
    [ObservableProperty] private Guid _clipId = Guid.NewGuid();
    [ObservableProperty] private string _gestureKey = "";
    [ObservableProperty] private string _intensity = "normal";
    [ObservableProperty] private string _tempo = "normal";
    [ObservableProperty] private string _variant = "default";
    [ObservableProperty] private uint _seed;
    [ObservableProperty] private int _animId;
    [ObservableProperty] private ushort _target = 0xFFFF;
    // Absolute offset from the sequence's own t=0 (not a delay from the
    // previous step — see docs/FIRMWARE-CONTRACT.md §6).
    [ObservableProperty] private int _startMs;

    // Persistent explicit endpoint for looping gestures (POWER_DOWN/TALK). It is ignored by
    // finite/immediate gestures but retained so changing a clip temporarily does not destroy
    // the user's chosen length.
    [ObservableProperty] private int _endAfterMs = Services.AnimationDurationProvider.DefaultInfiniteEndMs;

    // Transient duration projection supplied by AnimationDurationProvider at each document
    // commit/metadata refresh. Geometry, active state and text all bind to this same value.
    [ObservableProperty] private int _resolvedDurationMs;
    [ObservableProperty] private string _durationSummary = "provisional";
    [ObservableProperty] private string _durationDetail = "Firmware metadata has not been received yet.";
    [ObservableProperty] private bool _durationProvisional = true;
    [ObservableProperty] private AnimationDurationKind _durationKind = AnimationDurationKind.Finite;

    public bool IsInfinite => DurationKind == AnimationDurationKind.Infinite ||
        GestureKey == "dialogue.talk" || AnimId is 16 or 17;

    // Matches the catalog's seedPolicy:"required" gestures (communicate.nod, dialogue.talk) —
    // see catalog/gesture-catalog-v1.json. Everything else declares seedPolicy:"ignored".
    public bool RequiresSeed => GestureKey is "communicate.nod" or "dialogue.talk";

    // Transient view state: true while the clip is being held/dragged on the timeline
    // (dimmed to show it's "in hand"). Never serialized — same idea as AudioClip.Dragging.
    [ObservableProperty] private bool _dragging;

    // Transient view state: vertical pixel offset while dragged, so the clip glides with the
    // cursor instead of hopping row-to-row — Target only settles at mouse-up (like the
    // horizontal snap). Drives a TranslateTransform in the view; never serialized.
    [ObservableProperty] private double _dragOffsetY;

    // Transient execution telemetry. It is intentionally excluded from
    // Clone()/serialization: every Play pass starts with fresh reports.
    [ObservableProperty] private string _executionSummary = "";
    [ObservableProperty] private string _executionDetail = "";
    [ObservableProperty] private string _executionTone = "none";

    partial void OnAnimIdChanged(int value) => OnPropertyChanged(nameof(IsInfinite));
    partial void OnGestureKeyChanged(string value)
    {
        OnPropertyChanged(nameof(IsInfinite));
        OnPropertyChanged(nameof(RequiresSeed));
    }
    partial void OnDurationKindChanged(AnimationDurationKind value) => OnPropertyChanged(nameof(IsInfinite));

    public SequenceStep Clone() => new()
    {
        ClipId = Guid.NewGuid(),
        GestureKey = GestureKey,
        Intensity = Intensity,
        Tempo = Tempo,
        Variant = Variant,
        Seed = Seed,
        AnimId = AnimId,
        Target = Target,
        StartMs = StartMs,
        EndAfterMs = EndAfterMs,
    };
}
