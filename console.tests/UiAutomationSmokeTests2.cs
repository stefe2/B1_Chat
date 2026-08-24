using System.Linq;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;

namespace b1_chat_console.Tests;

// SEQ-H06 continued: second pass of real UI-automation coverage, driving the same shared
// compiled-exe instance as UiAutomationSmokeTests.cs (see UiAutomationFixture.cs for the harness
// and its safety note — nothing here dispatches Play/Restart/Stop/SAFE/E-STOP, a real OTA/flash,
// or a real calibration command to hardware). Organized by the five categories of this pass:
//   A — the ComboBox ToString() regression class (GESTURE / TARGET TRACK closed-selection text)
//   B — previously-skipped SEQ-H06 items, reconsidered (Fit, Copy/Paste, keyboard Undo/Redo,
//       Drag, Preflight)
//   C — first-time coverage of the Firmware, Mesh Topology, Scene Library and Help panels
//   D — regression guards for the rich-tooltip and overlapping-clip-zorder bugs fixed this session
//   E — real (not assumed) disabled/enabled contracts for Delete/Duplicate/Regenerate/Connect/Save/Export
[Collection("UI Automation")]
public sealed class UiAutomationSmokeTests2
{
    private readonly UiAppFixture _fixture;

    public UiAutomationSmokeTests2(UiAppFixture fixture) => _fixture = fixture;

    // =====================================================================================
    // Category A — ComboBox ToString() regression class
    // =====================================================================================
    //
    // A live FlaUI/UIA3 read of the CLOSED box's rendered text turned out not to be reachable
    // for this control, confirmed empirically (not assumed) across five separate probes before
    // settling on the design below: ComboBox.Value throws PatternNotSupportedException (ValuePattern
    // is editable-ComboBox-only in WPF's automation peer); AutomationElement.Name and
    // LegacyIAccessible's Name/Value are all blank; FindAllDescendants returns zero elements while
    // collapsed — WPF's ComboBoxAutomationPeer.GetChildrenCore only ever exposes ComboBoxItem
    // peers, never the ControlTemplate's own "ContentSite" ContentPresenter that actually renders
    // the closed selection (Content="{TemplateBinding SelectionBoxItem}", no ContentTemplate,
    // Themes/Effects.xaml's DarkComboBoxStyle); and even AutomationElement.FromPoint at
    // ContentSite's own screen coordinates resolves no deeper than the ComboBox root, since
    // ContentSite is IsHitTestVisible="False". Screenshot OCR could technically reach it but
    // trades a real automation check for a font-rendering-dependent one — exactly the kind of
    // flaky test this pass was told not to force.
    //
    // The regression is still guarded, just at the layer that's actually reachable and
    // deterministic: SequenceStepModelTests.cs now asserts GestureLibraryEntry.ToString() and
    // TimelineTrack.ToString() return the display name (the exact mechanism ContentSite's
    // default-Content-to-string fallback depends on). The two tests below cover the rest of the
    // same risk class that genuinely IS UIA-observable: that the combo is wired to real,
    // correctly-identified objects end to end — the dropdown lists real gesture/track names (never
    // a type name), and the live SelectionItem pattern reports the correct one actually selected.

    [Fact]
    public void GestureCombo_IsWiredToRealGestureObjects_DropdownAndSelectionNeverShowATypeName()
    {
        _fixture.ResetToCleanNewScene();
        _fixture.ArmBroadcastTrack();
        _fixture.ClickGestureChip("Center");

        var combo = _fixture.MainWindow.FindFirstDescendant(cf => cf.ByAutomationId("GestureComboBox"))
            ?? throw new InvalidOperationException("GESTURE combo not found.");

        AssertComboIsWiredToRealNamedSelection(combo, "Center", "GestureLibraryEntry");
    }

    [Fact]
    public void TargetTrackCombo_IsWiredToRealTrackObjects_DropdownAndSelectionNeverShowATypeName()
    {
        _fixture.ResetToCleanNewScene();
        _fixture.ArmBroadcastTrack();
        _fixture.ClickGestureChip("Center");

        var combo = _fixture.MainWindow.FindFirstDescendant(cf => cf.ByAutomationId("TargetTrackComboBox"))
            ?? throw new InvalidOperationException("TARGET TRACK combo not found.");

        AssertComboIsWiredToRealNamedSelection(combo, "All droids", "TimelineTrack");
    }

    // Calibration panel's DROID combo: deliberately NOT exercised here, even though it is the
    // same ToString() risk class. That combo only reaches a real item once the Calibration
    // window is opened for a specific live Droid row (DroidsCardView's per-row ⚙ button; see
    // ViewModels/DroidsViewModel.cs OpenCalibrationRequested), and selecting a Droid there
    // immediately fires ProtocolClient.RequestCalib(id) — a real serial request sent to
    // hardware. This dev machine may have a live fleet attached, and every slider/Goto button
    // in that panel also sends a real Preview/SetCalib command the instant it's touched. Droid
    // already carries the same ToString() override the Gesture/Track fix used (confirmed this
    // session, no fix was needed there), so the regression risk is already covered structurally;
    // automating a live-hardware-request just to re-confirm it wasn't judged worth the risk.

