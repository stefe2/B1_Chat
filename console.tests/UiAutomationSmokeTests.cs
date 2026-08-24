using System.Linq;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;

namespace b1_chat_console.Tests;

// SEQ-H06: real UI-automation smoke tests, driving the compiled b1-chat-console.exe (not the
// static-XAML-inspection style of MainWindowInteractionTests/TooltipCoverageTests). See
// UiAutomationFixture.cs for the shared-instance harness, the "why" behind its focus/scroll
// workarounds, and the hardware-safety note that scopes every test here to local document edits.
//
// The acceptance list in docs/SEQUENCER-HARDENING.md (SEQ-H06) is: drag/capture loss, Snap, Fit,
// arming, broadcast confirmation, disabled controls, preflight navigation, inspector, context
// menus, and keyboard accessibility. This file covers, with real passing automated runs:
//   - app launch / main window identity
//   - disabled controls (Undo/Redo start disabled, become enabled after an edit, and Undo
//     restores the disabled state)
//   - Snap (a real bound toggle, verified round-trip)
//   - context menus (right-click a real inserted gesture clip; Duplicate/Delete present)
//   - keyboard accessibility (Delete removes the selected clip via a real key press)
// Deliberately not attempted here — see the class remarks below each area for why.
[Collection("UI Automation")]
public sealed class UiAutomationSmokeTests
{
    private readonly UiAppFixture _fixture;

    public UiAutomationSmokeTests(UiAppFixture fixture) => _fixture = fixture;

    [Fact]
    public void AppLaunches_MainWindowIsVisibleWithExpectedTitleAndClass()
    {
        var window = _fixture.MainWindow;

        Assert.Equal(ControlType.Window, window.ControlType);
        Assert.Contains("B1 Chat", window.Name);
        Assert.Equal("Window", window.Properties.ClassName.ValueOrDefault);
        Assert.True(window.Properties.IsEnabled.ValueOrDefault);
        Assert.False(window.Properties.IsOffscreen.ValueOrDefault);
    }

    [Fact]
    public void DisabledControls_UndoRedoStartDisabled_EnableAfterAnEdit_AndUndoRestoresBaseline()
    {
        _fixture.ResetToCleanNewScene();

        var undo = FindButton("Undo");
        var redo = FindButton("Redo");
        Assert.False(undo.Properties.IsEnabled.ValueOrDefault);
        Assert.False(redo.Properties.IsEnabled.ValueOrDefault);

        // Toggling Loop is a persistent, undoable document edit (SetSequenceLoop ->
        // ExecuteSequenceEdit) that requires no connection, hardware, or dialog — a clean,
        // deterministic way to dirty the document from document state alone, exactly as the task
        // asked for ("gated by document state, like a New/Open Scene making Play available, not
        // by serial connection").
        var loopToggle = _fixture.MainWindow.FindFirstDescendant(
            cf => cf.ByName("⟲ Loop").And(cf.ByControlType(ControlType.Button)))
            ?? throw new InvalidOperationException("Loop toggle not found.");
        loopToggle.Click();
        Thread.Sleep(300);

        undo = FindButton("Undo");
        Assert.True(undo.Properties.IsEnabled.ValueOrDefault);

        undo.Click();
        Thread.Sleep(300);

        undo = FindButton("Undo");
        redo = FindButton("Redo");
        Assert.False(undo.Properties.IsEnabled.ValueOrDefault);
        Assert.True(redo.Properties.IsEnabled.ValueOrDefault);
    }

    [Fact]
    public void Snap_CheckboxTogglesAndRoundTrips()
    {
        _fixture.ResetToCleanNewScene();

        var snap = _fixture.MainWindow.FindFirstDescendant(
            cf => cf.ByName("Snap").And(cf.ByControlType(ControlType.CheckBox)))
            ?? throw new InvalidOperationException("Snap checkbox not found.");
        var checkBox = snap.AsCheckBox();

        // SnapToGrid defaults to true (SequencerViewModel._snapToGrid = true).
        Assert.Equal(true, checkBox.IsChecked);

        checkBox.Toggle();
        Thread.Sleep(200);
        Assert.Equal(false, checkBox.IsChecked);

        checkBox.Toggle();
        Thread.Sleep(200);
        Assert.Equal(true, checkBox.IsChecked);
    }

