using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Microsoft.Win32;
using MySingingMonsterKaraokeStudio.Models;
using MySingingMonsterKaraokeStudio.Services;

namespace MySingingMonsterKaraokeStudio;

public partial class MainWindow : Window
{
    private readonly ProjectService _projects;
    private readonly FfmpegService _ffmpeg = new();
    private readonly TmbService _tmb = new();
    private readonly ExportService _export = new();
    private readonly MidiService _midi = new();
    private readonly AudioDraftService _draft = new();
    private GameSongService _game;
    private IReadOnlyList<InstalledGameSong> _gameSongs = [];
    private bool _gameReadFailed;
    private readonly DispatcherTimer _gameStatusTimer = new() { Interval = TimeSpan.FromSeconds(3) };
    private readonly DispatcherTimer _autoSaveTimer = new() { Interval = TimeSpan.FromMilliseconds(750) };
    private SongProject? _project;
    private string _projectKey = "", _loadedProjectKey = "", _audioKey = "", _editorFolder = "";
    private string? _audioPath;
    private bool _editorReady, _busy, _dirty, _canClose, _canUndo, _canRedo, _audioReady, _changingDifficulty, _autoSaving, _refreshingRecents;
    private double _audioDuration;
    private string _trimAudioKey = "";
    private bool _updatingTrimRange, _keepEditorHistory;
    private sealed record ProjectEdit(string Label, string Before, string After);
    private readonly Dictionary<string, ProjectEdit> _projectEdits = [];

    private int _editorNoteCount, _selectedCount;
    private long _editorRevision;
    private RecentProjectItem? _recentContextItem;

    public MainWindow() : this(new ProjectService()) { }
    public MainWindow(ProjectService projects, GameSongService? game = null)
    {
        _projects = projects;
        _game = game ?? new GameSongService(projects.GameDataFolder);
        InitializeComponent();
        TrimStartText.TextChanged += TrimRangeText_Changed;
        TrimEndText.TextChanged += TrimRangeText_Changed;
        _autoSaveTimer.Tick += AutoSaveTimer_Tick;
        _gameStatusTimer.Tick += (_, _) => { if (!_busy) RefreshRecentProjects(); };
        foreach (var property in typeof(StudioCommands).GetProperties())
            if (property.GetValue(null) is RoutedUICommand command)
                CommandBindings.Add(new CommandBinding(command, ExecuteStudioCommand, CanExecuteStudioCommand));
        WorkingFolderText.Text = _projects.ProjectsRoot;
        WorkingFolderText.ToolTip = _projects.ProjectsRoot;
        UpdateFfmpegStatus();
        Loaded += MainWindow_Loaded;
        Closing += MainWindow_Closing;
    }

    private void CanExecuteStudioCommand(object sender, CanExecuteRoutedEventArgs e)
    {
        var name = ((RoutedUICommand)e.Command).Name;
        e.CanExecute = !_busy && name switch
        {
            "New" or "Open" or "OpenFolder" or "WorkingFolder" or "StudioFolder" or "ChangeWorkingFolder" or "Setup" or "Exit" or "GameSongs" or "ChangeGameFolder" or "RefreshGame" or "Library" => true,
            "GameFolder" => _game.IsReady,
            "ImportGame" => _editorReady && _project is not null,
            "RemoveGame" => _project is not null && GameSongService.FindLevel(_project, _gameSongs) is not null,
            "OpenChart" or "Import" or "Convert" or "Generate" or "Build" or "Export" => _editorReady,
            "Trim" => _editorReady && _project is not null && _audioReady && _audioDuration > 0,
            "ProjectFolder" => _project is not null,
            "Undo" => _editorReady && _project is not null && _canUndo,
            "Redo" => _editorReady && _project is not null && _canRedo,
            "Delete" or "ClearSelection" => _editorReady && _project is not null && _selectedCount > 0,
            "SelectAll" => _editorReady && _project is not null && _editorNoteCount > 0,
            "Play" or "Stop" => _editorReady && _project is not null && _audioReady,
            _ => _editorReady && _project is not null
        };
        e.Handled = true;
    }

    private async void ExecuteStudioCommand(object sender, ExecutedRoutedEventArgs e)
    {
        e.Handled = true;
        var name = ((RoutedUICommand)e.Command).Name;
        await RunActionAsync(name, () => ExecuteCommandAsync(name));
    }