    /// <summary>
    /// Proves a DarkComboBoxStyle combo is genuinely wired to real, correctly-named objects:
    /// the live Selection pattern reports the expected item as selected, and no dropdown entry
    /// (nor the selected one) ever renders as a raw type name. This is the reachable subset of
    /// the ComboBox ToString() regression class — see the Category A remarks above for what
    /// isn't reachable and why.
    /// </summary>
    private static void AssertComboIsWiredToRealNamedSelection(
        AutomationElement combo, string expectedSelectedName, string forbiddenTypeFragment)
    {
        var selected = combo.Patterns.Selection.Pattern.Selection.Value;
        Assert.Single(selected);
        var selectedName = selected[0].Name;
        Assert.Equal(expectedSelectedName, selectedName);
        Assert.DoesNotContain(forbiddenTypeFragment, selectedName);
        Assert.DoesNotContain("b1_chat_console", selectedName);

        // Every candidate in the dropdown is a real display name too, not a type name — proves
        // ItemsSource/DisplayMemberPath wiring is healthy end to end, not just for this one item.
        combo.Patterns.ExpandCollapse.Pattern.Expand();
        Thread.Sleep(300);
        var items = combo.FindAllDescendants(cf => cf.ByControlType(ControlType.ListItem))
            .Select(i => i.Name)
            .ToArray();
        Assert.NotEmpty(items);
        Assert.All(items, n => Assert.DoesNotContain(forbiddenTypeFragment, n ?? ""));
        Assert.All(items, n => Assert.DoesNotContain("b1_chat_console", n ?? ""));
        combo.Patterns.ExpandCollapse.Pattern.Collapse();
        Thread.Sleep(200);
    }

    // =====================================================================================
    // Category B — previously-skipped SEQ-H06 items, reconsidered
    // =====================================================================================

    [Fact]
    public void Fit_ChangesZoomFromAKnownStartingState()
    {
        _fixture.ResetToCleanNewScene();
        _fixture.ArmBroadcastTrack();
        _fixture.ClickGestureChip("Nod"); // 1400ms finite gesture — a distinctive, non-zero content span.

        var slider = _fixture.MainWindow.FindFirstDescendant(cf => cf.ByControlType(ControlType.Slider))
            ?.AsSlider() ?? throw new InvalidOperationException("Zoom slider not found.");
        slider.Value = slider.Minimum; // known starting state (20 px/s), regardless of any prior test's zoom.
        Thread.Sleep(200);
        var before = slider.Value;

        var fit = _fixture.MainWindow.FindFirstDescendant(
            cf => cf.ByName("Fit").And(cf.ByControlType(ControlType.Button)))
            ?? throw new InvalidOperationException("'Fit' button not found.");
        fit.Click();
        Thread.Sleep(300);

        var after = slider.Value;
        Assert.NotEqual(before, after);
        // For a ~1.4s clip in a window this wide, Fit's viewportPx/totalSec always clamps well
        // above the minimum — a real, visible zoom-out-to-fit change, not a formula pin.
        Assert.True(after > before);
    }

    [Fact]
    public void CopyPaste_KeyboardShortcuts_CreateAnOffsetDuplicate()
    {
        _fixture.ResetToCleanNewScene();
        _fixture.ArmBroadcastTrack();
        _fixture.ClickGestureChip("Center");
        Assert.Single(_fixture.FindStepItems());

        _fixture.FocusTimelineScrollArea();
        // Re-select the inserted clip: focusing the ScrollArea itself does not clear selection
        // (established by the existing Delete-key test), but re-arm defensively via the label.
        var label = _fixture.GetSoleClipNameLabel();
        label.Click();
        Thread.Sleep(200);
        _fixture.FocusTimelineScrollArea();

        var originalStart = ReadSelectedStepStartMsText();

        Keyboard.TypeSimultaneously(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_C);
        Thread.Sleep(300);
        Keyboard.TypeSimultaneously(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_V);
        Thread.Sleep(400);

        var steps = _fixture.FindStepItems();
        Assert.Equal(2, steps.Length);

        // Paste selects the new clone (SequencerViewModel.PasteStep), so the inspector's START
        // pill now reflects it: original StartMs + 200ms, per SEQUENCER-BEHAVIOR.md's "Timeline
        // clip editing" (Paste reproduces Duplicate's own +200ms placement exactly).
        var pastedStart = ReadSelectedStepStartMsText();
        Assert.Equal(originalStart + 200, pastedStart);

        // Both clips are the same gesture (Copy/Paste never changes AnimId).
        var labels = steps
            .Select(s => s.FindFirstDescendant(cf => cf.ByControlType(ControlType.Text))?.Name)
            .ToArray();
        Assert.All(labels, l => Assert.Equal("Center", l));
    }

    [Fact]
    public void UndoRedo_KeyboardShortcuts_MirrorTheButtonBasedBehavior()
    {
        _fixture.ResetToCleanNewScene();

        var loopToggle = _fixture.MainWindow.FindFirstDescendant(
            cf => cf.ByName("⟲ Loop").And(cf.ByControlType(ControlType.Button)))
            ?? throw new InvalidOperationException("Loop toggle not found.");
        loopToggle.Click();
        Thread.Sleep(300);

        var undo = FindButton("Undo");
        var redo = FindButton("Redo");
        Assert.True(undo.Properties.IsEnabled.ValueOrDefault);
        Assert.False(redo.Properties.IsEnabled.ValueOrDefault);

        // Buttons themselves are excluded from the shortcut handler's focus check
        // (Keyboard.FocusedElement is ... or ButtonBase), so move focus onto the timeline's own
        // ScrollViewer first, exactly like the existing Delete-key keyboard test.
        _fixture.FocusTimelineScrollArea();

        Keyboard.TypeSimultaneously(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_Z);
        Thread.Sleep(300);
        undo = FindButton("Undo");
        redo = FindButton("Redo");
        Assert.False(undo.Properties.IsEnabled.ValueOrDefault);
        Assert.True(redo.Properties.IsEnabled.ValueOrDefault);

        _fixture.FocusTimelineScrollArea();
        Keyboard.TypeSimultaneously(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_Y);
        Thread.Sleep(300);
        undo = FindButton("Undo");
        redo = FindButton("Redo");
        Assert.True(undo.Properties.IsEnabled.ValueOrDefault);
        Assert.False(redo.Properties.IsEnabled.ValueOrDefault);
    }

