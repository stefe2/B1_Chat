using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using b1_chat_console.Converters;
using b1_chat_console.Models;
using b1_chat_console.ViewModels;

namespace b1_chat_console.Views;

// First mouse-capture/drag interaction in this app (see the Sequencer timeline plan) — kept
// intentionally simple/explicit: raw MouseLeftButtonDown/Move/Up + CaptureMouse, no Thumb, no
// native DragDrop (including the gesture-library "drag-and-drop", which is really the same
// mouse-capture idiom driving a floating ghost element rather than DragDrop.DoDragDrop).
public partial class SequenceTimelineView : UserControl
{
    private bool _draggingClip;
    private SequenceStep? _dragStep;
    private double _dragStartMouseX;
    private double _dragStartMouseY;
    private int _dragStartMs;

    private bool _draggingAudioClip;
    private AudioClip? _dragAudioClip;
    private AudioLane? _dragAudioSourceLane;
    private double _dragAudioStartMouseX;
    private double _dragAudioStartMouseY;
    private int _dragAudioStartMs;

    private bool _scrubbing;

    // Gesture-library click-vs-drag: MouseLeftButtonDown always captures and arms candidate
    // state; only once the mouse has moved past a small threshold does this become a real drag
    // (ghost shown, insertion deferred to MouseUp) — otherwise MouseUp falls back to today's
    // plain-click behavior (insert on the armed track at the playhead).
    private const double DragThresholdPx = 5;
    private bool _chipCandidate;
    private bool _chipDragging;
    private int _chipAnimId;
    private Point _chipDownPos;

    public SequenceTimelineView()
    {
        InitializeComponent();
    }

    private SequencerViewModel? Vm => DataContext as SequencerViewModel;

