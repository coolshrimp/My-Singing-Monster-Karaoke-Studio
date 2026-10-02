using System.Diagnostics;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using Microsoft.Win32;
using MySingingMonsterKaraokeStudio.Models;
using MySingingMonsterKaraokeStudio.Services;

namespace MySingingMonsterKaraokeStudio;

public sealed class LibraryWindow : Window
{
    private readonly LibraryService _library;
    private readonly ProjectService _projects;
    private readonly WebView2 _browser = new();
    private readonly TextBox _address = new();
    private readonly ListBox _downloads = new() { DisplayMemberPath = nameof(LibraryDownload.Label) };
    private readonly ListBox _songs = new() { DisplayMemberPath = nameof(SongProject.Name) };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap };
    private readonly CheckBox _autoOpen = new() { Content = "Download → import and open in editor", IsChecked = true };
    private readonly List<CoreWebView2DownloadOperation> _active = [];
    private readonly Dictionary<string, List<SongProject>> _opened = new(StringComparer.OrdinalIgnoreCase);
    private bool _importing;
    public SongProject? SelectedProject { get; private set; }
    public LibraryWindow(Window owner, ProjectService projects, LibraryService? library = null, string? startUri = null)
    {
        Owner = owner; Title = "Online Library / Downloads"; Width = 1120; Height = 800;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; ShowInTaskbar = false;
        Background = new SolidColorBrush(Color.FromRgb(25,34,49)); Foreground = Brushes.White;
        Resources[typeof(Button)] = owner.FindResource(typeof(Button));
        _projects = projects; _library = library ?? new LibraryService(projects);
        _autoOpen.Foreground = Brushes.White; _status.Foreground = Brushes.LightSteelBlue;
        _address.Background = new SolidColorBrush(Color.FromRgb(37,49,71)); _address.Foreground = Brushes.White; _address.Padding = new Thickness(8);
        foreach (var list in new[] { _downloads, _songs }) { list.Background = new SolidColorBrush(Color.FromRgb(20,28,42)); list.Foreground = Brushes.White; }
        var layout = new DockPanel { Margin = new Thickness(12) };
        var top = new StackPanel(); DockPanel.SetDock(top, Dock.Top); layout.Children.Add(top);
        var sources = new WrapPanel(); top.Children.Add(sources);
        AddButton(sources, "TromboneDB", () => Navigate("https://tc-mods.github.io/TromboneDB/"));
        AddButton(sources, "Trombone Charts", () => Navigate("https://trombonecharts.com/"));
        AddButton(sources, "TootTally", () => Navigate("https://toottally.com/search/"));
        AddButton(sources, "Back", () => { if (_browser.CoreWebView2?.CanGoBack == true) _browser.GoBack(); });
        AddButton(sources, "Refresh", () => _browser.CoreWebView2?.Reload());
        AddButton(sources, "Open in Browser", () => { if (Uri.TryCreate(_address.Text, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http") Process.Start(new ProcessStartInfo(uri.ToString()) { UseShellExecute = true }); });
        var nav = new DockPanel { Margin = new Thickness(0,8,0,8) }; top.Children.Add(nav);
        var go = new Button { Content = "Go", Margin = new Thickness(8,0,0,0) }; DockPanel.SetDock(go, Dock.Right); nav.Children.Add(go); nav.Children.Add(_address); go.Click += (_,_) => Navigate(_address.Text);
        _address.KeyDown += (_,e) => { if (e.Key == System.Windows.Input.Key.Enter) Navigate(_address.Text); };
        var bottom = new StackPanel { Margin = new Thickness(0,8,0,0) }; DockPanel.SetDock(bottom, Dock.Bottom); layout.Children.Add(bottom);
        bottom.Children.Add(_autoOpen); bottom.Children.Add(_status);
        var center = new Grid(); center.ColumnDefinitions.Add(new ColumnDefinition()); center.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(290) }); layout.Children.Add(center);
        center.Children.Add(_browser);
        var side = new DockPanel { Margin = new Thickness(12,0,0,0) }; Grid.SetColumn(side,1); center.Children.Add(side);
        var actions = new WrapPanel(); DockPanel.SetDock(actions,Dock.Bottom); side.Children.Add(actions);
        AddButton(actions, "Open / Import", () => ImportSelection());
        AddButton(actions, "Open Local ZIP…", () => { var dialog = new OpenFileDialog { Filter = "Song packages / charts / projects|*.zip;*.tmb;*.msmproj" }; if (dialog.ShowDialog(this)==true) ImportFile(dialog.FileName, "Local file"); });
        AddButton(actions, "Downloads Folder", () => Process.Start(new ProcessStartInfo(_library.Folder) { UseShellExecute = true }));
        AddButton(actions, "Close", () => Close());
        var lists = new Grid(); lists.RowDefinitions.Add(new RowDefinition { Height = new GridLength(28) }); lists.RowDefinitions.Add(new RowDefinition()); lists.RowDefinitions.Add(new RowDefinition { Height = new GridLength(28) }); lists.RowDefinitions.Add(new RowDefinition()); side.Children.Add(lists);
        lists.Children.Add(new TextBlock { Text = "Downloaded packages", FontSize = 16 });
        Grid.SetRow(_downloads,1); lists.Children.Add(_downloads); lists.Children.Add(new TextBlock { Text = "Editable songs in selected package", Margin = new Thickness(0,8,0,0) }.AtRow(2));
        Grid.SetRow(_songs,3); lists.Children.Add(_songs);
        _downloads.SelectionChanged += (_,_) => ShowDownloadedSongs();
        _songs.MouseDoubleClick += (_,_) => OpenSelected();
        Content = layout; RefreshHistory();
        Loaded += async (_,_) =>
        {
            try
            {
                var environment = await CoreWebView2Environment.CreateAsync(userDataFolder: Path.Combine(_library.Folder, "Browser"));
                await _browser.EnsureCoreWebView2Async(environment);
                var core = _browser.CoreWebView2;
                core.Settings.AreHostObjectsAllowed = false; core.Settings.IsWebMessageEnabled = false;
                core.NewWindowRequested += (_,e) => { e.Handled = true; Navigate(e.Uri); };
                core.NavigationStarting += (_,e) => { if (!Uri.TryCreate(e.Uri,UriKind.Absolute,out var uri) || uri.Scheme is not ("https" or "http")) e.Cancel = true; };
                core.SourceChanged += (_,_) => _address.Text = core.Source;
                core.NavigationCompleted += (_,e) => _status.Text = e.IsSuccess ? "Browse a source and download a ZIP. Existing notes are kept; unsupported charts report why they cannot open." : "This page could not load: " + e.WebErrorStatus + ". Try another source or Open Local ZIP.";
                core.DownloadStarting += DownloadStarting;
                Navigate(startUri ?? "https://toottally.com/search/");
            }
            catch(Exception ex) { _status.Text = "Browser unavailable: " + ex.Message + ". Open Local ZIP is still available."; }
        };
        Closing += (_,e) => { if (_importing || _active.Any(d=>d.State==CoreWebView2DownloadState.InProgress)) { e.Cancel=true; _status.Text="Wait for the download/import to finish before closing."; } };
        Closed += (_,_) => _browser.Dispose();
    }
    private void AddButton(Panel panel, string label, Action action)
    {
        var button = new Button { Content=label, Margin=new Thickness(0,0,6,6), Padding=new Thickness(9,6,9,6) }; button.Click += (_,_) => { try { action(); } catch(Exception ex) { _status.Text=ex.Message; } }; panel.Children.Add(button);
    }
    private void Navigate(string address)
    {
        if (!Uri.TryCreate(address, UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http")) { _status.Text="Enter a web address starting with https://."; return; }
        _address.Text=uri.ToString(); if (_browser.CoreWebView2 is not null) _browser.CoreWebView2.Navigate(uri.ToString());
    }
    private void RefreshHistory() => _downloads.ItemsSource = _library.History();
    private void ShowDownloadedSongs()
    {
        _songs.ItemsSource = null;
        if (_downloads.SelectedItem is not LibraryDownload download) return;
        if (_opened.TryGetValue(download.File,out var projects)) _songs.ItemsSource=projects;
        else
        {
            var valid = new List<SongProject>();
            foreach(var file in download.Projects) try { if(File.Exists(file)) valid.Add(_projects.LoadFile(file,false)); } catch(Exception ex) when(ex is IOException or InvalidDataException or JsonException) { }
            _songs.ItemsSource=valid;
        }
    }
    private void OpenSelected()
    {
        if(_songs.SelectedItem is SongProject project) { SelectedProject=project; DialogResult=true; }
    }
    private void ImportSelection()
    {
        if(_songs.SelectedItem is SongProject) { OpenSelected(); return; }
        if(_downloads.SelectedItem is LibraryDownload download) ImportFile(download.File,download.Source);
    }
    private void DownloadStarting(object? sender, CoreWebView2DownloadStartingEventArgs e)
    {
        if(!LibraryService.Supported(e.ResultFilePath)) { e.Cancel=true; _status.Text="Only ZIP, TMB and studio project downloads are supported. Use a site's direct chart package download."; return; }
        var path=_library.NewDownloadPath(e.ResultFilePath); var source=e.DownloadOperation.Uri;
        e.ResultFilePath=path; e.Handled=true; var operation=e.DownloadOperation; _active.Add(operation);
        operation.BytesReceivedChanged += (_,_) =>
        {
            if(operation.BytesReceived > 512L*1024*1024) { operation.Cancel(); _status.Text="Download exceeds the 512 MB package limit."; return; }
            _status.Text="Downloading " + Path.GetFileName(path) + " · " + operation.BytesReceived/1024 + " KB";
        };
        operation.StateChanged += (_,_) =>
        {
            if(operation.State==CoreWebView2DownloadState.Interrupted) { _active.Remove(operation); _status.Text="Download interrupted: " + operation.InterruptReason; }
            if(operation.State!=CoreWebView2DownloadState.Completed) return;
            _active.Remove(operation); _library.Record(path,source); RefreshHistory();
            if(_autoOpen.IsChecked==true) ImportFile(path,source); else _status.Text="Downloaded " + Path.GetFileName(path) + ". Select it and Open / Import to edit.";
        };
    }
    private async void ImportFile(string file,string source)
    {
        if(_importing) return; _importing=true; _status.Text="Importing editable charts…";
        try
        {
            var projects=await Task.Run(()=>_library.Import(file));
            _library.Record(file,source,projects); _opened[file]=projects; RefreshHistory();
            _downloads.SelectedItem=_downloads.Items.OfType<LibraryDownload>().FirstOrDefault(d=>d.File==file); ShowDownloadedSongs();
            _status.Text="Imported " + projects.Count + " chart(s). Select one and Open / Import to edit.";
            if(projects.Count==1 && _autoOpen.IsChecked==true && !_active.Any()) { SelectedProject=projects[0]; _importing=false; DialogResult=true; }
        }
        catch(Exception ex) { _status.Text="Import failed: " + ex.Message; }
        finally { _importing=false; }
    }
}

internal static class LibraryLayout
{
    public static T AtRow<T>(this T element,int row) where T: UIElement { Grid.SetRow(element,row); return element; }
}