    [Fact]
    public void Preflight_TogglesThePanelOpenAndClosed()
    {
        _fixture.ResetToCleanNewScene();

        var preflight = _fixture.MainWindow.FindFirstDescendant(
            cf => cf.ByName("Preflight").And(cf.ByControlType(ControlType.Button)))
            ?? throw new InvalidOperationException("'Preflight' button not found.");

        Assert.Null(_fixture.MainWindow.FindFirstDescendant(cf => cf.ByName("PREFLIGHT")));

        preflight.Click();
        Thread.Sleep(400);
        Assert.NotNull(_fixture.MainWindow.FindFirstDescendant(cf => cf.ByName("PREFLIGHT")));

        preflight.Click();
        Thread.Sleep(400);
        Assert.Null(_fixture.MainWindow.FindFirstDescendant(cf => cf.ByName("PREFLIGHT")));
    }

    [Fact]
    public void Drag_GestureClip_MovesStartMsToApproximatelyTheDroppedPosition_SnappedToGrid()
    {
        _fixture.ResetToCleanNewScene();
        _fixture.ArmBroadcastTrack();
        _fixture.ClickGestureChip("Nod");

        // Zoom is ambient ViewModel state shared across the whole app session, not reset by
        // ResetToCleanNewScene — a preceding test (e.g. Fit, which deliberately drives it to its
        // 20 px/s minimum) can leave clips only a few pixels wide, well below the precision a
        // synthetic mouse drag needs. Pin a known, generous value here instead of trusting
        // whatever an earlier test left behind.
        var slider = _fixture.MainWindow.FindFirstDescendant(cf => cf.ByControlType(ControlType.Slider))
            ?.AsSlider() ?? throw new InvalidOperationException("Zoom slider not found.");
        slider.Value = 150;
        Thread.Sleep(500); // let WPF's re-layout at the new zoom settle before reading positions
        var pxPerSecond = slider.Value;
        var pxPerMs = pxPerSecond / 1000.0;

        var label = _fixture.GetSoleClipNameLabel();
        var startRect = label.BoundingRectangle;
        var startPoint = new System.Drawing.Point(startRect.X + 5, startRect.Y + startRect.Height / 2);
        const int dragPixels = 300;
        var endPoint = new System.Drawing.Point(startPoint.X + dragPixels, startPoint.Y);

        Mouse.Position = startPoint;
        Thread.Sleep(150);
        Mouse.Down(MouseButton.Left);
        Thread.Sleep(100);
        // Several intermediate moves: WPF needs real MouseMove events past the 5px drag
        // threshold (SequenceTimelineView.xaml.cs ExceedsDragThreshold) before it starts
        // treating this as a drag rather than a click.
        for (var i = 1; i <= 8; i++)
        {
            var x = startPoint.X + dragPixels * i / 8;
            Mouse.Position = new System.Drawing.Point(x, startPoint.Y);
            Thread.Sleep(80);
        }
        Thread.Sleep(200);
        Mouse.Up(MouseButton.Left);
        Thread.Sleep(500);

        // dragPixels is a real physical-screen-pixel distance (it drives Mouse.Position), while
        // pxPerMs is WPF's own logical (96-DPI) px/ms — the two only match 1:1 at 100% display
        // scaling. Convert through the real DPI scale so this holds on a scaled display too.
        var expectedDeltaMs = dragPixels / (pxPerMs * PhysicalDpiScale());

        var actualStartMs = ReadSelectedStepStartMsText();

        // "Roughly" per the task: a synthetic drag's intermediate MouseMove events can be
        // partially coalesced/delayed under heavy concurrent system load (observed: full test
        // suite runs alongside ~350 other parallel unit tests competing for CPU), so this
        // deliberately doesn't pin a tight range around the requested distance — it proves real,
        // rightward, grid-snapped motion occurred and landed in the right neighborhood, without
        // being fragile to how many of the intermediate move events actually registered.
        Assert.True(actualStartMs > 0, $"Expected the clip to have moved right from StartMs=0, got {actualStartMs}ms.");
        Assert.True(actualStartMs <= expectedDeltaMs + 100,
            $"Expected roughly {expectedDeltaMs}ms of rightward motion (drag={dragPixels}px @ {pxPerSecond}px/s), got {actualStartMs}ms — overshot or wrong direction.");
        Assert.Equal(0, actualStartMs % 100); // snapped to the 100ms grid (SnapToGrid defaults on)
    }

    // =====================================================================================
    // Category C — first-time coverage of new panels
    // =====================================================================================

