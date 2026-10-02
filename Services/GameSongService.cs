using System.Text.Json;
using System.Text.Json.Nodes;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using MySingingMonsterKaraokeStudio.Models;

namespace MySingingMonsterKaraokeStudio.Services;

public sealed record InstalledGameSong(string Folder, string ChartFile, string AudioFile, string Name, string TrackRef, string Level, bool HasAudio, bool Readable)
{
    public string DisplayLabel => $"{Name} · {Level}" + (!Readable ? " · Missing/invalid chart" : !HasAudio ? " · Missing audio" : "") + $"  [{Path.GetFileName(Folder)}]";
}

public sealed class GameSongService
{
    public sealed record DeletedSong(string Folder, string OriginalFolder, string Name, DateTime ArchivedUtc, string Reason)
    {
        public string DisplayLabel => $"{Name} · {Reason} · {ArchivedUtc.ToLocalTime():g}";
    }
    public static string DefaultDataFolder => Path.Combine(Path.GetDirectoryName(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData))!,
        "LocalLow", "Big Blue Bubble Inc", "My Singing Monsters Karaoke");
    public string DataFolder { get; }
    public string SongsFolder => Path.Combine(DataFolder, "Songs");
    public string BackupsFolder => Path.Combine(DataFolder, ".MSMStudio", "backups");
    public bool IsReady => Directory.Exists(SongsFolder);
    private readonly Dictionary<string, (DateTime Written, long Length, InstalledGameSong Song)> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (DateTime Written, long Length, string Hash)> _hashes = new(StringComparer.OrdinalIgnoreCase);

    public static string HashFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private string CachedHash(string path)
    {
        var info = new FileInfo(path);
        if (_hashes.TryGetValue(path, out var cache) && cache.Written == info.LastWriteTimeUtc && cache.Length == info.Length) return cache.Hash;
        var hash = HashFile(path); _hashes[path] = (info.LastWriteTimeUtc, info.Length, hash); return hash;
    }

    public string SyncStatus(SongProject project, InstalledGameSong song)
    {
        try
        {
            if (!song.HasAudio || !File.Exists(song.AudioFile)) return "audio missing";
            var expected = JsonNode.Parse(new TmbService().SerializeChart(project));
            var actual = JsonNode.Parse(File.ReadAllText(song.ChartFile));
            if (!JsonNode.DeepEquals(expected, actual)) return "chart or info changed";
            var ogg = project.OggFile is null ? null : Path.Combine(project.ProjectFolder, project.OggFile);
            if (ogg is null || !File.Exists(ogg) || CachedHash(ogg) != CachedHash(song.AudioFile)) return "audio changed";
            var source = project.SourceAudioFile is null ? null : Path.Combine(project.ProjectFolder, project.SourceAudioFile);
            var manifest = Path.Combine(song.Folder, ".msmstudio-import.json");
            if (source is not null && File.Exists(source) && File.Exists(manifest))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(manifest));
                if (doc.RootElement.GetProperty("sourceHash").GetString() != CachedHash(source)) return "source audio changed";
            }
            return "up to date";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException or InvalidOperationException or KeyNotFoundException)
        { return "comparison unavailable"; }
    }

    public GameSongService(string? dataFolder = null) => DataFolder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(dataFolder ?? DefaultDataFolder));

    public IReadOnlyList<InstalledGameSong> GetSongs()
    {
        if (!IsReady) return [];
        CheckDirectory(SongsFolder);
        var songs = new List<InstalledGameSong>();
        foreach (var folder in Directory.GetDirectories(SongsFolder))
        {
            if ((File.GetAttributes(folder) & FileAttributes.ReparsePoint) != 0) continue;
            var charts = Directory.GetFiles(folder, "*.tmb");
            if (charts.Length == 0)
            {
                var stem = Path.GetFileName(folder);
                songs.Add(new(folder, Path.Combine(folder, stem + ".tmb"), Path.Combine(folder, stem + ".ogg"), stem, "", "Unknown", false, false));
            }
            foreach (var chart in charts)
            {
                var info = new FileInfo(chart);
                InstalledGameSong song;
                if (_cache.TryGetValue(chart, out var cached) && cached.Written == info.LastWriteTimeUtc && cached.Length == info.Length) song = cached.Song;
                else
                {
                    var stem = Path.GetFileNameWithoutExtension(chart);
                    song = new(folder, chart, Path.Combine(folder, stem + ".ogg"), stem, "", "Unknown", false, false);
                    try
                    {
                        using var stream = File.OpenRead(chart);
                        using var json = JsonDocument.Parse(stream);
                        var root = json.RootElement;
                        var name = root.GetProperty("name").GetString();
                        var track = root.TryGetProperty("trackRef", out var id) ? id.ToString() : "";
                        var level = root.GetProperty("difficulty").GetInt32() switch { <= 3 => "Easy", <= 6 => "Normal", <= 8 => "Hard", _ => "Expert" };
                        level = new[] { "Easy", "Normal", "Hard", "Expert" }.FirstOrDefault(l => track.EndsWith("_" + l.ToLowerInvariant(), StringComparison.Ordinal)) ?? level;
                        if (root.GetProperty("notes").ValueKind != JsonValueKind.Array) throw new InvalidDataException("Missing notes.");
                        song = song with { Name = string.IsNullOrWhiteSpace(name) ? stem : name.Trim(), TrackRef = track, Level = level, Readable = true };
                    }
                    catch (Exception ex) when (ex is IOException or JsonException or InvalidOperationException or KeyNotFoundException or FormatException or InvalidDataException) { }
                    _cache[chart] = (info.LastWriteTimeUtc, info.Length, song);
                }
                songs.Add(song with { HasAudio = File.Exists(song.AudioFile) });
            }
        }
        var existing = songs.Select(s => s.ChartFile).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var stale in _cache.Keys.Where(k => !existing.Contains(k)).ToList()) _cache.Remove(stale);
        return songs.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ThenBy(s => s.Level).ToList();
    }

    private static bool SameName(string first, string second) => first.Trim().Equals(second.Trim(), StringComparison.OrdinalIgnoreCase);
    public static IReadOnlyList<InstalledGameSong> ForProject(SongProject project, IReadOnlyList<InstalledGameSong> songs) => songs.Where(s =>
        s.Readable && (new[] { "Easy", "Normal", "Hard", "Expert" }.Any(level => s.TrackRef.Equals(project.TrackRef + "_" + level.ToLowerInvariant(), StringComparison.Ordinal)) ||
        !s.TrackRef.StartsWith("msmstudio_", StringComparison.Ordinal) && SameName(s.Name, project.Name))).ToList();

    public static InstalledGameSong? FindLevel(SongProject project, IReadOnlyList<InstalledGameSong> songs) =>
        ForProject(project, songs).FirstOrDefault(s => s.TrackRef == project.TrackRef + "_" + project.Difficulty.ToLowerInvariant()) ??
        ForProject(project, songs).FirstOrDefault(s => s.Level == project.Difficulty);

    public string ImportSong(SongProject project)
    {
        RequireReady();
        var chart = new TmbService().ReadTmb(project.ChartPath);
        if (chart.TrackRef != project.TrackRef || chart.Difficulty != project.Difficulty) throw new InvalidDataException("Build this song's selected level before importing it.");
        var source = Path.GetFullPath(Path.Combine(project.ProjectFolder, project.OggFile ?? throw new InvalidOperationException("Convert the song to OGG first.")));
        using (var audio = File.OpenRead(source))
        {
            Span<byte> header = stackalloc byte[4];
            if (audio.Read(header) != 4 || !header.SequenceEqual("OggS"u8)) throw new InvalidDataException("The converted audio is not a valid OGG file.");
        }
        var songs = GetSongs();
        var installed = FindLevel(project, songs);
        if (installed is not null && songs.Count(s => s.Folder.Equals(installed.Folder, StringComparison.OrdinalIgnoreCase)) > 1)
            throw new InvalidDataException("That game folder contains several charts. Manage it from Game Songs Folder before updating.");
        var basename = ProjectService.SafeName(project.Name);
        var desired = Child(SongsFolder, basename);
        if (Directory.Exists(desired) && (installed is null || !desired.Equals(installed.Folder, StringComparison.OrdinalIgnoreCase))) basename += " (" + project.Difficulty + ")";
        var stem = basename;
        for (var i = 2; Directory.Exists(Child(SongsFolder, basename)) && (installed is null || !Child(SongsFolder, basename).Equals(installed.Folder, StringComparison.OrdinalIgnoreCase)); i++) basename = stem + " (" + i + ")";
        var destination = Child(SongsFolder, basename);
        var stagingRoot = Workspace("staging");
        var staging = Child(stagingRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        string? backup = null;
        try
        {
            File.Copy(source, Path.Combine(staging, basename + ".ogg"));
            File.Copy(project.ChartPath, Path.Combine(staging, basename + ".tmb"));
            var original = Path.Combine(project.ProjectFolder, project.SourceAudioFile ?? project.OggFile!);
            File.WriteAllText(Path.Combine(staging, ".msmstudio-import.json"), JsonSerializer.Serialize(new { sourceHash = HashFile(original) }));
            if (installed is not null) backup = RemoveSong(installed, "Previous version");
            try { Directory.Move(staging, destination); }
            catch
            {
                if (backup is not null && !Directory.Exists(installed!.Folder)) Directory.Move(backup, installed.Folder);
                throw;
            }
            _cache.Clear(); return destination;
        }
        finally
        {
            if (Directory.Exists(staging)) { CheckDirectory(staging); Directory.Delete(staging, true); }
        }
    }

    // Removal archives the whole custom-song folder outside Songs, retaining a recoverable copy.
    public string RemoveSong(InstalledGameSong song, string reason = "Deleted song")
    {
        RequireReady();
        var folder = Child(SongsFolder, Path.GetFileName(song.Folder));
        if (!folder.Equals(Path.GetFullPath(song.Folder), StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Choose a song inside the game's Songs folder.");
        CheckDirectory(folder);
        var current = GetSongs().FirstOrDefault(s => s.ChartFile.Equals(song.ChartFile, StringComparison.OrdinalIgnoreCase) && s.TrackRef == song.TrackRef && s.Name == song.Name);
        if (current is null) throw new InvalidOperationException("The game song changed or was removed. Refresh the song list and try again.");
        if (GetSongs().Count(s => s.Folder.Equals(folder, StringComparison.OrdinalIgnoreCase)) > 1)
            throw new InvalidDataException("That game folder contains several charts. Open Game Songs Folder to manage it.");
        var backup = Child(Workspace("backups"), Path.GetFileName(folder) + "-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmssfff") + "-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.Move(folder, backup);
        try { File.WriteAllText(Path.Combine(backup, ".msmstudio-backup.json"), JsonSerializer.Serialize(new DeletedSong(backup, Path.GetFileName(folder), song.Name, DateTime.UtcNow, reason))); }
        catch { Directory.Move(backup, folder); throw; }
        _cache.Clear(); return backup;
    }

    public SongProject OpenAsProject(InstalledGameSong song, ProjectService projects)
    {
        RequireReady();
        var current = GetSongs().FirstOrDefault(s => s.ChartFile.Equals(song.ChartFile, StringComparison.OrdinalIgnoreCase) && s.TrackRef == song.TrackRef);
        if (current is null || !current.Readable || !current.HasAudio) throw new InvalidDataException("Refresh and choose a song with a readable chart and OGG audio.");
        CheckTree(current.Folder);
        var chart = new TmbService().ReadTmb(current.ChartFile);
        using (var audio = File.OpenRead(current.AudioFile))
        {
            Span<byte> magic = stackalloc byte[4];
            if (audio.Read(magic) != 4 || !magic.SequenceEqual("OggS"u8)) throw new InvalidDataException("The game audio is not a valid OGG file.");
        }
        var project = projects.Create(chart.Name);
        chart.ProjectFolder = project.ProjectFolder; chart.ProjectFilePath = project.ProjectJsonPath; chart.AssetsFolder = null;
        File.Copy(current.AudioFile, Path.Combine(chart.ProjectFolder, "song.ogg"));
        chart.SourceAudioFile = chart.OggFile = "song.ogg"; chart.AudioName = Path.GetFileName(current.AudioFile);
        projects.Save(chart); new TmbService().WriteTmb(chart);
        return chart;
    }

    public IReadOnlyList<DeletedSong> GetDeletedSongs()
    {
        if (!Directory.Exists(BackupsFolder)) return [];
        CheckDirectory(BackupsFolder);
        var result = new List<DeletedSong>();
        foreach (var folder in Directory.GetDirectories(BackupsFolder))
        {
            if ((File.GetAttributes(folder) & FileAttributes.ReparsePoint) != 0) continue;
            var original = Regex.Replace(Path.GetFileName(folder), @"-\d{8}-\d{9}-[a-fA-F0-9]{8}$", "");
            var entry = new DeletedSong(folder, original, original, Directory.GetLastWriteTimeUtc(folder), "Older backup");
            try
            {
                var manifest = Path.Combine(folder, ".msmstudio-backup.json");
                if (File.Exists(manifest) && (File.GetAttributes(manifest) & FileAttributes.ReparsePoint) == 0)
                {
                    var saved = JsonSerializer.Deserialize<DeletedSong>(File.ReadAllText(manifest));
                    if (saved is not null) { Child(SongsFolder, saved.OriginalFolder); entry = saved with { Folder = folder }; }
                }
            }
            catch (Exception ex) when (ex is IOException or JsonException or InvalidDataException or ArgumentException) { }
            result.Add(entry);
        }
        return result.OrderByDescending(s => s.ArchivedUtc).ToList();
    }

    private DeletedSong ValidateDeleted(DeletedSong song)
    {
        var folder = Child(BackupsFolder, Path.GetFileName(song.Folder));
        if (!folder.Equals(Path.GetFullPath(song.Folder), StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Choose a song inside the studio's game backup folder.");
        CheckTree(folder);
        return GetDeletedSongs().FirstOrDefault(s => s.Folder.Equals(folder, StringComparison.OrdinalIgnoreCase)) ?? throw new InvalidOperationException("This backup no longer exists. Refresh the list.");
    }

    public string RestoreSong(DeletedSong song)
    {
        RequireReady(); var current = ValidateDeleted(song);
        var destination = Child(SongsFolder, current.OriginalFolder); CheckDirectory(destination);
        if (Directory.Exists(destination) || File.Exists(destination)) throw new IOException("A song already uses this game folder. Remove the installed version first; it will also be kept in Deleted Songs.");
        Directory.Move(current.Folder, destination); _cache.Clear(); return destination;
    }

    // Called only after the manager's explicit, default-No permanent-deletion confirmation.
    public void DeleteBackupPermanently(DeletedSong song)
    {
        var current = ValidateDeleted(song);
        Directory.Delete(current.Folder, true);
    }

    private void CheckTree(string folder)
    {
        CheckDirectory(folder);
        var pending = new Stack<string>(); pending.Push(folder);
        while (pending.TryPop(out var directory))
            foreach (var path in Directory.GetFileSystemEntries(directory))
            {
                var attributes = File.GetAttributes(path);
                if ((attributes & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Linked backup or game-song files cannot be managed here.");
                if ((attributes & FileAttributes.Directory) != 0) pending.Push(path);
            }
    }

    private void RequireReady()
    {
        if (!IsReady) throw new DirectoryNotFoundException("Choose the My Singing Monsters Karaoke data folder containing Songs from the Game menu.");
        CheckDirectory(SongsFolder);
    }
    private string Workspace(string name)
    {
        var root = Child(DataFolder, ".MSMStudio"); CheckDirectory(root); Directory.CreateDirectory(root);
        var folder = Child(root, name); CheckDirectory(folder); Directory.CreateDirectory(folder); return folder;
    }
    private static string Child(string parent, string name)
    {
        if (string.IsNullOrWhiteSpace(name) || Path.GetFileName(name) != name || name is "." or "..") throw new InvalidDataException("Invalid game song folder.");
        var child = Path.GetFullPath(Path.Combine(parent, name));
        if (!Path.GetDirectoryName(child)!.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(parent)), StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Invalid game song path.");
        return child;
    }
    private void CheckDirectory(string folder)
    {
        var path = Path.GetFullPath(folder);
        if (!path.StartsWith(DataFolder + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) && !path.Equals(DataFolder, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The game song path is outside the selected data folder.");
        for (var current = path; current is not null; current = Path.GetDirectoryName(current))
        {
            if (Directory.Exists(current) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Linked game song folders cannot be modified. Choose the actual game data folder.");
            if (current.Equals(DataFolder, StringComparison.OrdinalIgnoreCase)) break;
        }
    }
}
