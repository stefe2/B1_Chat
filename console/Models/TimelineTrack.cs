using CommunityToolkit.Mvvm.ComponentModel;

namespace b1_chat_console.Models;

/// <summary>
/// One row of the Sequencer timeline: a droid, or the synthetic "All droids" broadcast
/// row (Id = 0xFFFF). Wholesale rebuilt whenever the droid roster changes (never mutated
/// in place) — except Muted, which SequencerViewModel.RebuildTracks() carries forward by
/// Id from the previous generation, same as ArmedTrack, so it survives a heartbeat-driven
/// refresh instead of silently resetting.
/// </summary>
public partial class TimelineTrack : ObservableObject
{
    // Single source of truth for row geometry — shared by TimelineGeometryConverter (clip
    // Top) and the view (track-gutter row heights, total canvas height). Rows are contiguous
    // (mockup-matched rail: full-width 52px rows divided by bottom separators, no gap).
    public const double RowHeight = 52;
    public const double RowGap = 0;

    public ushort Id { get; init; }
    public string Label { get; init; } = "";
    // "MASTER"/"SLAVE"/"BROADCAST" — small caps caption under the name in the gutter row.
    public string Role { get; init; } = "";
    public bool IsBroadcast { get; init; }
    public int RowIndex { get; init; }

    // Applies to Play (the only playback path there is — see SequencerViewModel.ScheduleTimers).
    [ObservableProperty] private bool _muted;

    // DarkComboBoxStyle's ControlTemplate renders the selected item via SelectionBoxItem,
    // which fell back to this type's default ToString() instead of DisplayMemberPath for the
    // inspector's Target combo (SelectedItem-bound, not an items-panel row) — overriding it
    // is a simple, robust fix regardless of the exact WPF templating cause.
    public override string ToString() => Label;
}
