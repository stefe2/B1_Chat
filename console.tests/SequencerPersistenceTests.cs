using System.Text;
using b1_chat_console.Models;
using b1_chat_console.Services;
using b1_chat_console.ViewModels;

namespace b1_chat_console.Tests;

public sealed class SequencerPersistenceTests
{
    [Fact]
    public void DirtyProperty_HasNoPublicOrInternalSetter()
    {
        var dirty = typeof(SequencerViewModel).GetProperty(nameof(SequencerViewModel.Dirty));

        Assert.NotNull(dirty);
        Assert.Null(dirty.GetSetMethod(nonPublic: false));
        Assert.True(dirty.GetSetMethod(nonPublic: true)?.IsPrivate);
    }

    [Fact]
    public void AtomicWriter_CreatesAndReplacesACompleteFileWithoutLeavingTemps()
    {
        using var fixture = new TemporaryJsonFixture();
        var path = Path.Combine(fixture.DirectoryPath, "scene.b1seq.json");
        var writer = new AtomicTextFileWriter();

        writer.WriteAllText(path, "first");
        Assert.Equal("first", File.ReadAllText(path, Encoding.UTF8));

        writer.WriteAllText(path, "second and complete");
        Assert.Equal("second and complete", File.ReadAllText(path, Encoding.UTF8));
        Assert.Empty(Directory.GetFiles(fixture.DirectoryPath, "*.tmp"));
    }

    [Fact]
    public void AtomicWriter_FailedReplacementPreservesDestinationAndCleansTemp()
    {
        using var fixture = new TemporaryJsonFixture();
        var path = fixture.Write("scene.b1seq.json", "known-good");
        var writer = new AtomicTextFileWriter(new FailingMoveFileOperations());

        var error = Assert.Throws<IOException>(() => writer.WriteAllText(path, "partial-new-data"));

        Assert.Contains("Simulated replacement failure", error.Message);
        Assert.Equal("known-good", File.ReadAllText(path));
        Assert.Empty(Directory.GetFiles(fixture.DirectoryPath, "*.tmp"));
    }

    [Fact]
    public void SavedCheckpoint_FollowsExportEditUndoRedoAndSecondExport()
    {
        using var fixture = new TemporaryJsonFixture();
        var path = Path.Combine(fixture.DirectoryPath, "checkpoint.b1seq.json");
        var settings = new FakeSequencerSettings();
        var protocol = new FakeSequencerProtocol();
        protocol.Droids.Add(new Droid { Id = 0x4001, Name = "R2-D2" });
        using var vm = CreateViewModel(protocol, settings, writer: new AtomicTextFileWriter());
        Assert.True(vm.SetSequenceName("Checkpoint"));
        vm.EditableLoop = true;
        vm.InsertGestureAt(2, vm.Tracks.Single(track => track.Id == 0x4001), 400);
        Assert.True(vm.InsertAudioClip(vm.AudioLanes[0], new AudioClip
        {
            FilePath = @"C:\fixtures\voice.wav",
            DurationMs = 600,
            StartMs = 100,
            Loop = false,
        }));
        Assert.True(vm.Dirty);

        vm.ExportTo(path);

        Assert.False(vm.Dirty);
        Assert.Equal(path, settings.LastSequencePath);
        var exported = SequenceImportService.ParseFile(path);
        Assert.Equal("Checkpoint", exported.Name);
        Assert.True(exported.Loop);
        Assert.Equal((2, (ushort)0x4001, 400),
            (Assert.Single(exported.Steps).AnimId, exported.Steps[0].Target, exported.Steps[0].StartMs));
        Assert.Equal(@"C:\fixtures\voice.wav", Assert.Single(exported.AudioLanes[0].Clips).FilePath);
        using (var reopened = CreateViewModel())
        {
            reopened.ImportFrom(path);
            Assert.Equal("Checkpoint", reopened.Name);
            Assert.True(reopened.Loop);
            Assert.Equal((2, (ushort)0x4001, 400),
                (Assert.Single(reopened.Steps).AnimId, reopened.Steps[0].Target, reopened.Steps[0].StartMs));
            Assert.Equal(@"C:\fixtures\voice.wav", Assert.Single(reopened.AudioLanes[0].Clips).FilePath);
            Assert.False(reopened.Dirty);
        }

        vm.SelectedStep = vm.Steps[0];
        vm.NudgeStartForwardCommand.Execute(null);
        Assert.True(vm.Dirty);
        vm.UndoCommand.Execute(null);
        Assert.False(vm.Dirty); // exact equality with the first export checkpoint
        vm.RedoCommand.Execute(null);
        Assert.True(vm.Dirty);

        vm.ExportTo(path); // atomic replacement establishes the moved state as saved
        Assert.False(vm.Dirty);
        Assert.Equal(2, settings.SequencePathWrites);
        vm.UndoCommand.Execute(null);
        Assert.True(vm.Dirty);
        vm.RedoCommand.Execute(null);
        Assert.False(vm.Dirty);
    }

