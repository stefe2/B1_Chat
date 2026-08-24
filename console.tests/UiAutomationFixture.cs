using System.Linq;
using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Input;
using FlaUI.UIA3;

namespace b1_chat_console.Tests;

// SEQ-H06: real UI-automation smoke tests, driving the compiled b1-chat-console.exe through
// FlaUI/UIA3 — the project's first automation of an actual running window, as opposed to the
// static-XAML-inspection style used by MainWindowInteractionTests/TooltipCoverageTests.
//
// WPF startup + UIA attach has real latency (multiple seconds), so every test in the "UI
// Automation" collection shares one launched instance via this fixture instead of relaunching
// per test. Tests in that collection must run with parallelization disabled (see
// UiAutomationCollection below) since they all drive the same real window.
//
// Safety note: this machine may have a real B1 droid fleet connected on a USB serial port, and
// the console auto-reconnects to the last-used port on startup. Every test built against this
// fixture is restricted to local document edits (New/Insert/Delete/Undo/Loop/Snap and similar) —
// nothing that calls Play/Restart/Stop/SAFE/E-STOP or otherwise dispatches a real anim/audio
// command to hardware. Do not add a test here that clicks those controls.
public sealed class UiAppFixture : IDisposable
{
    public Application App { get; }
    public UIA3Automation Automation { get; }
    public Window MainWindow { get; }

