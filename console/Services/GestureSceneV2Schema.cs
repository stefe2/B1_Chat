using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using b1_chat_console.Models;

namespace b1_chat_console.Services;

/// <summary>
/// Strict, side-effect-free V2 schema readers. They deliberately have no dependency on the
/// legacy Sequencer document or current firmware protocol; Stage 4 will connect this boundary
/// to generated catalog and wire artifacts.
/// </summary>
internal static class GestureCatalogV2Parser
{
    internal const string SchemaType = "b1-gesture-catalog";
    internal const int CurrentVersion = 1;

    internal static GestureCatalogV2 Parse(string json)
    {
        using var document = Schema.ReadJson(json);
        var root = document.RootElement;
        Schema.Fields(root, "$", new[] { "type", "version", "catalogId", "revision", "hash", "gestures" });
        Schema.Equal(Schema.String(root, "type", "$", 64), SchemaType, "$.type");
        Schema.Equal(Schema.Int(root, "version", "$", 1, CurrentVersion), CurrentVersion, "$.version");

        var identity = new GestureCatalogIdentity(
            Schema.Key(Schema.String(root, "catalogId", "$", 64), "$.catalogId"),
            Schema.Token(Schema.String(root, "revision", "$", 64), "$.revision"),
            Schema.Hash(Schema.String(root, "hash", "$", 72), "$.hash"));
        var values = new Dictionary<string, GestureDefinitionV2>(StringComparer.Ordinal);
        var gestures = Schema.Array(root, "gestures", "$", 1, 512);
        var index = 0;
        foreach (var value in gestures.EnumerateArray())
        {
            var path = $"$.gestures[{index++}]";
            Schema.Fields(value, path, new[]
            {
                "key", "displayName", "description", "family", "tags", "execution", "endBehavior", "composition",
                "tempos", "intensities", "variants", "seedPolicy", "minimumMotionEngine",
                "auditionSafe", "broadcastSafe", "trajectory"
            });
            var key = Schema.Key(Schema.String(value, "key", path, 96), $"{path}.key");
            if (!values.TryAdd(key, ReadGesture(value, path, key)))
                throw Schema.Error($"{path}.key", $"duplicate gesture key \"{key}\"");
        }
        var declaredHash = CatalogIntegrity.DeclaredHash(json);
        var computedHash = CatalogIntegrity.ComputeHash(json);
        if (!string.Equals(declaredHash, computedHash, StringComparison.Ordinal))
            throw Schema.Error("$.hash", "catalog content does not match its declared hash");
        return new GestureCatalogV2(identity, values);
    }

