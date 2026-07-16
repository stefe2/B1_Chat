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
    // Top) and the view (track-gutter row heights, total canvas height).
    public const double RowHeight = 48;
    public const double RowGap = 4;

    public ushort Id { get; init; }
    public string Label { get; init; } = "";
    // "MASTER"/"SLAVE"/"BROADCAST" — small caps caption under the name in the gutter row.
    public string Role { get; init; } = "";
    public bool IsBroadcast { get; init; }
    public int RowIndex { get; init; }

    // Rehearse (local) only — a live hardware Play cannot honor this, see CLAUDE.md.
    [ObservableProperty] private bool _muted;

    // DarkComboBoxStyle's ControlTemplate renders the selected item via SelectionBoxItem,
    // which fell back to this type's default ToString() instead of DisplayMemberPath for the
    // inspector's Target combo (SelectedItem-bound, not an items-panel row) — overriding it
    // is a simple, robust fix regardless of the exact WPF templating cause.
    public override string ToString() => Label;
}
