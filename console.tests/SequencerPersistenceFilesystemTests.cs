using b1_chat_console.Services;

namespace b1_chat_console.Tests;

// SEQ-H04 gaps: the existing atomic-write coverage in SequencerPersistenceTests.cs and
// SceneLibraryTests.cs simulates failures through an injected exception-throwing writer.
// These tests exercise the real AtomicTextFileWriter against real filesystem path problems
// and a stray leftover temp file, which nothing previously covered.
public sealed class SequencerPersistenceFilesystemTests
{
    [Fact]
    public void WriteAllText_ToAMissingDirectory_ThrowsWithoutCreatingAnything()
    {
        using var fixture = new TemporaryJsonFixture();
        var missingDir = Path.Combine(fixture.DirectoryPath, "does-not-exist");
        var destination = Path.Combine(missingDir, "scene.json");
        var writer = new AtomicTextFileWriter();

        Assert.Throws<DirectoryNotFoundException>(() => writer.WriteAllText(destination, "{}"));

        Assert.False(Directory.Exists(missingDir));
    }

    [Fact]
    public void WriteAllText_RealRoundTrip_ProducesExactContentAndNoLeftoverTempFile()
    {
        using var fixture = new TemporaryJsonFixture();
        var destination = Path.Combine(fixture.DirectoryPath, "scene.json");
        var writer = new AtomicTextFileWriter();

        writer.WriteAllText(destination, "{\"name\":\"first\"}");
        writer.WriteAllText(destination, "{\"name\":\"second\"}");

        Assert.Equal("{\"name\":\"second\"}", File.ReadAllText(destination));
        Assert.Empty(Directory.GetFiles(fixture.DirectoryPath, "*.tmp"));
    }

    [Fact]
    public void WriteAllText_WithAStrayTempFileFromAPriorCrash_SucceedsAndLeavesTheStrayFileUntouched()
    {
        // Documents current behavior: each write generates its own GUID-named temp file and
        // only cleans up that one in its own finally block. A stray .tmp left behind by an
        // earlier interrupted write is neither an obstacle to the next write nor swept up by
        // it — nothing in this codebase currently reconciles orphaned temp files on next use.
        using var fixture = new TemporaryJsonFixture();
        var destination = Path.Combine(fixture.DirectoryPath, "scene.json");
        var strayTemp = Path.Combine(fixture.DirectoryPath, ".scene.json.deadbeefdeadbeefdeadbeefdeadbeef.tmp");
        File.WriteAllText(strayTemp, "leftover from a crash");
        var writer = new AtomicTextFileWriter();

        writer.WriteAllText(destination, "{\"name\":\"first\"}");

        Assert.Equal("{\"name\":\"first\"}", File.ReadAllText(destination));
        Assert.True(File.Exists(strayTemp));
        Assert.Equal("leftover from a crash", File.ReadAllText(strayTemp));
    }

    [Fact]
    public void WriteAllText_ToAPathWithInvalidCharacters_ThrowsAndLeavesNoTempFile()
    {
        using var fixture = new TemporaryJsonFixture();
        var invalidName = "scene" + Path.GetInvalidFileNameChars()[0] + ".json";
        var destination = Path.Combine(fixture.DirectoryPath, invalidName);
        var writer = new AtomicTextFileWriter();

        Assert.ThrowsAny<IOException>(() => writer.WriteAllText(destination, "{}"));

        Assert.Empty(Directory.GetFiles(fixture.DirectoryPath));
    }
}