    private async Task ExecuteCommandAsync(string name)
    {
        switch (name)
        {
            case "New":
                var newDialog = ProjectSaveDialog("New Song", "Create New Song");
                if (newDialog.ShowDialog(this) != true) return;
                await SaveBeforeSwitchAsync();
                OpenProject(_projects.CreateAtFile(newDialog.FileName));
                break;
            case "Open":
                var openDialog = CreateSongOpenDialog();
                if (openDialog.ShowDialog(this) != true) return;
                await OpenSongFileAsync(openDialog.FileName);
                break;
            case "OpenFolder":
                var folderDialog = new OpenFolderDialog { Title = "Open an existing project folder", InitialDirectory = _projects.ProjectsRoot };
                if (folderDialog.ShowDialog(this) != true) return;
                var legacy = _projects.Load(folderDialog.FolderName);
                await SaveBeforeSwitchAsync();
                legacy = _projects.Load(folderDialog.FolderName);
                OpenProject(legacy);
                break;
            case "OpenChart": await OpenChartAsync(); break;
            case "SaveChart": await SaveChartAsAsync(); break;
            case "ChartInfo": await EditChartInfoAsync(); break;
            case "Library": await OpenLibraryAsync(); break;
            case "SaveMidiTmb": await SaveMidiTmbAsAsync(); break;
            case "SaveProject": await SaveChartFromEditorAsync(); StatusText.Text = "Project saved: " + _project!.ProjectJsonPath; break;
            case "SaveProjectAs":
                var copyDialog = ProjectSaveDialog(_project!.Name, "Save Project As");
                if (copyDialog.ShowDialog(this) != true) return;
                await SaveChartFromEditorAsync();
                var source = _project;
                var copy = await Task.Run(() => _projects.SaveCopyAs(source, copyDialog.FileName));
                OpenProject(copy);
                StatusText.Text = "Project saved: " + copy.ProjectJsonPath;
                break;
            case "Import": await ImportAudioAsync(); break;
            case "Convert":
                if (!await EnsureAudioForActionAsync()) return;
                await SaveChartFromEditorAsync(); await EnsureOggAsync(true); SendAudioToEditor(true);
                UpdateProjectDetails(); StatusText.Text = "OGG saved: " + Path.Combine(_project!.ProjectFolder, _project.OggFile!); break;
            case "SaveOggAs": await SaveOggAsAsync(); break;
            case "Build":
                if (!await EnsureAudioForActionAsync()) return;
                await BuildSongAsync();
                StatusText.Text = "Built OGG, MIDI and TMB chart. Use Export ZIP to save the package."; break;
            case "Generate":
                if (!await EnsureAudioForActionAsync()) return;
                if (_editorNoteCount > 0 && MessageBox.Show(this, "Replace this level's notes with an automatic first draft? Other levels are kept.", "Auto Chart", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
                await SaveChartFromEditorAsync(); await EnsureOggAsync(); await GenerateMidiChartAsync(); break;
            case "Trim":
                if (!double.TryParse(TrimStartText.Text, out var start) || !double.TryParse(TrimEndText.Text, out var end)) throw new InvalidDataException("Enter start and end times in seconds.");
                await TrimAudioAsync(start, end); break;
            case "Export": await ExportZipAsync(); break;
            case "ImportGame": await ImportIntoGameAsync(); break;
            case "RemoveGame": await RemoveProjectFromGameAsync(_project!); break;
            case "GameSongs":
                await SaveBeforeSwitchAsync();
                var manager = new GameSongsWindow(this, _game, _projects);
                if (manager.ShowDialog() == true && manager.SelectedProject is not null)
                {
                    OpenProject(manager.SelectedProject);
                    StatusText.Text = "Game song copied into an editable project with its original chart and audio.";
                }
                RefreshRecentProjects(); break;
            case "GameFolder": RevealFolder(_game.SongsFolder); break;
            case "ChangeGameFolder": ChooseGameDataFolder(); break;
            case "RefreshGame": RefreshRecentProjects(); StatusText.Text = "Game song status refreshed."; break;
            case "ProjectFolder": RevealFolder(_project!.ProjectFolder); break;
            case "WorkingFolder": RevealFolder(_projects.ProjectsRoot); break;
            case "StudioFolder": RevealFolder(AppContext.BaseDirectory); break;
            case "ChangeWorkingFolder":
                var workingDialog = new OpenFolderDialog { Title = "Choose the working folder for new songs", InitialDirectory = _projects.ProjectsRoot };
                if (workingDialog.ShowDialog(this) != true) return;
                _projects.SetWorkingFolder(workingDialog.FolderName);
                WorkingFolderText.Text = _projects.ProjectsRoot;
                WorkingFolderText.ToolTip = _projects.ProjectsRoot;
                RefreshRecentProjects(); StatusText.Text = "Working folder: " + _projects.ProjectsRoot; break;
            case "Setup": await SetupFfmpegAsync(); StatusText.Text = "FFmpeg is ready for conversion and export."; break;
            case "Undo": case "Redo": case "Delete": case "SelectAll": case "ClearSelection": case "Play": case "Stop":
                await ExecuteEditorCommandAsync(name); break;
            case "Exit": await SaveBeforeSwitchAsync(); _canClose = true; Close(); break;
        }
    }

    private const string AudioPattern = "*.mp3;*.wav;*.ogg;*.flac;*.m4a;*.aac;*.wma;*.aiff;*.aif";
    private static bool IsAudioFile(string path) => new[] { ".mp3", ".wav", ".ogg", ".flac", ".m4a", ".aac", ".wma", ".aiff", ".aif" }
        .Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    private OpenFileDialog CreateSongOpenDialog() => new()
    {
        Title = "Open Song or Project", Multiselect = false,
        Filter = "Songs, projects and charts|" + AudioPattern + ";*.msmproj;project.json;*.tmb|Audio files|" + AudioPattern + "|Studio projects|*.msmproj;project.json|TMB charts|*.tmb",
        InitialDirectory = _projects.ProjectsRoot
    };

    private async Task OpenSongFileAsync(string file)
    {
        if (IsAudioFile(file)) { await ImportAudioFileAsync(file, true); return; }
        if (Path.GetExtension(file).Equals(".tmb", StringComparison.OrdinalIgnoreCase))
        {
            var savedProject = _projects.FindProjectForChart(file);
            if (savedProject is null) { await OpenChartFileAsync(file); return; }
            file = savedProject;
        }
        _ = _projects.LoadFile(file);
        await SaveBeforeSwitchAsync(); OpenProject(_projects.LoadFile(file));
    }

    private Task ExecuteEditorCommandAsync(string name)
    {
        var method = name switch
        {
            "Undo" => "undo", "Redo" => "redo", "Delete" => "deleteSelected", "SelectAll" => "selectAll",
            "ClearSelection" => "clearSelection", "Play" => "togglePlay", "Stop" => "stopPlayback",
            _ => throw new InvalidOperationException("Unknown editor command.")
        };
        return EditorWebView.CoreWebView2.ExecuteScriptAsync($"window.msmEditor.{method}()");
    }

    private SaveFileDialog ProjectSaveDialog(string name, string title) => new()
    {
        Title = title, Filter = "MSM Studio project|*.msmproj", DefaultExt = ".msmproj",
        AddExtension = true, OverwritePrompt = true, FileName = ProjectService.SafeName(name) + ".msmproj",
        InitialDirectory = _projects.ProjectsRoot
    };

    private SaveFileDialog CreateChartSaveDialog()
    {
        var project = _project!;
        var last = project.LastChartSavePath;
        return new SaveFileDialog
        {
            Title = "Save Chart As", Filter = "MSM Studio chart|*.tmb", DefaultExt = ".tmb",
            AddExtension = true, OverwritePrompt = true,
            FileName = last is null ? ProjectService.SafeName(project.Name) + ".tmb" : Path.GetFileName(last),
            InitialDirectory = last is not null && Directory.Exists(Path.GetDirectoryName(last)) ? Path.GetDirectoryName(last) : project.ProjectFolder
        };
    }

    private async Task SaveChartAsAsync()
    {
        var dialog = CreateChartSaveDialog();
        if (dialog.ShowDialog(this) != true) return;
        await SaveChartToFileAsync(dialog.FileName);
    }

    private async Task SaveChartToFileAsync(string path)
    {
        await SaveChartFromEditorAsync();
        _tmb.WriteTmb(_project!, path);
        _project!.LastChartSavePath = Path.GetFullPath(path);
        _projects.Save(_project);
        StatusText.Text = "Chart saved: " + path;
    }

    private SaveFileDialog CreateMidiTmbSaveDialog() => new()
    {
        Title = "Save MIDI / TMB As", Filter = "MIDI notes|*.mid|Game chart|*.tmb", DefaultExt = ".mid",
        AddExtension = true, OverwritePrompt = true,
        FileName = ProjectService.SafeName(_project!.Name), InitialDirectory = _project.ProjectFolder
    };

    private async Task SaveMidiTmbAsAsync()
    {
        var dialog = CreateMidiTmbSaveDialog();
        if (dialog.ShowDialog(this) == true) await SaveMidiTmbToFileAsync(dialog.FileName);
    }

    private async Task SaveMidiTmbToFileAsync(string path)
    {
        var extension = Path.GetExtension(path);
        if (extension.Equals(".tmb", StringComparison.OrdinalIgnoreCase))
        {
            await SaveChartToFileAsync(path);
            return;
        }
        if (!extension.Equals(".mid", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Choose a .mid or .tmb filename.");
        await SaveChartFromEditorAsync();
        _midi.Write(_project!, path);
        StatusText.Text = "MIDI saved: " + path;
    }

    private async Task EditChartInfoAsync(string? file = null)
    {
        file ??= _project!.ProjectJsonPath;
        if (IsCurrentProject(file)) await SaveChartFromEditorAsync();
        var project = IsCurrentProject(file) ? _project! : _projects.LoadFile(file, false);
        var dialog = new ChartInfoWindow(this, project);
        if (dialog.ShowDialog() == true) await ApplyRecentChartInfoAsync(file, dialog.Info);
    }

    private async Task OpenLibraryAsync()
    {
        await SaveBeforeSwitchAsync();
        var window = new LibraryWindow(this, _projects);
        if (window.ShowDialog() == true && window.SelectedProject is not null)
        {
            OpenProject(window.SelectedProject); RefreshRecentProjects();
            StatusText.Text = "Downloaded chart opened with its original notes. Use Chart Info, the editor or Play Test to review.";
        }
        else RefreshRecentProjects();
    }

    private Task ApplyChartInfoAsync(SongProject info) => ApplyRecentChartInfoAsync(_project!.ProjectJsonPath, info);

    private Task ApplyRecentChartInfoAsync(string file, SongProject info) =>
        IsCurrentProject(file) ? RunProjectEditAsync("Chart Info", () => ApplyRecentChartInfoCoreAsync(file, info)) : ApplyRecentChartInfoCoreAsync(file, info);

    private async Task ApplyRecentChartInfoCoreAsync(string file, SongProject info)
    {
        ChartValidation.Validate(info);
        var current = IsCurrentProject(file);
        if (current) await SaveChartFromEditorAsync();
        var project = current ? _project! : _projects.LoadFile(file, false);
        project.Name = info.Name; project.ShortName = info.ShortName; project.Author = info.Author;
        project.Year = info.Year; project.Genre = info.Genre; project.Description = info.Description;
        project.Bpm = info.Bpm; project.TimeSignature = info.TimeSignature;
        project.LevelRatings = info.LevelRatings; project.TmbMetadata = info.TmbMetadata;
        _tmb.WriteTmb(project); _projects.Save(project);
        if (current) OpenProject(project, false);
        RefreshRecentProjects(); StatusText.Text = "Chart info saved: " + project.Name + ". Update in Game to apply changes to an installed song.";
    }

    private SaveFileDialog CreateOggSaveDialog()
    {
        var project = _project!;
        var last = project.LastOggSavePath;
        return new SaveFileDialog
        {
            Title = "Save OGG As", Filter = "OGG audio|*.ogg", DefaultExt = ".ogg",
            AddExtension = true, OverwritePrompt = true,
            FileName = last is null ? ProjectService.SafeName(project.Name) + ".ogg" : Path.GetFileName(last),
            InitialDirectory = last is not null && Directory.Exists(Path.GetDirectoryName(last)) ? Path.GetDirectoryName(last) : project.ProjectFolder
        };
    }

    private async Task SaveOggAsAsync()
    {
        var dialog = CreateOggSaveDialog();
        if (dialog.ShowDialog(this) != true) return;
        await SaveOggToFileAsync(dialog.FileName);
    }

    private async Task SaveOggToFileAsync(string path)
    {
        if (!Path.GetExtension(path).Equals(".ogg", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Choose a .ogg filename for the audio.");
        await SaveChartFromEditorAsync();
        await EnsureOggAsync();
        var project = _project!;
        var saved = await Task.Run(() => _export.SaveOgg(project, path));
        project.LastOggSavePath = saved;
        _projects.Save(project);
        UpdateProjectDetails(); StatusText.Text = "OGG saved: " + saved;
    }

    private async Task OpenChartAsync()
    {
        var dialog = new OpenFileDialog { Title = "Open TMB Chart", Filter = "TMB chart|*.tmb", InitialDirectory = _project?.ProjectFolder ?? _projects.ProjectsRoot };
        if (dialog.ShowDialog(this) != true) return;
        await OpenChartFileAsync(dialog.FileName);
    }

    private async Task OpenChartFileAsync(string file)
    {
        var chart = _tmb.ReadTmb(file);
        await SaveBeforeSwitchAsync();
        var project = _project ?? _projects.Create(chart.Name);
        project.Bpm = chart.Bpm; project.OffsetSeconds = chart.OffsetSeconds; project.TimeSignature = chart.TimeSignature; project.Notes = chart.Notes;
        project.Difficulty = chart.Difficulty; project.Levels = chart.Levels;
        project.Author = chart.Author; project.Genre = chart.Genre; project.Year = chart.Year;
        project.ShortName = chart.ShortName; project.LevelRatings = chart.LevelRatings;
        project.Description = chart.Description; project.TmbMetadata = chart.TmbMetadata;
        project.LastChartSavePath = file;
        _projects.Save(project); _tmb.WriteTmb(project);
        OpenProject(project);
        StatusText.Text = "Chart loaded: " + file;
    }

    private static void RevealFolder(string path)
    {
        path = Path.GetFullPath(path);
        if (!Directory.Exists(path)) throw new DirectoryNotFoundException("The folder was not found: " + path);
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }

    private void CreatorLink_RequestNavigate(object sender, System.Windows.Navigation.RequestNavigateEventArgs e)
    {
        e.Handled = true;
        try { Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true }); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            StatusText.Text = "Could not open the creator website: " + ex.Message;
        }
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            RefreshRecentProjects();
            _gameStatusTimer.Start();
            var userData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MSM Song Studio", "WebView2");
            var environment = await CoreWebView2Environment.CreateAsync(userDataFolder: userData);
            await EditorWebView.EnsureCoreWebView2Async(environment);
            _editorFolder = WebEditorAssets.EnsureExtracted();
            var core = EditorWebView.CoreWebView2;
            core.WebMessageReceived += CoreWebView2_WebMessageReceived;
            core.AddWebResourceRequestedFilter("https://app.local/*", CoreWebView2WebResourceContext.All);
            core.WebResourceRequested += (_, args) =>
            {
                try
                {
                    var relative = new Uri(args.Request.Uri).AbsolutePath;
                    string? path = relative switch
                    {
                        "/index.html" => Path.Combine(_editorFolder, "index.html"),
                        "/style.css" => Path.Combine(_editorFolder, "style.css"),
                        "/editor.js" => Path.Combine(_editorFolder, "editor.js"),
                        "/trim.js" => Path.Combine(_editorFolder, "trim.js"),
                        "/preview.js" => Path.Combine(_editorFolder, "preview.js"),
                        _ => relative == $"/audio/{_audioKey}" ? _audioPath : null
                    };
                    if (path is null || !File.Exists(path))
                    {
                        args.Response = core.Environment.CreateWebResourceResponse(new MemoryStream(), 404, "Not Found", "Content-Length: 0");
                        return;
                    }
                    var range = args.Request.Headers.Contains("Range") ? args.Request.Headers.GetHeader("Range") : null;
                    var response = LocalResourceService.Open(path, range);
                    args.Response = core.Environment.CreateWebResourceResponse(response.Content, response.Status, response.Reason, response.Headers);
                }
                catch (Exception ex)
                {
                    StatusText.Text = "Could not read audio: " + ex.Message;
                    args.Response = core.Environment.CreateWebResourceResponse(new MemoryStream(), 500, "Read Error", "Content-Length: 0");
                }
            };
            EditorWebView.Source = new Uri("https://app.local/index.html");
        }
        catch (Exception ex) { ShowError("Editor startup", ex); }
    }

    private async void RecentProjectsList_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_busy || _refreshingRecents || e.AddedItems.Count == 0 || RecentProjectsList.SelectedItem is not RecentProjectItem item) return;
        if (string.Equals(_project?.ProjectJsonPath, item.FilePath, StringComparison.OrdinalIgnoreCase)) return;
        await RunActionAsync("Open project", () => OpenSongFileAsync(item.FilePath));
    }

    private RecentProjectItem? RecentItemAt(object source) => source is DependencyObject element &&
        ItemsControl.ContainerFromElement(RecentProjectsList, element) is ListBoxItem container ? container.DataContext as RecentProjectItem : null;

    private void RecentProjectsList_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        _recentContextItem = RecentItemAt(e.OriginalSource);
        // Opening an item's menu must not switch away from the song currently being edited.
        e.Handled = true;
    }

