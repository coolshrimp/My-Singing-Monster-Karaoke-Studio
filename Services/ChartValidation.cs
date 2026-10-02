using MySingingMonsterKaraokeStudio.Models;

namespace MySingingMonsterKaraokeStudio.Services;

public static class ChartValidation
{
    public static void Validate(SongProject project)
    {
        if (project.LevelRatings is null || project.LevelRatings.Any(p => p.Key is not ("Easy" or "Normal" or "Hard" or "Expert") || p.Value is < 1 or > 10))
            throw new InvalidDataException("Use a chart rating from 1 to 10.");
        if (!double.IsFinite(project.Bpm) || project.Bpm < 20 || project.Bpm > 400 || !double.IsFinite(project.OffsetSeconds))
            throw new InvalidDataException("Use a BPM from 20 to 400 and a valid timing offset.");
        if (project.TimeSignature is < 1 or > 32 || project.Notes is null)
            throw new InvalidDataException("The chart has an invalid time signature or missing notes.");
        if (project.Difficulty is not ("Easy" or "Normal" or "Hard" or "Expert") || project.Levels is null)
            throw new InvalidDataException("The chart has an invalid difficulty level.");
        if (project.DetectedBpm is double tempo && (!double.IsFinite(tempo) || tempo is < 20 or > 400) ||
            project.AudioOnsets is null || project.AudioOnsets.Any(t => !double.IsFinite(t) || t < 0) ||
            project.AudioOnsets.Zip(project.AudioOnsets.Skip(1)).Any(p => p.First >= p.Second))
            throw new InvalidDataException("The detected audio timing is invalid.");
        ValidateNotes(project.Notes);
        foreach (var level in project.Levels)
        {
            if (level.Key is not ("Easy" or "Normal" or "Hard" or "Expert") || level.Value is null) throw new InvalidDataException("The chart has an invalid difficulty level.");
            ValidateNotes(level.Value);
        }
    }

    private static void ValidateNotes(List<ChartNote> notes)
    {
        if (notes.Any(n => n is null || !double.IsFinite(n.Time) || n.Time < 0 || !double.IsFinite(n.Length) || n.Length <= 0 || !double.IsFinite(n.Time + n.Length) || !double.IsFinite(n.Pitch) || !double.IsFinite(n.EndPitch) || n.Pitch is < 0 or > 127 || n.EndPitch is < 0 or > 127))
            throw new InvalidDataException("A note has an invalid time, length, or pitch.");
        foreach (var note in notes)
        {
            var points = note.CurvePoints;
            if (points is null || (points.Count > 0 && (points.Count < 2 || points.Count > 8192 ||
                points.Any(p => p is null || !double.IsFinite(p.Position) || !double.IsFinite(p.Pitch) || p.Position is < 0 or > 1 || p.Pitch is < 0 or > 127) ||
                points[0].Position != 0 || points[^1].Position != 1 || points[0].Pitch != note.Pitch || points[^1].Pitch != note.EndPitch ||
                points.Zip(points.Skip(1)).Any(pair => pair.First.Position >= pair.Second.Position))))
                throw new InvalidDataException("A curve must have ordered points with valid positions and pitches, including both ends of the note.");
        }
        if (notes.Select(n => n.Id).Distinct().Count() != notes.Count)
            throw new InvalidDataException("Two chart notes have the same identifier.");
    }
}