    [Fact]
    public void FirmwarePanel_PortComboIsPresent_AndLocalOnlyControlsAreSafeToClick()
    {
        // FirmwareWindow (like every owned window in this app — confirmed while diagnosing the
        // ResetToCleanNewScene bug and reconfirmed for Help below) does not appear as a separate
        // entry in App.GetAllTopLevelWindows() or even Automation.GetDesktop() in this
        // environment; its content is exposed as part of MainWindow's own automation subtree
        // instead. So this test interacts with it entirely through MainWindow, and confirms
        // "opened" via a before/after control-count jump rather than finding a window object.
        var beforeCombos = _fixture.MainWindow.FindAllDescendants(cf => cf.ByControlType(ControlType.ComboBox)).Length;

        var firmwareButton = FindAncestorButton("Firmware…")
            ?? throw new InvalidOperationException("Firmware… header button not found.");
        firmwareButton.Click();
        Thread.Sleep(600);

        var afterCombos = _fixture.MainWindow.FindAllDescendants(cf => cf.ByControlType(ControlType.ComboBox)).Length;
        // The main header's own port combo already exists baseline; Firmware's FLASH PORT combo
        // is a second, additional one once the panel opens.
        Assert.True(afterCombos > beforeCombos, $"Expected an additional ComboBox after opening Firmware (before={beforeCombos}, after={afterCombos}).");

        // Local-only, no hardware/network interaction: Rescan just re-enumerates COM ports,
        // the role toggles and "Advanced options" only flip local ViewModel flags (see
        // FirmwareViewModel.SelectMasterRole/SelectSlaveRole/ToggleAdvanced — none of them touch
        // _flash, _update, or the wire).
        var rescan = _fixture.MainWindow.FindFirstDescendant(
            cf => cf.ByName("Rescan").And(cf.ByControlType(ControlType.Button)));
        Assert.NotNull(rescan);
        rescan!.Click();
        Thread.Sleep(200);

        var slaveRole = _fixture.MainWindow.FindFirstDescendant(
            cf => cf.ByName("Slave").And(cf.ByControlType(ControlType.Button)));
        slaveRole?.Click();
        Thread.Sleep(150);
        var masterRole = _fixture.MainWindow.FindFirstDescendant(
            cf => cf.ByName("Master").And(cf.ByControlType(ControlType.Button)));
        masterRole?.Click();
        Thread.Sleep(150);

        var advanced = _fixture.MainWindow.FindFirstDescendant(
            cf => cf.ByName("Advanced options").And(cf.ByControlType(ControlType.Button)));
        Assert.NotNull(advanced);
        advanced!.Click();
        Thread.Sleep(200);
        advanced.Click(); // toggle back off
        Thread.Sleep(200);

        // Flash never becomes enabled without a verified binary (CanFlash defaults false and
        // this test never calls PrepareFromGithub/PickBin) — confirms it's not one accidental
        // click away from a real flash. FlashLabel defaults to "Flash" (FirmwareViewModel).
        var flash = _fixture.MainWindow.FindFirstDescendant(cf => cf.ByControlType(ControlType.Button)
            .And(cf.ByName("Flash")));
        if (flash != null) Assert.False(flash.Properties.IsEnabled.ValueOrDefault);

        CloseOwnedWindowByTitle("Firmware — B1 Chat");
        Thread.Sleep(400);
        var closedCombos = _fixture.MainWindow.FindAllDescendants(cf => cf.ByControlType(ControlType.ComboBox)).Length;
        Assert.Equal(beforeCombos, closedCombos);
    }

    [Fact]
    public void MeshTopologyPanel_RendersStaticElementsRegardlessOfLiveFleetState()
    {
        // Embedded directly on MainWindow (not a separate window) — safe to inspect with or
        // without a live droid fleet connected, since it's read-only telemetry rendering.
        var title = _fixture.MainWindow.FindFirstDescendant(cf => cf.ByName("MESH TOPOLOGY"));
        Assert.NotNull(title);

        var masterLegend = _fixture.MainWindow.FindFirstDescendant(cf => cf.ByName("Master")
            .And(cf.ByControlType(ControlType.Text)));
        var slaveLegend = _fixture.MainWindow.FindFirstDescendant(cf => cf.ByName("Slave")
            .And(cf.ByControlType(ControlType.Text)));
        Assert.NotNull(masterLegend);
        Assert.NotNull(slaveLegend);

        // Either the empty-state message or the radar canvas is present — never both, never
        // neither — regardless of whether this machine currently has a live fleet. The link-count
        // TextBlock is two <Run>s ("{count}" + " direct links"), which UIA exposes as one combined
        // Name (e.g. "1 direct links") — Runs aren't separate automation elements, so this must be
        // a Contains search, not an exact ByName match.
        var emptyState = _fixture.MainWindow.FindFirstDescendant(cf => cf.ByName("No droid detected."));
        var linkCountLabel = _fixture.MainWindow.FindAllDescendants(cf => cf.ByControlType(ControlType.Text))
            .FirstOrDefault(t => (t.Name ?? "").Contains("direct link"));
        Assert.True(emptyState != null || linkCountLabel != null);
    }

    // SceneBrowserWindow/SceneNameWindow, like every owned window this app creates (see
    // UiAutomationFixture.cs's ResetToCleanNewScene remarks for the full empirical finding), have
    // their content exposed by UIA as part of MainWindow's own automation subtree rather than as
    // a separate entry in App.GetAllTopLevelWindows() — so these two tests search MainWindow
    // directly instead of the top-level window list.

    [Fact]
    public void SceneLibraryBrowser_OpensReadOnlyListing_AndCancelsWithoutChangingTheDocument()
    {
        _fixture.ResetToCleanNewScene();

        var openButton = FindButton("Open…");
        openButton.Click();
        Thread.Sleep(500);

        Assert.NotNull(_fixture.MainWindow.FindFirstDescendant(cf => cf.ByName("OPEN SCENE")));

        // Cancel without selecting/opening anything: RefreshLibrary() only reads the real Local
        // Library directory to populate the list — Cancel (IsCancel=True) never calls Load,
        // Delete or any write path.
        var cancel = _fixture.MainWindow.FindFirstDescendant(
            cf => cf.ByName("Cancel").And(cf.ByControlType(ControlType.Button)))
            ?? throw new InvalidOperationException("Scene browser Cancel button not found.");
        cancel.Click();
        Thread.Sleep(300);

        Assert.Null(_fixture.MainWindow.FindFirstDescendant(cf => cf.ByName("OPEN SCENE")));
    }

