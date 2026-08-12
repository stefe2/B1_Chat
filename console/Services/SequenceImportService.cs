using System.IO;
using System.Text.Json;
using b1_chat_console.Models;

namespace b1_chat_console.Services;

/// <summary>
/// Strict, side-effect-free reader for exported Sequencer documents. Every supported schema
/// is migrated into the current in-memory DTO shape before the ViewModel is allowed to mutate.
/// </summary>
internal static class SequenceImportService
{
    internal const string SchemaType = "b1-sequence";
    internal const int CurrentVersion = 5;
    internal const int MaxSequenceNameLength = 128;
    internal const int MaxTrackNameLength = 128;
    internal const int MaxLaneLabelLength = 64;
    internal const int MaxAudioPathLength = 32_767;
    internal const int MaxTracks = 256;
    internal const int MaxSteps = 10_000;
    internal const int MaxAudioLanes = 64;
    internal const int MaxAudioClips = 10_000;
    internal const int MaxTimelineMs = 86_400_000; // 24 hours

    internal static ImportedSequenceDocument ParseFile(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return Parse(File.ReadAllText(path));
    }

    internal static ImportedSequenceDocument Parse(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        JsonDocument parsed;
        try
        {
            parsed = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            var location = ex.LineNumber.HasValue
                ? $" at line {ex.LineNumber.Value + 1}, byte {ex.BytePositionInLine.GetValueOrDefault() + 1}"
                : "";
            throw new SequenceImportException("$", $"invalid JSON{location}", ex);
        }

        using (parsed)
        {
            var root = parsed.RootElement;
            RequireKind(root, JsonValueKind.Object, "$", "an object");

            var type = ReadString(root, "type", "$", maxLength: 64, allowEmpty: false);
            if (!string.Equals(type, SchemaType, StringComparison.Ordinal))
                throw Error("$.type", $"expected \"{SchemaType}\", found \"{type}\"");

            var version = ReadInt(root, "version", "$", 1, int.MaxValue);
            if (version > CurrentVersion)
                throw Error("$.version", $"schema version {version} is newer than supported version {CurrentVersion}");

            var name = ReadString(root, "name", "$", MaxSequenceNameLength, allowEmpty: true);
            var loop = ReadBoolean(root, "loop", "$", required: true);

            return version switch
            {
                1 => MigrateVersion1(root, name, loop),
                2 => MigrateVersion2(root, name, loop),
                3 => MigrateVersion3(root, name, loop),
                4 => ReadVersion4(root, name, loop),
                5 => ReadVersion5(root, name, loop),
                _ => throw Error("$.version", $"unsupported schema version {version}"),
            };
        }
    }

    // Version 1 stored the wait after each gesture. The historical player dispatched the
    // current step immediately, then waited delayMs before dispatching the next one.
    private static ImportedSequenceDocument MigrateVersion1(JsonElement root, string name, bool loop)
    {
        ValidateRetiredAudioTrack(root);
        var steps = ReadSteps(root, relativeDelays: true);
        return new ImportedSequenceDocument(1, name, loop, new(), new(), steps);
    }

    // Version 2 introduced absolute gesture startMs but still referenced the retired master
    // audio-track number, which has no console-side file-path equivalent and is discarded.
    private static ImportedSequenceDocument MigrateVersion2(JsonElement root, string name, bool loop)
    {
        ValidateRetiredAudioTrack(root);
        var steps = ReadSteps(root, relativeDelays: false);
        return new ImportedSequenceDocument(2, name, loop, new(), new(), steps);
    }

    // Version 3 introduced console-side audio lanes. Early v3 exports also retained the old
    // audioTrack metadata; accept and validate it, but do not pretend it maps to an audio file.
    private static ImportedSequenceDocument MigrateVersion3(JsonElement root, string name, bool loop)
    {
        ValidateRetiredAudioTrack(root);
        var lanes = ReadAudioLanes(root);
        var steps = ReadSteps(root, relativeDelays: false);
        return new ImportedSequenceDocument(3, name, loop, new(), lanes, steps);
    }

    // Version 4 added the saved offline droid roster.
    private static ImportedSequenceDocument ReadVersion4(JsonElement root, string name, bool loop)
    {
        var tracks = ReadTracks(root);
        var lanes = ReadAudioLanes(root);
        var steps = ReadSteps(root, relativeDelays: false);
        return new ImportedSequenceDocument(4, name, loop, tracks, lanes, steps);
    }

    // Version 5 persists the real endpoint of infinite gesture clips. Versions 1-4 migrate
    // to the historical two-second drawing, now promoted to an actual IDLE termination.
    private static ImportedSequenceDocument ReadVersion5(JsonElement root, string name, bool loop)
    {
        var tracks = ReadTracks(root);
        var lanes = ReadAudioLanes(root);
        var steps = ReadSteps(root, relativeDelays: false, explicitInfiniteEnds: true);
        return new ImportedSequenceDocument(5, name, loop, tracks, lanes, steps);
    }