    // Keeps the timeline's minimum drawn width in sync with the visible viewport, so row
    // backgrounds/gridlines always fill the body (mockup: width = max(content, viewport)).
    // -2 keeps the content strictly inside the viewport so no phantom horizontal scrollbar
    // appears from rounding.
    private void ScrollArea_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (Vm is { } vm) vm.ViewportWidthPx = Math.Max(0, e.NewSize.Width - 2);
    }

    // Scales zoom so the whole sequence fits the visible scroll area — mirrors the mockup's
    // "Fit" button. Needs the viewport's actual pixel width (a view concern), so this stays
    // code-behind rather than a ViewModel RelayCommand.
    private void BtnFit_Click(object sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm) return;
        var totalSec = vm.TotalDurationMs() / 1000.0;
        if (totalSec <= 0) return;
        var viewportPx = ScrollArea.ActualWidth - 40;
        if (viewportPx <= 0) return;
        vm.PxPerSecond = Math.Clamp(viewportPx / totalSec, 20, 300);
        // "Fit" means "show me the whole sequence" — scroll back to t=0, otherwise a
        // previously-scrolled view can still be looking past the (now fully zoomed-out) content.
        ScrollArea.ScrollToHorizontalOffset(0);
    }

    // --- Gesture clip drag: StartMs (horizontal) + Target (vertical, retarget to another
    // droid's row). Clips aren't resizable — no user-editable duration exists in the protocol. ---

    private void Clip_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.DataContext is not SequenceStep step || Vm is not { } vm) return;
        vm.SelectedStep = step;
        _dragStep = step;
        var pos = e.GetPosition(TracksCanvas);
        _dragStartMouseX = pos.X;
        _dragStartMouseY = pos.Y;
        _dragStartMs = step.StartMs;
        _draggingClip = true;
        step.Dragging = true; // dimmed while "in hand" (cleared on mouse-up)
        fe.CaptureMouse();
        vm.BeginStepDrag(); // once per gesture, not per pixel — Undo restores in one step
        e.Handled = true;
    }

    private void Clip_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_draggingClip || _dragStep == null || Vm is not { } vm || vm.PxPerMs <= 0) return;
        var pos = e.GetPosition(TracksCanvas);
        var deltaMs = (pos.X - _dragStartMouseX) / vm.PxPerMs;
        // Free pixel-level movement while dragging, on BOTH axes — Snap (horizontal grid) and
        // Target (row) only apply at release, so the clip glides with the cursor instead of
        // hopping 100ms or a full 52px row at a time.
        _dragStep.StartMs = Math.Max(0, (int)(_dragStartMs + deltaMs));
        _dragStep.DragOffsetY = pos.Y - _dragStartMouseY;
    }

    private void Clip_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_draggingClip) return;
        _draggingClip = false;
        if (_dragStep != null && Vm is { } vm)
        {
            _dragStep.StartMs = Math.Max(0, vm.RoundToGrid(_dragStep.StartMs)); // snap settles here
            // The row settles here too: retarget to whichever track is under the cursor —
            // released outside the tracks area vertically, the clip snaps back to its own row.
            var pos = e.GetPosition(TracksCanvas);
            if (pos.Y >= 0 && pos.Y <= TracksCanvas.ActualHeight && vm.TrackAtY(pos.Y) is { } track)
                _dragStep.Target = track.Id;
            _dragStep.DragOffsetY = 0;
            _dragStep.Dragging = false;
        }
        _dragStep = null;
        if (sender is FrameworkElement fe) fe.ReleaseMouseCapture();
        // Settle the ruler/extent once at drag-end, not on every MouseMove (would jitter
        // under the cursor mid-drag).
        Vm?.RefreshTimelineExtent();
    }

    // Clicking empty timeline space clears the selection — clip mouse-downs mark their event
    // handled, so this only ever fires for the bare canvas (row backgrounds/gridlines are
    // IsHitTestVisible=False).
    private void TracksCanvas_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (Vm is { } vm) vm.SelectedStep = null;
    }

    // --- Audio clip drag: StartMs (horizontal) plus an optional cross-lane move. The clip
    // itself glides with the cursor on both axes (transient DragOffsetY → TranslateTransform,
    // Canvas doesn't clip so it stays visible outside its own lane's row) — the actual
    // re-parent into another lane's Clips collection only happens once, at MouseUp, exactly
    // like the gesture clips' row retarget. ---

    // No single fixed Canvas exists for audio clips (each lane gets its own, generated by the
    // outer ItemsControl) — RootGrid is used purely as a stable measurement frame for the mouse
    // delta, same trick as the gesture-chip ghost drag below. Only the delta matters (current
    // minus start), so horizontal ScrollViewer scroll doesn't skew it: a pixel of mouse movement
    // is a pixel of delta in any non-scaling ancestor's coordinate space.
    private void AudioClip_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.DataContext is not AudioClip clip || Vm is not { } vm) return;
        _dragAudioClip = clip;
        _dragAudioSourceLane = vm.AudioLanes.FirstOrDefault(l => l.Clips.Contains(clip));
        var posRoot = e.GetPosition(RootGrid);
        _dragAudioStartMouseX = posRoot.X;
        _dragAudioStartMouseY = posRoot.Y;
        _dragAudioStartMs = clip.StartMs;
        _draggingAudioClip = true;
        clip.Dragging = true; // dimmed while "in hand" (cleared on mouse-up)
        fe.CaptureMouse();
        vm.BeginAudioClipDrag();
        e.Handled = true;
    }

    private void AudioClip_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_draggingAudioClip || _dragAudioClip == null || Vm is not { } vm || vm.PxPerMs <= 0) return;
        var posRoot = e.GetPosition(RootGrid);
        var deltaMs = (posRoot.X - _dragAudioStartMouseX) / vm.PxPerMs;
        // Same smooth-drag rule as gesture clips: free on both axes while moving, snap (time
        // grid) and lane both settle at release.
        _dragAudioClip.StartMs = Math.Max(0, (int)(_dragAudioStartMs + deltaMs));
        _dragAudioClip.DragOffsetY = posRoot.Y - _dragAudioStartMouseY;
    }

    private void AudioClip_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_draggingAudioClip) return;
        _draggingAudioClip = false;
        if (sender is FrameworkElement fe) fe.ReleaseMouseCapture();

        if (_dragAudioClip != null && Vm is { } vm)
        {
            _dragAudioClip.StartMs = Math.Max(0, vm.RoundToGrid(_dragAudioClip.StartMs));
            // The lane settles here: released over another lane's row → move the clip there;
            // released outside the lanes area entirely → snap back to its own lane.
            var yInLanes = e.GetPosition(AudioLanesItemsControl).Y;
            if (yInLanes >= 0 && yInLanes <= AudioLanesItemsControl.ActualHeight
                && vm.AudioLaneAtY(yInLanes) is { } lane && !ReferenceEquals(lane, _dragAudioSourceLane))
                vm.MoveAudioClipToLane(_dragAudioClip, lane);
            _dragAudioClip.DragOffsetY = 0;
            _dragAudioClip.Dragging = false;
        }
        _dragAudioClip = null;
        _dragAudioSourceLane = null;
        Vm?.RefreshTimelineExtent();
    }

    // --- Ruler: local scrub (ignored while a real hardware playback is driving the
    // playhead — see SequencerViewModel.SetPlayheadFromPixel). ---

    private void Ruler_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (Vm == null) return;
        _scrubbing = true;
        ((UIElement)sender).CaptureMouse();
        Vm.SetPlayheadFromPixel(e.GetPosition(RulerCanvas).X);
    }

    private void Ruler_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_scrubbing || Vm == null) return;
        Vm.SetPlayheadFromPixel(e.GetPosition(RulerCanvas).X);
    }

    private void Ruler_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        _scrubbing = false;
        ((UIElement)sender).ReleaseMouseCapture();
    }

    // --- Gesture library: plain click inserts on the armed track at the playhead (unchanged);
    // dragging past a small threshold instead drops the gesture on a specific droid+time cell. ---

    private void GestureChip_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.DataContext is not GestureLibraryEntry entry) return;
        _chipCandidate = true;
        _chipDragging = false;
        _chipAnimId = entry.Id;
        _chipDownPos = e.GetPosition(RootGrid);
        fe.CaptureMouse();
        e.Handled = true;
    }

    private void GestureChip_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_chipCandidate) return;
        var pos = e.GetPosition(RootGrid);

        if (!_chipDragging)
        {
            var moved = Math.Abs(pos.X - _chipDownPos.X) + Math.Abs(pos.Y - _chipDownPos.Y);
            if (moved < DragThresholdPx) return;
            _chipDragging = true;
            GhostText.Text = Vm?.GestureLibrary.FirstOrDefault(g => g.Id == _chipAnimId)?.Name ?? "";
            // Chips are neutral pills now (only their left edge is family-colored), so the
            // ghost takes the full family color instead of copying the chip's background —
            // it has to read against the timeline it's being dropped onto.
            GhostBorder.Background = (TryFindResource("AnimFamilyToBrushConv") as AnimFamilyToBrushConverter)
                ?.Convert(_chipAnimId, typeof(Brush), string.Empty, CultureInfo.InvariantCulture) as Brush;
            GhostBorder.Visibility = Visibility.Visible;
        }

        Canvas.SetLeft(GhostBorder, pos.X + 10);
        Canvas.SetTop(GhostBorder, pos.Y + 10);
    }

    private void GestureChip_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_chipCandidate) return;
        _chipCandidate = false;
        if (sender is FrameworkElement fe) fe.ReleaseMouseCapture();

        if (!_chipDragging)
        {
            // Plain click — today's behavior, insert on the armed track at the playhead.
            Vm?.InsertGestureCommand.Execute(_chipAnimId);
            return;
        }

        _chipDragging = false;
        GhostBorder.Visibility = Visibility.Collapsed;

        if (Vm is not { } vm) return;
        var posInTracks = e.GetPosition(TracksCanvas);
        var withinX = posInTracks.X >= 0 && posInTracks.X <= TracksCanvas.ActualWidth;
        var withinY = posInTracks.Y >= 0 && posInTracks.Y <= TracksCanvas.ActualHeight;
        if (!withinX || !withinY) return; // dropped outside the timeline: cancel, nothing inserted

        var startMs = vm.RoundToGrid(posInTracks.X / vm.PxPerMs);
        var track = vm.TrackAtY(posInTracks.Y);
        vm.InsertGestureAt(_chipAnimId, track, startMs);
    }
}
