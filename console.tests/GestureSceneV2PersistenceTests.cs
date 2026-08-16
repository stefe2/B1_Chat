using System.Text.Json;
using b1_chat_console.Models;
using b1_chat_console.Services;

namespace b1_chat_console.Tests;

public sealed class GestureSceneV2PersistenceTests
{
    [Fact]
    public void SerializeAndParse_RoundTripsNamedClipPropertiesWithoutNumericIdentity()
    {
        var clipId = Guid.Parse("ccf96a44-5b30-4da9-a34b-47961e6a6b4f");
        var document = new SequenceSnapshot(
            "Named scene", false, new(),
            new List<SequenceStepDto>
            {
                new()
                {
                    Id = clipId,
                    GestureKey = "dialogue.talk",
                    Intensity = "normal",
                    Tempo = "normal",
                    Variant = "default",
                    Seed = 8123,
                    AnimId = 17,
                    Target = 0x4001,
                    StartMs = 400,
                    EndAfterMs = 2_400,
                },
            });

        var json = GestureSceneV2Persistence.Serialize(document,
            new[] { new SequenceTrackDto { Id = 0x4001, Name = "R2-D2" } });
        using var parsedJson = JsonDocument.Parse(json);
        var root = parsedJson.RootElement;
        var clip = root.GetProperty("gestureClips")[0];

        Assert.Equal("b1-scene", root.GetProperty("type").GetString());
        Assert.False(json.Contains("animId", StringComparison.Ordinal));
        Assert.Equal("dialogue.talk", clip.GetProperty("gestureKey").GetString());
        Assert.Equal(2_400, clip.GetProperty("holdMs").GetInt32());
        Assert.Equal("b1.core", root.GetProperty("catalog").GetProperty("id").GetString());

        var reopened = GestureSceneV2Persistence.Parse(json);
        var persisted = Assert.Single(reopened.Steps);
        Assert.Equal((clipId, "dialogue.talk", (uint)8123, 2_400),
            (persisted.Id, persisted.GestureKey, persisted.Seed, persisted.EndAfterMs));
    }

    [Fact]
    public void Serialize_RejectsGestureThatIsOutsideTheActiveV2Catalog()
    {
        var document = new SequenceSnapshot("Unsupported", false, new(),
            new List<SequenceStepDto> { new() { AnimId = 4, Target = ushort.MaxValue } });

        Assert.Throws<GestureSceneV2PersistenceException>(() =>
            GestureSceneV2Persistence.Serialize(document, Array.Empty<SequenceTrackDto>()));
    }

    [Fact]
    public void Parse_RejectsTheRetiredSequenceSchema()
    {
        Assert.Throws<GestureSceneV2SchemaException>(() => GestureSceneV2Persistence.Parse("""
            { "type":"b1-sequence", "version":6, "name":"old", "loop":false,
              "endMs":null, "tracks":[], "audioLanes":[], "steps":[] }
            """));
    }
}
