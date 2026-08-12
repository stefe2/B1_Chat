using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using b1_chat_console.Models;
using Microsoft.Win32;

namespace b1_chat_console.Services;

public enum UnsavedSceneChoice
{
    Save,
    Discard,
    Cancel,
}

public sealed record SceneBrowserResult(SequenceLibraryItem? Scene, bool CreateNew = false);

public interface ISequencerPersistenceDialogs
{
    string? ChooseExportPath(string suggestedFileName);
    string? ChooseImportPath();
    string? PromptForSceneName(string initialName, string title);
    SceneBrowserResult? ChooseSceneToOpen(
        IReadOnlyList<SequenceLibraryItem> scenes,
        string? currentSceneId,
        string libraryStatus,
        string libraryIssueText);
    UnsavedSceneChoice ConfirmUnsavedSceneChanges(string sceneName, string replacementDescription);
    bool ConfirmStopPlayback(string replacementDescription);
    bool ConfirmMoveSceneToTrash(string sceneName);
    void ShowError(string title, string message);
}

internal sealed class WpfSequencerPersistenceDialogs : ISequencerPersistenceDialogs
{
    public string? ChooseExportPath(string suggestedFileName)
    {
        var dialog = new SaveFileDialog
        {
            FileName = suggestedFileName,
            Filter = "B1 Sequence (*.b1seq.json)|*.b1seq.json",
        };
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    public string? ChooseImportPath()
    {
        var dialog = new OpenFileDialog
        {
            Filter = "B1 Sequence (*.b1seq.json)|*.b1seq.json|JSON (*.json)|*.json",
        };
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    public string? PromptForSceneName(string initialName, string title)
    {
        var dialog = new SceneNameWindow(initialName, title)
        {
            Owner = Application.Current?.MainWindow,
        };
        return dialog.ShowDialog() == true ? dialog.SceneName : null;
    }

    public SceneBrowserResult? ChooseSceneToOpen(
        IReadOnlyList<SequenceLibraryItem> scenes,
        string? currentSceneId,
        string libraryStatus,
        string libraryIssueText)
    {
        var dialog = new SceneBrowserWindow(scenes, currentSceneId, libraryStatus, libraryIssueText)
        {
            Owner = Application.Current?.MainWindow,
        };
        return dialog.ShowDialog() == true ? dialog.Selection : null;
    }

    public UnsavedSceneChoice ConfirmUnsavedSceneChanges(string sceneName, string replacementDescription)
    {
        var dialog = new SceneDecisionWindow(
            "Unsaved Scene Changes",
            $"Save changes to \"{sceneName}\"?",
            $"This Scene has changes that have not been saved. Choose what to do before you {replacementDescription}.",
            "Save and Continue",
            "Continue Without Saving")
        {
            Owner = Application.Current?.MainWindow,
        };
        _ = dialog.ShowDialog();
        return dialog.Selection switch
        {
            SceneDecisionResult.Primary => UnsavedSceneChoice.Save,
            SceneDecisionResult.Secondary => UnsavedSceneChoice.Discard,
            _ => UnsavedSceneChoice.Cancel,
        };
    }

    public bool ConfirmStopPlayback(string replacementDescription)
    {
        var dialog = new SceneDecisionWindow(
            "Stop Playback",
            "Stop the current playback?",
            $"Playback must stop before the editor can {replacementDescription}.",
            "Stop and Continue")
        {
            Owner = Application.Current?.MainWindow,
        };
        _ = dialog.ShowDialog();
        return dialog.Selection == SceneDecisionResult.Primary;
    }

    public bool ConfirmMoveSceneToTrash(string sceneName) =>
        MessageBox.Show(
            $"Move the Local Library scene \"{sceneName}\" to the recoverable trash folder?",
            "Remove scene from Local Library",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning) == MessageBoxResult.Yes;

    public void ShowError(string title, string message) =>
        MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Error);
}

public interface IAtomicTextFileWriter
{
    void WriteAllText(string destinationPath, string contents);
}

internal interface IAtomicFileOperations
{
    FileStream CreateNew(string path);
    bool Exists(string path);
    void Move(string sourcePath, string destinationPath, bool overwrite);
    void Delete(string path);
}

internal sealed class SystemAtomicFileOperations : IAtomicFileOperations
{
    public FileStream CreateNew(string path) => new(
        path,
        FileMode.CreateNew,
        FileAccess.Write,
        FileShare.None,
        bufferSize: 16 * 1024,
        FileOptions.WriteThrough);

    public bool Exists(string path) => File.Exists(path);

    public void Move(string sourcePath, string destinationPath, bool overwrite) =>
        File.Move(sourcePath, destinationPath, overwrite);

    public void Delete(string path) => File.Delete(path);
}

/// <summary>
/// Writes and flushes a sibling temporary file, then renames it over the destination. The
/// destination is never opened or truncated before the complete replacement is durable.
/// </summary>
internal sealed class AtomicTextFileWriter : IAtomicTextFileWriter
{
    private readonly IAtomicFileOperations _files;

    internal AtomicTextFileWriter(IAtomicFileOperations? files = null) =>
        _files = files ?? new SystemAtomicFileOperations();

    public void WriteAllText(string destinationPath, string contents)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        ArgumentNullException.ThrowIfNull(contents);

        var destination = Path.GetFullPath(destinationPath);
        var directory = Path.GetDirectoryName(destination)
            ?? throw new IOException("The export destination has no parent directory.");
        if (!Directory.Exists(directory))
            throw new DirectoryNotFoundException($"Export directory does not exist: {directory}");

        var temporary = Path.Combine(
            directory,
            $".{Path.GetFileName(destination)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = _files.CreateNew(temporary))
            {
                using (var writer = new StreamWriter(
                    stream,
                    new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                    bufferSize: 16 * 1024,
                    leaveOpen: true))
                {
                    writer.Write(contents);
                    writer.Flush();
                }
                stream.Flush(flushToDisk: true);
            }

            _files.Move(temporary, destination, overwrite: _files.Exists(destination));
        }
        finally
        {
            try
            {
                if (_files.Exists(temporary)) _files.Delete(temporary);
            }
            catch
            {
                // Preserve the original write/replace error. A sibling .tmp can be cleaned
                // manually; hiding the real export failure would be less actionable.
            }
        }
    }
}

internal static class SequenceExportSerializer
{
    internal static string Serialize(
        SequenceSnapshot document,
        IReadOnlyList<SequenceTrackDto> tracks)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(tracks);

        var root = new JsonObject
        {
            ["type"] = SequenceImportService.SchemaType,
            ["version"] = SequenceImportService.CurrentVersion,
            ["name"] = document.Name,
            ["loop"] = document.Loop,
            ["tracks"] = new JsonArray(tracks.Select(track => (JsonNode)new JsonObject
            {
                ["id"] = track.Id,
                ["name"] = track.Name,
            }).ToArray()),
            ["audioLanes"] = new JsonArray(document.AudioLanes.Select(lane => (JsonNode)new JsonObject
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
            ["steps"] = new JsonArray(document.Steps.Select(step => (JsonNode)new JsonObject
            {
                ["animId"] = step.AnimId,
                ["target"] = step.Target,
                ["startMs"] = step.StartMs,
                ["endAfterMs"] = step.EndAfterMs,
            }).ToArray()),
        };
        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }
}
