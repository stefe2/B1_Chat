using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using b1_chat_console.Models;

namespace b1_chat_console.Services;

public interface ISequenceLibraryService
{
    SequenceLibraryScan Scan();
    SequenceLibraryItem? Get(string id);
    void Save(SequenceLibraryItem item);
    void MoveToTrash(string id);
}

/// <summary>
/// Versioned, atomic V2 Scene Library storage. A legacy b1-sequence entry is an
/// incompatible document, not something the V2 console attempts to reinterpret.
/// </summary>
public sealed class LibraryService : ISequenceLibraryService
{
    internal const string SchemaType = "b1-scene-library-item";
    internal const int CurrentVersion = 1;

    private readonly string _libraryDir;
    private readonly string _trashDir;
    private readonly IAtomicTextFileWriter _writer;

    public LibraryService(string? libraryDirectory = null, IAtomicTextFileWriter? writer = null)
    {
        _libraryDir = libraryDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "B1ChatConsole", "library");
        _trashDir = Path.Combine(_libraryDir, "trash");
        _writer = writer ?? new AtomicTextFileWriter();
    }

    public SequenceLibraryScan Scan()
    {
        var items = new List<SequenceLibraryItem>();
        var issues = new List<SequenceLibraryIssue>();
        if (!Directory.Exists(_libraryDir))
            return new SequenceLibraryScan(items, issues);

        IEnumerable<string> files;
        try
        {
            files = Directory.GetFiles(_libraryDir, "*.json").OrderBy(path => path).ToArray();
        }
        catch (Exception ex)
        {
            issues.Add(new SequenceLibraryIssue("library", ex.Message));
            return new SequenceLibraryScan(items, issues);
        }

        foreach (var path in files)
        {
            try
            {
                items.Add(Read(path));
            }
            catch (Exception ex)
            {
                issues.Add(new SequenceLibraryIssue(Path.GetFileName(path), ex.Message));
            }
        }

        return new SequenceLibraryScan(
            items.OrderBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(item => item.Id, StringComparer.Ordinal)
                .ToArray(),
            issues);
    }

    public SequenceLibraryItem? Get(string id)
    {
        ValidateId(id);
        var path = ScenePath(id);
        if (!File.Exists(path)) return null;
        return Read(path);
    }

    public void Save(SequenceLibraryItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        ValidateId(item.Id);
        ValidateName(item.Name);

        var document = ToSnapshot(item);
        var serializedDocument = GestureSceneV2Persistence.Serialize(document, item.Tracks);
        _ = GestureSceneV2Persistence.Parse(serializedDocument);

        var root = new JsonObject
        {
            ["type"] = SchemaType,
            ["version"] = CurrentVersion,
            ["id"] = item.Id,
            ["savedAtUtc"] = item.SavedAt.ToUniversalTime(),
            ["document"] = JsonNode.Parse(serializedDocument),
        };
        Directory.CreateDirectory(_libraryDir);
        _writer.WriteAllText(
            ScenePath(item.Id),
            root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    public void MoveToTrash(string id)
    {
        ValidateId(id);
        var source = ScenePath(id);
        if (!File.Exists(source))
            throw new FileNotFoundException($"Scene {id} no longer exists in the Local Library.", source);

        Directory.CreateDirectory(_trashDir);
        var timestamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmssfff");
        var destination = Path.Combine(_trashDir, $"{id}.{timestamp}.b1scene.json");
        File.Move(source, destination, overwrite: false);
    }

    private SequenceLibraryItem Read(string path)
    {
        string json;
        try
        {
            json = File.ReadAllText(path);
        }
        catch (Exception ex)
        {
            throw new IOException($"Cannot read the library entry: {ex.Message}", ex);
        }

        JsonDocument parsed;
        try
        {
            parsed = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"Invalid JSON ({ex.Message}).", ex);
        }

        using (parsed)
        {
            var root = parsed.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("The library entry root must be an object.");

            if (!root.TryGetProperty("type", out var type))
                throw new InvalidDataException("A Local Library entry must use the V2 Scene Library envelope.");
            var current = ReadCurrent(root, type);
            var expectedFileName = $"{current.Id}.b1scene.json";
            if (!string.Equals(Path.GetFileName(path), expectedFileName, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException(
                    $"Scene id does not match its file name (expected {expectedFileName}).");
            return current;
        }
    }

    private static SequenceLibraryItem ReadCurrent(JsonElement root, JsonElement type)
    {
        if (type.ValueKind != JsonValueKind.String || type.GetString() != SchemaType)
            throw new InvalidDataException("Unknown Scene Library entry type.");
        if (!root.TryGetProperty("version", out var version) ||
            version.ValueKind != JsonValueKind.Number ||
            !version.TryGetInt32(out var versionNumber) || versionNumber != CurrentVersion)
            throw new InvalidDataException("Unsupported Scene Library entry version.");
        if (!root.TryGetProperty("id", out var idElement) || idElement.ValueKind != JsonValueKind.String)
            throw new InvalidDataException("Scene Library entry has no valid id.");
        var id = idElement.GetString() ?? "";
        ValidateId(id);
        if (!root.TryGetProperty("savedAtUtc", out var savedAtElement) ||
            savedAtElement.ValueKind != JsonValueKind.String ||
            !savedAtElement.TryGetDateTime(out var savedAt))
            throw new InvalidDataException("Scene Library entry has no valid savedAtUtc.");
        if (!root.TryGetProperty("document", out var document))
            throw new InvalidDataException("Scene Library entry has no document.");

        var imported = GestureSceneV2Persistence.Parse(document.GetRawText());
        return FromImported(id, savedAt.ToUniversalTime(), imported);
    }

    private string ScenePath(string id) => Path.Combine(_libraryDir, $"{id}.b1scene.json");

    private static SequenceSnapshot ToSnapshot(SequenceLibraryItem item) => new(
        item.Name,
        item.Loop,
        item.AudioLanes,
        item.Steps,
        item.EndMs);

    private static SequenceLibraryItem FromImported(
        string id,
        DateTime savedAt,
        ImportedSceneV2Document document) => new()
    {
        Id = id,
        Name = document.Name,
        Loop = document.Loop,
        EndMs = document.EndMs,
        Tracks = document.Tracks,
        AudioLanes = document.AudioLanes,
        Steps = document.Steps,
        SavedAt = savedAt,
    };

    private static void ValidateId(string id)
    {
        if (!Guid.TryParse(id, out _))
            throw new InvalidDataException("Scene Library id must be a GUID.");
    }

    private static void ValidateName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new InvalidDataException("A Scene Library entry must have a name.");
        if (name.Trim().Length > 128)
            throw new InvalidDataException(
                "Scene name exceeds 128 characters.");
    }
}