    [Fact]
    public void SceneNameDialog_AcceptsTypedInput_ThenCancelsWithoutSaving()
    {
        _fixture.ResetToCleanNewScene();

        // Only "Save As…" (context menu) always prompts unconditionally (SaveAsNewScene(true)).
        // The plain "Save" button on a fresh untitled document silently saves to the real Local
        // Library with no dialog at all (SaveScene -> SaveAsNewScene(promptAlways: Name blank))
        // — never click it in an automated test that must not write real library files.
        var more = _fixture.MainWindow.FindFirstDescendant(
            cf => cf.ByAutomationId("SceneMoreButton"))
            ?? throw new InvalidOperationException("Scene '⋯' button not found.");
        more.Click();
        Thread.Sleep(300);
        var menu = _fixture.FindOpenContextMenu()
            ?? throw new InvalidOperationException("Scene '⋯' context menu did not open.");
        var saveAs = menu.FindAllDescendants(cf => cf.ByControlType(ControlType.MenuItem))
            .FirstOrDefault(m => (m.Name ?? "").StartsWith("Save As"))
            ?? throw new InvalidOperationException("'Save As…' menu item not found.");
        saveAs.Click();
        Thread.Sleep(500);

        Assert.NotNull(_fixture.MainWindow.FindFirstDescendant(cf => cf.ByName("SAVE SCENE AS")));

        var nameBox = _fixture.MainWindow.FindFirstDescendant(cf => cf.ByAutomationId("NameBox"))
            ?? throw new InvalidOperationException("Scene name TextBox not found.");
        nameBox.AsTextBox().Text = "SEQ-H06 UI test throwaway name";
        Thread.Sleep(200);
        Assert.Equal("SEQ-H06 UI test throwaway name", nameBox.AsTextBox().Text);

        // Cancel (IsCancel=True): SaveSceneAs's PromptForSceneName returns null, SaveAsNewScene
        // returns immediately — no write to the real Local Library.
        var cancel = _fixture.MainWindow.FindFirstDescendant(
            cf => cf.ByName("Cancel").And(cf.ByControlType(ControlType.Button)))
            ?? throw new InvalidOperationException("Scene name dialog Cancel button not found.");
        cancel.Click();
        Thread.Sleep(300);

        Assert.Null(_fixture.MainWindow.FindFirstDescendant(cf => cf.ByName("SAVE SCENE AS")));
    }

    [Fact]
    public void HelpWindow_OpensShowsContent_AndCloses()
    {
        // Same environment quirk as FirmwareWindow above: HelpWindow's content is exposed as
        // part of MainWindow's own automation subtree (confirmed: opening it added dozens of
        // Button/Text descendants directly under MainWindow, and neither
        // App.GetAllTopLevelWindows() nor Automation.GetDesktop() ever listed a separate "Help"
        // window) — so this is confirmed via a before/after content jump, not a window object.
        var beforeButtons = _fixture.MainWindow.FindAllDescendants(cf => cf.ByControlType(ControlType.Button)).Length;
        var beforeTexts = _fixture.MainWindow.FindAllDescendants(cf => cf.ByControlType(ControlType.Text)).Length;

        var helpButton = _fixture.MainWindow.FindFirstDescendant(cf => cf.ByName("Help")
            .And(cf.ByControlType(ControlType.Button)))
            ?? throw new InvalidOperationException("Help button not found.");
        helpButton.Click();
        Thread.Sleep(600);

        var afterButtons = _fixture.MainWindow.FindAllDescendants(cf => cf.ByControlType(ControlType.Button)).Length;
        var afterTexts = _fixture.MainWindow.FindAllDescendants(cf => cf.ByControlType(ControlType.Text)).Length;
        // Sidebar table-of-contents page buttons plus rendered FlowDocument content: both counts
        // should grow substantially.
        Assert.True(afterButtons > beforeButtons, $"Expected more buttons after opening Help (before={beforeButtons}, after={afterButtons}).");
        Assert.True(afterTexts > beforeTexts, $"Expected more text content after opening Help (before={beforeTexts}, after={afterTexts}).");

        CloseOwnedWindowByTitle("Help — B1 Chat");
        Thread.Sleep(400);
        var closedButtons = _fixture.MainWindow.FindAllDescendants(cf => cf.ByControlType(ControlType.Button)).Length;
        Assert.Equal(beforeButtons, closedButtons);
    }

    // Calibration window: deliberately NOT opened in this pass. Unlike Firmware/Help, it has no
    // unconditional entry point — it only opens pre-targeted at a specific live Droid row
    // (DroidsCardView's ⚙ button -> DroidsViewModel.OpenCalibrationRequested(Droid)), which
    // requires a real connected droid to exist in the Droids collection at all. On a machine
    // with no fleet attached there is nothing to open; on a machine with one attached, opening
    // it immediately issues a real RequestCalib serial command and every slider/Goto button
    // sends a real Preview/SetCalib movement command the instant it's touched (see
    // CalibrationViewModel.cs). There's no safe, hardware-state-independent path to this panel
    // through the real UI, so it's left uncovered rather than forced.

    // =====================================================================================
    // Category D — regression guards for bugs fixed this session
    // =====================================================================================

    [Fact]
    public void RichTooltip_GestureClip_RendersItsActualTextNotAStringifiedTypeName()
    {
        _fixture.ResetToCleanNewScene();
        _fixture.ArmBroadcastTrack();
        _fixture.ClickGestureChip("Center");

        var label = _fixture.GetSoleClipNameLabel();
        var rect = label.BoundingRectangle;
        // Move away first so the subsequent move is a genuine MouseEnter, then hover and wait
        // out ToolTipService.InitialShowDelay.
        Mouse.Position = new System.Drawing.Point(rect.X - 60, rect.Y - 60);
        Thread.Sleep(150);
        Mouse.Position = new System.Drawing.Point(rect.X + 2, rect.Y + rect.Height / 2);
        Thread.Sleep(1200);

        var tooltip = _fixture.Automation.GetDesktop().FindFirstDescendant(
            cf => cf.ByControlType(ControlType.ToolTip));
        Assert.NotNull(tooltip);

        var texts = tooltip!.FindAllDescendants(cf => cf.ByControlType(ControlType.Text))
            .Select(t => t.Name ?? "")
            .ToArray();
        var combined = string.Join(" ", texts);

        Assert.Contains("Click to inspect this gesture", combined);
        Assert.DoesNotContain("System.Windows.Controls.StackPanel", combined);
        Assert.DoesNotContain("b1_chat_console.Models", combined);

        // Move away again so the tooltip doesn't linger and interfere with later tests.
        Mouse.Position = new System.Drawing.Point(rect.X - 60, rect.Y - 60);
        Thread.Sleep(200);
    }

