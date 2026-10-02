using System.Text.Json;
using MySingingMonsterKaraokeStudio.Models;

namespace MySingingMonsterKaraokeStudio.Services;

public sealed class ProjectService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };
    private readonly string _settingsFile;
    private readonly StudioSettings _settings;
    public string ProjectsRoot { get; private set; }
    public string GameDataFolder => _settings.GameDataFolder ?? GameSongService.DefaultDataFolder;
    public string? LastProjectFile => new[] { _settings.LastProjectFile }.Concat(_settings.RecentFiles).FirstOrDefault(f => f is not null && File.Exists(f) && !IsHidden(f));

    public void RememberOpened(SongProject project, bool restoreRecent = true)
    {
        _settings.LastProjectFile = project.ProjectJsonPath;
        if (restoreRecent) _settings.HiddenRecentFiles.RemoveAll(f => f.Equals(Path.GetFullPath(project.ProjectJsonPath), StringComparison.OrdinalIgnoreCase));
        if (IsHidden(project.ProjectJsonPath)) { WriteSettings(); return; }
        RegisterRecent(project.ProjectJsonPath);
    }

    public string? FindProjectForChart(string chartFile)
    {
        var target = Path.GetFullPath(chartFile);
        foreach (var file in RecentProjectFiles(50))
        {
            try
            {
                var saved = JsonSerializer.Deserialize<SongProject>(File.ReadAllText(file), JsonOptions);
                if (saved is null) continue;
                var folder = saved.AssetsFolder is null ? Path.GetDirectoryName(file)! : Path.Combine(Path.GetDirectoryName(file)!, saved.AssetsFolder);
                if (string.Equals(saved.LastChartSavePath, target, StringComparison.OrdinalIgnoreCase) ||
                    Path.GetFullPath(Path.Combine(folder, saved.ChartFile)).Equals(target, StringComparison.OrdinalIgnoreCase)) return file;
            }
            catch (Exception ex) when (ex is IOException or JsonException or ArgumentException) { }
        }
        return null;
    }

    public ProjectService(string? projectsRoot = null, string? settingsFile = null)
    {
        _settingsFile = settingsFile ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MSM Song Studio", "studio-settings.json");
        try { _settings = File.Exists(_settingsFile) ? JsonSerializer.Deserialize<StudioSettings>(File.ReadAllText(_settingsFile), JsonOptions) ?? new() : new(); }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { _settings = new(); }
        ProjectsRoot = Path.GetFullPath(projectsRoot ?? _settings.WorkingFolder ??
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "MSM Song Studio", "Projects"));
        Directory.CreateDirectory(ProjectsRoot);
        _settings.WorkingFolder = ProjectsRoot;
        _settings.RecentFiles ??= [];
        _settings.HiddenRecentFiles ??= [];
    }

    public void SetWorkingFolder(string folder)
    {
        var path = Path.GetFullPath(folder);
        Directory.CreateDirectory(path);
        ProjectsRoot = path;
        _settings.WorkingFolder = path;
        WriteSettings();
    }

    public void SetGameDataFolder(string folder)
    {
        var path = Path.GetFullPath(folder);
        if (!Directory.Exists(Path.Combine(path, "Songs"))) throw new DirectoryNotFoundException("Choose the game's data folder containing Songs.");
        _settings.GameDataFolder = path; WriteSettings();
    }

    public SongProject Create(string name)
    {
        var safeName = SafeName(name);
        var folder = GetUniqueFolder(Path.Combine(ProjectsRoot, safeName));
        Directory.CreateDirectory(folder);
        Directory.CreateDirectory(Path.Combine(folder, "backups"));
        var project = new SongProject { Name = Path.GetFileName(folder), ProjectFolder = folder };
        Save(project);
        return project;
    }

    public SongProject CreateAtFile(string file)
    {
        file = ProjectFilename(file);
        var folder = GetUniqueFolder(Path.Combine(Path.GetDirectoryName(file)!, Path.GetFileNameWithoutExtension(file) + ".assets"));
        Directory.CreateDirectory(folder);
        var project = new SongProject
        {
            Name = Path.GetFileNameWithoutExtension(file), ProjectFilePath = file,
            ProjectFolder = folder, AssetsFolder = Path.GetFileName(folder)
        };
        Save(project);
        return project;
    }

    public SongProject CreateForAudio(string audioFile)
    {
        var name = SafeName(Path.GetFileNameWithoutExtension(audioFile));
        var file = Path.Combine(ProjectsRoot, name + ".msmproj");
        for (var i = 2; File.Exists(file); i++) file = Path.Combine(ProjectsRoot, name + " (" + i + ").msmproj");
        return CreateAtFile(file);
    }

    public SongProject Load(string folder) => LoadFile(Path.Combine(folder, "project.json"));

    public SongProject LoadFile(string file, bool rememberRecent = true)
    {
        file = Path.GetFullPath(file);
        if (!File.Exists(file)) throw new FileNotFoundException("The project file was not found.", file);
        var project = JsonSerializer.Deserialize<SongProject>(File.ReadAllText(file), JsonOptions) ?? throw new InvalidDataException("Invalid project file.");
        var parent = Path.GetDirectoryName(file)!;
        if (project.AssetsFolder is not null)
        {
            if (Path.GetFileName(project.AssetsFolder) != project.AssetsFolder || project.AssetsFolder is "." or "..")
                throw new InvalidDataException("The project has an invalid assets folder.");
            project.ProjectFolder = Path.Combine(parent, project.AssetsFolder);
            if (!Directory.Exists(project.ProjectFolder)) throw new DirectoryNotFoundException("The project's companion assets folder is missing. Keep it beside the .msmproj file.");
        }
        else project.ProjectFolder = parent;
        project.ProjectFilePath = file;
        if (project.Notes is { Count: 0 } && project.Levels is not null && project.Levels.TryGetValue(project.Difficulty, out var savedLevel) && savedLevel is { Count: > 0 })
            project.Notes = savedLevel;
        ChartValidation.Validate(project);
        if (rememberRecent) RegisterRecent(file);
        return project;
    }

    public SongProject SaveCopyAs(SongProject source, string file)
    {
        file = ProjectFilename(file);
        if (Path.GetFullPath(source.ProjectJsonPath).Equals(file, StringComparison.OrdinalIgnoreCase)) { Save(source); return source; }
        ChartValidation.Validate(source);
        var folder = GetUniqueFolder(Path.Combine(Path.GetDirectoryName(file)!, Path.GetFileNameWithoutExtension(file) + ".assets"));
        Directory.CreateDirectory(folder);
        var copy = JsonSerializer.Deserialize<SongProject>(JsonSerializer.Serialize(source, JsonOptions), JsonOptions)!;
        copy.Name = Path.GetFileNameWithoutExtension(file);
        copy.TrackRef = "msmstudio_" + Guid.NewGuid().ToString("N");
        copy.ProjectFilePath = file;
        copy.ProjectFolder = folder;
        copy.AssetsFolder = Path.GetFileName(folder);
        copy.LastChartSavePath = null;
        copy.LastOggSavePath = null;
        foreach (var name in new[] { source.SourceAudioFile, source.OggFile, source.MidiFile, source.ChartFile }.Where(n => n is not null).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var input = AssetPath(source.ProjectFolder, name!);
            if (!File.Exists(input))
            {
                if (name == source.ChartFile) continue;
                throw new FileNotFoundException("A project audio file is missing. Reimport it before saving a copy.", input);
            }
            var output = AssetPath(folder, name!);
            Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            File.Copy(input, output);
        }
        Save(copy);
        return copy;
    }

    private static string AssetPath(string folder, string name)
    {
        var root = Path.GetFullPath(folder) + Path.DirectorySeparatorChar;
        var path = Path.GetFullPath(Path.Combine(folder, name));
        if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("A project asset points outside the project folder.");
        return path;
    }

    private static string ProjectFilename(string file)
    {
        file = Path.GetFullPath(file);
        if (!Path.GetExtension(file).Equals(".msmproj", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Choose a .msmproj filename for the studio project.");
        return file;
    }

    public void Save(SongProject project)
    {
        ChartValidation.Validate(project);
        project.LastEditedUtc = DateTime.UtcNow;
        Directory.CreateDirectory(project.ProjectFolder);
        AtomicWrite(project.ProjectJsonPath, JsonSerializer.Serialize(project, JsonOptions));
        RegisterRecent(project.ProjectJsonPath);
    }

    public IReadOnlyList<string> RecentProjectFiles(int max = 20)
    {
        var discovered = Directory.GetFiles(ProjectsRoot, "*.msmproj")
            .Concat(Directory.GetDirectories(ProjectsRoot).Select(d => Path.Combine(d, "project.json")));
        return _settings.RecentFiles.Concat(discovered).Where(f => File.Exists(f) && !IsHidden(f))
            .Distinct(StringComparer.OrdinalIgnoreCase).OrderByDescending(File.GetLastWriteTimeUtc).Take(max).ToList();
    }

    public IReadOnlyList<string> RecentProjectFolders(int max = 20) =>
        RecentProjectFiles(max).Select(f => Path.GetDirectoryName(f)!).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    public void RemoveRecent(string file)
    {
        file = Path.GetFullPath(file);
        _settings.RecentFiles.RemoveAll(f => f.Equals(file, StringComparison.OrdinalIgnoreCase));
        if (!IsHidden(file)) _settings.HiddenRecentFiles.Add(file);
        WriteSettings();
    }

    private bool IsHidden(string file) => _settings.HiddenRecentFiles.Contains(Path.GetFullPath(file), StringComparer.OrdinalIgnoreCase);

    public string ProjectName(string file)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(file));
            foreach (var property in document.RootElement.EnumerateObject())
                if (property.Name.Equals("Name", StringComparison.OrdinalIgnoreCase) && property.Value.ValueKind == JsonValueKind.String &&
                    !string.IsNullOrWhiteSpace(property.Value.GetString())) return property.Value.GetString()!;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or InvalidOperationException) { }
        return (Path.GetFileName(file).Equals("project.json", StringComparison.OrdinalIgnoreCase) ?
            Path.GetFileName(Path.GetDirectoryName(file)) : Path.GetFileNameWithoutExtension(file)) ?? "Untitled Song";
    }

    private void RegisterRecent(string file)
    {
        file = Path.GetFullPath(file);
        if (IsHidden(file)) return;
        _settings.RecentFiles.RemoveAll(f => f.Equals(file, StringComparison.OrdinalIgnoreCase));
        _settings.RecentFiles.Insert(0, file);
        _settings.RecentFiles = _settings.RecentFiles.Take(50).ToList();
        WriteSettings();
    }

    private void WriteSettings()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_settingsFile)!);
        AtomicWrite(_settingsFile, JsonSerializer.Serialize(_settings, JsonOptions));
    }

    private static void AtomicWrite(string path, string text)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllText(temporary, text); File.Move(temporary, path, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public static string SafeName(string name)
    {
        var result = string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)).Trim().TrimEnd(' ', '.');
        if (string.IsNullOrWhiteSpace(result)) return "Untitled Song";
        var stem = result.Split('.')[0];
        if (new[] { "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9" }
            .Contains(stem, StringComparer.OrdinalIgnoreCase)) result = "_" + result;
        return result;
    }

    private static string GetUniqueFolder(string desired)
    {
        if (!Directory.Exists(desired)) return desired;
        for (var i = 2; i < 1000; i++) { var path = desired + " (" + i + ")"; if (!Directory.Exists(path)) return path; }
        return desired + "-" + Guid.NewGuid().ToString("N")[..8];
    }

    private sealed class StudioSettings
    {
        public string? WorkingFolder { get; set; }
        public string? GameDataFolder { get; set; }
        public string? LastProjectFile { get; set; }
        public List<string> RecentFiles { get; set; } = [];
        public List<string> HiddenRecentFiles { get; set; } = [];
    }
}
