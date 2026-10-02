using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using MySingingMonsterKaraokeStudio.Models;
using MySingingMonsterKaraokeStudio.Services;

namespace MySingingMonsterKaraokeStudio;

public sealed class GameSongsWindow : Window
{
    private readonly GameSongService _game;
    private readonly ProjectService? _projects;
    private readonly ListBox _songs, _deleted;
    private readonly TextBlock _status;
    private readonly Button _remove, _open, _restore, _delete;
    public SongProject? SelectedProject { get; private set; }

    public GameSongsWindow(Window owner, GameSongService game, ProjectService? projects = null)
    {
        _game = game; _projects = projects; Owner = owner; Title = "Manage Game Songs";
        Icon = owner.Icon; Width = 940; Height = 610; MinWidth = 720; MinHeight = 420;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; ShowInTaskbar = false;
        Background = new SolidColorBrush(Color.FromRgb(25, 34, 49)); Foreground = Brushes.White;
        foreach (var type in new[] { typeof(Button), typeof(ListBox), typeof(ListBoxItem) }) Resources[type] = owner.FindResource(type);
        var panel = new DockPanel { Margin = new Thickness(20) };
        var heading = new StackPanel { Margin = new Thickness(0, 0, 0, 14) };
        heading.Children.Add(new TextBlock { Text = "Manage game songs", FontSize = 22 });
        heading.Children.Add(new TextBlock { Text = game.SongsFolder, Foreground = Brushes.LightSteelBlue, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) });
        DockPanel.SetDock(heading, Dock.Top); panel.Children.Add(heading);
        var bottom = new StackPanel { Margin = new Thickness(0, 14, 0, 0) };
        _status = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = Brushes.LightSteelBlue, Margin = new Thickness(0, 0, 0, 12) };
        bottom.Children.Add(_status);
        var buttons = new WrapPanel();
        var refresh = AddButton(buttons, "Refresh", () => Refresh());
        AddButton(buttons, "Open Songs Folder", () => Reveal(_game.SongsFolder));
        AddButton(buttons, "Open Backups", () => Reveal(_game.BackupsFolder));
        var close = AddButton(buttons, "Close", () => Close()); close.IsCancel = true;
        bottom.Children.Add(buttons); DockPanel.SetDock(bottom, Dock.Bottom); panel.Children.Add(bottom);
        _songs = MakeList(); _deleted = MakeList();
        var installedPanel = new DockPanel(); var installedButtons = new WrapPanel { Margin = new Thickness(0, 12, 0, 0) };
        _open = AddButton(installedButtons, "Open as Project", OpenSelected);
        _remove = AddButton(installedButtons, "Remove from Game", RemoveSelected);
        DockPanel.SetDock(installedButtons, Dock.Bottom); installedPanel.Children.Add(installedButtons); installedPanel.Children.Add(_songs);
        var deletedPanel = new DockPanel(); var deletedButtons = new WrapPanel { Margin = new Thickness(0, 12, 0, 0) };
        _restore = AddButton(deletedButtons, "Restore to Game", RestoreSelected);
        _delete = AddButton(deletedButtons, "Delete Permanently…", DeleteSelected);
        DockPanel.SetDock(deletedButtons, Dock.Bottom); deletedPanel.Children.Add(deletedButtons); deletedPanel.Children.Add(_deleted);
        var tabs = new TabControl { Background = Background, Foreground = Brushes.White, BorderBrush = Brushes.SlateGray };
        tabs.Items.Add(new TabItem { Header = "Installed Songs", Content = installedPanel, Background = Brushes.LightSteelBlue, Foreground = Brushes.Black });
        tabs.Items.Add(new TabItem { Header = "Deleted Songs / Previous Versions", Content = deletedPanel, Background = Brushes.LightSteelBlue, Foreground = Brushes.Black });
        _songs.SelectionChanged += (_, _) => UpdateButtons(); _deleted.SelectionChanged += (_, _) => UpdateButtons();
        _songs.MouseDoubleClick += (_, _) => { if (_open.IsEnabled) OpenSelected(); };
        panel.Children.Add(tabs); Content = panel; Refresh();
    }

    private static ListBox MakeList() => new() { Background = Brushes.Transparent, Foreground = Brushes.White, BorderThickness = new Thickness(0), DisplayMemberPath = "DisplayLabel" };
    private static Button AddButton(Panel panel, string text, Action action)
    {
        var button = new Button { Content = text, Margin = new Thickness(0, 0, 8, 0) };
        button.Click += (_, _) => action(); panel.Children.Add(button); return button;
    }
    private void Reveal(string folder)
    {
        if (Directory.Exists(folder)) Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true });
        else _status.Text = "No backups yet. Removed and updated songs will appear here.";
    }
    private void UpdateButtons()
    {
        _remove.IsEnabled = _songs.SelectedItem is InstalledGameSong;
        _open.IsEnabled = _projects is not null && _songs.SelectedItem is InstalledGameSong { Readable: true, HasAudio: true };
        _restore.IsEnabled = _game.IsReady && _deleted.SelectedItem is GameSongService.DeletedSong;
        _delete.IsEnabled = _deleted.SelectedItem is GameSongService.DeletedSong;
    }
    private void Refresh()
    {
        try
        {
            var songs = _game.GetSongs(); var deleted = _game.GetDeletedSongs();
            _songs.ItemsSource = songs; _deleted.ItemsSource = deleted; UpdateButtons();
            _status.Text = !_game.IsReady ? "Choose Game Data Folder from the studio's Game menu." :
                $"{songs.Count} installed song(s) · {deleted.Count} deleted song(s) / previous version(s). Open as Project copies the chart and audio to your studio library.";
        }
        catch (Exception ex) { _status.Text = "Could not read game songs: " + ex.Message; }
    }
    private void OpenSelected()
    {
        if (_projects is null || _songs.SelectedItem is not InstalledGameSong song) return;
        try { SelectedProject = _game.OpenAsProject(song, _projects); DialogResult = true; }
        catch (Exception ex) { ShowError(ex, "Open game song as project"); }
    }
    private void RemoveSelected()
    {
        if (_songs.SelectedItem is not InstalledGameSong song) return;
        if (MessageBox.Show(this, $"Remove ‘{song.Name}’ ({song.Level}) from the game?\n\nIts files will appear in Deleted Songs for restoration. Studio projects are kept.",
            "Remove Song from Game", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        try { _game.RemoveSong(song); Refresh(); _status.Text = "Removed from game. Restore it from Deleted Songs. Restart the game or refresh its song list."; }
        catch (Exception ex) { ShowError(ex, "Remove song"); }
    }
    private void RestoreSelected()
    {
        if (_deleted.SelectedItem is not GameSongService.DeletedSong song) return;
        try { _game.RestoreSong(song); Refresh(); _status.Text = "Restored to game. Restart the game or refresh its song list."; }
        catch (Exception ex) { ShowError(ex, "Restore song"); }
    }
    private void DeleteSelected()
    {
        if (_deleted.SelectedItem is not GameSongService.DeletedSong song) return;
        if (MessageBox.Show(this, $"Permanently delete ‘{song.Name}’ ({song.Reason})?\n\nThis removes the entire backup, including audio and charts. It cannot be restored afterward. Installed songs and studio projects are kept.\n\nBackup: {song.Folder}",
            "Permanently Delete Song Backup", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        try { _game.DeleteBackupPermanently(song); Refresh(); _status.Text = "The selected backup was permanently deleted."; }
        catch (Exception ex) { ShowError(ex, "Delete song backup"); }
    }
    private void ShowError(Exception ex, string title) => MessageBox.Show(this, ex.Message, title, MessageBoxButton.OK, MessageBoxImage.Warning);
}
