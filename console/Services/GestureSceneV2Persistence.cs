using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.IO;
using b1_chat_console.Models;

namespace b1_chat_console.Services;

/// <summary>
/// The only persistence boundary used by the Sequencer from Stage 3B onward.
/// It writes named V2 clips and projects them to generated wire identifiers only
/// at the transient Sequencer execution boundary. Numeric identifiers never enter
/// a Scene and are regenerated from the active catalog after every load.
/// </summary>
internal static class GestureSceneV2Persistence
{
    private const string CatalogRelativePath = "catalog/gesture-catalog-v1.json";
    private static readonly Lazy<GestureCatalogV2> CatalogLoader = new(LoadCatalog);
    private static readonly Lazy<IReadOnlyDictionary<string, int>> AnimIdByKeyLoader = new(
        () => Catalog.Ordered.Select((gesture, id) => (gesture.Key, Id: id))
            .ToDictionary(pair => pair.Key, pair => pair.Id, StringComparer.Ordinal));

    internal static GestureCatalogV2 Catalog => CatalogLoader.Value;
    private static IReadOnlyDictionary<string, int> AnimIdByKey => AnimIdByKeyLoader.Value;

    internal static bool IsSupportedTemporaryAnimId(int animId) => animId >= 0 && animId < Catalog.Ordered.Count;

    // The single place execution kind (immediate/finite/continuous) is looked up by gesture key
    // or animId. Replaces the old "AnimId is 16 or 17" legacy magic numbers (pre-V2 POWER_DOWN/
    // TALK IDs) that broke once a new catalog entry could land on animId 17 by ordinary array
    // position instead of that specific meaning.
    internal static GestureExecutionKind? ExecutionKindFor(string gestureKey) =>
        Catalog.Gestures.TryGetValue(gestureKey, out var gesture) ? gesture.Execution : null;

    internal static GestureExecutionKind? ExecutionKindFor(int animId) =>
        animId >= 0 && animId < Catalog.Ordered.Count ? Catalog.Ordered[animId].Execution : null;

    internal static string Serialize(SequenceSnapshot document, IReadOnlyList<SequenceTrackDto> tracks)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(tracks);