    [Fact]
    public void OverlappingClips_TheShorterTopmostClipReceivesTheClick()
    {
        _fixture.ResetToCleanNewScene();
        _fixture.ArmBroadcastTrack();

        // Zoom is ambient ViewModel state shared across the whole app session, not reset by
        // ResetToCleanNewScene — a preceding test (e.g. Fit, which deliberately drives it to its
        // 20 px/s minimum) can leave clips only a few pixels wide, well below the precision the
        // pixel-math click points below need. Pin a known, generous value up front instead of
        // trusting whatever an earlier test left behind.
        var slider = _fixture.MainWindow.FindFirstDescendant(cf => cf.ByControlType(ControlType.Slider))
            ?.AsSlider() ?? throw new InvalidOperationException("Zoom slider not found.");
        slider.Value = 150;
        Thread.Sleep(500); // let WPF's re-layout at the new zoom settle before reading positions
        var pxPerMs = slider.Value / 1000.0;

        // Both inserted at playhead 0 (nothing moves the playhead between the two inserts), so
        // they start at the identical StartMs and visually overlap from x=0: Nod (1400ms) is the
        // wider base, "Look right" (750ms) is the narrower overlay — exactly the
        // shorter-renders-on-top/shorter-wins-the-click case DurationToZIndexConverter and
        // PickStepAt (SequenceTimelineView.xaml.cs) exist for.
        ClickGestureChipVerified("Nod");
        Thread.Sleep(200); // settle between the two clicks under concurrent system load
        ClickGestureChipVerified("Look right");
        Thread.Sleep(300); // extra settle for layout under concurrent system load
        var steps = _fixture.FindStepItems();
        Assert.Equal(2, steps.Length);

        // The per-item DataTemplate root is a bare Canvas (position via Canvas.Left/Top on its
        // Border child) with no explicit Width/Height, so the ItemsControl-generated container's
        // own BoundingRectangle does NOT span the visible clip — only its Text descendants (the
        // gesture-name/duration labels) have real, reliable rectangles. Compute each click point
        // from the label's position plus known px-per-ms instead.

        var lookRightLabel = FindStepLabel(steps, "Look right");
        var lrRect = lookRightLabel.BoundingRectangle;
        var y = lrRect.Y + lrRect.Height / 2;
        // Both clips start at StartMs=0 (nothing moved the playhead between inserts), so the
        // label's left edge is ~the clip's own left edge (Border Padding "8,2") for both clips.
        var clipLeftX = lrRect.X - 8;

        // A point well inside BOTH spans (near the shared left edge, inside Look right's 750ms).
        var overlapPoint = new System.Drawing.Point(clipLeftX + 20, y);
        Mouse.Click(overlapPoint);
        Thread.Sleep(300);

        var gestureCombo = _fixture.MainWindow.FindFirstDescendant(cf => cf.ByAutomationId("GestureComboBox"))
            ?? throw new InvalidOperationException("GESTURE combo not found.");
        Assert.Equal("Look right", ReadComboSelectedName(gestureCombo));

        // Sanity: clicking past Look right's own 750ms extent but still inside Nod's 1400ms one
        // must fall back to selecting "Nod" — proving the geometry, not merely "always the
        // last-inserted item", drives the result. 1000ms sits safely between the two.
        //
        // clipLeftX is a real physical-screen-pixel coordinate (read from a FlaUI
        // BoundingRectangle), while pxPerMs is WPF's own logical (96-DPI) px/ms — the two only
        // match 1:1 at 100% display scaling. This mismatch was the actual cause of a real,
        // reproducible failure found this session on a scaled display: adding an un-scaled
        // logical distance to a physical-pixel base landed short of the intended 1000ms mark,
        // still inside Look right's own extent, and wrongly re-selected it instead of Nod.
        var nodOnlyX = clipLeftX + (int)(1000 * pxPerMs * PhysicalDpiScale());
        var nodOnlyPoint = new System.Drawing.Point(nodOnlyX, y);
        Mouse.Click(nodOnlyPoint);
        Thread.Sleep(300);
        gestureCombo = _fixture.MainWindow.FindFirstDescendant(cf => cf.ByAutomationId("GestureComboBox"))
            ?? throw new InvalidOperationException("GESTURE combo not found.");
        Assert.Equal("Nod", ReadComboSelectedName(gestureCombo));
    }

    // =====================================================================================
    // Category E — real (verified, not assumed) disabled/enabled contracts
    // =====================================================================================

    [Fact]
    public void DeleteDuplicateRegenerate_AreVisibilityGatedBySelection_NotDisabledByIt()
    {
        _fixture.ResetToCleanNewScene();

        // No selection: DeleteStepCommand/DuplicateStepCommand/RegenerateSeedCommand are all
        // gated only by CanEditSequence (transport lock), not by SelectedStep — the buttons
        // themselves live inside the "SELECTED CLIP" Border, whose own Visibility is Collapsed
        // via a DataTrigger on SelectedStep==null (SequenceTimelineView.xaml). So the real
        // contract is: the buttons don't exist at all yet, not "exist but disabled".
        Assert.Null(_fixture.MainWindow.FindFirstDescendant(
            cf => cf.ByName("Delete").And(cf.ByControlType(ControlType.Button))));
        Assert.Null(_fixture.MainWindow.FindFirstDescendant(
            cf => cf.ByName("Duplicate").And(cf.ByControlType(ControlType.Button))));

        _fixture.ArmBroadcastTrack();
        _fixture.ClickGestureChip("Nod"); // RequiresSeed:true — also exposes "Regenerate".

        var delete = FindButton("Delete");
        var duplicate = FindButton("Duplicate");
        var regenerate = FindButton("Regenerate");
        Assert.True(delete.Properties.IsEnabled.ValueOrDefault);
        Assert.True(duplicate.Properties.IsEnabled.ValueOrDefault);
        Assert.True(regenerate.Properties.IsEnabled.ValueOrDefault);
    }

