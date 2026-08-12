using System.Text.Json;
using b1_chat_console.Models;
using b1_chat_console.Services;
using b1_chat_console.ViewModels;

namespace b1_chat_console.Tests;

public sealed class SceneLibraryTests
{
    [Fact]
    public void VersionedStorage_SaveScanAndGetRoundTripAtomically()
    {
        using var fixture = new TemporaryJsonFixture();
        var id = Guid.NewGuid().ToString("N");
        var service = new LibraryService(fixture.DirectoryPath, new AtomicTextFileWriter());
        var item = Scene(id, "Bench Wakeup");

        service.Save(item);

        var path = Assert.Single(Directory.GetFiles(fixture.DirectoryPath, "*.b1scene.json"));
        using var json = JsonDocument.Parse(File.ReadAllText(path));
        Assert.Equal(LibraryService.SchemaType, json.RootElement.GetProperty("type").GetString());
        Assert.Equal(LibraryService.CurrentVersion, json.RootElement.GetProperty("version").GetInt32());
        Assert.Equal(id, json.RootElement.GetProperty("id").GetString());
        Assert.Equal("b1-sequence", json.RootElement.GetProperty("document").GetProperty("type").GetString());
        Assert.Empty(Directory.GetFiles(fixture.DirectoryPath, "*.tmp"));

        var scan = service.Scan();
        Assert.Empty(scan.Issues);
        Assert.Equal("Bench Wakeup", Assert.Single(scan.Items).Name);
        var loaded = Assert.IsType<SequenceLibraryItem>(service.Get(id));
        Assert.Equal(id, loaded.Id);
        Assert.Equal((3, (ushort)0x1234, 250),
            (Assert.Single(loaded.Steps).AnimId, loaded.Steps[0].Target, loaded.Steps[0].StartMs));
    }

    [Fact]
    public void LegacyEntry_IsValidatedMigratedAndOriginalMovedToTrash()
    {
        using var fixture = new TemporaryJsonFixture();
        var legacy = Scene("old-human-id", "Legacy Scene");
        var legacyPath = fixture.Write("old-human-id.json", JsonSerializer.Serialize(legacy));
        var service = new LibraryService(fixture.DirectoryPath, new AtomicTextFileWriter());

        var scan = service.Scan();

        Assert.Empty(scan.Issues);
        var migrated = Assert.Single(scan.Items);
        Assert.True(Guid.TryParse(migrated.Id, out _));
        Assert.Equal("Legacy Scene", migrated.Name);
        Assert.False(File.Exists(legacyPath));
        Assert.True(File.Exists(Path.Combine(fixture.DirectoryPath, $"{migrated.Id}.b1scene.json")));
        Assert.Single(Directory.GetFiles(Path.Combine(fixture.DirectoryPath, "trash"), "legacy-*.json"));
    }

    [Fact]
    public void InvalidEntry_RemainsOnDiskAndIsReportedWithoutHidingValidScenes()
    {
        using var fixture = new TemporaryJsonFixture();
        var invalid = fixture.Write("broken.json", "{ definitely-not-json");
        var service = new LibraryService(fixture.DirectoryPath, new AtomicTextFileWriter());
        service.Save(Scene(Guid.NewGuid().ToString("N"), "Valid Scene"));

        var scan = service.Scan();

        Assert.Equal("Valid Scene", Assert.Single(scan.Items).Name);
        var issue = Assert.Single(scan.Issues);
        Assert.Equal("broken.json", issue.FileName);
        Assert.Contains("Invalid JSON", issue.Message);
        Assert.True(File.Exists(invalid));
    }

    [Fact]
    public void MoveToTrash_IsRecoverableAndMissingSceneIsAnError()
    {
        using var fixture = new TemporaryJsonFixture();
        var id = Guid.NewGuid().ToString("N");
        var service = new LibraryService(fixture.DirectoryPath, new AtomicTextFileWriter());
        service.Save(Scene(id, "Temporary Scene"));

        service.MoveToTrash(id);

        Assert.Empty(service.Scan().Items);
        var trashFile = Assert.Single(Directory.GetFiles(
            Path.Combine(fixture.DirectoryPath, "trash"), $"{id}.*.b1scene.json"));
        Assert.Contains(id, Path.GetFileName(trashFile));
        Assert.Throws<FileNotFoundException>(() => service.MoveToTrash(id));
    }

