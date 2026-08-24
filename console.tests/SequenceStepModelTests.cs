using b1_chat_console.Models;
using b1_chat_console.Services;
using b1_chat_console.ViewModels;

namespace b1_chat_console.Tests;

public sealed class SequenceStepModelTests
{
    [Theory]
    [InlineData("communicate.nod", true)]
    [InlineData("dialogue.talk", true)]
    [InlineData("idle.center", false)]
    [InlineData("attention.look-right", false)]
    [InlineData("", false)]
    public void RequiresSeed_MatchesTheCatalogsSeedPolicyRequiredGestures(string gestureKey, bool expected)
    {
        var step = new SequenceStep { GestureKey = gestureKey };

        Assert.Equal(expected, step.RequiresSeed);
    }

    [Fact]
    public void RegenerateSeedCommand_IsANoOpWithoutASeedRequiringSelection()
    {
        // Same pattern as NudgeEndLonger/NudgeEndShorter: CanExecute reflects only
        // CanEditSequence (transport-lock gating). Applicability to the current selection is a
        // no-op guard inside the command body, backed in the UI by the VARIATION section's own
        // Visibility binding to SelectedStep.RequiresSeed rather than a disabled button.
        var vm = CreateViewModel();
        vm.Steps.Add(new SequenceStep { GestureKey = "attention.look-right", Target = 0xFFFF, Seed = 111 });
        vm.SelectedStep = vm.Steps[0];

        vm.RegenerateSeedCommand.Execute(null);

        Assert.Equal((uint)111, vm.Steps[0].Seed);
        Assert.False(vm.Dirty);
        Assert.False(vm.UndoCommand.CanExecute(null));
    }

    [Fact]
    public void InsertGestureAt_AssignsANonZeroSeedRatherThanAlwaysTheSameDefault()
    {
        var vm = CreateViewModel();
        var track = vm.Tracks.First();

        vm.InsertGestureAt(animId: 1, track, startMs: 0);

        Assert.NotEqual((uint)0, vm.Steps.Single().Seed);
    }

    // DarkComboBoxStyle's ControlTemplate renders the closed selection through the custom
    // ContentPresenter "ContentSite" (Content="{TemplateBinding SelectionBoxItem}", no
    // ContentTemplate) — when Content isn't a string or UIElement, WPF falls back to
    // Content.ToString() wrapped in a default TextBlock. Confirmed empirically (this session,
    // via FlaUI/UIA3) that this specific rendering has no independently UIA-observable surface
    // for a non-editable custom-templated ComboBox: Value pattern is unsupported, Name and
    // LegacyIAccessible Name/Value are all blank whether the dropdown is open or closed,
    // FindAllDescendants returns nothing while collapsed (WPF's ComboBoxAutomationPeer only
    // ever exposes ComboBoxItem child peers, never template chrome), and even
    // AutomationElement.FromPoint at the rendered text's own screen coordinates resolves no
    // deeper than the ComboBox itself (ContentSite is IsHitTestVisible="False"). A live
    // UI-automation assertion of the closed-box text is therefore not reachable without
    // screenshot OCR, which risks exactly the flakiness this test suite avoids — so the root
    // cause is guarded directly here instead: GestureLibraryEntry.ToString() must return the
    // gesture's display name, matching what DarkComboBoxStyle's ContentSite would render.
    [Fact]
    public void GestureLibraryEntry_ToString_ReturnsTheDisplayName_NotTheTypeName()
    {
        var entry = new GestureLibraryEntry { Id = 1, Name = "Nod" };

        Assert.Equal("Nod", entry.ToString());
        Assert.DoesNotContain("GestureLibraryEntry", entry.ToString());
    }

    // Same regression class, same ContentSite mechanism, for the TARGET TRACK combo.
    [Fact]
    public void TimelineTrack_ToString_ReturnsTheLabel_NotTheTypeName()
    {
        var track = new TimelineTrack { Id = 0xFFFF, Label = "All droids", Role = "BROADCAST", IsBroadcast = true };

        Assert.Equal("All droids", track.ToString());
        Assert.DoesNotContain("TimelineTrack", track.ToString());
    }

    private static SequencerViewModel CreateViewModel() => new(
        new FakeSequencerProtocol(),
        new FakeSequencerSettings(),
        new FakeAudioPlayer(),
        new FakePlaybackTimerScheduler(),
        new FakePlaybackClock(),
        new FakePlaybackTimerScheduler(),
        new FakeSequencerPersistenceDialogs(),
        new ThrowingAtomicTextFileWriter(new InvalidOperationException("not used")),
        new FakeSequenceLibraryService(),
        preflightService: new PermissiveSequencerPreflightService());
}