    [Fact]
    public void ConnectButton_RealCanExecuteContract_NoGateOnSelectedPort()
    {
        // ConnectCommand ([RelayCommand] with no CanExecute) is unconditionally enabled — the
        // guard against an empty port is an internal no-op inside Connect(), not a WPF disabled
        // state. This machine may already be auto-connected to a real fleet on launch, in which
        // case the Connect button isn't even in the tree (Visibility is Invert(Connected)) —
        // cover both real states rather than assuming one.
        var connect = _fixture.MainWindow.FindFirstDescendant(
            cf => cf.ByName("Connect").And(cf.ByControlType(ControlType.Button)));
        if (connect != null)
        {
            Assert.True(connect.Properties.IsEnabled.ValueOrDefault);
            return;
        }

        var disconnect = _fixture.MainWindow.FindFirstDescendant(
            cf => cf.ByName("Disconnect").And(cf.ByControlType(ControlType.Button)))
            ?? throw new InvalidOperationException(
                "Neither 'Connect' nor 'Disconnect' found — unexpected header state.");
        Assert.True(disconnect.Properties.IsEnabled.ValueOrDefault);
    }

    [Fact]
    public void SaveExport_RealEnabledDisabledContract_GatedByTransportLockOnly_NotByContent()
    {
        _fixture.ResetToCleanNewScene();

        // Empty document (no Steps).
        var save = FindButton("Save");
        var export = _fixture.MainWindow.FindFirstDescendant(cf => cf.ByAutomationId("SceneMoreButton"))
            ?? throw new InvalidOperationException("Scene '⋯' button not found.");
        Assert.True(save.Properties.IsEnabled.ValueOrDefault);

        export.Click();
        Thread.Sleep(300);
        var menu = _fixture.FindOpenContextMenu()
            ?? throw new InvalidOperationException("Scene '⋯' context menu did not open.");
        var exportItem = menu.FindAllDescendants(cf => cf.ByControlType(ControlType.MenuItem))
            .FirstOrDefault(m => (m.Name ?? "").StartsWith("Export a copy"))
            ?? throw new InvalidOperationException("'Export a copy…' menu item not found.");
        // ExportCommand is a plain [RelayCommand] with no CanExecute at all — always enabled,
        // on an empty document included. Confirmed without invoking it (Click would open a real
        // native Save File dialog).
        Assert.True(exportItem.Properties.IsEnabled.ValueOrDefault);
        Keyboard.Press(VirtualKeyShort.ESC);
        Thread.Sleep(200);

        // Now populate the document and confirm Save's enabled state is unchanged — its
        // CanExecute (CanEditSequence) never looked at Steps.Count either way.
        _fixture.ArmBroadcastTrack();
        _fixture.ClickGestureChip("Center");
        save = FindButton("Save");
        Assert.True(save.Properties.IsEnabled.ValueOrDefault);
    }

    // --- helpers -----------------------------------------------------------------------

    private AutomationElement FindButton(string name) =>
        _fixture.MainWindow.FindFirstDescendant(cf => cf.ByName(name).And(cf.ByControlType(ControlType.Button)))
        ?? throw new InvalidOperationException($"Button '{name}' not found.");

    /// <summary>
    /// Finds a Button whose accessible Name isn't the plain text itself (e.g. a Button whose
    /// Content is a StackPanel/Canvas+TextBlock rather than a plain string, which WPF's default
    /// automation peer does not automatically summarize into a Name) by locating the matching
    /// Text descendant under MainWindow and walking up to its ancestor Button.
    /// </summary>
    private AutomationElement? FindAncestorButton(string innerText)
    {
        var text = _fixture.MainWindow.FindFirstDescendant(
            cf => cf.ByName(innerText).And(cf.ByControlType(ControlType.Text)));
        var current = text?.Parent;
        while (current != null)
        {
            if (current.ControlType == ControlType.Button) return current;
            current = current.Parent;
        }
        return null;
    }

    /// <summary>
    /// Closes a secondary owned window (Firmware, Help, …) by its exact window title, via a raw
    /// Win32 WM_CLOSE sent directly to that window's own HWND — found by real Win32 enumeration
    /// scoped to this app's process ID, not by FlaUI/UIA (these owned windows are not reachable
    /// through App.GetAllTopLevelWindows()/Automation.GetDesktop() in this environment; see the
    /// remarks on FirmwarePanel/HelpWindow tests above). Deliberately NOT Alt+F4: that depends on
    /// OS keyboard focus actually being on the intended window, and a focus mix-up under load was
    /// observed to send Alt+F4 to MainWindow instead and take the whole app down — WM_CLOSE
    /// targeted at a specific HWND has no such ambiguity.
    /// </summary>
    private void CloseOwnedWindowByTitle(string exactTitle)
    {
        var pid = (uint)_fixture.App.ProcessId;
        var found = IntPtr.Zero;
        Win32.EnumWindows((hWnd, _) =>
        {
            Win32.GetWindowThreadProcessId(hWnd, out var windowPid);
            if (windowPid != pid) return true;
            var sb = new System.Text.StringBuilder(256);
            Win32.GetWindowText(hWnd, sb, 256);
            if (sb.ToString() == exactTitle) { found = hWnd; return false; }
            return true;
        }, IntPtr.Zero);
        if (found == IntPtr.Zero)
            throw new InvalidOperationException($"No window titled '{exactTitle}' found for this process.");
        const uint WM_CLOSE = 0x0010;
        Win32.PostMessage(found, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
    }

    private static class Win32
    {
        public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        public static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Auto)]
        public static extern int GetWindowText(IntPtr hWnd, System.Text.StringBuilder lpString, int nMaxCount);

