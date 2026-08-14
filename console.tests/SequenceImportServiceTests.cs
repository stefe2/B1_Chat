using System.Text.Json.Nodes;
using b1_chat_console.Models;
using b1_chat_console.Services;
using b1_chat_console.ViewModels;

namespace b1_chat_console.Tests;

public sealed class SequenceImportServiceTests
{
    [Theory]
    [InlineData("sequence-v1.json", 1)]
    [InlineData("sequence-v2.json", 2)]
    [InlineData("sequence-v3.json", 3)]
    [InlineData("sequence-v4.json", 4)]
    [InlineData("sequence-v5.json", 5)]
    [InlineData("sequence-v6.json", 6)]
    public void GoldenSchemaFixtures_MigrateToTheCurrentDocumentShape(
        string fileName,
        int expectedVersion)
    {
        var document = SequenceImportService.ParseFile(FixturePath(fileName));

        Assert.Equal(expectedVersion, document.SourceVersion);
        switch (expectedVersion)
        {
            case 1:
                Assert.Equal("Legacy relative", document.Name);
                Assert.True(document.Loop);
                Assert.Equal(new[] { 0, 100, 350 }, document.Steps.Select(step => step.StartMs));
                Assert.Empty(document.Tracks);
                Assert.Empty(document.AudioLanes);
                break;
            case 2:
                Assert.Equal("Absolute gestures", document.Name);
                Assert.False(document.Loop);
                Assert.Equal(new[] { 250, 1_250 }, document.Steps.Select(step => step.StartMs));
                Assert.Empty(document.Tracks);
                Assert.Empty(document.AudioLanes);
                break;
            case 3:
                Assert.Equal("Audio lanes", document.Name);
                Assert.Equal("VOICE", Assert.Single(document.AudioLanes).Label);
                var voice = Assert.Single(document.AudioLanes[0].Clips);
                Assert.Equal(@"C:\fixtures\voice.wav", voice.FilePath);
                Assert.Equal((1_750, 125, false), (voice.DurationMs, voice.StartMs, voice.Loop));
                Assert.Empty(document.Tracks);
                break;
            case 4:
                Assert.Equal("Current document", document.Name);
                Assert.Equal(new ushort[] { 0x4001, 0x4002 }, document.Tracks.Select(track => track.Id));
                Assert.Equal(new[] { "AMBIENT", "VOICE" }, document.AudioLanes.Select(lane => lane.Label));
                Assert.Equal(new[] { 7, 8 }, document.Steps.Select(step => step.AnimId));
                break;
            case 5:
                Assert.Equal("Infinite endpoint", document.Name);
                Assert.Equal(2_250, Assert.Single(document.Steps).EndAfterMs);
                Assert.Null(document.EndMs);
                break;
            case 6:
                Assert.Equal("Explicit scene end", document.Name);
                Assert.True(document.Loop);
                Assert.Equal(5_000, document.EndMs);
                Assert.True(Assert.Single(document.AudioLanes[0].Clips).Loop);
                break;
        }
    }

    [Fact]
    public void Version4InfiniteGesture_MigratesFormerDisplayWidthToARealEndpoint()
    {
        const string json = """
            {
              "type":"b1-sequence", "version":4, "name":"Legacy loop", "loop":false,
              "tracks":[], "audioLanes":[],
              "steps":[{"animId":17,"target":65535,"startMs":250}]
            }
            """;

        var document = SequenceImportService.Parse(json);

        Assert.Equal(AnimationDurationProvider.DefaultInfiniteEndMs,
            Assert.Single(document.Steps).EndAfterMs);
    }

    [Fact]
    public void CurrentSchemaInfiniteGesture_RequiresItsExplicitEndpoint()
    {
        var root = ValidCurrentDocument();
        root["steps"]![0]!["animId"] = 17;

        var error = Assert.Throws<SequenceImportException>(() =>
            SequenceImportService.Parse(root.ToJsonString()));

        Assert.Equal("$.steps[0].endAfterMs", error.FieldPath);
    }

