using System.Globalization;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
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
    private bool _clipCandidate;
    private bool _draggingClip;
    private SequenceStep? _dragStep;
    private double _dragStartMouseX;
    private double _dragStartMouseY;
    private int _dragStartMs;

    private bool _audioClipCandidate;
    private bool _draggingAudioClip;
    private AudioClip? _dragAudioClip;
    private AudioLane? _dragAudioSourceLane;
    private double _dragAudioStartMouseX;
    private double _dragAudioStartMouseY;
    private int _dragAudioStartMs;

    private bool _scrubbing;
    private double _scrubStartPlayheadMs;

    // Gesture-library click-vs-drag: MouseLeftButtonDown always captures a candidate
    // state; only once the mouse has moved past a small threshold does this become a real drag
    // (ghost shown, insertion deferred to MouseUp) — otherwise MouseUp falls back to today's
    // plain-click behavior (insert only on an explicitly armed track at the playhead).
    private const double DragThresholdPx = 5;
    private bool _chipCandidate;
    private bool _chipDragging;
    private int _chipAnimId;
    private Point _chipDownPos;
    private Window? _hostWindow;
    private SequencerViewModel? _subscribedVm;
    // ScrollChanged is raised after ScrollToHorizontalOffset returns. A transient Boolean is
    // therefore already false when the event arrives and makes Follow disable itself. Retain
    // the requested destination until that exact automatic change is observed instead.
    private double? _automaticHorizontalScrollTarget;
    private bool _horizontalScrollbarInteraction;
    private bool _restoreFollowAfterScrollbarInteraction;

    private const double FollowCorridorLeftRatio = 0.15;
    private const double FollowCorridorRightRatio = 0.72;
    private const double ZoomStepFactor = 1.15;
    private const double MinimumZoomPxPerSecond = 20;
    private const double MaximumZoomPxPerSecond = 300;

    public SequenceTimelineView()
    {
        InitializeComponent();
    }

    private SequencerViewModel? Vm => DataContext as SequencerViewModel;

    internal static bool ExceedsDragThreshold(Point start, Point current) =>
        Math.Abs(current.X - start.X) + Math.Abs(current.Y - start.Y) >= DragThresholdPx;

    internal static double CalculateWheelZoom(double currentPxPerSecond, int wheelDelta) =>
        Math.Clamp(
            currentPxPerSecond * Math.Pow(ZoomStepFactor, wheelDelta / 120.0),
            MinimumZoomPxPerSecond,
            MaximumZoomPxPerSecond);

    internal static double CalculatePointerCenteredOffset(
        double currentOffset, double pointerViewportX,
        double oldPxPerSecond, double newPxPerSecond, double scrollableWidth)
    {
        if (oldPxPerSecond <= 0 || newPxPerSecond <= 0) return currentOffset;
        var contentX = currentOffset + Math.Max(0, pointerViewportX);
        var scaledContentX = contentX * newPxPerSecond / oldPxPerSecond;
        return Math.Clamp(scaledContentX - Math.Max(0, pointerViewportX), 0, Math.Max(0, scrollableWidth));
    }

    internal static double CalculateFollowOffset(
        double currentOffset, double playheadContentX, double viewportWidth, double scrollableWidth)
    {
        if (viewportWidth <= 0) return currentOffset;
        var viewportX = playheadContentX - currentOffset;
        var left = viewportWidth * FollowCorridorLeftRatio;
        var right = viewportWidth * FollowCorridorRightRatio;
        var desired = viewportX < left
            ? playheadContentX - left
            : viewportX > right
                ? playheadContentX - right
                : currentOffset;
        return Math.Clamp(desired, 0, Math.Max(0, scrollableWidth));
    }

    internal static bool MatchesAutomaticScrollTarget(double? requestedOffset, double observedOffset) =>
        requestedOffset is { } requested && Math.Abs(requested - observedOffset) <= 0.75;

    internal static bool ShouldRestoreFollowAfterScrollbarInteraction(
        bool followWasEnabled, bool isPlaying) => followWasEnabled && isPlaying;

    private void SequenceTimelineView_Loaded(object sender, RoutedEventArgs e)
    {
        AttachViewModel(Vm);
        var window = Window.GetWindow(this);
        if (ReferenceEquals(window, _hostWindow)) return;
        if (_hostWindow != null) _hostWindow.Deactivated -= HostWindow_Deactivated;
        _hostWindow = window;
        if (_hostWindow != null) _hostWindow.Deactivated += HostWindow_Deactivated;
    }

    private void SequenceTimelineView_Unloaded(object sender, RoutedEventArgs e)
    {
        AttachViewModel(null);
        if (_hostWindow != null) _hostWindow.Deactivated -= HostWindow_Deactivated;
        _hostWindow = null;
        CancelAllInteractions();
    }

    private void HostWindow_Deactivated(object? sender, EventArgs e) => CancelAllInteractions();

    private void SequenceTimelineView_DataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (IsLoaded) AttachViewModel(e.NewValue as SequencerViewModel);
    }

    private void AttachViewModel(SequencerViewModel? vm)
    {
        if (ReferenceEquals(_subscribedVm, vm)) return;
        if (_subscribedVm != null) _subscribedVm.PropertyChanged -= ViewModel_PropertyChanged;
        _subscribedVm = vm;
        if (_subscribedVm != null) _subscribedVm.PropertyChanged += ViewModel_PropertyChanged;
    }

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not SequencerViewModel vm || !ReferenceEquals(vm, Vm)) return;
        if (e.PropertyName == nameof(vm.PlayheadMs) && vm.IsPlaying && vm.FollowPlayhead)
            KeepPlayheadInComfortCorridor(vm);
        else if (e.PropertyName == nameof(vm.FollowPlayhead) && vm.FollowPlayhead)
            KeepPlayheadInComfortCorridor(vm);
    }

    private void SequenceTimelineView_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            CancelAllInteractions();
            Keyboard.ClearFocus();
            e.Handled = true;
            return;
        }
        if (Vm is not { } vm || Keyboard.FocusedElement is TextBoxBase or PasswordBox
            or ComboBox or Slider) return;

        if (e.Key == Key.Space && Keyboard.Modifiers == ModifierKeys.None)
        {
            // A focused button (e.g. Play/Stop still holding focus after being clicked) handles
            // its own Space activation; every other shortcut below is unaffected by button focus,
            // otherwise Delete/Undo/Redo/Restart stay dead until the operator clicks a clip again.
            if (Keyboard.FocusedElement is ButtonBase) return;
            vm.PlayCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.Control)
        {
            vm.RestartCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == Key.Home && Keyboard.Modifiers == ModifierKeys.Control
                 && vm.ReturnToStartCommand.CanExecute(null))
        {
            vm.ReturnToStartCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == Key.Delete && Keyboard.Modifiers == ModifierKeys.None
                 && vm.SelectedStep is { } selected && vm.DeleteStepCommand.CanExecute(selected))
        {
            vm.DeleteStepCommand.Execute(selected);
            e.Handled = true;
        }
        else if (e.Key == Key.Z && Keyboard.Modifiers == ModifierKeys.Control && vm.UndoCommand.CanExecute(null))
        {
            vm.UndoCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == Key.Y && Keyboard.Modifiers == ModifierKeys.Control && vm.RedoCommand.CanExecute(null))
        {
            vm.RedoCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == Key.C && Keyboard.Modifiers == ModifierKeys.Control
                 && vm.CopyStepCommand.CanExecute(vm.SelectedStep))
        {
            vm.CopyStepCommand.Execute(vm.SelectedStep);
            e.Handled = true;
        }
        else if (e.Key == Key.V && Keyboard.Modifiers == ModifierKeys.Control && vm.PasteStepCommand.CanExecute(null))
        {
            vm.PasteStepCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void Interaction_LostMouseCapture(object sender, MouseEventArgs e)
    {
        if (_clipCandidate || _audioClipCandidate || _chipCandidate || _scrubbing)
            CancelAllInteractions();
    }

    private void CancelAllInteractions()
    {
        var captured = Mouse.Captured as UIElement;
        var restoreScrub = _scrubbing;
        _clipCandidate = false;
        _draggingClip = false;
        if (_dragStep != null)
        {
            _dragStep.DragOffsetY = 0;
            _dragStep.Dragging = false;
        }
        _dragStep = null;

        _audioClipCandidate = false;
        _draggingAudioClip = false;
        if (_dragAudioClip != null)
        {
            _dragAudioClip.DragOffsetY = 0;
            _dragAudioClip.Dragging = false;
        }
        _dragAudioClip = null;
        _dragAudioSourceLane = null;

        _chipCandidate = false;
        _chipDragging = false;
        GhostBorder.Visibility = Visibility.Collapsed;
        _scrubbing = false;

        Vm?.CancelEditTransaction();
        if (restoreScrub && Vm is { } vm) vm.PlayheadMs = _scrubStartPlayheadMs;
        captured?.ReleaseMouseCapture();
    }

    // Keeps the timeline's minimum drawn width in sync with the visible viewport, so row
    // backgrounds/gridlines always fill the body (mockup: width = max(content, viewport)).
    // -2 keeps the content strictly inside the viewport so no phantom horizontal scrollbar
    // appears from rounding.
    private void ScrollArea_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (Vm is { } vm) vm.ViewportWidthPx = Math.Max(0, e.NewSize.Width - 2);
    }

    private void ScrollArea_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (Math.Abs(e.HorizontalChange) <= 0.01) return;
        if (MatchesAutomaticScrollTarget(_automaticHorizontalScrollTarget, e.HorizontalOffset))
        {
            _automaticHorizontalScrollTarget = null;
            return;
        }

        // Any different offset is a user/navigation change (scrollbar, Shift+wheel, or a
        // layout coercion), so discard a stale automatic request and yield control.
        _automaticHorizontalScrollTarget = null;
        if (Vm is { } vm) vm.FollowPlayhead = false;
    }

    private void ScrollArea_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (!OriginatesInHorizontalScrollbar(e.OriginalSource as DependencyObject)) return;
        _horizontalScrollbarInteraction = true;
        _restoreFollowAfterScrollbarInteraction = Vm is { } vm &&
            ShouldRestoreFollowAfterScrollbarInteraction(vm.FollowPlayhead, vm.IsPlaying);
    }

    private void ScrollArea_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_horizontalScrollbarInteraction) return;
        _horizontalScrollbarInteraction = false;
        var restore = _restoreFollowAfterScrollbarInteraction;
        _restoreFollowAfterScrollbarInteraction = false;
        if (!restore) return;

        // The thumb's final ScrollChanged can be deferred until after MouseUp. Restore on the
        // dispatcher only after that notification has had a chance to mark the manual offset.
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, () =>
        {
            if (Vm is { IsPlaying: true } vm)
                vm.FollowPlayhead = true;
        });
    }

    private static bool OriginatesInHorizontalScrollbar(DependencyObject? source)
    {
        while (source != null)
        {
            if (source is ScrollBar scrollBar)
                return scrollBar.Orientation == Orientation.Horizontal;
            source = source is ContentElement content
                ? ContentOperations.GetParent(content) ??
                  (content as FrameworkContentElement)?.Parent
                : source is Visual
                    ? VisualTreeHelper.GetParent(source)
                    : LogicalTreeHelper.GetParent(source);
        }
        return false;
    }

    private void ScrollArea_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (Vm is not { } vm || e.Delta == 0) return;
        var modifiers = Keyboard.Modifiers;
        if ((modifiers & ModifierKeys.Control) != 0)
        {
            var oldZoom = vm.PxPerSecond;
            var newZoom = CalculateWheelZoom(oldZoom, e.Delta);
            if (Math.Abs(newZoom - oldZoom) < 0.001) { e.Handled = true; return; }
            var pointerX = e.GetPosition(ScrollArea).X;
            var oldOffset = ScrollArea.HorizontalOffset;
            vm.FollowPlayhead = false;
            vm.PxPerSecond = newZoom;
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, () =>
            {
                var target = CalculatePointerCenteredOffset(
                    oldOffset, pointerX, oldZoom, newZoom, ScrollArea.ScrollableWidth);
                ScrollHorizontally(target, automatic: true);
            });
            e.Handled = true;
        }
        else if ((modifiers & ModifierKeys.Shift) != 0)
        {
            vm.FollowPlayhead = false;
            ScrollHorizontally(ScrollArea.HorizontalOffset - e.Delta, automatic: false);
            e.Handled = true;
        }
    }

    private void ZoomSlider_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (Vm is { } vm) vm.FollowPlayhead = false;
    }

    private void KeepPlayheadInComfortCorridor(SequencerViewModel vm)
    {
        var target = CalculateFollowOffset(
            ScrollArea.HorizontalOffset,
            vm.PlayheadMs * vm.PxPerMs,
            ScrollArea.ViewportWidth,
            ScrollArea.ScrollableWidth);
        if (Math.Abs(target - ScrollArea.HorizontalOffset) > 0.25)
            ScrollHorizontally(target, automatic: true);
    }

    private void ScrollHorizontally(double offset, bool automatic)
    {
        var target = Math.Clamp(offset, 0, Math.Max(0, ScrollArea.ScrollableWidth));
        _automaticHorizontalScrollTarget = automatic ? target : null;
        ScrollArea.ScrollToHorizontalOffset(target);
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
        vm.FollowPlayhead = false;
        vm.PxPerSecond = Math.Clamp(viewportPx / totalSec, MinimumZoomPxPerSecond, MaximumZoomPxPerSecond);
        // "Fit" means "show me the whole sequence" — scroll back to t=0, otherwise a
        // previously-scrolled view can still be looking past the (now fully zoomed-out) content.
        ScrollHorizontally(0, automatic: true);
    }

    // --- Gesture clip drag: StartMs (horizontal) + Target (vertical, retarget to another
    // droid's row). Infinite endpoints are edited precisely in the inspector rather than by
    // dragging the clip edge. ---

    private void Clip_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.DataContext is not SequenceStep hitStep || Vm is not { } vm) return;
        var pos = e.GetPosition(TracksCanvas);
        var step = PickStepAt(vm, pos, Keyboard.Modifiers.HasFlag(ModifierKeys.Alt), hitStep);
        vm.SelectedStep = step;
        if (!vm.CanEditSequence) { e.Handled = true; return; }
        _clipCandidate = true;
        _draggingClip = false;
        _dragStep = step;
        _dragStartMouseX = pos.X;
        _dragStartMouseY = pos.Y;
        _dragStartMs = step.StartMs;
        fe.CaptureMouse();
        e.Handled = true;
    }

    // Deterministic hit-testing for clips that overlap in time on the same row (e.g. a short
    // TILT overlay sitting on top of a longer PAN base clip, the Stage 5 composition case): the
    // NARROWEST clip whose [StartMs, StartMs+ResolvedDurationMs] contains the click point wins,
    // rather than whichever happens to be topmost in z-order. Clicking the part of a longer clip
    // that extends beyond every shorter one still reaches it normally, since nothing narrower
    // covers that point. Alt+Click cycles to the next-larger candidate at the same point, for a
    // same-width tie no width difference can resolve. Falls back to whatever WPF's own hit-test
    // already picked (hitStep) if geometry recomputation somehow finds no candidate at all.
    private static SequenceStep PickStepAt(SequencerViewModel vm, Point canvasPos, bool cycle, SequenceStep hitStep)
    {
        if (vm.PxPerMs <= 0) return hitStep;
        var track = vm.TrackAtY(canvasPos.Y);
        if (track == null) return hitStep;
        var ms = canvasPos.X / vm.PxPerMs;
        var candidates = vm.Steps
            .Where(s => s.Target == track.Id && s.StartMs <= ms && ms <= s.StartMs + s.ResolvedDurationMs)
            .OrderBy(s => s.ResolvedDurationMs)
            .ToList();
        if (candidates.Count == 0) return hitStep;
        if (!cycle) return candidates[0];
        var currentIndex = vm.SelectedStep != null ? candidates.IndexOf(vm.SelectedStep) : -1;
        return candidates[(currentIndex + 1) % candidates.Count];
    }

    private void Clip_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_clipCandidate || _dragStep == null || Vm is not { CanEditSequence: true } vm || vm.PxPerMs <= 0) return;
        var pos = e.GetPosition(TracksCanvas);
        if (!_draggingClip)
        {
            if (!ExceedsDragThreshold(new Point(_dragStartMouseX, _dragStartMouseY), pos)) return;
            if (!vm.BeginStepDrag()) { CancelAllInteractions(); return; }
            _draggingClip = true;
            _dragStep.Dragging = true;
        }
        var deltaMs = (pos.X - _dragStartMouseX) / vm.PxPerMs;
        // Free pixel-level movement while dragging, on BOTH axes — Snap (horizontal grid) and
        // Target (row) only apply at release, so the clip glides with the cursor instead of
        // hopping 100ms or a full 52px row at a time.
        _dragStep.StartMs = Math.Max(0, (int)(_dragStartMs + deltaMs));
        _dragStep.DragOffsetY = pos.Y - _dragStartMouseY;
    }

    private void Clip_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_clipCandidate) return;
        var completedDrag = _draggingClip;
        _clipCandidate = false;
        _draggingClip = false;
        if (_dragStep != null)
        {
            if (completedDrag && Vm is { CanEditSequence: true } vm)
            {
                _dragStep.StartMs = Math.Max(0, vm.RoundToGrid(_dragStep.StartMs)); // snap settles here
                // The row settles here too: retarget to whichever track is under the cursor —
                // released outside the tracks area vertically, the clip snaps back to its own row.
                var pos = e.GetPosition(TracksCanvas);
                if (pos.Y >= 0 && pos.Y <= TracksCanvas.ActualHeight && vm.TrackAtY(pos.Y) is { } track)
                    _dragStep.Target = track.Id;
            }
            // A Play transition can lock editing while the mouse is still captured. In that
            // case preserve the position captured by Play, but always clear visual drag state.
            _dragStep.DragOffsetY = 0;
            _dragStep.Dragging = false;
        }
        _dragStep = null;
        if (sender is FrameworkElement fe) fe.ReleaseMouseCapture();
        // The transaction compares the final persistent state with mouse-down, then refreshes
        // the timeline once only if something really moved.
        if (completedDrag) Vm?.CompleteEditTransaction();
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
        if (!vm.CanEditSequence) { e.Handled = true; return; }
        _audioClipCandidate = true;
        _draggingAudioClip = false;
        _dragAudioClip = clip;
        _dragAudioSourceLane = vm.AudioLanes.FirstOrDefault(l => l.Clips.Contains(clip));
        var posRoot = e.GetPosition(RootGrid);
        _dragAudioStartMouseX = posRoot.X;
        _dragAudioStartMouseY = posRoot.Y;
        _dragAudioStartMs = clip.StartMs;
        fe.CaptureMouse();
        e.Handled = true;
    }

    private void AudioClip_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_audioClipCandidate || _dragAudioClip == null || Vm is not { CanEditSequence: true } vm || vm.PxPerMs <= 0) return;
        var posRoot = e.GetPosition(RootGrid);
        if (!_draggingAudioClip)
        {
            if (!ExceedsDragThreshold(
                    new Point(_dragAudioStartMouseX, _dragAudioStartMouseY), posRoot)) return;
            if (!vm.BeginAudioClipDrag()) { CancelAllInteractions(); return; }
            _draggingAudioClip = true;
            _dragAudioClip.Dragging = true;
        }
        var deltaMs = (posRoot.X - _dragAudioStartMouseX) / vm.PxPerMs;
        // Same smooth-drag rule as gesture clips: free on both axes while moving, snap (time
        // grid) and lane both settle at release.
        _dragAudioClip.StartMs = Math.Max(0, (int)(_dragAudioStartMs + deltaMs));
        _dragAudioClip.DragOffsetY = posRoot.Y - _dragAudioStartMouseY;
    }

    private void AudioClip_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_audioClipCandidate) return;
        var completedDrag = _draggingAudioClip;
        _audioClipCandidate = false;
        _draggingAudioClip = false;
        if (sender is FrameworkElement fe) fe.ReleaseMouseCapture();

        if (_dragAudioClip != null)
        {
            if (completedDrag && Vm is { CanEditSequence: true } vm)
            {
                _dragAudioClip.StartMs = Math.Max(0, vm.RoundToGrid(_dragAudioClip.StartMs));
                // The lane settles here: released over another lane's row → move the clip there;
                // released outside the lanes area entirely → snap back to its own lane.
                var yInLanes = e.GetPosition(AudioLanesItemsControl).Y;
                if (yInLanes >= 0 && yInLanes <= AudioLanesItemsControl.ActualHeight
                    && vm.AudioLaneAtY(yInLanes) is { } lane && !ReferenceEquals(lane, _dragAudioSourceLane))
                    vm.MoveAudioClipToLane(_dragAudioClip, lane);
            }
            // See the gesture drag path above: transient visuals must be released even if
            // transport became active during capture, but persistent placement stays frozen.
            _dragAudioClip.DragOffsetY = 0;
            _dragAudioClip.Dragging = false;
        }
        _dragAudioClip = null;
        _dragAudioSourceLane = null;
        if (completedDrag) Vm?.CompleteEditTransaction();
    }

    private void LaneLabel_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is not TextBox textBox || textBox.DataContext is not AudioLane ||
            Vm is not { } vm || vm.BeginLaneRename()) return;
        Keyboard.ClearFocus();
    }

    private void LaneLabel_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e) =>
        Vm?.CompleteEditTransaction();

    private void LaneLabel_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        Vm?.CompleteEditTransaction();
        if (sender is UIElement element)
            element.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
        e.Handled = true;
    }

    // --- Ruler: local scrub (ignored while a real hardware playback is driving the
    // playhead — see SequencerViewModel.SetPlayheadFromPixel). ---

    private void Ruler_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (Vm is not { IsLiveTracking: false } vm) return;
        _scrubStartPlayheadMs = vm.PlayheadMs;
        _scrubbing = true;
        ((UIElement)sender).CaptureMouse();
        vm.SetPlayheadFromPixel(e.GetPosition(RulerCanvas).X);
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

    // --- Gesture library: plain click requires an explicitly armed track at the playhead;
    // dragging past a small threshold instead drops the gesture on a specific droid+time cell. ---

    private void GestureChip_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.DataContext is not GestureLibraryEntry entry) return;
        if (Vm is not { CanEditSequence: true }) { e.Handled = true; return; }
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
            if (!ExceedsDragThreshold(_chipDownPos, pos)) return;
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
            // Plain click never guesses a target. A direct drag remains target-explicit.
            if (Vm?.InsertGestureCommand.CanExecute(_chipAnimId) == true)
                Vm.InsertGestureCommand.Execute(_chipAnimId);
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
