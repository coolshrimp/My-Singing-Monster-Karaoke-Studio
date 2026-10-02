using System.IO.Compression;
using MySingingMonsterKaraokeStudio.Models;

namespace MySingingMonsterKaraokeStudio.Services;

public sealed class ExportService
{
    public string ExportZip(SongProject project, string? destination = null)
    {
        if (string.IsNullOrWhiteSpace(project.OggFile) || !File.Exists(Path.Combine(project.ProjectFolder, project.OggFile)))
            throw new InvalidOperationException("song.ogg has not been generated yet.");
        if (!File.Exists(project.ChartPath))
            throw new InvalidOperationException("song.tmb has not been generated yet.");

        var exports = Path.Combine(project.ProjectFolder, "exports");
        var songName = ProjectService.SafeName(project.Name);
        if (destination is null) Directory.CreateDirectory(exports);
        var zipPath = Path.GetFullPath(destination ?? Path.Combine(exports, $"{songName}-{DateTime.Now:yyyyMMdd-HHmmssfff}-{Guid.NewGuid().ToString("N")[..4]}.zip"));
        if (!Path.GetExtension(zipPath).Equals(".zip", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Choose a .zip filename for the song package.");
        var temporary = zipPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var archive = ZipFile.Open(temporary, ZipArchiveMode.Create))
            {
                archive.CreateEntryFromFile(Path.Combine(project.ProjectFolder, project.OggFile), songName + ".ogg", CompressionLevel.Optimal);
                archive.CreateEntryFromFile(project.ChartPath, songName + ".tmb", CompressionLevel.Optimal);
            }
            File.Move(temporary, zipPath, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
        return zipPath;
    }

    public string SaveOgg(SongProject project, string destination)
    {
        var target = Path.GetFullPath(destination);
        if (!Path.GetExtension(target).Equals(".ogg", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Choose a .ogg filename for the audio.");
        if (string.IsNullOrWhiteSpace(project.OggFile)) throw new InvalidOperationException("Convert the audio to OGG first.");
        var source = Path.GetFullPath(Path.Combine(project.ProjectFolder, project.OggFile));
        if (!File.Exists(source)) throw new FileNotFoundException("The converted OGG audio was not found.", source);
        if (source.Equals(target, StringComparison.OrdinalIgnoreCase)) return target;
        var temporary = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.Copy(source, temporary); File.Move(temporary, target, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
        return target;
    }
}
