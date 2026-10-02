using MySingingMonsterKaraokeStudio.Models;

namespace MySingingMonsterKaraokeStudio.Services;

public static class NotePath
{
    public static ChartNote Simplify(ChartNote note, double tolerance)
    {
        var result = Clone(note); var points = result.CurvePoints;
        if (points.Count <= 2) return result;
        var retained = new HashSet<int> { 0, points.Count - 1 };
        var pending = new Stack<(int Start, int End)>(); pending.Push((0, points.Count - 1));
        while (pending.TryPop(out var span))
        {
            var a = points[span.Start]; var b = points[span.End]; var error = tolerance; var index = -1;
            for (var i = span.Start + 1; i < span.End; i++)
            {
                var expected = a.Pitch + (b.Pitch - a.Pitch) * (points[i].Position - a.Position) / (b.Position - a.Position);
                var difference = Math.Abs(points[i].Pitch - expected);
                if (difference > error) { error = difference; index = i; }
            }
            if (index < 0) continue;
            retained.Add(index); pending.Push((span.Start, index)); pending.Push((index, span.End));
        }
        result.CurvePoints = points.Where((_, i) => retained.Contains(i)).ToList();
        return result;
    }
    public static ChartNote Clone(ChartNote note) => new()
    {
        Id = note.Id, Time = note.Time, Length = note.Length, Pitch = note.Pitch, EndPitch = note.EndPitch,
        CurvePoints = note.CurvePoints.Select(p => new CurvePoint { Position = p.Position, Pitch = p.Pitch }).ToList()
    };

    public static List<CurvePoint> Points(ChartNote note) => note.CurvePoints.Count >= 2 ? note.CurvePoints :
        [new() { Position = 0, Pitch = note.Pitch }, new() { Position = 1, Pitch = note.EndPitch }];

    public static double PitchAt(ChartNote note, double position)
    {
        var points = Points(note);
        position = Math.Clamp(position, 0, 1);
        for (var i = 1; i < points.Count; i++)
        {
            if (position > points[i].Position) continue;
            var a = points[i - 1]; var b = points[i];
            return a.Pitch + (b.Pitch - a.Pitch) * (position - a.Position) / (b.Position - a.Position);
        }
        return points[^1].Pitch;
    }

    public static ChartNote Slice(ChartNote note, double start, double end)
    {
        if (start < 0 || end > note.Length + 1e-8 || end <= start)
            throw new InvalidDataException("Choose a valid section of the note.");
        var result = Clone(note);
        result.Time += start; result.Length = end - start;
        var left = start / note.Length; var right = Math.Min(1, end / note.Length);
        result.Pitch = PitchAt(note, left); result.EndPitch = PitchAt(note, right);
        if (note.CurvePoints.Count > 0)
        {
            result.CurvePoints = [new() { Position = 0, Pitch = result.Pitch }];
            result.CurvePoints.AddRange(note.CurvePoints.Where(p => p.Position > left && p.Position < right)
                .Select(p => new CurvePoint { Position = (p.Position - left) / (right - left), Pitch = p.Pitch }));
            result.CurvePoints.Add(new() { Position = 1, Pitch = result.EndPitch });
        }
        return result;
    }

    // TMB stores straight slides. Joined segments preserve the drawn trajectory and fractional pitches.
    public static IEnumerable<ChartNote> Segments(ChartNote note)
    {
        if (note.CurvePoints.Count == 0) { yield return note; yield break; }
        var points = Points(note);
        for (var i = 1; i < points.Count; i++)
        {
            var a = points[i - 1]; var b = points[i];
            var start = note.Time + a.Position * note.Length;
            var end = note.Time + b.Position * note.Length;
            yield return new() { Time = start, Length = end - start, Pitch = a.Pitch, EndPitch = b.Pitch };
        }
    }
}