    [Fact]
    public void ContextMenu_GestureClip_ShowsDuplicateAndDelete()
    {
        _fixture.ResetToCleanNewScene();
        _fixture.ArmBroadcastTrack();
        _fixture.ClickGestureChip("Center");

        var label = _fixture.GetSoleClipNameLabel();
        label.RightClick();
        Thread.Sleep(400);

        var menu = _fixture.FindOpenContextMenu();
        Assert.NotNull(menu);
        var itemNames = menu!.FindAllChildren()
            .Where(e => e.ControlType == ControlType.MenuItem)
            .Select(e => e.Name)
            .ToArray();
        Assert.Contains("Duplicate", itemNames);
        Assert.Contains("Delete", itemNames);

        Keyboard.Press(VirtualKeyShort.ESC);
        Thread.Sleep(200);
    }

    [Fact]
    public void KeyboardAccessibility_DeleteKeyRemovesTheSelectedGestureClip()
    {
        _fixture.ResetToCleanNewScene();
        _fixture.ArmBroadcastTrack();
        _fixture.ClickGestureChip("Center");
        Assert.Single(_fixture.FindStepItems());

        // Insertion already leaves the new clip selected (SequencerViewModel.InsertGestureAt sets
        // SelectedStep), but keyboard focus is still wherever it was before (clicking a plain
        // Border does not move WPF keyboard focus). Move it onto the timeline's own ScrollViewer
        // so the Delete shortcut's PreviewKeyDown handler actually receives the key.
        _fixture.FocusTimelineScrollArea();

        Keyboard.Press(VirtualKeyShort.DELETE);
        Thread.Sleep(400);

        Assert.Empty(_fixture.FindStepItems());
    }

    private AutomationElement FindButton(string name) =>
        _fixture.MainWindow.FindFirstDescendant(cf => cf.ByName(name).And(cf.ByControlType(ControlType.Button)))
        ?? throw new InvalidOperationException($"Button '{name}' not found.");

    // --- Deliberately not covered here -----------------------------------------------------
    //
    // Fit: no assertion beyond "it's clickable" would be meaningful without pinning the current
    // zoom-fit formula as an implementation detail, which is brittle; left for a future test once
    // that formula is treated as a stable contract.
    //
    // Drag / capture loss: simulating a real mouse-drag-then-lose-capture sequence reliably
    // through synthetic input, without flaking on timing, is materially harder than the
    // click-based interactions above and was out of scope for this first automation pass.
    //
    // Arming (explicit Edit/Ready/Armed/Playing lifecycle): SEQ-A08 in SEQUENCER-HARDENING.md
    // records this as deliberately deferred — "the current editor deliberately keeps Play direct
    // and Preflight advisory" — so there is no such lifecycle concept in the shipped Sequencer to
    // test yet. (Note: "arm a target track" — clicking a track row — is a different, already-
    // implemented concept and IS exercised above via ArmBroadcastTrack.)
    //
    // Broadcast confirmation, Play/Restart/Stop, SAFE/E-STOP, preflight navigation: all of these
    // either dispatch, or sit one click away from dispatching, a real anim/audio/servo command.
    // This machine may have a live B1 droid fleet connected over USB serial (the console
    // auto-reconnects on launch), so exercising them here risks real hardware motion. Left for a
    // hardware-gated manual/bench pass (see SEQ-H07), not this smoke suite.
    //
    // Inspector: no dedicated inspector panel/AutomationId was confirmed reachable in the probed
    // automation tree within this pass's scope; left unimplemented rather than guessed at.
}
