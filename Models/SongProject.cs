using System.Text.Json.Serialization;

namespace MySingingMonsterKaraokeStudio.Models;

public sealed class SongProject
{
    public string Name { get; set; } = "Untitled Song";
    public string ShortName { get; set; } = "";
    public Dictionary<string, int> LevelRatings { get; set; } = [];
    public string? ConvertedSourceHash { get; set; }
    public string TrackRef { get; set; } = "msmstudio_" + Guid.NewGuid().ToString("N");
    public string Author { get; set; } = "Unknown";
    public string Genre { get; set; } = "Other";
    public string Description { get; set; } = "Created with MSM Song Studio";
    public int Year { get; set; } = DateTime.Now.Year;
    public double AudioDurationSeconds { get; set; }
    public double? DetectedBpm { get; set; }
    public List<double> AudioOnsets { get; set; } = [];
    public string ProjectFolder { get; set; } = string.Empty;
    public string? AssetsFolder { get; set; }
    public string? LastChartSavePath { get; set; }
    public string? LastOggSavePath { get; set; }
    public string? SourceAudioFile { get; set; }
    public string? AudioName { get; set; }
    public string? OggFile { get; set; }
    public string? MidiFile { get; set; }
    public string Difficulty { get; set; } = "Normal";
    public Dictionary<string, List<ChartNote>> Levels { get; set; } = [];
    public string ChartFile { get; set; } = "song.tmb";
    public double Bpm { get; set; } = 120;
    public double OffsetSeconds { get; set; }
    public bool ShowGrid { get; set; } = true;
    public int TimeSignature { get; set; } = 4;
    public DateTime LastEditedUtc { get; set; } = DateTime.UtcNow;
    public List<ChartNote> Notes { get; set; } = [];
    public Dictionary<string, System.Text.Json.JsonElement> TmbMetadata { get; set; } = [];

    [JsonIgnore]
    public string ProjectFilePath { get; set; } = string.Empty;

    [JsonIgnore]
    public string ProjectJsonPath => string.IsNullOrEmpty(ProjectFilePath) ? Path.Combine(ProjectFolder, "project.json") : ProjectFilePath;

    [JsonIgnore]
    public string ChartPath => Path.Combine(ProjectFolder, ChartFile);
}

public sealed class ChartNote
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public double Time { get; set; }
    public double Length { get; set; } = 0.25;
    public double Pitch { get; set; } = 60;
    public double EndPitch { get; set; } = 60;
    public List<CurvePoint> CurvePoints { get; set; } = [];
}

public sealed class CurvePoint
{
    // Relative position along the note, independent of its current duration.
    public double Position { get; set; }
    public double Pitch { get; set; }
}