    private static GestureDefinitionV2 ReadGesture(JsonElement value, string path, string key)
    {
        var execution = Schema.Enum(Schema.String(value, "execution", path, 16), $"{path}.execution",
            ("immediate", GestureExecutionKind.Immediate), ("finite", GestureExecutionKind.Finite),
            ("continuous", GestureExecutionKind.Continuous));
        var tempos = new Dictionary<string, GestureTempoDefinition>(StringComparer.Ordinal);
        var tempoValues = Schema.Array(value, "tempos", path, 1, 3);
        var tempoIndex = 0;
        foreach (var tempo in tempoValues.EnumerateArray())
        {
            var tempoPath = $"{path}.tempos[{tempoIndex++}]";
            Schema.Fields(tempo, tempoPath, new[] { "key", "durationMs" });
            var tempoKey = Schema.Enum(Schema.String(tempo, "key", tempoPath, 8), $"{tempoPath}.key",
                ("slow", "slow"), ("normal", "normal"), ("fast", "fast"));
            var duration = Schema.Int(tempo, "durationMs", tempoPath, execution == GestureExecutionKind.Immediate ? 0 : 100, Schema.MaxTimelineMs);
            if (execution == GestureExecutionKind.Immediate && duration != 0)
                throw Schema.Error($"{tempoPath}.durationMs", "immediate gestures must have a 0 ms duration");
            if (!tempos.TryAdd(tempoKey, new GestureTempoDefinition(tempoKey, duration)))
                throw Schema.Error($"{tempoPath}.key", $"duplicate tempo \"{tempoKey}\"");
        }
        if (!tempos.ContainsKey("normal")) throw Schema.Error($"{path}.tempos", "a normal tempo is required");

        var intensities = Schema.TokenSet(value, "intensities", path, 1, 3, "soft", "normal", "strong");
        if (!intensities.Contains("normal")) throw Schema.Error($"{path}.intensities", "a normal intensity is required");
        var variants = Schema.TokenSet(value, "variants", path, 1, 32);
        if (!variants.Contains("default")) throw Schema.Error($"{path}.variants", "a default variant is required");
        var seedPolicy = Schema.Enum(Schema.String(value, "seedPolicy", path, 16), $"{path}.seedPolicy",
            ("required", true), ("ignored", false));
        var endBehavior = Schema.Enum(Schema.String(value, "endBehavior", path, 24), $"{path}.endBehavior",
            ("resetAll", "resetAll"), ("holdPose", "holdPose"), ("clearLayer", "clearLayer"));
        var composition = ReadComposition(value, path, execution, endBehavior);

        return new GestureDefinitionV2(
            key,
            Schema.String(value, "displayName", path, 96),
            Schema.String(value, "description", path, 512),
            Schema.Token(Schema.String(value, "family", path, 48), $"{path}.family"),
            Schema.StringArray(value, "tags", path, 0, 16, 48), execution, endBehavior, composition, tempos,
            intensities, variants, seedPolicy,
            Schema.Int(value, "minimumMotionEngine", path, 1, 255),
            Schema.Bool(value, "auditionSafe", path), Schema.Bool(value, "broadcastSafe", path),
            ReadTrajectory(value, path, execution, tempos));
    }

    private static GestureCompositionV2 ReadComposition(JsonElement value, string path,
        GestureExecutionKind execution, string endBehavior)
    {
        var composition = Schema.Object(value, "composition", path);
        var compositionPath = $"{path}.composition";
        Schema.Fields(composition, compositionPath, new[] { "layer", "axes" });
        var layer = Schema.Enum(Schema.String(composition, "layer", compositionPath, 16),
            $"{compositionPath}.layer", ("reset", "reset"), ("base", "base"), ("overlay", "overlay"));
        var axes = Schema.TokenSet(composition, "axes", compositionPath, 1, 2, "pan", "tilt");
        if (layer == "reset" && (execution != GestureExecutionKind.Immediate || endBehavior != "resetAll" || axes.Count != 2))
            throw Schema.Error(compositionPath, "reset gestures must be immediate, resetAll and control pan plus tilt");
        if (layer != "reset" && (execution == GestureExecutionKind.Immediate || endBehavior == "resetAll" || axes.Count != 1))
            throw Schema.Error(compositionPath, "base and overlay gestures must be moving single-axis layers");
        if (layer == "overlay" && endBehavior != "clearLayer")
            throw Schema.Error($"{path}.endBehavior", "overlay gestures must clear their own layer when finished");
        if (layer == "base" && endBehavior != "holdPose")
            throw Schema.Error($"{path}.endBehavior", "base gestures must hold their final pose");
        return new GestureCompositionV2(layer, axes);
    }