    [Fact]
    public void ExportFailure_PreservesDocumentCheckpointHistoryAndLastPath()
    {
        var settings = new FakeSequencerSettings();
        var denied = new ThrowingAtomicTextFileWriter(
            new UnauthorizedAccessException("Simulated denied export path."));
        using var vm = CreateViewModel(settings: settings, writer: denied);
        Assert.True(vm.SetSequenceName("Unsaved change"));
        Assert.True(vm.Dirty);

        Assert.Throws<UnauthorizedAccessException>(() => vm.ExportTo(@"Z:\denied\scene.b1seq.json"));

        Assert.Equal("Unsaved change", vm.Name);
        Assert.True(vm.Dirty);
        Assert.True(vm.UndoCommand.CanExecute(null));
        Assert.Null(settings.LastSequencePath);
        Assert.Equal(0, settings.SequencePathWrites);
        vm.UndoCommand.Execute(null);
        Assert.False(vm.Dirty); // failed export did not move the original checkpoint
    }

    [Fact]
    public void ExportCommand_ReportsFailureWithoutEscapingTheUiCommand()
    {
        var dialogs = new FakeSequencerPersistenceDialogs
        {
            ExportPath = @"Z:\denied\scene.b1seq.json",
        };
        var denied = new ThrowingAtomicTextFileWriter(new IOException("Simulated disk failure."));
        using var vm = CreateViewModel(dialogs: dialogs, writer: denied);
        Assert.True(vm.SetSequenceName("Still open"));

        vm.ExportCommand.Execute(null);

        Assert.Equal(1, denied.Attempts);
        var error = Assert.Single(dialogs.Errors);
        Assert.Equal("Sequencer export failed", error.Title);
        Assert.Contains("Simulated disk failure", error.Message);
        Assert.True(vm.Dirty);
    }

    [Fact]
    public void Export_PreservesExplicitDocumentNameIndependentlyOfChosenFilename()
    {
        using var fixture = new TemporaryJsonFixture();
        var path = Path.Combine(fixture.DirectoryPath, "Different Filename.b1seq.json");
        using var vm = CreateViewModel(writer: new AtomicTextFileWriter());

        vm.ExportTo(path);

        Assert.Equal("", vm.Name);
        Assert.Equal("", SequenceImportService.ParseFile(path).Name);
        Assert.False(vm.Dirty);
    }

    [Fact]
    public void Export_RejectsEditorStateThatWouldNotRoundTripBeforeWriting()
    {
        var writer = new RecordingAtomicTextFileWriter();
        using var vm = CreateViewModel(writer: writer);
        Assert.True(vm.SetAudioLaneLabel(vm.AudioLanes[0], "   "));

        var error = Assert.Throws<SequenceImportException>(() =>
            vm.ExportTo(@"C:\unused\invalid.b1seq.json"));

        Assert.Equal("$.audioLanes[0].label", error.FieldPath);
        Assert.Equal(0, writer.Attempts);
        Assert.True(vm.Dirty);
    }

