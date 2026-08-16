using System.Text.Json.Nodes;
using b1_chat_console.Models;
using b1_chat_console.Services;

namespace b1_chat_console.Tests;

public sealed class GestureSceneV2SchemaTests
{
    [Fact]
    public void CatalogFixture_DefinesTheThreeStage4GesturesWithNormalTempo()
    {
        var catalog = GestureCatalogV2Parser.Parse(ReadFixture("catalog-v1.json"));

        Assert.Equal("b1.core", catalog.Identity.Id);
        Assert.Equal(new[] { "communicate.nod", "dialogue.talk", "idle.center" },
            catalog.Gestures.Keys.OrderBy(key => key));
        Assert.All(catalog.Gestures.Values, gesture => Assert.True(gesture.Tempos.ContainsKey("normal")));
        Assert.Equal(800, catalog.Gestures["communicate.nod"].Tempos["normal"].DurationMs);
        Assert.Equal(GestureExecutionKind.Continuous, catalog.Gestures["dialogue.talk"].Execution);
    }

    [Fact]
    public void SceneFixture_UsesNamedGesturesAndValidatesAgainstItsCatalog()
    {
        var catalog = GestureCatalogV2Parser.Parse(ReadFixture("catalog-v1.json"));
        var scene = SceneV2Parser.Parse(ReadFixture("scene-v1.json"));

        SceneV2Parser.ValidateAgainstCatalog(scene, catalog);

        Assert.Equal("First V2 scene", scene.Name);
        Assert.Equal("communicate.nod", scene.GestureClips[0].GestureKey);
        Assert.Equal(6_000, scene.GestureClips[1].HoldMs);
        Assert.Equal((uint)92014, scene.GestureClips[1].Seed);
    }

    [Theory]
    [InlineData("animId")]
    [InlineData("wireId")]
    public void Catalog_RejectsLegacyOrWireIdentityFields(string field)
    {
        var root = JsonNode.Parse(ReadFixture("catalog-v1.json"))!.AsObject();
        root["gestures"]![0]!.AsObject()[field] = 1;

        var error = Assert.Throws<GestureSceneV2SchemaException>(() => GestureCatalogV2Parser.Parse(root.ToJsonString()));

        Assert.Equal("$.gestures[0]." + field, error.FieldPath);
    }

    [Fact]
    public void Catalog_RejectsATempoWithoutExactDuration()
    {
        var root = JsonNode.Parse(ReadFixture("catalog-v1.json"))!.AsObject();
        root["gestures"]![1]!["tempos"]![0]!.AsObject()["durationMs"] = 0;

        var error = Assert.Throws<GestureSceneV2SchemaException>(() => GestureCatalogV2Parser.Parse(root.ToJsonString()));

        Assert.Equal("$.gestures[1].tempos[0].durationMs", error.FieldPath);
    }

    [Fact]
    public void Catalog_RejectsContentWhoseDeclaredHashWasNotRegenerated()
    {
        var root = JsonNode.Parse(ReadFixture("catalog-v1.json"))!.AsObject();
        root["gestures"]![1]!["description"] = "A modified motion without a regenerated catalog hash.";

        var error = Assert.Throws<GestureSceneV2SchemaException>(
            () => GestureCatalogV2Parser.Parse(root.ToJsonString()));

        Assert.Equal("$.hash", error.FieldPath);
    }

    [Fact]
    public void Scene_RejectsNumericAnimationIdentity()
    {
        var root = JsonNode.Parse(ReadFixture("scene-v1.json"))!.AsObject();
        root["gestureClips"]![0]!.AsObject()["animId"] = 2;

        var error = Assert.Throws<GestureSceneV2SchemaException>(() => SceneV2Parser.Parse(root.ToJsonString()));

        Assert.Equal("$.gestureClips[0].animId", error.FieldPath);
    }

    [Fact]
    public void Scene_RejectsAnUnavailableTempoAndAContinuousGestureWithoutHold()
    {
        var catalog = GestureCatalogV2Parser.Parse(ReadFixture("catalog-v1.json"));
        var tempoRoot = JsonNode.Parse(ReadFixture("scene-v1.json"))!.AsObject();
        tempoRoot["gestureClips"]![0]!.AsObject()["tempo"] = "fast";
        var tempoScene = SceneV2Parser.Parse(tempoRoot.ToJsonString());
        Assert.Contains("does not allow tempo", Assert.Throws<GestureSceneV2SchemaException>(
            () => SceneV2Parser.ValidateAgainstCatalog(tempoScene, catalog)).Message);

        var holdRoot = JsonNode.Parse(ReadFixture("scene-v1.json"))!.AsObject();
        holdRoot["gestureClips"]![1]!.AsObject()["holdMs"] = null;
        var holdScene = SceneV2Parser.Parse(holdRoot.ToJsonString());
        Assert.Contains("requires holdMs", Assert.Throws<GestureSceneV2SchemaException>(
            () => SceneV2Parser.ValidateAgainstCatalog(holdScene, catalog)).Message);
    }

    [Fact]
    public void Scene_RejectsAMismatchedCatalogAndAnEndBeforeItsContent()
    {
        var catalog = GestureCatalogV2Parser.Parse(ReadFixture("catalog-v1.json"));
        var root = JsonNode.Parse(ReadFixture("scene-v1.json"))!.AsObject();
        root["catalog"]!["revision"] = "other";
        var mismatched = SceneV2Parser.Parse(root.ToJsonString());
        Assert.Equal("$.catalog", Assert.Throws<GestureSceneV2SchemaException>(
            () => SceneV2Parser.ValidateAgainstCatalog(mismatched, catalog)).FieldPath);

        root["catalog"]!["revision"] = "v1";
        root["endMs"] = 3000;
        var tooShort = SceneV2Parser.Parse(root.ToJsonString());
        Assert.Equal("$.endMs", Assert.Throws<GestureSceneV2SchemaException>(
            () => SceneV2Parser.ValidateAgainstCatalog(tooShort, catalog)).FieldPath);
    }

    private static string ReadFixture(string fileName) => File.ReadAllText(
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "V2", fileName));
}