    [Fact]
    public void FailedAtomicUpdate_PreservesPreviousLibraryDocument()
    {
        using var fixture = new TemporaryJsonFixture();
        var id = Guid.NewGuid().ToString("N");
        var good = new LibraryService(fixture.DirectoryPath, new AtomicTextFileWriter());
        good.Save(Scene(id, "Known Good"));
        var path = Path.Combine(fixture.DirectoryPath, $"{id}.b1scene.json");
        var original = File.ReadAllText(path);
        var failing = new LibraryService(fixture.DirectoryPath,
            new ThrowingAtomicTextFileWriter(new IOException("Simulated full disk.")));

        Assert.Throws<IOException>(() => failing.Save(Scene(id, "Replacement")));

        Assert.Equal(original, File.ReadAllText(path));
        Assert.Equal("Known Good", good.Get(id)?.Name);
    }

    [Fact]
    public void Save_NewNamedDocumentCreatesStableLibraryIdentityAndCheckpoint()
    {
        var library = new FakeSequenceLibraryService();
        var settings = new FakeSequencerSettings();
        var dialogs = new FakeSequencerPersistenceDialogs();
        using var vm = CreateViewModel(library, settings, dialogs);
        Assert.True(vm.SetSequenceName("Opening Scene"));
        vm.InsertGestureAt(4, vm.Tracks.Single(track => track.IsBroadcast), 300);

        vm.SaveSceneCommand.Execute(null);

        var saved = Assert.Single(library.Saved);
        Assert.True(Guid.TryParse(saved.Id, out _));
        Assert.Equal("Opening Scene", saved.Name);
        Assert.Equal(saved.Id, vm.CurrentSceneId);
        Assert.Equal(SequencerDocumentOrigin.LocalLibrary, vm.DocumentOrigin);
        Assert.False(vm.Dirty);
        Assert.Equal(saved.Id, settings.LastSceneId);
        Assert.Null(settings.LastSequencePath);
        Assert.Equal(0, dialogs.SceneNamePrompts);
        Assert.Contains("LOCAL LIBRARY · SAVED", vm.SequenceBadgeText);
    }

    [Fact]
    public void Save_UntitledDocumentPromptsAndCancelLeavesItUntouched()
    {
        var library = new FakeSequenceLibraryService();
        var dialogs = new FakeSequencerPersistenceDialogs { SceneNameResult = null };
        using var vm = CreateViewModel(library, dialogs: dialogs);

        vm.SaveSceneCommand.Execute(null);

        Assert.Equal(1, dialogs.SceneNamePrompts);
        Assert.Empty(library.Saved);
        Assert.Equal(SequencerDocumentOrigin.New, vm.DocumentOrigin);
        Assert.Null(vm.CurrentSceneId);
        Assert.Equal("", vm.Name);
    }

    [Fact]
    public void Save_ExistingSceneUpdatesSameIdentityWhileSaveAsCreatesAnother()
    {
        var id = Guid.NewGuid().ToString("N");
        var library = new FakeSequenceLibraryService();
        var original = Scene(id, "Original");
        library.Items.Add(original);
        var dialogs = new FakeSequencerPersistenceDialogs { SceneNameResult = "Variation" };
        using var vm = CreateViewModel(library, dialogs: dialogs);
        vm.LoadFromLibraryCommand.Execute(original);
        vm.EditableLoop = true;

        vm.SaveSceneCommand.Execute(null);
        vm.SaveSceneAsCommand.Execute(null);

        Assert.Equal(2, library.Saved.Count);
        Assert.Equal(id, library.Saved[0].Id);
        Assert.NotEqual(id, library.Saved[1].Id);
        Assert.Equal("Variation", library.Saved[1].Name);
        Assert.Equal(library.Saved[1].Id, vm.CurrentSceneId);
        Assert.Equal(1, dialogs.SceneNamePrompts);
    }