    private static GestureTrajectoryV2 ReadTrajectory(JsonElement value, string path,
        GestureExecutionKind execution, IReadOnlyDictionary<string, GestureTempoDefinition> tempos)
    {
        var trajectory = Schema.Object(value, "trajectory", path);
        var trajectoryPath = $"{path}.trajectory";
        Schema.Fields(trajectory, trajectoryPath, new[] { "coordinate", "frames" });
        Schema.Equal(Schema.String(trajectory, "coordinate", trajectoryPath, 16), "normalized",
            $"{trajectoryPath}.coordinate");
        var frames = new List<GestureTrajectoryFrameV2>();
        var index = 0;
        foreach (var frame in Schema.Array(trajectory, "frames", trajectoryPath, 0, 64).EnumerateArray())
        {
            var framePath = $"{trajectoryPath}.frames[{index++}]";
            Schema.Fields(frame, framePath, new[] { "pan", "tilt", "moveMs", "holdMs", "easing" });
            frames.Add(new GestureTrajectoryFrameV2(
                Schema.Int(frame, "pan", framePath, -100, 100),
                Schema.Int(frame, "tilt", framePath, -100, 100),
                Schema.Int(frame, "moveMs", framePath, 0, Schema.MaxTimelineMs),
                Schema.Int(frame, "holdMs", framePath, 0, Schema.MaxTimelineMs),
                Schema.Enum(Schema.String(frame, "easing", framePath, 16), $"{framePath}.easing",
                    ("smooth", "smooth"))));
        }
        var total = frames.Sum(frame => (long)frame.MoveMs + frame.HoldMs);
        if (execution == GestureExecutionKind.Immediate && frames.Count != 0)
            throw Schema.Error($"{trajectoryPath}.frames", "immediate gestures must not have trajectory frames");
        if (execution != GestureExecutionKind.Immediate && frames.Count == 0)
            throw Schema.Error($"{trajectoryPath}.frames", "motion gestures require trajectory frames");
        if (execution != GestureExecutionKind.Immediate && total != tempos["normal"].DurationMs)
            throw Schema.Error($"{trajectoryPath}.frames", "trajectory duration must equal the normal tempo duration");
        return new GestureTrajectoryV2(frames);
    }
}

internal static class CatalogIntegrity
{
    private static readonly Regex HashValue = new(
        "(\\\"hash\\\"\\s*:\\s*\\\"sha256:)[0-9a-f]{64}", RegexOptions.Compiled);

    internal static string DeclaredHash(string json)
    {
        var match = HashValue.Match(json);
        return match.Success ? "sha256:" + match.Value[^64..] : string.Empty;
    }

    internal static string ComputeHash(string json)
    {
        var normalized = json.Replace("\r\n", "\n").Replace("\r", "\n");
        var masked = HashValue.Replace(normalized,
            match => match.Groups[1].Value + new string('0', 64));
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(masked));
        return "sha256:" + Convert.ToHexString(digest).ToLowerInvariant();
    }
}

internal static class SceneV2Parser
{
    internal const string SchemaType = "b1-scene";
    internal const int CurrentVersion = 1;

    internal static SceneV2 Parse(string json)
    {
        using var document = Schema.ReadJson(json);
        var root = document.RootElement;
        Schema.Fields(root, "$", new[] { "type", "version", "name", "loop", "endMs", "catalog", "tracks", "audioLanes", "gestureClips" });
        Schema.Equal(Schema.String(root, "type", "$", 64), SchemaType, "$.type");
        Schema.Equal(Schema.Int(root, "version", "$", 1, CurrentVersion), CurrentVersion, "$.version");
        var catalog = ReadIdentity(Schema.Object(root, "catalog", "$"), "$.catalog");
        return new SceneV2(
            Schema.String(root, "name", "$", 128, allowEmpty: true), Schema.Bool(root, "loop", "$"),
            Schema.NullableInt(root, "endMs", "$", 0, Schema.MaxTimelineMs), catalog,
            ReadTracks(root), ReadAudioLanes(root), ReadClips(root));
    }