    [Theory]
    [InlineData(false, false, true)]
    [InlineData(true, false, false)]
    [InlineData(true, true, true)]
    public void ImportReplacement_PromptsOnlyForDirtyDocumentsAndHonorsCancel(
        bool dirty,
        bool confirm,
        bool shouldReplace)
    {
        var path = FixturePath("sequence-v4.json");
        var settings = new FakeSequencerSettings();
        var dialogs = new FakeSequencerPersistenceDialogs
        {
            ImportPath = path,
            ConfirmResult = confirm,
        };
        using var vm = CreateViewModel(settings: settings, dialogs: dialogs);
        if (dirty) Assert.True(vm.SetSequenceName("Unsaved"));

        vm.ImportCommand.Execute(null);

        Assert.Equal(dirty ? 1 : 0, dialogs.ConfirmationRequests.Count);
        if (dirty) Assert.Contains("sequence-v4.json", dialogs.ConfirmationRequests[0]);
        Assert.Equal(shouldReplace ? "Current document" : "Unsaved", vm.Name);
        Assert.Equal(shouldReplace, settings.LastSequencePath == path);
        Assert.Equal(!shouldReplace && dirty, vm.Dirty);
        Assert.Equal(shouldReplace ? 0 : dirty ? 1 : 0,
            vm.UndoCommand.CanExecute(null) ? 1 : 0);
    }

    [Theory]
    [InlineData(false, false, true)]
    [InlineData(true, false, false)]
    [InlineData(true, true, true)]
    public void LibraryReplacement_PromptsWithExactItemAndEstablishesCheckpoint(
        bool dirty,
        bool confirm,
        bool shouldReplace)
    {
        var dialogs = new FakeSequencerPersistenceDialogs { ConfirmResult = confirm };
        using var vm = CreateViewModel(dialogs: dialogs);
        if (dirty) Assert.True(vm.SetSequenceName("Unsaved"));
        var item = new SequenceLibraryItem
        {
            Id = "library-target",
            Name = "Library target",
            Loop = true,
            Steps = new List<SequenceStepDto>
            {
                new() { AnimId = 3, Target = 0x1234, StartMs = 250 },
            },
        };

        vm.LoadFromLibraryCommand.Execute(item);

        Assert.Equal(dirty ? 1 : 0, dialogs.ConfirmationRequests.Count);
        if (dirty) Assert.Contains("Library target", dialogs.ConfirmationRequests[0]);
        Assert.Equal(shouldReplace ? "Library target" : "Unsaved", vm.Name);
        Assert.Equal(!shouldReplace && dirty, vm.Dirty);
        if (shouldReplace)
        {
            Assert.False(vm.UndoCommand.CanExecute(null));
            Assert.True(vm.SetSequenceName("Edited after load"));
            Assert.True(vm.Dirty);
            vm.UndoCommand.Execute(null);
            Assert.False(vm.Dirty);
        }
    }