        var scene = ToScene(document, tracks);
        SceneV2Parser.ValidateAgainstCatalog(scene, Catalog);
        return SerializeScene(scene);
    }

    internal static ImportedSceneV2Document Parse(string json)
    {
        var scene = SceneV2Parser.Parse(json);
        SceneV2Parser.ValidateAgainstCatalog(scene, Catalog);
        return new ImportedSceneV2Document(
            scene.Name,
            scene.Loop,
            scene.Tracks.Select(track => new SequenceTrackDto { Id = track.DroidId, Name = track.Name }).ToList(),
            scene.AudioLanes.Select(lane => new AudioLaneDto
            {
                Label = lane.Label,
                Clips = lane.Clips.Select(clip => new AudioClipDto
                {
                    FilePath = clip.FilePath,
                    DurationMs = clip.DurationMs,
                    StartMs = clip.StartMs,
                    Loop = clip.Loop,
                }).ToList(),
            }).ToList(),
            scene.GestureClips.Select(ToStep).ToList(),
            scene.EndMs);
    }

    internal static ImportedSceneV2Document ParseFile(string path) => Parse(File.ReadAllText(path));

    private static GestureCatalogV2 LoadCatalog()
    {
        var path = Path.Combine(AppContext.BaseDirectory,
            CatalogRelativePath.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(path))
            throw new FileNotFoundException("The required V2 gesture catalog is missing beside the console.", path);
        return GestureCatalogV2Parser.Parse(File.ReadAllText(path));
    }

    private static SceneV2 ToScene(SequenceSnapshot document, IReadOnlyList<SequenceTrackDto> tracks)
    {
        var physicalTracks = tracks.Select(track => new SceneTrackV2(track.Id, track.Name)).ToArray();
        var clips = document.Steps.Select(ToClip).ToArray();
        return new SceneV2(
            document.Name,
            document.Loop,
            document.EndMs,
            Catalog.Identity,
            physicalTracks,
            document.AudioLanes.Select(lane => new SceneAudioLaneV2(
                lane.Label,
                lane.Clips.Select(clip => new SceneAudioClipV2(
                    clip.FilePath, clip.DurationMs, clip.StartMs, clip.Loop)).ToArray())).ToArray(),
            clips);
    }

    private static GestureClipV2 ToClip(SequenceStepDto step)
    {
        var gestureKey = ResolveGestureKey(step.GestureKey, step.AnimId);
        var gesture = Catalog.Gestures[gestureKey];
        var id = step.Id == Guid.Empty ? Guid.NewGuid() : step.Id;
        var seed = step.Seed == 0 && gesture.RequiresSeed ? SeedFor(id) : step.Seed;
        return new GestureClipV2(
            id,
            gestureKey,
            step.Target == ushort.MaxValue
                ? SceneTargetV2.Broadcast
                : new SceneTargetV2(false, step.Target),
            step.StartMs,
            string.IsNullOrWhiteSpace(step.Intensity) ? "normal" : step.Intensity,
            string.IsNullOrWhiteSpace(step.Tempo) ? "normal" : step.Tempo,
            string.IsNullOrWhiteSpace(step.Variant) ? "default" : step.Variant,
            seed,
            gesture.Execution == GestureExecutionKind.Continuous ? step.EndAfterMs : null);
    }

    private static SequenceStepDto ToStep(GestureClipV2 clip) => new()
    {
        Id = clip.Id,
        GestureKey = clip.GestureKey,
        Intensity = clip.Intensity,
        Tempo = clip.Tempo,
        Variant = clip.Variant,
        Seed = clip.Seed,
        AnimId = ResolveAnimId(clip.GestureKey),
        Target = clip.Target.IsBroadcast ? ushort.MaxValue : clip.Target.DroidId!.Value,
        StartMs = clip.StartMs,
        EndAfterMs = clip.HoldMs ?? AnimationDurationProvider.DefaultInfiniteEndMs,
    };

    private static string ResolveGestureKey(string authoredKey, int animId)
    {
        if (!string.IsNullOrWhiteSpace(authoredKey))
        {
            if (!Catalog.Gestures.ContainsKey(authoredKey))
                throw new GestureSceneV2PersistenceException(
                    $"The gesture \"{authoredKey}\" is not in the active V2 catalog.");
            return authoredKey;
        }
        if (animId >= 0 && animId < Catalog.Ordered.Count) return Catalog.Ordered[animId].Key;
        throw new GestureSceneV2PersistenceException(
            $"No gesture in the active V2 catalog corresponds to generated identifier {animId}. Replace the clip before saving.");
    }

    private static int ResolveAnimId(string gestureKey) => AnimIdByKey.TryGetValue(gestureKey, out var id)
        ? id
        : throw new GestureSceneV2PersistenceException(
            $"No generated execution identifier exists for V2 gesture \"{gestureKey}\".");

    private static uint SeedFor(Guid id)
    {
        var bytes = SHA256.HashData(id.ToByteArray());
        var seed = BitConverter.ToUInt32(bytes, 0);
        return seed == 0 ? 1u : seed;
    }

    private static string SerializeScene(SceneV2 scene)
    {
        var root = new JsonObject
        {
            ["type"] = SceneV2Parser.SchemaType,
            ["version"] = SceneV2Parser.CurrentVersion,
            ["name"] = scene.Name,
            ["loop"] = scene.Loop,
            ["endMs"] = scene.EndMs,
            ["catalog"] = new JsonObject
            {
                ["id"] = scene.Catalog.Id,
                ["revision"] = scene.Catalog.Revision,
                ["hash"] = scene.Catalog.Hash,
            },
            ["tracks"] = new JsonArray(scene.Tracks.Select(track => (JsonNode)new JsonObject
            {
                ["id"] = track.DroidId,
                ["name"] = track.Name,
            }).ToArray()),
            ["audioLanes"] = new JsonArray(scene.AudioLanes.Select(lane => (JsonNode)new JsonObject
            {
                ["label"] = lane.Label,
                ["clips"] = new JsonArray(lane.Clips.Select(clip => (JsonNode)new JsonObject
                {
                    ["filePath"] = clip.FilePath,
                    ["durationMs"] = clip.DurationMs,
                    ["startMs"] = clip.StartMs,
                    ["loop"] = clip.Loop,
                }).ToArray()),
            }).ToArray()),
            ["gestureClips"] = new JsonArray(scene.GestureClips.Select(clip => (JsonNode)new JsonObject
            {
                ["id"] = clip.Id.ToString("D"),
                ["gestureKey"] = clip.GestureKey,
                ["target"] = clip.Target.IsBroadcast
                    ? new JsonObject { ["mode"] = "all" }
                    : new JsonObject { ["mode"] = "droid", ["id"] = clip.Target.DroidId!.Value },
                ["startMs"] = clip.StartMs,
                ["intensity"] = clip.Intensity,
                ["tempo"] = clip.Tempo,
                ["variant"] = clip.Variant,
                ["seed"] = clip.Seed,
                ["holdMs"] = clip.HoldMs,
            }).ToArray()),
        };
        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }
}

internal sealed record ImportedSceneV2Document(
    string Name,
    bool Loop,
    List<SequenceTrackDto> Tracks,
    List<AudioLaneDto> AudioLanes,
    List<SequenceStepDto> Steps,
    int? EndMs);

internal sealed class GestureSceneV2PersistenceException : Exception
{
    internal GestureSceneV2PersistenceException(string message) : base(message) { }
}