    private static List<SequenceTrackDto> ReadTracks(JsonElement root)
    {
        var array = ReadArray(root, "tracks", "$", MaxTracks);
        var tracks = new List<SequenceTrackDto>(array.GetArrayLength());
        var ids = new HashSet<ushort>();
        var index = 0;
        foreach (var element in array.EnumerateArray())
        {
            var path = $"$.tracks[{index}]";
            RequireKind(element, JsonValueKind.Object, path, "an object");
            var id = ReadTarget(element, "id", path, allowBroadcast: false);
            if (!ids.Add(id)) throw Error($"{path}.id", $"duplicate track ID {id}");
            tracks.Add(new SequenceTrackDto
            {
                Id = id,
                Name = ReadString(element, "name", path, MaxTrackNameLength, allowEmpty: true),
            });
            index++;
        }
        return tracks;
    }

    private static List<AudioLaneDto> ReadAudioLanes(JsonElement root)
    {
        var array = ReadArray(root, "audioLanes", "$", MaxAudioLanes);
        var lanes = new List<AudioLaneDto>(array.GetArrayLength());
        var totalClips = 0;
        var laneIndex = 0;
        foreach (var element in array.EnumerateArray())
        {
            var lanePath = $"$.audioLanes[{laneIndex}]";
            RequireKind(element, JsonValueKind.Object, lanePath, "an object");
            var lane = new AudioLaneDto
            {
                Label = ReadString(element, "label", lanePath, MaxLaneLabelLength, allowEmpty: false),
            };
            var clips = ReadArray(element, "clips", lanePath, MaxAudioClips);
            totalClips = checked(totalClips + clips.GetArrayLength());
            if (totalClips > MaxAudioClips)
                throw Error($"{lanePath}.clips", $"document exceeds the maximum of {MaxAudioClips} audio clips");

            var clipIndex = 0;
            foreach (var clipElement in clips.EnumerateArray())
            {
                var clipPath = $"{lanePath}.clips[{clipIndex}]";
                RequireKind(clipElement, JsonValueKind.Object, clipPath, "an object");
                var startMs = ReadInt(clipElement, "startMs", clipPath, 0, MaxTimelineMs);
                var durationMs = ReadInt(clipElement, "durationMs", clipPath, 0, MaxTimelineMs);
                var endMs = checked((long)startMs + durationMs);
                if (endMs > MaxTimelineMs)
                    throw Error($"{clipPath}.durationMs", $"clip end {endMs} ms exceeds the {MaxTimelineMs} ms timeline limit");

                lane.Clips.Add(new AudioClipDto
                {
                    FilePath = ReadString(clipElement, "filePath", clipPath, MaxAudioPathLength, allowEmpty: false),
                    DurationMs = durationMs,
                    StartMs = startMs,
                    Loop = ReadBoolean(clipElement, "loop", clipPath, required: true),
                });
                clipIndex++;
            }
            lanes.Add(lane);
            laneIndex++;
        }
        return lanes;
    }

    private static List<SequenceStepDto> ReadSteps(
        JsonElement root,
        bool relativeDelays,
        bool explicitInfiniteEnds = false)
    {
        var array = ReadArray(root, "steps", "$", MaxSteps);
        var steps = new List<SequenceStepDto>(array.GetArrayLength());
        long nextStartMs = 0;
        var index = 0;
        foreach (var element in array.EnumerateArray())
        {
            var path = $"$.steps[{index}]";
            RequireKind(element, JsonValueKind.Object, path, "an object");
            var animId = ReadInt(element, "animId", path, 0, 17);
            var target = ReadTarget(element, "target", path, allowBroadcast: true);
            int startMs;
            if (relativeDelays)
            {
                if (element.TryGetProperty("startMs", out _))
                    throw Error($"{path}.startMs", "version 1 is ambiguous when startMs is present; expected delayMs only");
                var delayMs = ReadInt(element, "delayMs", path, 0, MaxTimelineMs);
                startMs = (int)nextStartMs;
                nextStartMs = checked(nextStartMs + delayMs);
                if (nextStartMs > MaxTimelineMs)
                    throw Error($"{path}.delayMs", $"cumulative delay exceeds the {MaxTimelineMs} ms timeline limit");
            }
            else
            {
                if (element.TryGetProperty("delayMs", out _))
                    throw Error($"{path}.delayMs", "relative delayMs is valid only in schema version 1");
                startMs = ReadInt(element, "startMs", path, 0, MaxTimelineMs);
            }

            var endAfterMs = AnimationDurationProvider.DefaultInfiniteEndMs;
            if (explicitInfiniteEnds)
            {
                if (element.TryGetProperty("endAfterMs", out _))
                    endAfterMs = ReadInt(element, "endAfterMs", path, 100, MaxTimelineMs);
                else if (animId is 16 or 17)
                    throw Error($"{path}.endAfterMs", "infinite gestures require an explicit endpoint in schema version 5");
            }
            if ((long)startMs + endAfterMs > MaxTimelineMs && animId is 16 or 17)
                throw Error($"{path}.endAfterMs", "infinite gesture end exceeds the timeline limit");

            steps.Add(new SequenceStepDto
            {
                AnimId = animId,
                Target = target,
                StartMs = startMs,
                EndAfterMs = endAfterMs,
            });
            index++;
        }
        return steps;
    }

