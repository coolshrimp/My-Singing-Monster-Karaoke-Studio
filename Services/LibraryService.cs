using System.IO.Compression;
using System.Text.Json;
using MySingingMonsterKaraokeStudio.Models;

namespace MySingingMonsterKaraokeStudio.Services;

public sealed record LibraryDownload(string File, string Source, DateTime DownloadedUtc, List<string> Projects)
{
    public string Label => Path.GetFileName(File) + " · " + (Projects.Count > 0 ? Projects.Count + " editable song(s)" : "Downloaded");
}

public sealed class LibraryService
{
    private readonly ProjectService _projects;
    public string Folder { get; }
    private string HistoryFile => Path.Combine(Folder, "history.json");
    public LibraryService(ProjectService projects, string? folder = null)
    {
        _projects = projects;
        Folder = folder ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MSM Song Studio", "Downloads");
        Directory.CreateDirectory(Folder);
    }
    public static bool Supported(string file) => Path.GetExtension(file).ToLowerInvariant() is ".zip" or ".tmb" or ".msmproj";
    public string NewDownloadPath(string file)
    {
        var name = ProjectService.SafeName(Path.GetFileNameWithoutExtension(file)) + Path.GetExtension(file).ToLowerInvariant();
        if (!Supported(name)) throw new InvalidDataException("Download a ZIP song package, TMB chart or studio project.");
        var folder = Path.Combine(Folder, Guid.NewGuid().ToString("N")); Directory.CreateDirectory(folder);
        return Path.Combine(folder, name);
    }
    public IReadOnlyList<LibraryDownload> History()
    {
        try { return File.Exists(HistoryFile) ? JsonSerializer.Deserialize<List<LibraryDownload>>(File.ReadAllText(HistoryFile)) ?? [] : []; }
        catch (Exception ex) when (ex is IOException or JsonException) { return []; }
    }
    public void Record(string file, string source, IEnumerable<SongProject>? projects = null)
    {
        var history = History().Where(x => !x.File.Equals(file, StringComparison.OrdinalIgnoreCase)).ToList();
        history.Insert(0, new(file, source, DateTime.UtcNow, projects?.Select(p => p.ProjectJsonPath).ToList() ?? []));
        var temporary = HistoryFile + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(history.Take(200), new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temporary, HistoryFile, true);
    }

    public List<SongProject> Import(string file)
    {
        if (!Supported(file)) throw new InvalidDataException("Choose a ZIP song package, TMB chart or studio project.");
        if (Path.GetExtension(file).Equals(".msmproj", StringComparison.OrdinalIgnoreCase)) return [_projects.LoadFile(file)];
        if (Path.GetExtension(file).Equals(".tmb", StringComparison.OrdinalIgnoreCase)) return [ImportChart(file)];
        var root = Path.Combine(Folder, "packages", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var archive = ZipFile.OpenRead(file);
            if (archive.Entries.Count > 2048 || archive.Entries.Sum(e => e.Length) > 512L * 1024 * 1024) throw new InvalidDataException("This package is too large (maximum 512 MB / 2048 files).");
            var targets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in archive.Entries)
            {
                var name = entry.FullName.Replace('\\', '/');
                if (name.StartsWith('/') || name.Split('/').Any(p => p is ".." or "." || p.Contains(':')) || (entry.ExternalAttributes >> 16 & 0xF000) == 0xA000)
                    throw new InvalidDataException("The ZIP contains an unsafe path or linked file.");
                var path = Path.GetFullPath(Path.Combine(root, name));
                if (!path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("The ZIP contains an unsafe path.");
                if (name.EndsWith('/')) continue;
                if (!targets.Add(path)) throw new InvalidDataException("The ZIP contains duplicate filenames.");
                if (Path.GetExtension(path).ToLowerInvariant() is not (".tmb" or ".ogg" or ".msmproj" or ".json" or ".mid" or ".mp3" or ".wav" or ".flac")) continue;
                Directory.CreateDirectory(Path.GetDirectoryName(path)!); entry.ExtractToFile(path);
            }
            var projectFiles = Directory.GetFiles(root, "*.msmproj", SearchOption.AllDirectories);
            if (projectFiles.Length > 0)
            {
                // ProjectService validates companion paths; validate audio/chart asset names before opening untrusted packages.
                var result = projectFiles.Select(p => _projects.LoadFile(p, false)).ToList();
                foreach (var project in result) ValidateAssets(project, root);
                foreach (var project in result) _projects.Save(project);
                return result;
            }
            var charts = Directory.GetFiles(root, "*.tmb", SearchOption.AllDirectories);
            if (charts.Length == 0) throw new InvalidDataException("The ZIP has no editable TMB chart or studio project.");
            // Validate every chart before creating any project, so unsupported packages cannot partly import.
            foreach (var chart in charts) new TmbService().ReadTmb(chart);
            return charts.Select(ImportChart).ToList();
        }
        catch
        {
            // Only the fresh, resolved extraction directory created above is removed.
            if (Directory.Exists(root)) Directory.Delete(root, true);
            throw;
        }
    }

    private static void ValidateAssets(SongProject project, string root)
    {
        foreach (var asset in new[] { project.SourceAudioFile, project.OggFile, project.MidiFile, project.ChartFile }.Where(a => a is not null))
        {
            var path = Path.GetFullPath(Path.Combine(project.ProjectFolder, asset!));
            if (!path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("The project references files outside its package.");
        }
    }
    private SongProject ImportChart(string chartFile)
    {
        var chart = new TmbService().ReadTmb(chartFile);
        var project = _projects.Create(chart.Name);
        var name = project.Name; var folder = project.ProjectFolder;
        chart.ProjectFolder = folder; chart.ProjectFilePath = project.ProjectJsonPath; chart.AssetsFolder = null;
        // Preserve title and original identity while storing an independent editable local copy.
        chart.Name = new TmbService().ReadTmb(chartFile).Name;
        var paired = Path.ChangeExtension(chartFile, ".ogg");
        if (!File.Exists(paired))
        {
            var audio = Directory.GetFiles(Path.GetDirectoryName(chartFile)!, "*.ogg");
            if (audio.Length == 1) paired = audio[0];
        }
        if (File.Exists(paired))
        {
            using var stream = File.OpenRead(paired); Span<byte> magic = stackalloc byte[4];
            if (stream.Read(magic) != 4 || !magic.SequenceEqual("OggS"u8)) throw new InvalidDataException("The package audio is not a valid OGG file.");
            File.Copy(paired, Path.Combine(folder, "song.ogg"));
            chart.SourceAudioFile = chart.OggFile = "song.ogg"; chart.AudioName = Path.GetFileName(paired);
        }
        _projects.Save(chart); new TmbService().WriteTmb(chart); return chart;
    }
}