    internal static void ValidateAgainstCatalog(SceneV2 scene, GestureCatalogV2 catalog)
    {
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentNullException.ThrowIfNull(catalog);
        if (scene.Catalog != catalog.Identity)
            throw Schema.Error("$.catalog", "Scene catalog identity does not match the available catalog");

        var tail = 0L;
        foreach (var clip in scene.GestureClips)
        {
            if (!catalog.Gestures.TryGetValue(clip.GestureKey, out var gesture))
                throw Schema.Error("$.gestureClips", $"unknown gesture key \"{clip.GestureKey}\"");
            if (!gesture.Intensities.Contains(clip.Intensity))
                throw Schema.Error("$.gestureClips", $"gesture \"{clip.GestureKey}\" does not allow intensity \"{clip.Intensity}\"");
            if (!gesture.Tempos.TryGetValue(clip.Tempo, out var tempo))
                throw Schema.Error("$.gestureClips", $"gesture \"{clip.GestureKey}\" does not allow tempo \"{clip.Tempo}\"");
            if (!gesture.Variants.Contains(clip.Variant))
                throw Schema.Error("$.gestureClips", $"gesture \"{clip.GestureKey}\" does not allow variant \"{clip.Variant}\"");
            if (gesture.Execution == GestureExecutionKind.Continuous && clip.HoldMs is null)
                throw Schema.Error("$.gestureClips", $"continuous gesture \"{clip.GestureKey}\" requires holdMs");
            if (gesture.Execution != GestureExecutionKind.Continuous && clip.HoldMs is not null)
                throw Schema.Error("$.gestureClips", $"non-continuous gesture \"{clip.GestureKey}\" must not have holdMs");
            var duration = clip.HoldMs ?? tempo.DurationMs;
            tail = Math.Max(tail, (long)clip.StartMs + duration);
        }
        foreach (var lane in scene.AudioLanes)
            foreach (var clip in lane.Clips) tail = Math.Max(tail, (long)clip.StartMs + clip.DurationMs);
        if (tail > Schema.MaxTimelineMs) throw Schema.Error("$", "content exceeds the maximum timeline length");
        if (scene.EndMs is int endMs && endMs < tail)
            throw Schema.Error("$.endMs", "Scene end cannot truncate its content");
    }

    private static GestureCatalogIdentity ReadIdentity(JsonElement value, string path)
    {
        Schema.Fields(value, path, new[] { "id", "revision", "hash" });
        return new GestureCatalogIdentity(
            Schema.Key(Schema.String(value, "id", path, 64), $"{path}.id"),
            Schema.Token(Schema.String(value, "revision", path, 64), $"{path}.revision"),
            Schema.Hash(Schema.String(value, "hash", path, 72), $"{path}.hash"));
    }

    private static IReadOnlyList<SceneTrackV2> ReadTracks(JsonElement root)
    {
        var values = new List<SceneTrackV2>(); var ids = new HashSet<ushort>(); var index = 0;
        foreach (var value in Schema.Array(root, "tracks", "$", 0, 256).EnumerateArray())
        {
            var path = $"$.tracks[{index++}]"; Schema.Fields(value, path, new[] { "id", "name" });
            var id = Schema.DroidId(value, "id", path);
            if (!ids.Add(id)) throw Schema.Error($"{path}.id", $"duplicate track ID {id}");
            values.Add(new SceneTrackV2(id, Schema.String(value, "name", path, 128, allowEmpty: true)));
        }
        return values;
    }

    private static IReadOnlyList<SceneAudioLaneV2> ReadAudioLanes(JsonElement root)
    {
        var lanes = new List<SceneAudioLaneV2>(); var total = 0; var index = 0;
        foreach (var value in Schema.Array(root, "audioLanes", "$", 0, 64).EnumerateArray())
        {
            var path = $"$.audioLanes[{index++}]"; Schema.Fields(value, path, new[] { "label", "clips" });
            var clips = new List<SceneAudioClipV2>(); var clipIndex = 0;
            foreach (var clip in Schema.Array(value, "clips", path, 0, 10_000).EnumerateArray())
            {
                var clipPath = $"{path}.clips[{clipIndex++}]"; Schema.Fields(clip, clipPath, new[] { "filePath", "durationMs", "startMs", "loop" });
                var duration = Schema.Int(clip, "durationMs", clipPath, 0, Schema.MaxTimelineMs);
                var start = Schema.Int(clip, "startMs", clipPath, 0, Schema.MaxTimelineMs);
                if ((long)duration + start > Schema.MaxTimelineMs) throw Schema.Error($"{clipPath}.durationMs", "clip exceeds the maximum timeline length");
                clips.Add(new SceneAudioClipV2(Schema.String(clip, "filePath", clipPath, 32_767), duration, start, Schema.Bool(clip, "loop", clipPath)));
                if (++total > 10_000) throw Schema.Error("$.audioLanes", "document exceeds 10000 audio clips");
            }
            lanes.Add(new SceneAudioLaneV2(Schema.String(value, "label", path, 64), clips));
        }
        return lanes;
    }

