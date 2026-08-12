using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using b1_chat_console.Models;
using Microsoft.Win32;

namespace b1_chat_console.Services;

public interface ISequencerPersistenceDialogs
{
    string? ChooseExportPath(string suggestedFileName);
    string? ChooseImportPath();
    string? PromptForSceneName(string initialName, string title);
    bool ConfirmDiscardUnsavedChanges(string replacementDescription);
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
        var nameBox = new TextBox
        {
            Text = initialName,
            MaxLength = SequenceImportService.MaxSequenceNameLength,
            MinWidth = 320,
            Margin = new Thickness(0, 8, 0, 14),
        };
        var ok = new Button { Content = "Save", IsDefault = true, MinWidth = 80, Margin = new Thickness(0, 0, 8, 0) };
        var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 80 };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        var panel = new StackPanel { Margin = new Thickness(18) };
        panel.Children.Add(new TextBlock { Text = "Scene name" });
        panel.Children.Add(nameBox);
        panel.Children.Add(buttons);
        var dialog = new Window
        {
            Title = title,
            Content = panel,
            SizeToContent = SizeToContent.WidthAndHeight,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = Application.Current?.MainWindow,
            ShowInTaskbar = false,
        };
        ok.Click += (_, _) => dialog.DialogResult = true;
        dialog.ContentRendered += (_, _) => { nameBox.Focus(); nameBox.SelectAll(); };
        return dialog.ShowDialog() == true ? nameBox.Text : null;
    }

    public bool ConfirmDiscardUnsavedChanges(string replacementDescription) =>
        MessageBox.Show(
            $"The current sequence has unsaved changes.\n\nDiscard them and {replacementDescription}?",
            "Replace unsaved sequence",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning) == MessageBoxResult.Yes;

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
            }).ToArray()),
        };
        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }
}