    [Fact]
    public void SaveAs_NameConflictIsExplicitAndNeverOverwritesAnotherIdentity()
    {
        var library = new FakeSequenceLibraryService();
        library.Items.Add(Scene(Guid.NewGuid().ToString("N"), "Finale"));
        var dialogs = new FakeSequencerPersistenceDialogs { SceneNameResult = "  finale  " };
        using var vm = CreateViewModel(library, dialogs: dialogs);

        vm.SaveSceneAsCommand.Execute(null);

        Assert.Empty(library.Saved);
        var error = Assert.Single(dialogs.Errors);
        Assert.Equal("Scene name already exists", error.Title);
        Assert.Contains("never overwrites", error.Message);
        Assert.Equal(SequencerDocumentOrigin.New, vm.DocumentOrigin);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Trash_RequiresExactConfirmationAndOnlyRefreshesAfterSuccess(bool confirm)
    {
        var id = Guid.NewGuid().ToString("N");
        var library = new FakeSequenceLibraryService();
        var item = Scene(id, "Scene To Remove");
        library.Items.Add(item);
        var dialogs = new FakeSequencerPersistenceDialogs { DeleteConfirmResult = confirm };
        using var vm = CreateViewModel(library, dialogs: dialogs);

        vm.DeleteFromLibraryCommand.Execute(item);

        Assert.Equal("Scene To Remove", Assert.Single(dialogs.DeleteConfirmationRequests));
        Assert.Equal(confirm, library.Trashed.Count == 1);
        Assert.Equal(confirm ? 0 : 1, vm.Library.Count);
    }

    [Fact]
    public void TrashFailure_IsReportedAndLoadedSceneRemainsSavedAndVisible()
    {
        var id = Guid.NewGuid().ToString("N");
        var library = new FakeSequenceLibraryService { TrashError = new UnauthorizedAccessException("Denied trash folder.") };
        var item = Scene(id, "Protected");
        library.Items.Add(item);
        var dialogs = new FakeSequencerPersistenceDialogs { DeleteConfirmResult = true };
        using var vm = CreateViewModel(library, dialogs: dialogs);
        vm.LoadFromLibraryCommand.Execute(item);

        vm.DeleteFromLibraryCommand.Execute(item);

        Assert.Equal(id, vm.CurrentSceneId);
        Assert.False(vm.Dirty);
        Assert.Single(vm.Library);
        Assert.Contains("Denied trash folder", Assert.Single(dialogs.Errors).Message);
    }

    [Fact]
    public void Trash_CurrentSceneLeavesRecoverableModifiedNewDocument()
    {
        var id = Guid.NewGuid().ToString("N");
        var library = new FakeSequenceLibraryService();
        var item = Scene(id, "Keep In Editor");
        library.Items.Add(item);
        var settings = new FakeSequencerSettings();
        var dialogs = new FakeSequencerPersistenceDialogs { DeleteConfirmResult = true };
        using var vm = CreateViewModel(library, settings, dialogs);
        vm.LoadFromLibraryCommand.Execute(item);

        vm.DeleteFromLibraryCommand.Execute(item);

        Assert.Equal("Keep In Editor", vm.Name);
        Assert.Null(vm.CurrentSceneId);
        Assert.Equal(SequencerDocumentOrigin.New, vm.DocumentOrigin);
        Assert.True(vm.Dirty);
        Assert.Null(settings.LastSceneId);
    }

    [Fact]
    public void ExportOfModifiedLibrarySceneDoesNotClaimLibraryWasSaved()
    {
        using var fixture = new TemporaryJsonFixture();
        var id = Guid.NewGuid().ToString("N");
        var library = new FakeSequenceLibraryService();
        var item = Scene(id, "Library Source");
        library.Items.Add(item);
        var settings = new FakeSequencerSettings();
        using var vm = CreateViewModel(library, settings: settings);
        vm.LoadFromLibraryCommand.Execute(item);
        vm.EditableLoop = !vm.Loop;

        vm.ExportTo(Path.Combine(fixture.DirectoryPath, "external.b1seq.json"));

        Assert.True(vm.Dirty);
        Assert.Equal(SequencerDocumentOrigin.LocalLibrary, vm.DocumentOrigin);
        Assert.Equal(id, vm.CurrentSceneId);
        Assert.Null(settings.LastSequencePath);
        Assert.Equal(id, settings.LastSceneId);
        Assert.Contains("MODIFIED", vm.SequenceBadgeText);
    }

    [Fact]
    public void StartupRestorePrefersRememberedLibraryIdentity()
    {
        var id = Guid.NewGuid().ToString("N");
        var library = new FakeSequenceLibraryService();
        library.Items.Add(Scene(id, "Remembered Scene"));
        var settings = new FakeSequencerSettings
        {
            LastSceneId = id,
            LastSequencePath = @"Z:\stale\external.b1seq.json",
        };
        using var vm = CreateViewModel(library, settings: settings);

        vm.TryLoadLastSequence();

        Assert.Equal("Remembered Scene", vm.Name);
        Assert.Equal(id, vm.CurrentSceneId);
        Assert.Equal(SequencerDocumentOrigin.LocalLibrary, vm.DocumentOrigin);
        Assert.False(vm.Dirty);
    }

    [Fact]
    public void BadgeAndLibraryStatusExposeOriginDirtyAndInvalidFileCounts()
    {
        var library = new FakeSequenceLibraryService();
        library.Issues.Add(new SequenceLibraryIssue("broken.json", "Invalid JSON"));
        using var vm = CreateViewModel(library);
        Assert.Contains("UNTITLED · NEW · CLEAN", vm.SequenceBadgeText);
        Assert.Contains("1 file issue", vm.LibraryStatusText);

        vm.ImportFrom(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Sequences", "sequence-v4.json"));
        Assert.Contains("IMPORTED / EXTERNAL FILE · SAVED", vm.SequenceBadgeText);

        vm.EditableLoop = !vm.Loop;
        Assert.Contains("IMPORTED / EXTERNAL FILE · MODIFIED", vm.SequenceBadgeText);
    }

    [Fact]
    public void OpenSceneBrowser_UsesSelectedLibrarySceneAndMarksItCurrent()
    {
        var library = new FakeSequenceLibraryService();
        var selected = Scene(Guid.NewGuid().ToString("N"), "Scene 1");
        library.Items.Add(selected);
        var dialogs = new FakeSequencerPersistenceDialogs
        {
            BrowserResult = new SceneBrowserResult(selected),
        };
        var settings = new FakeSequencerSettings();
        using var vm = CreateViewModel(library, settings, dialogs);

        vm.OpenSceneLibraryCommand.Execute(null);

        Assert.Equal(1, dialogs.SceneBrowserSelections);
        Assert.Equal("Scene 1", vm.Name);
        Assert.Equal(selected.Id, vm.CurrentSceneId);
        Assert.Equal(selected.Id, settings.LastSceneId);
        Assert.False(vm.Dirty);
    }

    [Fact]
    public void NewScene_WithSaveChoiceSavesDraftBeforeCreatingCleanUntitledDocument()
    {
        var library = new FakeSequenceLibraryService();
        var settings = new FakeSequencerSettings();
        var dialogs = new FakeSequencerPersistenceDialogs
        {
            UnsavedChoice = UnsavedSceneChoice.Save,
        };
        using var vm = CreateViewModel(library, settings, dialogs);
        Assert.True(vm.SetSequenceName("Draft Scene"));
        vm.InsertGestureAt(4, vm.Tracks.Single(track => track.IsBroadcast), 300);

        vm.NewSceneCommand.Execute(null);

        Assert.Equal("Draft Scene", Assert.Single(library.Saved).Name);
        Assert.Equal("", vm.Name);
        Assert.Equal("Untitled Scene", vm.SceneDisplayName);
        Assert.Equal(SequencerDocumentOrigin.New, vm.DocumentOrigin);
        Assert.Null(vm.CurrentSceneId);
        Assert.Empty(vm.Steps);
        Assert.Equal(2, vm.AudioLanes.Count);
        Assert.False(vm.Dirty);
        Assert.Null(settings.LastSceneId);
        Assert.Null(settings.LastSequencePath);
    }

    [Fact]
    public void NewScene_CancelAtUnsavedPromptKeepsDraftIntact()
    {
        var library = new FakeSequenceLibraryService();
        var dialogs = new FakeSequencerPersistenceDialogs
        {
            UnsavedChoice = UnsavedSceneChoice.Cancel,
        };
        using var vm = CreateViewModel(library, dialogs: dialogs);
        Assert.True(vm.SetSequenceName("Keep Editing"));

        vm.NewSceneCommand.Execute(null);

        Assert.Equal("Keep Editing", vm.Name);
        Assert.True(vm.Dirty);
        Assert.Empty(library.Saved);
        Assert.Single(dialogs.ConfirmationRequests);
    }

    [Fact]
    public void BrowserNewSceneChoiceUsesTheSameProtectedNewDocumentWorkflow()
    {
        var library = new FakeSequenceLibraryService();
        var dialogs = new FakeSequencerPersistenceDialogs
        {
            BrowserResult = new SceneBrowserResult(null, CreateNew: true),
            UnsavedChoice = UnsavedSceneChoice.Discard,
        };
        using var vm = CreateViewModel(library, dialogs: dialogs);
        Assert.True(vm.SetSequenceName("Discard Me"));

        vm.OpenSceneLibraryCommand.Execute(null);

        Assert.Equal(1, dialogs.SceneBrowserSelections);
        Assert.Equal("", vm.Name);
        Assert.False(vm.Dirty);
        Assert.Single(dialogs.ConfirmationRequests);
    }

    [Fact]
    public void ReplacementSaveCancelledAtNamePromptKeepsUntitledDraft()
    {
        var library = new FakeSequenceLibraryService();
        var dialogs = new FakeSequencerPersistenceDialogs
        {
            UnsavedChoice = UnsavedSceneChoice.Save,
            SceneNameResult = null,
        };
        using var vm = CreateViewModel(library, dialogs: dialogs);
        vm.InsertGestureAt(4, vm.Tracks.Single(track => track.IsBroadcast), 300);

        vm.NewSceneCommand.Execute(null);

        Assert.Single(vm.Steps);
        Assert.True(vm.Dirty);
        Assert.Equal(1, dialogs.SceneNamePrompts);
        Assert.Empty(library.Saved);
    }

    [Fact]
    public void DeleteCurrentSceneCommandIsAvailableOnlyForTheOpenLibraryIdentity()
    {
        var id = Guid.NewGuid().ToString("N");
        var library = new FakeSequenceLibraryService();
        var scene = Scene(id, "Disposable");
        library.Items.Add(scene);
        var dialogs = new FakeSequencerPersistenceDialogs { DeleteConfirmResult = true };
        using var vm = CreateViewModel(library, dialogs: dialogs);
        Assert.False(vm.DeleteCurrentSceneCommand.CanExecute(null));
        vm.LoadFromLibraryCommand.Execute(scene);
        Assert.True(vm.DeleteCurrentSceneCommand.CanExecute(null));

        vm.DeleteCurrentSceneCommand.Execute(null);

        Assert.Equal(id, Assert.Single(library.Trashed));
        Assert.False(vm.DeleteCurrentSceneCommand.CanExecute(null));
        Assert.Equal(SequencerDocumentOrigin.New, vm.DocumentOrigin);
    }

    private static SequenceLibraryItem Scene(string id, string name) => new()
    {
        Id = id,
        Name = name,
        Loop = false,
        Tracks = new List<SequenceTrackDto> { new() { Id = 0x1234, Name = "R2-D2" } },
        AudioLanes = new List<AudioLaneDto>(),
        Steps = new List<SequenceStepDto> { new() { AnimId = 3, Target = 0x1234, StartMs = 250 } },
        SavedAt = DateTime.UtcNow,
    };

    private static SequencerViewModel CreateViewModel(
        ISequenceLibraryService library,
        FakeSequencerSettings? settings = null,
        FakeSequencerPersistenceDialogs? dialogs = null)
    {
        dialogs ??= new FakeSequencerPersistenceDialogs();
        return new SequencerViewModel(
            new FakeSequencerProtocol(),
            settings ?? new FakeSequencerSettings(),
            new FakeAudioPlayer(),
            new FakePlaybackTimerScheduler(),
            new FakePlaybackClock(),
            new FakePlaybackTimerScheduler(),
            dialogs,
            new AtomicTextFileWriter(),
            library);
    }
}