    private static IReadOnlyList<GestureClipV2> ReadClips(JsonElement root)
    {
        var values = new List<GestureClipV2>(); var ids = new HashSet<Guid>(); var index = 0;
        foreach (var value in Schema.Array(root, "gestureClips", "$", 0, 10_000).EnumerateArray())
        {
            var path = $"$.gestureClips[{index++}]";
            Schema.Fields(value, path, new[] { "id", "gestureKey", "target", "startMs", "intensity", "tempo", "variant", "seed", "holdMs" });
            var id = Schema.Guid(value, "id", path);
            if (!ids.Add(id)) throw Schema.Error($"{path}.id", "duplicate clip ID");
            values.Add(new GestureClipV2(id,
                Schema.Key(Schema.String(value, "gestureKey", path, 96), $"{path}.gestureKey"),
                ReadTarget(Schema.Object(value, "target", path), $"{path}.target"),
                Schema.Int(value, "startMs", path, 0, Schema.MaxTimelineMs),
                Schema.Token(Schema.String(value, "intensity", path, 16), $"{path}.intensity"),
                Schema.Token(Schema.String(value, "tempo", path, 16), $"{path}.tempo"),
                Schema.Token(Schema.String(value, "variant", path, 64), $"{path}.variant"),
                Schema.UInt(value, "seed", path), Schema.NullableInt(value, "holdMs", path, 100, Schema.MaxTimelineMs)));
        }
        return values;
    }

    private static SceneTargetV2 ReadTarget(JsonElement value, string path)
    {
        var mode = Schema.String(value, "mode", path, 8);
        if (mode == "all") { Schema.Fields(value, path, new[] { "mode" }); return SceneTargetV2.Broadcast; }
        if (mode != "droid") throw Schema.Error($"{path}.mode", "expected droid or all");
        Schema.Fields(value, path, new[] { "mode", "id" });
        return new SceneTargetV2(false, Schema.DroidId(value, "id", path));
    }
}

