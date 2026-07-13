using System.IO;
using System.Text.Json;
using b1_chat_console.Models;

namespace b1_chat_console.Services;

/// <summary>Porte de SendLibrary/libSave/libDelete (ex-MainWindow.xaml.cs) : meme dossier, meme format JSON.</summary>
public class LibraryService
{
    private static readonly string LibraryDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "B1ChatConsole", "library");

    private static string SafeId(string? id)
    {
        var safe = string.IsNullOrWhiteSpace(id) ? "sequence" : id;
        foreach (var c in Path.GetInvalidFileNameChars()) safe = safe.Replace(c, '_');
        return safe;
    }

    public List<SequenceLibraryItem> List()
    {
        var items = new List<SequenceLibraryItem>();
        try
        {
            if (!Directory.Exists(LibraryDir)) return items;
            foreach (var f in Directory.GetFiles(LibraryDir, "*.json").OrderBy(x => x))
            {
                try
                {
                    var item = JsonSerializer.Deserialize<SequenceLibraryItem>(File.ReadAllText(f));
                    if (item != null) items.Add(item);
                }
                catch { /* fichier corrompu : ignore, pas de quoi bloquer la liste */ }
            }
        }
        catch { /* repertoire illisible : liste vide */ }
        return items;
    }

    public void Save(string id, SequenceLibraryItem item)
    {
        Directory.CreateDirectory(LibraryDir);
        var path = Path.Combine(LibraryDir, SafeId(id) + ".json");
        File.WriteAllText(path, JsonSerializer.Serialize(item));
    }

    public void Delete(string id)
    {
        var path = Path.Combine(LibraryDir, SafeId(id) + ".json");
        if (File.Exists(path)) File.Delete(path);
    }
}