    [Fact]
    public void Version5MigratesToAutomaticSceneEnd()
    {
        var root = ValidCurrentDocument();
        root["version"] = 5;
        root.Remove("endMs");

        var document = SequenceImportService.Parse(root.ToJsonString());

        Assert.Equal(5, document.SourceVersion);
        Assert.Null(document.EndMs);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    [InlineData(12345)]
    public void Version6AcceptsAutomaticOrBoundedExplicitSceneEnd(int? endMs)
    {
        var root = ValidCurrentDocument();
        root["endMs"] = endMs;

        var document = SequenceImportService.Parse(root.ToJsonString());

        Assert.Equal(endMs, document.EndMs);
    }

    [Theory]
    [InlineData("wrong-type", "$.type")]
    [InlineData("future-version", "$.version")]
    [InlineData("invalid-animation", "$.steps[0].animId")]
    [InlineData("reserved-target", "$.steps[0].target")]
    [InlineData("target-overflow", "$.steps[0].target")]
    [InlineData("negative-start", "$.steps[0].startMs")]
    [InlineData("negative-duration", "$.audioLanes[0].clips[0].durationMs")]
    [InlineData("blank-lane-label", "$.audioLanes[0].label")]
    [InlineData("audio-end-overflow", "$.audioLanes[0].clips[0].durationMs")]
    [InlineData("duplicate-track", "$.tracks[1].id")]
    [InlineData("broadcast-track", "$.tracks[0].id")]
    [InlineData("too-many-tracks", "$.tracks")]
    [InlineData("too-many-steps", "$.steps")]
    [InlineData("too-many-lanes", "$.audioLanes")]
    [InlineData("too-many-clips", "$.audioLanes[0].clips")]
    [InlineData("missing-steps", "$.steps")]
    [InlineData("missing-end", "$.endMs")]
    [InlineData("invalid-end", "$.endMs")]
    [InlineData("negative-end", "$.endMs")]
    public void SchemaValidation_RejectsUnsafeOrUnboundedDocuments(
        string scenario,
        string expectedPath)
    {
        var invalid = BuildInvalidCurrentDocument(scenario);

        var error = Assert.Throws<SequenceImportException>(() =>
            SequenceImportService.Parse(invalid.ToJsonString()));

        Assert.Equal(expectedPath, error.FieldPath);
        Assert.StartsWith(expectedPath + ":", error.Message);
    }

    [Fact]
    public void SchemaValidation_ReportsMalformedJsonLocation()
    {
        var error = Assert.Throws<SequenceImportException>(() =>
            SequenceImportService.Parse("{ \"type\": \"b1-sequence\", ]"));

        Assert.Equal("$", error.FieldPath);
        Assert.Contains("invalid JSON", error.Message);
        Assert.Contains("line", error.Message);
    }

    [Fact]
    public void SchemaValidation_AcceptsDocumentedNumericAndLengthBoundaries()
    {
        var root = ValidCurrentDocument();
        root["name"] = new string('N', SequenceImportService.MaxSequenceNameLength);
        root["tracks"]![0]!["id"] = ushort.MaxValue - 1;
        root["tracks"]![0]!["name"] = new string('T', SequenceImportService.MaxTrackNameLength);
        root["audioLanes"]![0]!["label"] = new string('L', SequenceImportService.MaxLaneLabelLength);
        var clip = root["audioLanes"]![0]!["clips"]![0]!;
        clip["filePath"] = new string('p', SequenceImportService.MaxAudioPathLength);
        clip["startMs"] = SequenceImportService.MaxTimelineMs;
        clip["durationMs"] = 0;
        var step = root["steps"]![0]!;
        step["animId"] = 17;
        step["target"] = ushort.MaxValue;
        step["startMs"] = SequenceImportService.MaxTimelineMs - 100;
        step["endAfterMs"] = 100;

        var document = SequenceImportService.Parse(root.ToJsonString());

        Assert.Equal(SequenceImportService.MaxTimelineMs - 100, document.Steps[0].StartMs);
        Assert.Equal(100, document.Steps[0].EndAfterMs);
        Assert.Equal(0, document.AudioLanes[0].Clips[0].DurationMs);
    }

    [Fact]
    public void VersionedTimingFields_RejectAmbiguousOrMislabelledFiles()
    {
        const string ambiguousV1 = """
            {
              "type":"b1-sequence", "version":1, "name":"", "loop":false,
              "steps":[{"animId":1,"target":65535,"delayMs":100,"startMs":0}]
            }
            """;
        const string relativeFieldInV2 = """
            {
              "type":"b1-sequence", "version":2, "name":"", "loop":false,
              "steps":[{"animId":1,"target":65535,"delayMs":100}]
            }
            """;
        var overflowingV1 = $$"""
            {
              "type":"b1-sequence", "version":1, "name":"", "loop":false,
              "steps":[
                {"animId":1,"target":65535,"delayMs":{{SequenceImportService.MaxTimelineMs}}},
                {"animId":2,"target":65535,"delayMs":1}
              ]
            }
            """;

        Assert.Equal("$.steps[0].startMs",
            Assert.Throws<SequenceImportException>(() =>
                SequenceImportService.Parse(ambiguousV1)).FieldPath);
        Assert.Equal("$.steps[0].delayMs",
            Assert.Throws<SequenceImportException>(() =>
                SequenceImportService.Parse(relativeFieldInV2)).FieldPath);
        Assert.Equal("$.steps[1].delayMs",
            Assert.Throws<SequenceImportException>(() =>
                SequenceImportService.Parse(overflowingV1)).FieldPath);
    }

    [Theory]
    [InlineData("metadata")]
    [InlineData("tracks")]
    [InlineData("audio")]
    [InlineData("steps")]
    public void FailedImport_LeavesDocumentHistorySelectionAndTransientStateUntouched(
        string failingSection)
    {
        using var fixture = new TemporaryJsonFixture();
        using var vm = CreateViewModel();
        vm.ImportFrom(FixturePath("sequence-v4.json"));
        var selected = vm.Steps[0];
        vm.SelectedStep = selected;
        vm.ArmedTrack = vm.Tracks.Single(track => track.Id == 0x4002);
        vm.PlayheadMs = 777;
        Assert.True(vm.SetSequenceName("Working copy"));
        var before = Fingerprint(vm);
        var invalidPath = fixture.Write("invalid.b1seq.json", InvalidMajorSection(failingSection));

        Assert.Throws<SequenceImportException>(() => vm.ImportFrom(invalidPath));

        Assert.Equal(before, Fingerprint(vm));
        Assert.Same(selected, vm.SelectedStep);
        Assert.Equal((ushort)0x4002, vm.ArmedTrack?.Id);
        Assert.Equal(777, vm.PlayheadMs);
        Assert.True(vm.Dirty);
        Assert.True(vm.UndoCommand.CanExecute(null));
        Assert.False(vm.RedoCommand.CanExecute(null));
    }

    [Fact]
    public void SuccessfulImport_AppliesValidatedDocumentAsOneReplacementBoundary()
    {
        using var vm = CreateViewModel();
        vm.Steps.Add(new SequenceStep { AnimId = 17, Target = 0x2222, StartMs = 999 });
        vm.SelectedStep = vm.Steps[0];
        Assert.True(vm.SetSequenceName("Unsaved"));

        vm.ImportFrom(FixturePath("sequence-v1.json"));

        Assert.Equal("Legacy relative", vm.Name);
        Assert.Equal(new[] { 0, 100, 350 }, vm.Steps.Select(step => step.StartMs));
        Assert.Equal(new[] { "AMBIENT", "AUDIO" }, vm.AudioLanes.Select(lane => lane.Label));
        Assert.Null(vm.SelectedStep);
        Assert.False(vm.Dirty);
        Assert.False(vm.UndoCommand.CanExecute(null));
        Assert.False(vm.RedoCommand.CanExecute(null));
    }

    [Fact]
    public void CurrentSchema_ExplicitEmptyAudioLaneListRemainsEmpty()
    {
        using var fixture = new TemporaryJsonFixture();
        using var vm = CreateViewModel();
        var root = ValidCurrentDocument();
        root["audioLanes"] = new JsonArray();

        vm.ImportFrom(fixture.Write("empty-audio.b1seq.json", root.ToJsonString()));

        Assert.Empty(vm.AudioLanes);
        vm.AddAudioLaneCommand.Execute(null);
        Assert.Single(vm.AudioLanes);
        vm.UndoCommand.Execute(null);
        Assert.Empty(vm.AudioLanes);
        vm.RedoCommand.Execute(null);
        Assert.Single(vm.AudioLanes);
    }

    private static JsonObject BuildInvalidCurrentDocument(string scenario)
    {
        var root = ValidCurrentDocument();
        var steps = root["steps"]!.AsArray();
        var tracks = root["tracks"]!.AsArray();
        var lanes = root["audioLanes"]!.AsArray();
        var clips = lanes[0]!["clips"]!.AsArray();

        switch (scenario)
        {
            case "wrong-type": root["type"] = "b1-backup"; break;
            case "future-version": root["version"] = SequenceImportService.CurrentVersion + 1; break;
            case "invalid-animation": steps[0]!["animId"] = 18; break;
            case "reserved-target": steps[0]!["target"] = 0; break;
            case "target-overflow": steps[0]!["target"] = 65_536; break;
            case "negative-start": steps[0]!["startMs"] = -1; break;
            case "negative-duration": clips[0]!["durationMs"] = -1; break;
            case "blank-lane-label": lanes[0]!["label"] = "   "; break;
            case "audio-end-overflow":
                clips[0]!["startMs"] = SequenceImportService.MaxTimelineMs;
                clips[0]!["durationMs"] = 1;
                break;
            case "duplicate-track":
                tracks.Add(new JsonObject { ["id"] = 0x4001, ["name"] = "duplicate" });
                break;
            case "broadcast-track": tracks[0]!["id"] = ushort.MaxValue; break;
            case "too-many-tracks":
                root["tracks"] = ArrayOf(SequenceImportService.MaxTracks + 1,
                    index => new JsonObject { ["id"] = index + 1, ["name"] = $"Droid {index}" });
                break;
            case "too-many-steps":
                root["steps"] = ArrayOf(SequenceImportService.MaxSteps + 1,
                    _ => new JsonObject { ["animId"] = 1, ["target"] = ushort.MaxValue, ["startMs"] = 0 });
                break;
            case "too-many-lanes":
                root["audioLanes"] = ArrayOf(SequenceImportService.MaxAudioLanes + 1,
                    index => new JsonObject { ["label"] = $"Lane {index}", ["clips"] = new JsonArray() });
                break;
            case "too-many-clips":
                lanes[0]!["clips"] = ArrayOf(SequenceImportService.MaxAudioClips + 1,
                    _ => new JsonObject
                    {
                        ["filePath"] = "audio.wav", ["durationMs"] = 1,
                        ["startMs"] = 0, ["loop"] = false,
                    });
                break;
            case "missing-steps": root.Remove("steps"); break;
            case "missing-end": root.Remove("endMs"); break;
            case "invalid-end": root["endMs"] = "late"; break;
            case "negative-end": root["endMs"] = -1; break;
            default: throw new ArgumentOutOfRangeException(nameof(scenario));
        }
        return root;
    }

    private static JsonArray ArrayOf(int count, Func<int, JsonNode> factory) =>
        new(Enumerable.Range(0, count).Select(factory).ToArray());

    private static JsonObject ValidCurrentDocument() => new()
    {
        ["type"] = SequenceImportService.SchemaType,
        ["version"] = SequenceImportService.CurrentVersion,
        ["name"] = "Valid",
        ["loop"] = false,
        ["endMs"] = null,
        ["tracks"] = new JsonArray(
            new JsonObject { ["id"] = 0x4001, ["name"] = "R2-D2" }),
        ["audioLanes"] = new JsonArray(
            new JsonObject
            {
                ["label"] = "VOICE",
                ["clips"] = new JsonArray(
                    new JsonObject
                    {
                        ["filePath"] = "voice.wav", ["durationMs"] = 100,
                        ["startMs"] = 50, ["loop"] = false,
                    }),
            }),
        ["steps"] = new JsonArray(
            new JsonObject { ["animId"] = 2, ["target"] = 0x4001, ["startMs"] = 100 }),
    };

    private static string InvalidMajorSection(string section) => section switch
    {
        "metadata" => """{"type":"b1-sequence","version":4,"name":7,"loop":false,"tracks":[],"audioLanes":[],"steps":[]}""",
        "tracks" => """{"type":"b1-sequence","version":4,"name":"New","loop":false,"tracks":"bad","audioLanes":[],"steps":[]}""",
        "audio" => """{"type":"b1-sequence","version":4,"name":"New","loop":false,"tracks":[],"audioLanes":[{"label":"","clips":[]}],"steps":[]}""",
        "steps" => """{"type":"b1-sequence","version":4,"name":"New","loop":false,"tracks":[],"audioLanes":[],"steps":[{"animId":99,"target":65535,"startMs":0}]}""",
        _ => throw new ArgumentOutOfRangeException(nameof(section)),
    };

    private static string Fingerprint(SequencerViewModel vm)
    {
        var tracks = string.Join(";", vm.Tracks.Select(track => $"{track.Id}:{track.Label}"));
        var steps = string.Join(";", vm.Steps.Select(step => $"{step.AnimId},{step.Target},{step.StartMs}"));
        var lanes = string.Join(";", vm.AudioLanes.Select(lane =>
            $"{lane.Label}[{string.Join("/", lane.Clips.Select(clip => $"{clip.FilePath},{clip.DurationMs},{clip.StartMs},{clip.Loop}"))}]"));
        return $"{vm.Name}|{vm.Loop}|{tracks}|{steps}|{lanes}";
    }

    private static SequencerViewModel CreateViewModel() => new(
        new FakeSequencerProtocol(),
        new FakeSequencerSettings(),
        new FakeAudioPlayer(),
        new FakePlaybackTimerScheduler(),
        new FakePlaybackClock(),
        new FakePlaybackTimerScheduler(),
        library: new FakeSequenceLibraryService());

    private static string FixturePath(string fileName) =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "Sequences", fileName);
}