        [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Auto)]
        public static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        public static extern uint GetDpiForWindow(IntPtr hWnd);
    }

    /// <summary>
    /// WPF's own PxPerMs/PxPerSecond (and the zoom slider bound to it) operate in device-
    /// independent pixels (96 DPI logical units), while FlaUI's BoundingRectangle and Mouse
    /// coordinates operate in real physical screen pixels. On any display scaled above 100%
    /// those two pixel spaces are different sizes, so a distance computed in logical px/ms must
    /// be multiplied by this scale factor before it can be added to a physical-pixel screen
    /// coordinate (real root cause of a click landing short of its intended logical-ms target,
    /// found and fixed this session — see OverlappingClips's remarks for the concrete case).
    /// </summary>
    private double PhysicalDpiScale() =>
        Win32.GetDpiForWindow(new IntPtr(_fixture.MainWindow.Properties.NativeWindowHandle.ValueOrDefault)) / 96.0;

    /// <summary>
    /// Reads the currently-selected item's Name via UIA's Selection pattern (a data-level
    /// pattern that reports the real selection whether or not the dropdown is visually open —
    /// unlike the closed box's own rendered text, which isn't UIA-observable at all for this
    /// custom ControlTemplate; see the Category A remarks above).
    /// </summary>
    private static string ReadComboSelectedName(AutomationElement combo)
    {
        var selected = combo.Patterns.Selection.Pattern.Selection.Value;
        return selected.Length == 1
            ? selected[0].Name
            : throw new InvalidOperationException($"Expected exactly one selected item, found {selected.Length}.");
    }

    /// <summary>
    /// Clicks a gesture-library chip and verifies BY NAME that it actually landed (not merely
    /// that *some* clip count changed) — a plain count check can produce a false positive if an
    /// earlier click in the same test was itself silently missed and then retried, matching the
    /// expected count while inserting the wrong gesture entirely.
    ///
    /// Patiently polls for up to ~1.5s before concluding a click was missed, and only ever
    /// re-clicks when the step COUNT itself never moved during that whole window — i.e. only when
    /// nothing was inserted at all. A count that did increase but whose label read is momentarily
    /// stale (observed under the heaviest concurrent full-suite load, where UIA's own RPC channel
    /// and this app's data-binding refresh both compete for CPU) is a lagging-read situation, not
    /// a missed click, and must never trigger a re-click — doing so risks a genuine duplicate
    /// insert if the original click actually did land just after the count was first observed
    /// (this exact failure mode was seen and reverted during an earlier hardening pass).
    /// </summary>
    private void ClickGestureChipVerified(string gestureName)
    {
        var countBefore = _fixture.FindStepItems().Length;
        _fixture.ClickGestureChip(gestureName);

        bool Landed(out int count)
        {
            var steps = _fixture.FindStepItems();
            count = steps.Length;
            return steps.Any(s => s.FindFirstDescendant(cf => cf.ByControlType(ControlType.Text))?.Name == gestureName);
        }

        var reclicked = false;
        for (var attempt = 0; attempt < 10; attempt++)
        {
            Thread.Sleep(150);
            if (Landed(out var count)) return;
            // Only re-click once, and only once the count has proven the original click truly
            // never registered anything (never re-click just because the label hasn't caught up).
            if (!reclicked && count == countBefore && attempt >= 4)
            {
                reclicked = true;
                _fixture.ClickGestureChip(gestureName);
            }
        }
        throw new InvalidOperationException($"Gesture chip '{gestureName}' click did not land after retries.");
    }

    /// <summary>
    /// The gesture-name Text label inside a specific inserted clip's item container. Retries
    /// briefly: under heavy concurrent system load the label's data-bound text can lag a beat
    /// behind the item container's own appearance (observed rarely during a full 350+ test suite
    /// run; never in isolation or in this file's own 22-test batch).
    /// </summary>
    private static AutomationElement FindStepLabel(AutomationElement[] steps, string gestureName)
    {
        for (var i = 0; i < 5; i++)
        {
            foreach (var item in steps)
            {
                var label = item.FindFirstDescendant(cf => cf.ByControlType(ControlType.Text));
                if (label?.Name == gestureName) return label;
            }
            Thread.Sleep(200);
        }
        throw new InvalidOperationException($"No inserted clip found with gesture label '{gestureName}'.");
    }

    /// <summary>
    /// Reads the "SELECTED CLIP" inspector's START value pill (e.g. "200 ms") and returns the
    /// numeric millisecond value. Requires exactly one selected step.
    /// </summary>
    private int ReadSelectedStepStartMsText()
    {
        // START's own value pill has no AutomationId. It renders as "{StartMs} ms" and is the
        // first such "<n> ms" text in the inspector (DURATION's own value uses a "Xs / infinite"
        // style summary via DurationSummary, not a plain "<n> ms" pill — see
        // SequenceTimelineView.xaml), so the first match is unambiguous. A short retry-poll
        // absorbs both the rare case where the panel hasn't finished refreshing yet, and a raw
        // transient COMException from the UIA RPC channel itself — both observed occasionally
        // under the heaviest concurrent system load (running this file's tests alongside the
        // full ~350-test suite, where dozens of unrelated tests run truly in parallel).
        string?[] candidates = System.Array.Empty<string?>();
        for (var i = 0; i < 5; i++)
        {
            try
            {
                candidates = _fixture.MainWindow.FindAllDescendants(cf => cf.ByControlType(ControlType.Text))
                    .Select(e => e.Name)
                    .Where(n => n != null && n.EndsWith(" ms") && !n.Contains("Seed"))
                    .ToArray();
            }
            catch (System.Runtime.InteropServices.COMException)
            {
                candidates = System.Array.Empty<string?>();
            }
            if (candidates.Length > 0) break;
            Thread.Sleep(200);
        }
        if (candidates.Length == 0)
            throw new InvalidOperationException("No '<n> ms' value pill found — is a clip selected?");
        var text = candidates[0]!;
        var numeric = text.Substring(0, text.Length - " ms".Length);
        return int.Parse(numeric);
    }
}