    public UiAppFixture()
    {
        var exePath = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..",
            "console", "bin", "Debug", "net8.0-windows", "b1-chat-console.exe"));
        if (!File.Exists(exePath))
        {
            throw new FileNotFoundException(
                "Compiled console exe not found. Build console/b1-chat-console.csproj (Debug) " +
                "before running the UI Automation collection.", exePath);
        }

        App = Application.Launch(exePath);
        Automation = new UIA3Automation();
        MainWindow = App.GetMainWindow(Automation, TimeSpan.FromSeconds(20))
            ?? throw new InvalidOperationException("Main window did not appear within 20s of launch.");

        // Maximize, then scroll the outer page ScrollViewer to the bottom: the Sequencer card
        // sits below the Droids/MeshTopology cards and needs to be fully on-screen for FlaUI's
        // coordinate-based clicks — an element clipped out of an ancestor ScrollViewer's viewport
        // can report an empty (0,0,0,0) bounding rectangle, so clicks computed from it silently
        // land nowhere useful. Discovered empirically while building this harness.
        MainWindow.Patterns.Window.PatternOrDefault?.SetWindowVisualState(WindowVisualState.Maximized);
        Thread.Sleep(300);
        ScrollMainPageToBottom();
        Thread.Sleep(300);
    }

    /// <summary>
    /// Scrolls the outer page ScrollViewer ("MainScroll") to the bottom, where the Sequencer
    /// card — and inside it, the gesture-library chip row — lives. Done once at startup, but the
    /// Sequencer card's own height changes as clips/lanes/panels (Preflight, VARIATION, etc.) are
    /// added and removed across many tests sharing this one window instance, which can drift the
    /// chip row's actual screen position away from where it was when a coordinate was last
    /// computed. Called again before every chip click rather than trusting the one-time startup
    /// scroll to still hold many tests later.
    /// </summary>
    private void ScrollMainPageToBottom()
    {
        var mainScroll = MainWindow.FindFirstDescendant(cf => cf.ByAutomationId("MainScroll"));
        if (mainScroll == null) return;
        var r = mainScroll.BoundingRectangle;
        Mouse.MoveTo(new System.Drawing.Point(r.X + 50, r.Y + 50));
        for (var i = 0; i < 20; i++) Mouse.Scroll(-5);
    }

    /// <summary>
    /// Clicks the "All droids" broadcast track row in the Sequencer rail to arm it. The broadcast
    /// track always exists (RebuildTracks adds it unconditionally), so this works with no live
    /// droid fleet connected — unlike the per-droid rows, which only appear once heartbeats have
    /// been seen.
    /// </summary>
    public void ArmBroadcastTrack()
    {
        // Several elements can match by Name: the narrow clickable rail row, its own inner Text,
        // and a second, wider, IsHitTestVisible="False" copy used only to paint row backgrounds
        // under the horizontally-scrolling ruler/track area. The clickable row is the DataItem
        // with the rail's ~150px width.
        var candidates = MainWindow.FindAllDescendants(cf => cf.ByName("All droids"))
            .Where(e => e.ControlType == ControlType.DataItem)
            .OrderBy(e => e.BoundingRectangle.Width)
            .ToArray();
        if (candidates.Length == 0) throw new InvalidOperationException("'All droids' track row not found.");
        candidates[0].Click();
        Thread.Sleep(250);
    }

    /// <summary>
    /// Clicks a gesture-library chip by its display name (e.g. "Center", "Nod") to insert it onto
    /// the currently-armed track at the playhead. A plain click (no drag) on the chip is a
    /// document edit only — it does not dispatch anything to hardware.
    /// </summary>
    public void ClickGestureChip(string gestureName)
    {
        ScrollMainPageToBottom();
        Thread.Sleep(100);
        var chip = MainWindow.FindFirstDescendant(cf => cf.ByName(gestureName).And(cf.ByControlType(ControlType.Text)))
            ?? throw new InvalidOperationException($"Gesture chip '{gestureName}' not found.");
        chip.Click();
        Thread.Sleep(250);
    }

    /// <summary>
    /// Finds the single inserted gesture clip's item container (ItemsControl-generated DataItem
    /// for a SequenceStep) and returns its visible name-label Text descendant — the element whose
    /// on-screen position matches the rendered clip, suitable for a real mouse click/right-click.
    /// </summary>
    public AutomationElement GetSoleClipNameLabel()
    {
        var steps = FindStepItems();
        if (steps.Length != 1)
            throw new InvalidOperationException($"Expected exactly one inserted gesture clip, found {steps.Length}.");
        var label = steps[0].FindFirstDescendant(cf => cf.ByControlType(ControlType.Text))
            ?? throw new InvalidOperationException("Inserted clip has no Text descendant to click.");
        return label;
    }

    /// <summary>Every currently-inserted gesture clip's item container (SequenceStep DataItem).</summary>
    public AutomationElement[] FindStepItems() =>
        MainWindow.FindAllDescendants(cf => cf.ByName("b1_chat_console.Models.SequenceStep"));

    /// <summary>
    /// Clicks the ScrollArea (the horizontally-scrolling timeline ScrollViewer)'s own empty
    /// background. ScrollViewer is a real WPF Control (Focusable by default), and its own
    /// MouseDown class handler claims keyboard focus even when a descendant Border already marked
    /// the event Handled — clicking directly on a clip's Border does not move keyboard focus
    /// anywhere (Border is not a Control), which otherwise leaves focus on the Window and silently
    /// defeats every keyboard shortcut gated by SequenceTimelineView's PreviewKeyDown handler.
    /// Discovered empirically while building this harness.
    /// </summary>
    public void FocusTimelineScrollArea()
    {
        var scrollArea = MainWindow.FindFirstDescendant(cf => cf.ByAutomationId("ScrollArea"))
            ?? throw new InvalidOperationException("ScrollArea not found.");
        var r = scrollArea.BoundingRectangle;
        Mouse.Click(new System.Drawing.Point(r.X + 20, r.Bottom - 5));
        Thread.Sleep(200);
    }

    /// <summary>
    /// Resets the Sequencer document to a clean, empty New scene, regardless of what a previous
    /// test in this shared-instance collection left behind. If the document was dirty, the "New"
    /// button raises an "Unsaved Scene Changes" modal (SceneDecisionWindow); this dismisses it via
    /// its "Continue Without Saving" (discard) action so every test starts from the same baseline.
    /// </summary>
    public void ResetToCleanNewScene()
    {
        var newButton = MainWindow.FindFirstDescendant(
            cf => cf.ByName("New").And(cf.ByControlType(ControlType.Button)))
            ?? throw new InvalidOperationException("'New' button not found.");
        newButton.Click();

        // Poll for the discard dialog rather than a single fixed-delay check, and search for it
        // as a descendant of MainWindow itself rather than (only) via App.GetAllTopLevelWindows.
        // Real, empirically confirmed finding from this session (via screenshot capture, a raw
        // Win32 EnumWindows probe, and Automation.GetDesktop() — all agreeing): in this
        // environment, every owned window this app creates (SceneDecisionWindow, SceneNameWindow,
        // SceneBrowserWindow, FirmwareWindow, HelpWindow — modal and non-modal alike) has its
        // content exposed by UIA as part of MainWindow's own automation subtree, not as a
        // separate top-level window — App.GetAllTopLevelWindows() and even
        // Automation.GetDesktop() never list them, and no distinct HWND for one is ever found for
        // this process. The windows genuinely open and render — they're just not independently
        // reachable the way FindOpenContextMenu's top-level-window search finds a right-click
        // ContextMenu Popup. Searching MainWindow's own subtree finds their content reliably
        // instead (see UiAutomationSmokeTests2.cs's Firmware/Help tests for the same pattern, and
        // CloseOwnedWindowByTitle there for why closing such a window needs a raw WM_CLOSE by
        // HWND rather than Alt+F4 or a FlaUI Window.Close()).
        for (var i = 0; i < 10; i++)
        {
            Thread.Sleep(150);
            var discard = MainWindow.FindFirstDescendant(cf => cf.ByName("Continue Without Saving"));
            if (discard == null) continue;
            discard.Click();
            Thread.Sleep(300);
            break;
        }
    }

    /// <summary>Finds a top-level Menu (a right-click ContextMenu Popup) among this app's own windows.</summary>
    public AutomationElement? FindOpenContextMenu()
    {
        foreach (var w in App.GetAllTopLevelWindows(Automation))
        {
            var menu = w.FindFirstDescendant(cf => cf.ByControlType(ControlType.Menu));
            if (menu != null) return menu;
        }
        return null;
    }

    public void Dispose()
    {
        try { if (!App.HasExited) App.Close(); } catch { /* best effort */ }
        try { if (!App.HasExited) App.Kill(); } catch { /* best effort */ }
        try { Automation.Dispose(); } catch { /* best effort */ }
        try { App.Dispose(); } catch { /* best effort */ }
    }
}

[CollectionDefinition("UI Automation", DisableParallelization = true)]
public sealed class UiAutomationCollection : ICollectionFixture<UiAppFixture>
{
    // Marker class only — see FlaUI/xUnit collection fixture docs. No members needed.
}