    private static void ValidateRetiredAudioTrack(JsonElement root)
    {
        if (!root.TryGetProperty("audioTrack", out var audioTrack)) return;
        if (audioTrack.ValueKind != JsonValueKind.Number || !audioTrack.TryGetInt32(out var value))
            throw Error("$.audioTrack", "expected an integer");
        if (value is < 0 or > 255)
            throw Error("$.audioTrack", "expected a value from 0 to 255");
    }

    private static JsonElement ReadArray(
        JsonElement parent,
        string property,
        string parentPath,
        int maxCount)
    {
        var value = Required(parent, property, parentPath);
        var path = $"{parentPath}.{property}";
        RequireKind(value, JsonValueKind.Array, path, "an array");
        if (value.GetArrayLength() > maxCount)
            throw Error(path, $"contains {value.GetArrayLength()} items; maximum is {maxCount}");
        return value;
    }

    private static ushort ReadTarget(
        JsonElement parent,
        string property,
        string parentPath,
        bool allowBroadcast)
    {
        var value = ReadInt(parent, property, parentPath, 0, ushort.MaxValue);
        if (value == 0)
            throw Error($"{parentPath}.{property}", "target ID 0 is reserved and cannot be used");
        if (!allowBroadcast && value == ushort.MaxValue)
            throw Error($"{parentPath}.{property}", "the broadcast ID 65535 is not a physical track");
        return (ushort)value;
    }

    private static int ReadInt(
        JsonElement parent,
        string property,
        string parentPath,
        int minimum,
        int maximum)
    {
        var value = Required(parent, property, parentPath);
        var path = $"{parentPath}.{property}";
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var number))
            throw Error(path, "expected a 32-bit integer");
        if (number < minimum || number > maximum)
            throw Error(path, $"expected a value from {minimum} to {maximum}, found {number}");
        return number;
    }

    private static bool ReadBoolean(
        JsonElement parent,
        string property,
        string parentPath,
        bool required)
    {
        if (!parent.TryGetProperty(property, out var value))
        {
            if (!required) return false;
            throw Error($"{parentPath}.{property}", "required field is missing");
        }
        if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw Error($"{parentPath}.{property}", "expected true or false");
        return value.GetBoolean();
    }

    private static string ReadString(
        JsonElement parent,
        string property,
        string parentPath,
        int maxLength,
        bool allowEmpty)
    {
        var value = Required(parent, property, parentPath);
        var path = $"{parentPath}.{property}";
        if (value.ValueKind != JsonValueKind.String)
            throw Error(path, "expected a string");
        var text = value.GetString() ?? "";
        if (!allowEmpty && string.IsNullOrWhiteSpace(text))
            throw Error(path, "must not be empty or whitespace");
        if (text.Length > maxLength)
            throw Error(path, $"length {text.Length} exceeds the maximum of {maxLength}");
        return text;
    }

    private static JsonElement Required(JsonElement parent, string property, string parentPath)
    {
        if (!parent.TryGetProperty(property, out var value))
            throw Error($"{parentPath}.{property}", "required field is missing");
        return value;
    }

    private static void RequireKind(
        JsonElement value,
        JsonValueKind kind,
        string path,
        string description)
    {
        if (value.ValueKind != kind)
            throw Error(path, $"expected {description}, found {value.ValueKind}");
    }

    private static SequenceImportException Error(string path, string message) => new(path, message);
}

internal sealed record ImportedSequenceDocument(
    int SourceVersion,
    string Name,
    bool Loop,
    List<SequenceTrackDto> Tracks,
    List<AudioLaneDto> AudioLanes,
    List<SequenceStepDto> Steps);

internal sealed class SequenceImportException : Exception
{
    internal SequenceImportException(string path, string message, Exception? innerException = null)
        : base($"{path}: {message}", innerException)
    {
        FieldPath = path;
    }

    internal string FieldPath { get; }
}
