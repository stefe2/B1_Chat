using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using b1_chat_console.Models;
using b1_chat_console.Services;

namespace b1_chat_console;

public partial class SceneBrowserWindow : Window
{
    private readonly ICollectionView _sceneView;

    public ObservableCollection<SceneBrowserRow> Scenes { get; }
    public string LibraryStatus { get; }
    public string LibraryIssueText { get; }
    public SceneBrowserResult? Selection { get; private set; }

    public SceneBrowserWindow(
        IReadOnlyList<SequenceLibraryItem> scenes,
        string? currentSceneId,
        string libraryStatus,
        string libraryIssueText)
    {
        Scenes = new ObservableCollection<SceneBrowserRow>(scenes
            .OrderByDescending(scene => scene.SavedAt)
            .ThenBy(scene => scene.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(scene => new SceneBrowserRow(scene,
                string.Equals(scene.Id, currentSceneId, StringComparison.OrdinalIgnoreCase))));
        LibraryStatus = libraryStatus;
        LibraryIssueText = libraryIssueText;
        InitializeComponent();
        DarkTitleBar.Apply(this);
        DataContext = this;
        _sceneView = CollectionViewSource.GetDefaultView(Scenes);
        _sceneView.Filter = MatchesSearch;
        UpdateEmptyState();
    }

    private bool MatchesSearch(object item)
    {
        var query = SearchBox?.Text.Trim();
        return string.IsNullOrEmpty(query) ||
               item is SceneBrowserRow row && row.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase);
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        var current = Scenes.FirstOrDefault(scene => scene.IsCurrent);
        SceneList.SelectedItem = current ?? Scenes.FirstOrDefault();
        if (SceneList.SelectedItem != null) SceneList.ScrollIntoView(SceneList.SelectedItem);
        SearchBox.Focus();
    }

    private void SearchBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        _sceneView?.Refresh();
        if (SceneList != null && SceneList.SelectedItem == null)
            SceneList.SelectedItem = _sceneView?.Cast<object>().FirstOrDefault();
        UpdateEmptyState();
    }

    private void UpdateEmptyState()
    {
        if (EmptyState == null) return;
        var empty = _sceneView?.IsEmpty ?? Scenes.Count == 0;
        EmptyState.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        if (empty && Scenes.Count > 0)
        {
            EmptyTitle.Text = "No matching Scenes";
            EmptyHint.Text = "Try another search.";
        }
        else
        {
            EmptyTitle.Text = "No Scenes found";
            EmptyHint.Text = "Create a new Scene to begin.";
        }
    }

    private void SceneList_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e) =>
        OpenButton.IsEnabled = SceneList.SelectedItem is SceneBrowserRow;

    private void SceneList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (SceneList.SelectedItem is SceneBrowserRow) AcceptSelectedScene();
    }

    private void OpenScene_Click(object sender, RoutedEventArgs e) => AcceptSelectedScene();

    private void AcceptSelectedScene()
    {
        if (SceneList.SelectedItem is not SceneBrowserRow row) return;
        Selection = new SceneBrowserResult(row.Scene);
        DialogResult = true;
    }

    private void NewScene_Click(object sender, RoutedEventArgs e)
    {
        Selection = new SceneBrowserResult(null, CreateNew: true);
        DialogResult = true;
    }
}

public sealed class SceneBrowserRow
{
    public SequenceLibraryItem Scene { get; }
    public string Name => Scene.Name;
    public bool IsCurrent { get; }
    public string SavedText => Scene.SavedAt.ToLocalTime().ToString("g");
    public string ContentsText { get; }

    public SceneBrowserRow(SequenceLibraryItem scene, bool isCurrent)
    {
        Scene = scene;
        IsCurrent = isCurrent;
        var audioClips = scene.AudioLanes.Sum(lane => lane.Clips.Count);
        ContentsText = $"{scene.Steps.Count} gesture{(scene.Steps.Count == 1 ? "" : "s")} · " +
                       $"{audioClips} audio clip{(audioClips == 1 ? "" : "s")}";
    }
}
