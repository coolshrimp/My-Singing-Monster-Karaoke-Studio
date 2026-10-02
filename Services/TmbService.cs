using System.Text.Json;
using System.Globalization;
using MySingingMonsterKaraokeStudio.Models;

namespace MySingingMonsterKaraokeStudio.Services;

public sealed class TmbService
{
    // TMB uses beats and pitch units measured from middle C: 13.75 units per semitone.
    // Format reference: https://github.com/towai/TromboneCharter/blob/master/tmb_info.gd
    private const double PitchUnit = 13.75;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };
    private static readonly string[] ControlledFields = ["name", "shortName", "tempo", "timesig", "trackRef", "endpoint", "difficulty", "notes", "author", "genre", "description", "year", "UNK1"];

    public string SerializeChart(SongProject project)
    {
        ChartValidation.Validate(project);
        var beatsPerSecond = project.Bpm / 60;
        var notes = AudioDraftService.CropNotes(project.Notes, project.OffsetSeconds, 0, double.MaxValue);
        if (notes.Any(n => NotePath.Points(n).Any(p => p.Pitch is < 47 or > 73)))
            throw new InvalidDataException("TMB notes must be between MIDI pitch 47 and 73. Move out-of-range notes into the editor's range before exporting.");
        var rows = notes.SelectMany(NotePath.Segments).OrderBy(n => n.Time).Select(n => new[]
        {
            n.Time * beatsPerSecond, n.Length * beatsPerSecond,
            (n.Pitch - 60) * PitchUnit, (n.EndPitch - n.Pitch) * PitchUnit, (n.EndPitch - 60) * PitchUnit
        }).ToArray();
        var lastBeat = rows.Length == 0 ? 0 : rows.Max(n => n[0] + n[1]);
        var endpoint = Math.Ceiling(Math.Max(lastBeat, project.AudioDurationSeconds * beatsPerSecond) + 4);
        if (!double.IsFinite(endpoint) || endpoint > int.MaxValue) throw new InvalidDataException("The song is too long for TMB export.");
        var trackRef = string.Concat(project.TrackRef.Where(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-'));
        if (trackRef.Length == 0) throw new InvalidDataException("The song is missing its chart identifier. Save it as a new project.");
        var payload = new Dictionary<string, object?>();
        foreach (var pair in project.TmbMetadata)
            if (!ControlledFields.Contains(pair.Key)) payload[pair.Key] = pair.Value;
        payload["name"] = project.Name;
        payload["shortName"] = string.IsNullOrWhiteSpace(project.ShortName) ? project.Name[..Math.Min(project.Name.Length, 20)] : project.ShortName;
        payload["trackRef"] = trackRef + "_" + project.Difficulty.ToLowerInvariant();
        payload["author"] = project.Author;
        payload["genre"] = project.Genre;
        payload["description"] = project.Description;
        payload["year"] = project.Year;
        payload["difficulty"] = Rating(project);
        payload["savednotespacing"] = payload.GetValueOrDefault("savednotespacing") ?? 180;
        payload["tempo"] = project.Bpm;
        payload["timesig"] = project.TimeSignature;
        payload["endpoint"] = (int)endpoint;
        payload["notes"] = rows;
        payload["lyrics"] = payload.GetValueOrDefault("lyrics") ?? Array.Empty<object>();
        payload["bgdata"] = payload.GetValueOrDefault("bgdata") ?? Array.Empty<object>();
        payload["improv_zones"] = payload.GetValueOrDefault("improv_zones") ?? Array.Empty<object>();
        payload["UNK1"] = 0;
        return JsonSerializer.Serialize(payload, JsonOptions);
    }

    public static int Rating(SongProject project) => project.LevelRatings.TryGetValue(project.Difficulty, out var rating) ? rating :
        project.Difficulty switch { "Easy" => 2, "Hard" => 8, "Expert" => 10, _ => 5 };

    public void WriteTmb(SongProject project, string? destination = null)
    {
        var content = SerializeChart(project);

        var target = Path.GetFullPath(destination ?? project.ChartPath);
        if (!Path.GetExtension(target).Equals(".tmb", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Choose a .tmb filename for the chart.");
        var temporary = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, content);
            File.Move(temporary, target, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public SongProject ReadTmb(string path)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object) throw new InvalidDataException("The TMB chart must contain a JSON object.");
        if (root.TryGetProperty("schema", out var schema))
        {
            if (schema.GetString() != "msm-song-studio-draft-1") throw new InvalidDataException("This chart format is not supported.");
            return ReadLegacy(root, path);
        }
        if (!root.TryGetProperty("name", out var name) || name.ValueKind != JsonValueKind.String ||
            !root.TryGetProperty("tempo", out _) ||
            !root.TryGetProperty("notes", out var rows) || rows.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("Open a TMB chart with a constant tempo and five-value note rows.");
        var project = new SongProject
        {
            Name = string.IsNullOrWhiteSpace(name.GetString()) ? Path.GetFileNameWithoutExtension(path) : name.GetString()!,
            ShortName = Text(root, "shortName", ""),
            Bpm = Number(root, "tempo"), TimeSignature = Integer(root, "timesig"),
            Author = Text(root, "author", "Unknown"), Genre = Text(root, "genre", "Other"),
            Description = Text(root, "description", ""), Year = root.TryGetProperty("year", out _) ? Integer(root, "year") : DateTime.Now.Year
        };
        if (root.TryGetProperty("difficulty", out _))
        {
            var rating = Integer(root, "difficulty");
            project.Difficulty = rating <= 3 ? "Easy" : rating <= 6 ? "Normal" : rating <= 8 ? "Hard" : "Expert";
            project.LevelRatings[project.Difficulty] = rating;
        }
        if (root.TryGetProperty("trackRef", out var trackRef) && !string.IsNullOrWhiteSpace(trackRef.GetString()))
        {
            project.TrackRef = trackRef.GetString()!;
            var taggedLevel = new[] { "Easy", "Normal", "Hard", "Expert" }.FirstOrDefault(level => project.TrackRef.EndsWith("_" + level.ToLowerInvariant(), StringComparison.Ordinal));
            if (taggedLevel is not null)
            {
                var value = Rating(project); project.LevelRatings.Clear();
                project.Difficulty = taggedLevel; project.LevelRatings[taggedLevel] = value;
            }
            var suffix = "_" + project.Difficulty.ToLowerInvariant();
            if (project.TrackRef.EndsWith(suffix, StringComparison.Ordinal)) project.TrackRef = project.TrackRef[..^suffix.Length];
        }
        ChartValidation.Validate(project);
        var secondsPerBeat = 60 / project.Bpm;
        foreach (var row in rows.EnumerateArray())
        {
            if (row.ValueKind != JsonValueKind.Array || row.GetArrayLength() != 5 || row.EnumerateArray().Any(v => v.ValueKind != JsonValueKind.Number || !double.IsFinite(v.GetDouble())))
                throw new InvalidDataException("A TMB note must contain five finite numbers.");
            var values = row.EnumerateArray().Select(v => v.GetDouble()).ToArray();
            if (Math.Abs(values[2] + values[3] - values[4]) > .01) throw new InvalidDataException("A TMB slide has inconsistent pitch values.");
            project.Notes.Add(new ChartNote
            {
                Time = values[0] * secondsPerBeat, Length = values[1] * secondsPerBeat,
                Pitch = FromTmbPitch(values[2]), EndPitch = FromTmbPitch(values[4])
            });
        }
        project.Notes = project.Notes.OrderBy(n => n.Time).ToList();
        project.Levels[project.Difficulty] = project.Notes;
        foreach (var pair in root.EnumerateObject())
            if (!ControlledFields.Contains(pair.Name)) project.TmbMetadata[pair.Name] = pair.Value.Clone();
        ChartValidation.Validate(project);
        return project;
    }

    private static double FromTmbPitch(double pitch)
    {
        var midi = pitch / PitchUnit + 60;
        if (midi is < 0 or > 127) throw new InvalidDataException("A TMB note has an invalid pitch.");
        return midi;
    }

    private static string Text(JsonElement root, string key, string fallback) =>
        root.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? fallback : fallback;

    private static double Number(JsonElement root, string field)
    {
        if (root.TryGetProperty(field, out var value))
        {
            double number;
            if ((value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out number) ||
                 value.ValueKind == JsonValueKind.String && double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out number)) && double.IsFinite(number))
                return number;
        }
        throw new InvalidDataException($"The TMB '{field}' field must contain a finite number or numeric text.");
    }

    private static int Integer(JsonElement root, string field)
    {
        var number = Number(root, field);
        if (number == Math.Truncate(number) && number >= int.MinValue && number <= int.MaxValue) return (int)number;
        throw new InvalidDataException($"The TMB '{field}' field must contain a whole number.");
    }

    private static SongProject ReadLegacy(JsonElement root, string path)
    {
        var project = new SongProject
        {
            Name = Text(root, "title", Path.GetFileNameWithoutExtension(path)), Bpm = root.GetProperty("tempo").GetDouble(),
            OffsetSeconds = root.GetProperty("offset").GetDouble(), TimeSignature = root.GetProperty("timesig").GetInt32(),
            Notes = root.GetProperty("notes").Deserialize<List<ChartNote>>(JsonOptions) ?? []
        };
        if (root.TryGetProperty("difficulty", out var difficulty)) project.Difficulty = difficulty.GetString() ?? "Normal";
        if (root.TryGetProperty("levels", out var levels)) project.Levels = levels.Deserialize<Dictionary<string, List<ChartNote>>>(JsonOptions) ?? [];
        ChartValidation.Validate(project);
        return project;
    }
}