    private void RecentProjectsList_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (_busy) { e.Handled = true; return; }
        _recentContextItem = e.CursorLeft < 0 ? RecentProjectsList.SelectedItem as RecentProjectItem : RecentItemAt(e.OriginalSource) ?? _recentContextItem;
        foreach (var item in RecentProjectsContextMenu.Items.OfType<MenuItem>())
        {
            item.IsEnabled = item.Tag?.ToString() == "Refresh" || _recentContextItem is not null &&
                (item.Tag?.ToString() is "Folder" or "Remove" || _editorReady);
            if (item.Tag?.ToString() == "RemoveGame")
            {
                var saved = _recentContextItem is null ? null : ReadRecentProject(_recentContextItem.FilePath);
                item.IsEnabled = saved is not null && GameSongService.FindLevel(saved, _gameSongs) is not null;
            }
        }
    }

    private async void RecentProjectMenu_Click(object sender, RoutedEventArgs e)
    {
        var action = ((MenuItem)sender).Tag?.ToString();
        var item = _recentContextItem;
        if (action != "Refresh" && item is null) return;
        await RunActionAsync("Recent projects", async () =>
        {
            switch (action)
            {
                case "Open": await OpenSongFileAsync(item!.FilePath); break;
                case "Rename":
                    var dialog = new SongNameDialog(this, _projects.ProjectName(item!.FilePath));
                    if (dialog.ShowDialog() == true) await RenameRecentProjectAsync(item.FilePath, dialog.SongName);
                    break;
                case "Save": await SaveRecentProjectAsync(item!.FilePath); break;
                case "ChartInfo": await EditChartInfoAsync(item!.FilePath); break;
                case "Folder": RevealFolder(_projects.LoadFile(item!.FilePath).ProjectFolder); break;
                case "ImportGame":
                    if (!IsCurrentProject(item!.FilePath)) await OpenSongFileAsync(item.FilePath);
                    await ImportIntoGameAsync(); break;
                case "RemoveGame": await RemoveProjectFromGameAsync(IsCurrentProject(item!.FilePath) ? _project! : _projects.LoadFile(item.FilePath, false)); break;
                case "Remove": RemoveRecentProject(item!.FilePath); break;
                case "Refresh": RefreshRecentProjects(); StatusText.Text = "Recent projects refreshed."; break;
            }
        });
    }

    private bool IsCurrentProject(string file) => _project is not null &&
        Path.GetFullPath(file).Equals(Path.GetFullPath(_project.ProjectJsonPath), StringComparison.OrdinalIgnoreCase);

    private async Task SaveRecentProjectAsync(string file)
    {
        if (IsCurrentProject(file)) await SaveChartFromEditorAsync();
        else
        {
            var project = _projects.LoadFile(file);
            _tmb.WriteTmb(project); _projects.Save(project);
        }
        RefreshRecentProjects(); StatusText.Text = "Project saved: " + file;
    }

    private async Task RenameRecentProjectAsync(string file, string name)
    {
        name = name.Trim();
        if (string.IsNullOrWhiteSpace(name)) throw new InvalidDataException("Enter a song name.");
        var current = IsCurrentProject(file);
        if (current) await SaveChartFromEditorAsync();
        var project = current ? _project! : _projects.LoadFile(file);
        var previous = project.Name;
        project.Name = name;
        try { _tmb.WriteTmb(project); _projects.Save(project); }
        catch { project.Name = previous; throw; }
        if (current) UpdateProjectDetails();
        RefreshRecentProjects(); StatusText.Text = "Song renamed: " + name;
    }

    private void RemoveRecentProject(string file)
    {
        _projects.RemoveRecent(file); RefreshRecentProjects();
        StatusText.Text = "Removed from recent projects. Project and audio files are kept.";
    }

    private void OpenProject(SongProject project, bool restoreRecent = true)
    {
        if (!_keepEditorHistory) _projectEdits.Clear();
        _project = project; _projectKey = Guid.NewGuid().ToString("N"); _loadedProjectKey = "";
        _autoSaveTimer.Stop(); _editorRevision = 0;
        _projects.RememberOpened(project, restoreRecent);
        _dirty = _canUndo = _canRedo = _audioReady = false;
        _audioDuration = 0;
        _selectedCount = 0; _editorNoteCount = project.Notes.Count;
        _changingDifficulty = true;
        DifficultyPicker.SelectedIndex = Array.IndexOf(new[] { "Easy", "Normal", "Hard", "Expert" }, project.Difficulty);
        _changingDifficulty = false;
        DifficultyPicker.IsEnabled = !_busy;
        UpdateProjectDetails(); StatusText.Text = "Project loaded";
        RefreshRecentProjects(); SendProjectToEditor();
        CommandManager.InvalidateRequerySuggested();
    }

    private void UpdateProjectDetails()
    {
        if (_project is null) return;
        ProjectTitle.Text = _project.Name;
        ProjectPathText.Text = _project.ProjectJsonPath;
        ProjectInfoText.Text = $"{_project.Difficulty}  ·  {_editorNoteCount} {(_editorNoteCount == 1 ? "note" : "notes")}  ·  {_project.Bpm:0.##} BPM  ·  {_project.AudioName ?? (_project.SourceAudioFile is null ? "No audio" : Path.GetFileName(_project.SourceAudioFile))}";
        ProjectStateText.Text = _dirty ? "Unsaved changes" : "Saved";
        UpdateGameDetails();
        Title = (_dirty ? "* " : "") + _project.Name + " — My Singing Monster Karaoke Studio";
    }

    private async Task<bool> ImportAudioAsync()
    {
        var dialog = new OpenFileDialog { Title = "Choose MP3 or Audio", Filter = "Audio files|" + AudioPattern + "|MP3 audio|*.mp3|All files|*.*", Multiselect = false, CheckFileExists = true };
        if (dialog.ShowDialog(this) != true) return false;
        await ImportAudioFileAsync(dialog.FileName);
        return true;
    }

    private async Task ImportAudioFileAsync(string file, bool asNewSong = false)
    {
        file = Path.GetFullPath(file);
        if (!IsAudioFile(file)) throw new InvalidDataException("Choose an MP3, WAV, OGG, FLAC, M4A, AAC, WMA, or AIFF audio file.");
        if (!File.Exists(file)) throw new FileNotFoundException("The audio file was not found.", file);
        await SaveBeforeSwitchAsync();
        var create = asNewSong || _project is null || _project.SourceAudioFile is not null || _project.OggFile is not null || _project.Notes.Count > 0;
        var project = create ? _projects.CreateForAudio(file) : _project!;
        var destName = "source-" + Guid.NewGuid().ToString("N") + Path.GetExtension(file).ToLowerInvariant();
        var dest = Path.Combine(project.ProjectFolder, destName);
        await Task.Run(() => File.Copy(file, dest));
        project.SourceAudioFile = destName; project.AudioName = Path.GetFileName(file); project.OggFile = null; project.MidiFile = null;
        project.AudioDurationSeconds = 0;
        _projects.Save(project);
        if (create) OpenProject(project);
        else { _audioReady = false; SendAudioToEditor(); UpdateProjectDetails(); }
        CommandManager.InvalidateRequerySuggested();
        try
        {
            await EnsureOggAsync(); await GenerateMidiChartAsync(true);
            StatusText.Text = "Imported " + Path.GetFileName(file) + " and created automatic charts. Play the song and edit notes as needed.";
        }
        catch (InvalidDataException ex)
        {
            StatusText.Text = "Audio imported. Automatic notes need a clearer melody: " + ex.Message;
        }
    }

    private async Task<bool> EnsureAudioForActionAsync()
    {
        if (_project is not null && new[] { _project.SourceAudioFile, _project.OggFile }
            .Any(f => f is not null && File.Exists(Path.Combine(_project.ProjectFolder, f)))) return true;
        return await ImportAudioAsync();
    }

    private void UpdateFfmpegStatus()
    {
        FfmpegSetupStatus.Text = _ffmpeg.IsAvailable ? "Audio converter ready" : "Audio converter: setup available";
        InstallFfmpegButton.Content = _ffmpeg.IsAvailable ? "Check FFmpeg" : "Install FFmpeg";
    }

    private async Task SetupFfmpegAsync()
    {
        FfmpegSetupStatus.Text = "Setting up audio converter…";
        FfmpegInstallProgress.Visibility = Visibility.Visible; StatusText.Text = "Setting up FFmpeg…";
        try
        {
            await _ffmpeg.EnsureInstalledAsync(new Progress<double>(value =>
            {
                if (FfmpegInstallProgress.Visibility != Visibility.Visible) return;
                FfmpegInstallProgress.Value = value;
                FfmpegSetupStatus.Text = $"Setting up audio converter… {value:F0}%";
            }));
        }
        finally { FfmpegInstallProgress.Visibility = Visibility.Collapsed; UpdateFfmpegStatus(); }
    }

    private async Task ExportZipAsync()
    {
        if (!await EnsureAudioForActionAsync()) return;
        var dialog = new SaveFileDialog
        {
            Title = "Export Song ZIP", Filter = "ZIP archive|*.zip", DefaultExt = ".zip", AddExtension = true,
            OverwritePrompt = true, FileName = ProjectService.SafeName(_project!.Name) + "-" + _project.Difficulty + ".zip", InitialDirectory = _project.ProjectFolder
        };
        if (dialog.ShowDialog(this) != true) return;
        await ExportZipToFileAsync(dialog.FileName);
    }

    private async Task ExportZipToFileAsync(string path)
    {
        await BuildSongAsync();
        var project = _project!;
        var zip = await Task.Run(() => _export.ExportZip(project, path));
        UpdateProjectDetails(); StatusText.Text = "Song ZIP exported: " + zip + " · " + project.Difficulty + " level";
    }

    private bool ChooseGameDataFolder()
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Choose My Singing Monsters Karaoke data folder (contains Songs)",
            InitialDirectory = Directory.Exists(_game.DataFolder) ? _game.DataFolder : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
        };
        if (dialog.ShowDialog(this) != true) return false;
        _projects.SetGameDataFolder(dialog.FolderName); _game = new GameSongService(dialog.FolderName);
        RefreshRecentProjects(); StatusText.Text = "Game song folder: " + _game.SongsFolder; return true;
    }

    private async Task ImportIntoGameAsync()
    {
        if (!_game.IsReady && !ChooseGameDataFolder()) return;
        if (!await EnsureAudioForActionAsync()) return;
        var existing = GameSongService.FindLevel(_project!, _game.GetSongs());
        if (existing is not null && existing.TrackRef != _project!.TrackRef + "_" + _project.Difficulty.ToLowerInvariant() &&
            MessageBox.Show(this, $"A game song named ‘{existing.Name}’ ({existing.Level}) already exists. Replace it with this project's current level? A backup will be kept.",
                "Update Existing Game Song", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        await BuildSongAsync();
        var project = _project!;
        StatusText.Text = "Adding song to the game…";
        var folder = await Task.Run(() => _game.ImportSong(project));
        RefreshRecentProjects();
        StatusText.Text = $"{project.Difficulty} level added to game: {folder}. Restart the game or refresh its song list if it is open.";
    }

    private Task RemoveProjectFromGameAsync(SongProject project)
    {
        var song = GameSongService.FindLevel(project, _game.GetSongs());
        if (song is null) { RefreshRecentProjects(); StatusText.Text = "This level is not in the game."; return Task.CompletedTask; }
        if (MessageBox.Show(this, $"Remove ‘{song.Name}’ ({song.Level}) from the game?\n\nIts game files will be moved to a backup. The studio project and audio are kept.",
            "Remove Song from Game", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return Task.CompletedTask;
        var backup = _game.RemoveSong(song); RefreshRecentProjects();
        StatusText.Text = "Removed from game. Backup: " + backup + ". Restart the game or refresh its song list if it is open.";
        return Task.CompletedTask;
    }

    private SongProject? ReadRecentProject(string file)
    {
        try { return _projects.LoadFile(file, false); }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidDataException or UnauthorizedAccessException) { return null; }
    }

    private string GameStatus(SongProject project, bool currentLevel = false)
    {
        if (_gameReadFailed) return "Game status unavailable";
        if (!_game.IsReady) return "Choose game folder";
        var installed = GameSongService.ForProject(project, _gameSongs);
        if (currentLevel)
        {
            var level = GameSongService.FindLevel(project, installed);
            return level is not null ? LevelSyncLabel(project, level, IsCurrentProject(project.ProjectJsonPath) && _dirty) :
                installed.Count > 0 ? project.Difficulty + " not in game · other levels added" : "Not in game";
        }
        return installed.Count == 0 ? "Not in game" : string.Join("; ", installed.Select(s =>
        {
            var levelProject = JsonSerializer.Deserialize<SongProject>(JsonSerializer.Serialize(project))!;
            if (s.Level != project.Difficulty)
            {
                if (!project.Levels.TryGetValue(s.Level, out var notes)) return s.Level + " · comparison unavailable";
                levelProject.Difficulty = s.Level; levelProject.Notes = notes;
            }
            return LevelSyncLabel(levelProject, s, IsCurrentProject(project.ProjectJsonPath) && _dirty && s.Level == project.Difficulty);
        }).Distinct());
    }

    private string LevelSyncLabel(SongProject project, InstalledGameSong song, bool pending)
    {
        var state = pending ? "chart edits pending" : _game.SyncStatus(project, song);
        return state == "up to date" ? "In game · " + song.Level + " · Up to date" : "In game · " + song.Level + " · Out of sync (" + state + ")";
    }

    private void UpdateGameDetails()
    {
        GameStatusText.Text = _project is null ? "" : GameStatus(_project, true);
        GameStatusText.Foreground = (System.Windows.Media.Brush)new System.Windows.Media.BrushConverter().ConvertFromString(GameStatusColor(GameStatusText.Text))!;
        GameStatusText.ToolTip = GameStatusText.Text + "\n" + _game.SongsFolder;
        ImportGameButton.Content = _project is not null && GameSongService.FindLevel(_project, _gameSongs) is not null ? "Update in Game" : "Import into Game";
        CommandManager.InvalidateRequerySuggested();
    }

    private async Task BuildSongAsync()
    {
        await SaveChartFromEditorAsync(); await EnsureOggAsync();
        if (_project!.Notes.Count == 0) await GenerateMidiChartAsync();
        else
        {
            _midi.Write(_project, Path.Combine(_project.ProjectFolder, "song.mid"));
            _project.MidiFile = "song.mid";
            _projects.Save(_project);
        }
        _tmb.WriteTmb(_project); UpdateProjectDetails();
    }

    private Task GenerateMidiChartAsync(bool allLevels = false) =>
        RunProjectEditAsync("Auto Chart", () => GenerateMidiChartCoreAsync(allLevels));

    private async Task GenerateMidiChartCoreAsync(bool allLevels)
    {
        var project = _project!;
        StatusText.Text = "Following melody and attacks in the audio…";
        var draft = await _draft.AnalyzeAsync(Path.Combine(project.ProjectFolder, project.OggFile!), _ffmpeg, project.Bpm,
            new Progress<double>(p => StatusText.Text = $"Following melody and audio attacks… {p:0}%"));
        if (draft.Notes.Count == 0) throw new InvalidDataException("No clear notes were detected. Add notes manually in the chart editor, or try a clearer melody recording.");
        if (allLevels && draft.DetectedBpm is double tempo) project.Bpm = tempo;
        var midiProject = new SongProject { Bpm = project.Bpm, TimeSignature = project.TimeSignature, Notes = draft.MidiNotes };
        var midiPath = Path.Combine(project.ProjectFolder, "song.mid");
        _midi.Write(midiProject, midiPath);
        // Keep the continuous audio trajectory: a MIDI round-trip would discard curves and fractional pitches.
        project.DetectedBpm = draft.DetectedBpm; project.AudioOnsets = draft.Onsets;
        project.MidiFile = "song.mid";
        if (allLevels)
            foreach (var level in new[] { "Easy", "Normal", "Hard", "Expert" }) project.Levels[level] = AudioDraftService.CropNotes(AudioDraftService.MakeAudioLevel(draft, level), 0, project.OffsetSeconds, double.MaxValue);
        else project.Levels[project.Difficulty] = AudioDraftService.CropNotes(AudioDraftService.MakeAudioLevel(draft, project.Difficulty), 0, project.OffsetSeconds, double.MaxValue);
        project.Notes = project.Levels[project.Difficulty];
        _projects.Save(project); _tmb.WriteTmb(project); OpenProject(project, false);
        var curves = project.Notes.Count(n => n.CurvePoints.Count > 0);
        StatusText.Text = $"Generated {project.Notes.Count} notes, including {curves} melody curves, from audio attacks and pitch movement. No overlaps. Review the draft while playing.";
    }

    private async Task RunProjectEditAsync(string label, Func<Task> action)
    {
        await SaveChartFromEditorAsync();
        var before = JsonSerializer.Serialize(_project!);
        _keepEditorHistory = true;
        try
        {
            await action();
            var after = JsonSerializer.Serialize(_project!);
            var id = Guid.NewGuid().ToString("N");
            _projectEdits[id] = new ProjectEdit(label, before, after);
            // The editor retains at most 100 undo entries, including these project boundaries.
            while (_projectEdits.Count > 100) _projectEdits.Remove(_projectEdits.Keys.First());
            EditorWebView.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(new { type = "recordProjectEdit", projectKey = _projectKey, id, label }));
        }
        catch
        {
            RestoreProjectSnapshot(before);
            throw;
        }
        finally { _keepEditorHistory = false; }
    }

    private void RestoreProjectSnapshot(string json)
    {
        var restored = JsonSerializer.Deserialize<SongProject>(json) ?? throw new InvalidDataException("Undo snapshot is missing.");
        restored.ProjectFilePath = _project!.ProjectFilePath;
        var audioFile = restored.SourceAudioFile ?? restored.OggFile;
        if (audioFile is not null && !File.Exists(Path.Combine(restored.ProjectFolder, audioFile)))
            throw new FileNotFoundException("The audio for this undo step is missing. Restore the project's audio files first.");
        ChartValidation.Validate(restored);
        if (restored.MidiFile is not null) _midi.Write(restored, Path.Combine(restored.ProjectFolder, restored.MidiFile));
        _tmb.WriteTmb(restored); _projects.Save(restored);
        // Keep references used by the active project and windows valid.
        foreach (var property in typeof(SongProject).GetProperties().Where(p => p.CanWrite))
            property.SetValue(_project, property.GetValue(restored));
        OpenProject(_project, false);
    }

    private Task ReplayProjectEditAsync(string id, string direction)
    {
        if (!_projectEdits.TryGetValue(id, out var edit) || direction is not ("undo" or "redo"))
            throw new InvalidOperationException("This undo step is no longer available.");
        _keepEditorHistory = true;
        try
        {
            RestoreProjectSnapshot(direction == "undo" ? edit.Before : edit.After);
            StatusText.Text = (direction == "undo" ? "Undid " : "Redid ") + edit.Label + ". Project saved; exported and installed game files are unchanged.";
        }
        finally { _keepEditorHistory = false; }
        return Task.CompletedTask;
    }

    private void SetTrimRangeText(double start, double end)
    {
        _updatingTrimRange = true;
        try { TrimStartText.Text = start.ToString("0.###"); TrimEndText.Text = end.ToString("0.###"); }
        finally { _updatingTrimRange = false; }
    }

    private void TrimRangeText_Changed(object sender, TextChangedEventArgs e)
    {
        if (_updatingTrimRange || !_editorReady || _project is null || _busy || _audioDuration <= 0) return;
        if (!double.TryParse(TrimStartText.Text, out var start) || !double.TryParse(TrimEndText.Text, out var end) ||
            !double.IsFinite(start) || !double.IsFinite(end) || start < 0 || end <= start || end > _audioDuration + .001) return;
        EditorWebView.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(new { type = "trimRange", projectKey = _projectKey, start, end }));
    }

    private Task TrimAudioAsync(double start, double end) =>
        RunProjectEditAsync("Trim Audio", () => TrimAudioCoreAsync(start, end));

    private async Task TrimAudioCoreAsync(double start, double end)
    {
        if (!double.IsFinite(start) || !double.IsFinite(end) || start < 0 || end <= start || end > _audioDuration + .05)
            throw new InvalidDataException("Choose a start before the end, within the song's duration.");
        await SaveChartFromEditorAsync();
        var project = _project!;
        var file = "trimmed-" + Guid.NewGuid().ToString("N") + ".ogg";
        var input = _audioPath ?? throw new InvalidOperationException("Import audio first.");
        StatusText.Text = "Trimming audio and aligning chart notes…";
        await _ffmpeg.TrimToOggAsync(input, Path.Combine(project.ProjectFolder, file), start, end);
        project.Notes = AudioDraftService.CropNotes(project.Notes, project.OffsetSeconds, start, end);
        foreach (var level in project.Levels.Keys.ToList()) project.Levels[level] = AudioDraftService.CropNotes(project.Levels[level], project.OffsetSeconds, start, end);
        project.OffsetSeconds = 0; project.SourceAudioFile = project.OggFile = file; project.MidiFile = null;
        project.AudioDurationSeconds = end - start;
        project.AudioOnsets = project.AudioOnsets.Where(t => t >= start && t < end).Select(t => t - start).ToList();
        project.AudioName = (project.AudioName ?? "Audio") + " (trimmed)";
        _projects.Save(project); _tmb.WriteTmb(project); OpenProject(project, false);
        StatusText.Text = $"Trimmed to {end - start:0.###} seconds. Original audio is kept in the project folder.";
    }

    private async void DifficultyPicker_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_changingDifficulty || _project is null || _busy || DifficultyPicker.SelectedItem is not System.Windows.Controls.ComboBoxItem item) return;
        var difficulty = item.Content.ToString()!;
        if (difficulty == _project.Difficulty) return;
        await RunActionAsync("Change level", () => ChangeDifficultyAsync(difficulty));
    }

    private Task ChangeDifficultyAsync(string difficulty) =>
        RunProjectEditAsync("Change level", () => ChangeDifficultyCoreAsync(difficulty));

    private async Task ChangeDifficultyCoreAsync(string difficulty)
    {
        if (difficulty is not ("Easy" or "Normal" or "Hard" or "Expert")) throw new InvalidDataException("Choose Easy, Normal, Hard, or Expert.");
        await SaveChartFromEditorAsync();
        var project = _project!;
        if (!project.Levels.TryGetValue(difficulty, out var notes)) project.Levels[difficulty] = notes = AudioDraftService.MakeLevel(project.Notes, difficulty);
        project.Difficulty = difficulty; project.Notes = notes;
        _projects.Save(project); _tmb.WriteTmb(project); OpenProject(project, false);
        StatusText.Text = difficulty + " level loaded. Edits are saved separately for each level.";
    }

    private async Task EnsureOggAsync(bool force = false)
    {
        var project = _project!;
        var input = new[] { project.SourceAudioFile, project.OggFile }.FirstOrDefault(f => f is not null && File.Exists(Path.Combine(project.ProjectFolder, f)));
        if (input is null) throw new InvalidOperationException("Import an audio file first.");
        var sourceHash = await Task.Run(() => GameSongService.HashFile(Path.Combine(project.ProjectFolder, input)));
        if (!force && project.OggFile is not null && File.Exists(Path.Combine(project.ProjectFolder, project.OggFile)) &&
            (project.ConvertedSourceHash is null || project.ConvertedSourceHash == sourceHash))
        { project.ConvertedSourceHash = sourceHash; return; }
        if (input == project.OggFile) { project.ConvertedSourceHash = sourceHash; return; }
        await SetupFfmpegAsync(); StatusText.Text = "Converting audio to OGG…";
        await _ffmpeg.ConvertToOggAsync(Path.Combine(project.ProjectFolder, input), Path.Combine(project.ProjectFolder, "song.ogg"));
        project.OggFile = "song.ogg"; project.ConvertedSourceHash = sourceHash; _projects.Save(project);
    }

    private async Task SaveBeforeSwitchAsync() { if (_project is not null && _editorReady) await SaveChartFromEditorAsync(); }
    private async Task SaveChartFromEditorAsync()
    {
        if (_project is null) return;
        if (!_editorReady || EditorWebView.CoreWebView2 is null) throw new InvalidOperationException("The editor is still loading. Try again shortly.");
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (_loadedProjectKey != _projectKey)
        {
            if (DateTime.UtcNow >= deadline) throw new InvalidOperationException("The song is still loading in the editor. Try again shortly.");
            await Task.Delay(25);
        }
        var snapshot = await EditorWebView.CoreWebView2.ExecuteScriptAsync("window.msmEditor.getSnapshot()");
        SaveSnapshot(snapshot);
    }

    private async void CoreWebView2_WebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        if (!e.Source.StartsWith("https://app.local/", StringComparison.OrdinalIgnoreCase)) return;
        try
        {
            using var doc = JsonDocument.Parse(e.WebMessageAsJson);
            var root = doc.RootElement; var type = root.GetProperty("type").GetString();
            if (type == "editorReady")
            {
                _editorReady = true;
                if (_project is null && _projects.LastProjectFile is { } last) OpenProject(_projects.LoadFile(last));
                else SendProjectToEditor();
                CommandManager.InvalidateRequerySuggested(); return;
            }
            if (!root.TryGetProperty("projectKey", out var key) || key.GetString() != _projectKey) return;
            if (type == "studioCommand")
            {
                var name = root.GetProperty("command").GetString();
                var command = name switch { "New" => StudioCommands.New, "Open" => StudioCommands.Open, "SaveProject" => StudioCommands.SaveProject, "SaveProjectAs" => StudioCommands.SaveProjectAs, "Trim" => StudioCommands.Trim, _ => null };
                if (command?.CanExecute(null, this) == true) command.Execute(null, this);
                return;
            }
            if (_project is null) return;
            if (type == "projectHistory" && !_busy)
            {
                var id = root.GetProperty("id").GetString() ?? "";
                var direction = root.GetProperty("direction").GetString() ?? "";
                await RunActionAsync("Project undo/redo", async () =>
                {
                    try { await ReplayProjectEditAsync(id, direction); }
                    catch
                    {
                        EditorWebView.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(new { type = "cancelProjectHistory", projectKey = _projectKey, id, direction }));
                        throw;
                    }
                });
                return;
            }
            if (type == "trimRange" && !_busy)
            {
                if (root.TryGetProperty("start", out var trimStart) && trimStart.TryGetDouble(out var start) &&
                    root.TryGetProperty("end", out var trimEnd) && trimEnd.TryGetDouble(out var end) &&
                    double.IsFinite(start) && double.IsFinite(end) && start >= 0 && end > start && end <= _audioDuration + .001)
                    SetTrimRangeText(start, end);
                return;
            }
            if (type == "dirty")
            {
                _dirty = true;
                if (root.TryGetProperty("revision", out var revision)) _editorRevision = revision.GetInt64();
                _autoSaveTimer.Stop(); _autoSaveTimer.Start();
                UpdateProjectDetails(); StatusText.Text = "Saving chart changes…";
            }
            else if (type == "saveChart" && !_busy) await RunActionAsync("Save chart", SaveChartAsAsync);
            else if (type == "audioStatus" && !_busy)
            {
                var passive = root.TryGetProperty("passive", out var notice) && notice.GetBoolean();
                if (!passive || StatusText.Text is "Ready" or "Project loaded" or "Loading audio…")
                    StatusText.Text = root.GetProperty("message").GetString() ?? "Ready";
            }
            else if (type == "editorState")
            {
                _loadedProjectKey = _projectKey;
                _editorNoteCount = root.GetProperty("noteCount").GetInt32(); _selectedCount = root.GetProperty("selectedCount").GetInt32();
                _canUndo = root.GetProperty("canUndo").GetBoolean(); _canRedo = root.GetProperty("canRedo").GetBoolean();
                _audioReady = root.GetProperty("audioReady").GetBoolean();
                if (root.TryGetProperty("duration", out var duration)) _audioDuration = duration.GetDouble();
                if (_audioReady && double.IsFinite(_audioDuration) && _audioDuration > 0) _project.AudioDurationSeconds = _audioDuration;
                if (_audioDuration > 0 && _trimAudioKey != _audioKey)
                {
                    _trimAudioKey = _audioKey;
                    SetTrimRangeText(0, _audioDuration);
                }
                UpdateProjectDetails(); CommandManager.InvalidateRequerySuggested();
            }
        }
        catch (Exception ex) { ShowError("Studio action", ex); }
    }

    private void SaveSnapshot(string json)
    {
        var project = _project ?? throw new InvalidOperationException("Open a project first.");
        using var doc = JsonDocument.Parse(json); var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object || root.GetProperty("projectKey").GetString() != _projectKey)
            throw new InvalidOperationException("The editor has not finished loading this project.");
        var chart = new SongProject
        {
            Bpm = root.GetProperty("bpm").GetDouble(), OffsetSeconds = root.GetProperty("offset").GetDouble(), TimeSignature = project.TimeSignature,
            Notes = root.GetProperty("notes").Deserialize<List<ChartNote>>(new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                ?? throw new InvalidDataException("Chart notes are missing.")
        };
        ChartValidation.Validate(chart);
        project.Bpm = chart.Bpm; project.OffsetSeconds = chart.OffsetSeconds; project.Notes = chart.Notes.OrderBy(n => n.Time).ToList();
        if (root.TryGetProperty("showGrid", out var grid)) project.ShowGrid = grid.GetBoolean();
        project.Levels[project.Difficulty] = project.Notes.Select(NotePath.Clone).ToList();
        _projects.Save(project); _tmb.WriteTmb(project);
        var savedRevision = root.TryGetProperty("revision", out var revision) ? revision.GetInt64() : _editorRevision;
        _dirty = savedRevision < _editorRevision; _editorNoteCount = project.Notes.Count;
        EditorWebView.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(new { type = "saved", projectKey = _projectKey, revision = savedRevision }));
        UpdateProjectDetails(); StatusText.Text = $"Saved {project.Notes.Count} notes"; RefreshRecentProjects();
    }

    private string? PrepareAudio(bool preferConverted = false)
    {
        var project = _project!;
        var candidates = preferConverted ? new[] { project.OggFile, project.SourceAudioFile } : new[] { project.SourceAudioFile, project.OggFile };
        _audioPath = candidates.Where(name => name is not null).Select(name => Path.GetFullPath(Path.Combine(project.ProjectFolder, name!))).FirstOrDefault(File.Exists);
        _audioKey = Guid.NewGuid().ToString("N");
        return _audioPath is not null && File.Exists(_audioPath) ? $"https://app.local/audio/{_audioKey}" : null;
    }

    private void SendProjectToEditor()
    {
        if (!_editorReady || _project is null) return;
        EditorWebView.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(new
        {
            type = "loadProject", projectKey = _projectKey, name = _project.Name, bpm = _project.Bpm,
            offset = _project.OffsetSeconds, showGrid = _project.ShowGrid, audioUrl = PrepareAudio(), notes = _project.Notes, audioOnsets = _project.AudioOnsets, keepHistory = _keepEditorHistory
        }));
    }
    private void SendAudioToEditor(bool preferConverted = false) => EditorWebView.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(new
    {
        type = "loadAudio", projectKey = _projectKey, audioUrl = PrepareAudio(preferConverted)
    }));

    private async Task RunActionAsync(string name, Func<Task> action)
    {
        if (_busy) return;
        if (name is "Undo" or "Redo" or "Delete" or "SelectAll" or "ClearSelection" or "Play" or "Stop")
        {
            try { await action(); } catch (Exception ex) { ShowError(name, ex); }
            return;
        }
        _busy = true; SetButtonsEnabled(false);
        try { await action(); }
        catch (Exception ex) { ShowError(name, ex); }
        finally { _busy = false; SetButtonsEnabled(true); }
    }
    private async void AutoSaveTimer_Tick(object? sender, EventArgs e)
    {
        _autoSaveTimer.Stop();
        if (!_dirty) return;
        try { if (!await TryAutoSaveAsync()) _autoSaveTimer.Start(); }
        catch (Exception ex) { StatusText.Text = "Automatic save failed: " + ex.Message + ". Use Save Project to try again."; }
    }
    private async Task<bool> TryAutoSaveAsync()
    {
        if (!_dirty) return true;
        if (_busy || _autoSaving || !_editorReady || _project is null || _loadedProjectKey != _projectKey) return false;
        _autoSaving = true;
        var key = _projectKey;
        try
        {
            var snapshot = await EditorWebView.CoreWebView2.ExecuteScriptAsync("window.msmEditor.isInteracting() ? null : window.msmEditor.getSnapshot()");
            if (snapshot == "null" || _busy || key != _projectKey) return false;
            SaveSnapshot(snapshot);
            return !_dirty;
        }
        finally { _autoSaving = false; }
    }
    private void SetButtonsEnabled(bool enabled)
    {
        RecentProjectsList.IsEnabled = StudioMenu.IsEnabled = AudioActions.IsEnabled = FooterActions.IsEnabled = enabled;
        DifficultyPicker.IsEnabled = enabled && _project is not null;
        OperationProgress.Visibility = enabled ? Visibility.Collapsed : Visibility.Visible;
        CommandManager.InvalidateRequerySuggested();
        if (_editorReady) EditorWebView.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(new { type = "busy", busy = !enabled }));
    }
    private async void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        _autoSaveTimer.Stop();
        if (_canClose) { _gameStatusTimer.Stop(); return; }
        if (_busy) { e.Cancel = true; StatusText.Text = "Wait for the current operation to finish."; return; }
        if (!_dirty || _project is null || !_editorReady) { _gameStatusTimer.Stop(); return; }
        e.Cancel = true;
        await RunActionAsync("Save before closing", async () => { await SaveChartFromEditorAsync(); _canClose = true; });
        if (_canClose) Close();
    }
    private void ShowError(string name, Exception ex)
    {
        StatusText.Text = name + " failed: " + ex.Message;
        MessageBox.Show(this, ex.Message, name, MessageBoxButton.OK, MessageBoxImage.Warning);
    }
    private void RefreshRecentProjects()
    {
        try { _gameSongs = _game.GetSongs(); _gameReadFailed = false; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException) { _gameSongs = []; _gameReadFailed = true; }
        _refreshingRecents = true;
        try
        {
            var items = _projects.RecentProjectFiles().Select(f =>
            {
                var project = ReadRecentProject(f);
                return new RecentProjectItem(project?.Name ?? _projects.ProjectName(f), f, project is null ? "Game status unavailable" : GameStatus(project));
            }).ToList();
            if (!items.SequenceEqual(RecentProjectsList.Items.OfType<RecentProjectItem>())) RecentProjectsList.ItemsSource = items;
            RecentProjectsList.SelectedItem = RecentProjectsList.Items.OfType<RecentProjectItem>().FirstOrDefault(i => string.Equals(i.FilePath, _project?.ProjectJsonPath, StringComparison.OrdinalIgnoreCase));
        }
        finally { _refreshingRecents = false; UpdateGameDetails(); }
    }
    private static string GameStatusColor(string status)
    {
        if (status.Contains("audio missing", StringComparison.OrdinalIgnoreCase)) return "#FF8E94";
        if (status.Contains("unavailable", StringComparison.OrdinalIgnoreCase)) return "#A6B1C3";
        if (status.Contains("Out of sync", StringComparison.OrdinalIgnoreCase)) return "#FFC36B";
        if (status.Contains("not in game", StringComparison.OrdinalIgnoreCase) && status.Contains("other levels", StringComparison.OrdinalIgnoreCase)) return "#8EB5FF";
        if (status.Contains("Up to date", StringComparison.OrdinalIgnoreCase)) return "#73E0A9";
        return "#A6B1C3";
    }

    private sealed record RecentProjectItem(string Name, string FilePath, string GameStatus)
    {
        public string GameStatusColor => MainWindow.GameStatusColor(GameStatus);
    }

    private void ManageGameSongsButton_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        // TODO: handle the Click event
    }
}