    [Theory]
    [InlineData("Playing", false)]
    [InlineData("Playing", true)]
    [InlineData("Paused", false)]
    [InlineData("Paused", true)]
    public void ReplacementCommands_AreInertDuringPlayAndPause(
        string state,
        bool dirty)
    {
        var dialogs = new FakeSequencerPersistenceDialogs
        {
            ImportPath = FixturePath("sequence-v4.json"),
            ConfirmResult = true,
        };
        var protocol = new FakeSequencerProtocol();
        protocol.Durations[2] = 1_000;
        using var vm = CreateViewModel(protocol, dialogs: dialogs);
        vm.Steps.Add(new SequenceStep { AnimId = 2, Target = ushort.MaxValue, StartMs = 0 });
        vm.EstablishSavedCheckpoint();
        if (dirty) Assert.True(vm.SetSequenceName("Unsaved"));
        var before = vm.Name;
        vm.PlayCommand.Execute(null);
        if (state == "Paused") vm.PauseCommand.Execute(null);
        var item = new SequenceLibraryItem { Id = "blocked", Name = "Blocked library load" };

        Assert.False(vm.ImportCommand.CanExecute(null));
        Assert.False(vm.LoadFromLibraryCommand.CanExecute(item));
        vm.ImportCommand.Execute(null);
        vm.LoadFromLibraryCommand.Execute(item);

        Assert.Equal(before, vm.Name);
        Assert.Equal(0, dialogs.ImportSelections);
        Assert.Empty(dialogs.ConfirmationRequests);
    }

    [Fact]
    public void InvalidImportAfterConfirmation_PreservesUnsavedDocumentAndShowsError()
    {
        using var fixture = new TemporaryJsonFixture();
        var invalid = fixture.Write("invalid.b1seq.json", "{not-json");
        var settings = new FakeSequencerSettings();
        var dialogs = new FakeSequencerPersistenceDialogs
        {
            ImportPath = invalid,
            ConfirmResult = true,
        };
        using var vm = CreateViewModel(settings: settings, dialogs: dialogs);
        Assert.True(vm.SetSequenceName("Keep me"));

        vm.ImportCommand.Execute(null);

        Assert.Equal("Keep me", vm.Name);
        Assert.True(vm.Dirty);
        Assert.True(vm.UndoCommand.CanExecute(null));
        Assert.Null(settings.LastSequencePath);
        Assert.Contains("invalid JSON", Assert.Single(dialogs.Errors).Message);
    }

    [Fact]
    public void StartupRestore_IsSilentAndEstablishesTheImportedCheckpoint()
    {
        var settings = new FakeSequencerSettings { LastSequencePath = FixturePath("sequence-v4.json") };
        var dialogs = new FakeSequencerPersistenceDialogs();
        using var vm = CreateViewModel(settings: settings, dialogs: dialogs);

        vm.TryLoadLastSequence();

        Assert.Equal("Current document", vm.Name);
        Assert.False(vm.Dirty);
        Assert.Empty(dialogs.ConfirmationRequests);
        Assert.Empty(dialogs.Errors);
        Assert.Equal(0, dialogs.ImportSelections);
        Assert.Equal(0, settings.SequencePathWrites);
    }

    private static SequencerViewModel CreateViewModel(
        FakeSequencerProtocol? protocol = null,
        FakeSequencerSettings? settings = null,
        FakeSequencerPersistenceDialogs? dialogs = null,
        IAtomicTextFileWriter? writer = null) => new(
            protocol ?? new FakeSequencerProtocol(),
            settings ?? new FakeSequencerSettings(),
            new FakeAudioPlayer(),
            new FakePlaybackTimerScheduler(),
            new FakePlaybackClock(),
            new FakePlaybackTimerScheduler(),
            dialogs ?? new FakeSequencerPersistenceDialogs(),
            writer ?? new AtomicTextFileWriter());

    private static string FixturePath(string fileName) =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "Sequences", fileName);

    private sealed class FailingMoveFileOperations : IAtomicFileOperations
    {
        public FileStream CreateNew(string path) => new(
            path, FileMode.CreateNew, FileAccess.Write, FileShare.None);

        public bool Exists(string path) => File.Exists(path);

        public void Move(string sourcePath, string destinationPath, bool overwrite) =>
            throw new IOException("Simulated replacement failure.");

        public void Delete(string path) => File.Delete(path);
    }

    private sealed class RecordingAtomicTextFileWriter : IAtomicTextFileWriter
    {
        public int Attempts { get; private set; }
        public void WriteAllText(string destinationPath, string contents) => Attempts++;
    }
}