internal static class Schema
{
    internal const int MaxTimelineMs = 86_400_000;
    internal static JsonDocument ReadJson(string json)
    {
        try { return JsonDocument.Parse(json); }
        catch (JsonException exception) { throw Error("$", "invalid JSON", exception); }
    }
    internal static void Fields(JsonElement value, string path, IReadOnlyCollection<string> names)
    {
        if (value.ValueKind != JsonValueKind.Object) throw Error(path, "expected an object");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
        {
            if (!seen.Add(property.Name)) throw Error(path, $"duplicate field \"{property.Name}\"");
            if (!names.Contains(property.Name)) throw Error($"{path}.{property.Name}", "unknown field");
        }
        foreach (var name in names) if (!value.TryGetProperty(name, out _)) throw Error($"{path}.{name}", "required field is missing");
    }
    internal static JsonElement Object(JsonElement parent, string name, string path) { var value = Required(parent, name, path); if (value.ValueKind != JsonValueKind.Object) throw Error($"{path}.{name}", "expected an object"); return value; }
    internal static JsonElement Array(JsonElement parent, string name, string path, int minimum, int maximum) { var value = Required(parent, name, path); if (value.ValueKind != JsonValueKind.Array) throw Error($"{path}.{name}", "expected an array"); var count = value.GetArrayLength(); if (count < minimum || count > maximum) throw Error($"{path}.{name}", $"expected {minimum} to {maximum} items"); return value; }
    internal static string String(JsonElement parent, string name, string path, int maxLength, bool allowEmpty = false) { var value = Required(parent, name, path); if (value.ValueKind != JsonValueKind.String) throw Error($"{path}.{name}", "expected a string"); var text = value.GetString() ?? ""; if ((!allowEmpty && string.IsNullOrWhiteSpace(text)) || text.Length > maxLength) throw Error($"{path}.{name}", "invalid string length"); return text; }
    internal static bool Bool(JsonElement parent, string name, string path) { var value = Required(parent, name, path); if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw Error($"{path}.{name}", "expected true or false"); return value.GetBoolean(); }
    internal static int Int(JsonElement parent, string name, string path, int minimum, int maximum) { var value = Required(parent, name, path); if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var number) || number < minimum || number > maximum) throw Error($"{path}.{name}", $"expected an integer from {minimum} to {maximum}"); return number; }
    internal static uint UInt(JsonElement parent, string name, string path) { var value = Required(parent, name, path); if (value.ValueKind != JsonValueKind.Number || !value.TryGetUInt32(out var number)) throw Error($"{path}.{name}", "expected an unsigned 32-bit integer"); return number; }
    internal static int? NullableInt(JsonElement parent, string name, string path, int minimum, int maximum) { var value = Required(parent, name, path); if (value.ValueKind == JsonValueKind.Null) return null; if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var number) || number < minimum || number > maximum) throw Error($"{path}.{name}", $"expected null or an integer from {minimum} to {maximum}"); return number; }
    internal static Guid Guid(JsonElement parent, string name, string path) { var text = String(parent, name, path, 36); if (!System.Guid.TryParseExact(text, "D", out var value)) throw Error($"{path}.{name}", "expected a D-format GUID"); return value; }
    internal static ushort DroidId(JsonElement parent, string name, string path) { var value = Int(parent, name, path, 1, ushort.MaxValue - 1); return (ushort)value; }
    internal static string Key(string value, string path) { if (!System.Text.RegularExpressions.Regex.IsMatch(value, "^[a-z][a-z0-9]*(?:[.-][a-z0-9]+)*$")) throw Error(path, "expected a lowercase dotted key"); return value; }
    internal static string Token(string value, string path) { if (!System.Text.RegularExpressions.Regex.IsMatch(value, "^[a-z][a-z0-9-]*$")) throw Error(path, "expected a lowercase token"); return value; }
    internal static string Hash(string value, string path) { if (!System.Text.RegularExpressions.Regex.IsMatch(value, "^sha256:[0-9a-f]{64}$")) throw Error(path, "expected sha256: followed by 64 lowercase hexadecimal characters"); return value; }
    internal static IReadOnlySet<string> TokenSet(JsonElement parent, string name, string path, int minimum, int maximum, params string[] allowed) { var set = new HashSet<string>(StringComparer.Ordinal); var index = 0; foreach (var value in Array(parent, name, path, minimum, maximum).EnumerateArray()) { if (value.ValueKind != JsonValueKind.String) throw Error($"{path}.{name}[{index}]", "expected a string"); var token = Token(value.GetString() ?? "", $"{path}.{name}[{index++}]"); if (allowed.Length > 0 && !allowed.Contains(token, StringComparer.Ordinal)) throw Error($"{path}.{name}", $"unsupported token \"{token}\""); if (!set.Add(token)) throw Error($"{path}.{name}", $"duplicate token \"{token}\""); } return set; }
    internal static IReadOnlyList<string> StringArray(JsonElement parent, string name, string path, int minimum, int maximum, int itemMax) { var values = new List<string>(); var index = 0; foreach (var value in Array(parent, name, path, minimum, maximum).EnumerateArray()) { if (value.ValueKind != JsonValueKind.String) throw Error($"{path}.{name}[{index}]", "expected a string"); var text = value.GetString() ?? ""; if (string.IsNullOrWhiteSpace(text) || text.Length > itemMax) throw Error($"{path}.{name}[{index}]", "invalid string"); values.Add(text); index++; } return values; }
    internal static T Enum<T>(string value, string path, params (string Key, T Value)[] values) { foreach (var item in values) if (item.Key == value) return item.Value; throw Error(path, $"unsupported value \"{value}\""); }
    internal static void Equal<T>(T value, T expected, string path) where T : IEquatable<T> { if (!value.Equals(expected)) throw Error(path, $"expected \"{expected}\""); }
    internal static JsonElement Required(JsonElement parent, string name, string path) { if (!parent.TryGetProperty(name, out var value)) throw Error($"{path}.{name}", "required field is missing"); return value; }
    internal static GestureSceneV2SchemaException Error(string path, string message, Exception? inner = null) => new(path, message, inner);
}

internal sealed class GestureSceneV2SchemaException : Exception
{
    internal GestureSceneV2SchemaException(string fieldPath, string message, Exception? inner = null) : base($"{fieldPath}: {message}", inner) => FieldPath = fieldPath;
    internal string FieldPath { get; }
}
