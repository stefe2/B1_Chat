using System.IO;
using System.Security.Cryptography;
using System.Text;
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
/// Versioned, atomic Scene Library storage. Version 0 is the historical flat
/// SequenceLibraryItem JSON; it is migrated in place on the first successful scan.
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
                var (item, legacy) = Read(path);
                if (legacy) MigrateLegacy(path, item);
                items.Add(item);
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
        return Read(path).Item;
    }

    public void Save(SequenceLibraryItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        ValidateId(item.Id);
        ValidateName(item.Name);

        var document = ToSnapshot(item);
        var serializedDocument = SequenceExportSerializer.Serialize(document, item.Tracks);
        _ = SequenceImportService.Parse(serializedDocument);

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

    private (SequenceLibraryItem Item, bool Legacy) Read(string path)
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

            if (root.TryGetProperty("type", out var type))
            {
                var current = ReadCurrent(root, type);
                var expectedFileName = $"{current.Id}.b1scene.json";
                if (!string.Equals(Path.GetFileName(path), expectedFileName, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException(
                        $"Scene id does not match its file name (expected {expectedFileName}).");
                return (current, false);
            }

            return (ReadLegacy(path, json), true);
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

        var imported = SequenceImportService.Parse(document.GetRawText());
        return FromImported(id, savedAt.ToUniversalTime(), imported);
    }

    private static SequenceLibraryItem ReadLegacy(string path, string json)
    {
        var legacy = JsonSerializer.Deserialize<SequenceLibraryItem>(json,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidDataException("Empty legacy library entry.");
        ValidateName(legacy.Name);
        legacy.Id = Guid.TryParse(legacy.Id, out var existingId)
            ? existingId.ToString("N")
            : StableLegacyId(path, legacy.Id);
        legacy.SavedAt = legacy.SavedAt == default ? DateTime.UtcNow : legacy.SavedAt.ToUniversalTime();

        var snapshot = ToSnapshot(legacy);
        var normalized = SequenceExportSerializer.Serialize(snapshot, legacy.Tracks);
        var imported = SequenceImportService.Parse(normalized);
        return FromImported(legacy.Id, legacy.SavedAt, imported);
    }

    private static string StableLegacyId(string path, string legacyId)
    {
        var identity = $"{Path.GetFileName(path)}\0{legacyId}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(identity));
        return new Guid(hash.AsSpan(0, 16)).ToString("N");
    }

    private void MigrateLegacy(string legacyPath, SequenceLibraryItem item)
    {
        Save(item);
        Directory.CreateDirectory(_trashDir);
        var baseName = Path.GetFileNameWithoutExtension(legacyPath);
        var timestamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmssfff");
        var destination = Path.Combine(_trashDir, $"legacy-{baseName}.{timestamp}.json");
        File.Move(legacyPath, destination, overwrite: false);
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
        ImportedSequenceDocument document) => new()
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
        if (name.Trim().Length > SequenceImportService.MaxSequenceNameLength)
            throw new InvalidDataException(
                $"Scene name exceeds {SequenceImportService.MaxSequenceNameLength} characters.");
    }
}
