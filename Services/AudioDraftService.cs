using System.Numerics;
using MySingingMonsterKaraokeStudio.Models;

namespace MySingingMonsterKaraokeStudio.Services;

public sealed class AudioDraftService
{
    private const int SampleRate = 8000, Hop = 160;
    private sealed record Frame(double Time, double Pitch, double Energy, double Flux, double Confidence)
    {
        public bool Voiced { get; set; }
        public double SmoothedPitch { get; set; } = Pitch;
    }

    // A dominant-pitch draft; full mixes still need corrections in the chart editor.
    public async Task<List<ChartNote>> GenerateAsync(string oggPath, FfmpegService ffmpeg, IProgress<double>? progress = null)
        => (await AnalyzeAsync(oggPath, ffmpeg, progress: progress)).Notes;

    public async Task<AudioDraft> AnalyzeAsync(string oggPath, FfmpegService ffmpeg, double bpm = 120, IProgress<double>? progress = null)
    {
        if (!double.IsFinite(bpm) || bpm is < 20 or > 400) throw new InvalidDataException("Choose a BPM from 20 to 400.");
        var wave = Path.Combine(Path.GetDirectoryName(oggPath)!, "analysis-" + Guid.NewGuid().ToString("N") + ".wav");
        try
        {
            await ffmpeg.DecodeForNotesAsync(oggPath, wave);
            return await Task.Run(() => Analyze(ReadSamples(wave), bpm, progress));
        }
        finally { if (File.Exists(wave)) File.Delete(wave); }
    }

    private static short[] ReadSamples(string path)
    {
        using var reader = new BinaryReader(File.OpenRead(path));
        if (!reader.ReadBytes(4).SequenceEqual("RIFF"u8.ToArray())) throw new InvalidDataException("Audio analysis could not read the decoded audio.");
        reader.ReadUInt32();
        if (!reader.ReadBytes(4).SequenceEqual("WAVE"u8.ToArray())) throw new InvalidDataException("Invalid decoded audio.");
        while (reader.BaseStream.Position + 8 <= reader.BaseStream.Length)
        {
            var chunk = System.Text.Encoding.ASCII.GetString(reader.ReadBytes(4)); var length = reader.ReadUInt32();
            if (length > reader.BaseStream.Length - reader.BaseStream.Position) throw new InvalidDataException("Decoded audio is truncated.");
            if (chunk == "data")
            {
                var bytes = reader.ReadBytes(checked((int)length)); var samples = new short[bytes.Length / 2]; Buffer.BlockCopy(bytes, 0, samples, 0, samples.Length * 2); return samples;
            }
            reader.BaseStream.Position += length + (length & 1);
        }
        throw new InvalidDataException("Decoded audio contains no samples.");
    }

    private static AudioDraft Analyze(short[] samples, double bpm, IProgress<double>? progress)
    {
        const int size = 1024;
        var frames = new List<Frame>();
        var spectrum = new Complex[size];
        var previous = new double[size / 2];
        var window = Enumerable.Range(0, size).Select(i => .5 - .5 * Math.Cos(2 * Math.PI * i / (size - 1))).ToArray();
        for (var center = 0; center < samples.Length; center += Hop)
        {
            double energy = 0;
            // Center the FFT window, while measuring attack energy over only this 20 ms frame.
            for (var i = 0; i < size; i++)
            {
                var index = center + i - size / 2;
                spectrum[i] = index >= 0 && index < samples.Length ? samples[index] / 32768.0 * window[i] : 0;
            }
            var energySamples = 0;
            for (var i = Math.Max(0, center - Hop / 2); i < Math.Min(samples.Length, center + Hop / 2); i++)
            { var value = samples[i] / 32768.0; energy += value * value; energySamples++; }
            Fft(spectrum);
            var peak = 15;
            for (var bin = 16; bin < 225; bin++) if (spectrum[bin].Magnitude > spectrum[peak].Magnitude) peak = bin;
            var a = Math.Log(spectrum[peak - 1].Magnitude + 1e-12); var b = Math.Log(spectrum[peak].Magnitude + 1e-12); var c = Math.Log(spectrum[peak + 1].Magnitude + 1e-12);
            var divisor = a - 2 * b + c;
            var delta = Math.Abs(divisor) < 1e-10 ? 0 : Math.Clamp(.5 * (a - c) / divisor, -.5, .5);
            var frequency = (peak + delta) * SampleRate / size;
            var pitch = 69 + 12 * Math.Log2(frequency / 440);
            while (pitch > 73) pitch -= 12;
            while (pitch < 47) pitch += 12;
            double flux = 0, total = 0, power = 0;
            var magnitudes = new double[size / 2];
            for (var bin = 4; bin < magnitudes.Length; bin++) magnitudes[bin] = spectrum[bin].Magnitude;
            for (var bin = 5; bin < magnitudes.Length - 1; bin++)
            {
                // A local maximum in the previous spectrum suppresses false attacks from vibrato/glides.
                var reference = Math.Max(previous[bin - 1], Math.Max(previous[bin], previous[bin + 1]));
                flux += Math.Max(0, magnitudes[bin] - reference);
                total += magnitudes[bin]; power += magnitudes[bin] * magnitudes[bin];
            }
            frames.Add(new((double)center / SampleRate, pitch, Math.Sqrt(energy / Math.Max(1, energySamples)),
                flux / Math.Max(total, 1e-10), spectrum[peak].Magnitude * spectrum[peak].Magnitude / Math.Max(power, 1e-10)));
            previous = magnitudes;
            if (frames.Count % 100 == 0) progress?.Report(85.0 * center / samples.Length);
        }
        if (frames.Count == 0) return new([], [], [], null);
        var threshold = Math.Max(.0015, frames.Max(f => f.Energy) * .05);
        var onsets = FindOnsets(frames, threshold);
        var detectedBpm = EstimateTempo(onsets);
        foreach (var frame in frames) frame.Voiced = frame.Energy >= threshold && frame.Confidence >= .08;
        for (var i = 0; i < frames.Count; i++)
        {
            var nearby = frames.Skip(Math.Max(0, i - 2)).Take(Math.Min(frames.Count - Math.Max(0, i - 2), i + 3 - Math.Max(0, i - 2)))
                .Where(f => f.Voiced).Select(f => f.Pitch).Order().ToArray();
            if (nearby.Length > 0) frames[i].SmoothedPitch = nearby[nearby.Length / 2];
        }
        // Bridge brief tonal dropouts under percussion, but never fill actual silence.
        for (var i = 1; i < frames.Count - 1; i++)
        {
            if (frames[i].Voiced || frames[i].Energy < threshold || !frames[i - 1].Voiced) continue;
            var end = i; while (end < frames.Count && !frames[end].Voiced && end - i < 3 && frames[end].Energy >= threshold) end++;
            if (end >= frames.Count || !frames[end].Voiced || Math.Abs(frames[end].SmoothedPitch - frames[i - 1].SmoothedPitch) > 2) continue;
            for (var j = i; j < end; j++)
            {
                frames[j].Voiced = true;
                frames[j].SmoothedPitch = frames[i - 1].SmoothedPitch + (frames[end].SmoothedPitch - frames[i - 1].SmoothedPitch) * (j - i + 1) / (end - i + 1);
            }
        }
        var duration = (double)samples.Length / SampleRate;
        var notes = BuildPhrases(frames, onsets, duration, detectedBpm ?? bpm);
        var midi = BuildMidiNotes(frames, duration);
        progress?.Report(100);
        return new(notes, midi, onsets, detectedBpm);
    }

    private static List<double> FindOnsets(List<Frame> frames, double threshold)
    {
        // Spectral flux plus a short-time energy rise locates attacks in the waveform.
        // Reference for flux and local frequency filtering: librosa.onset.onset_strength.
        var strength = new double[frames.Count];
        for (var i = 0; i < frames.Count; i++)
        {
            var start = Math.Max(0, i - 4);
            var previousEnergy = i == 0 ? 0 : frames.Skip(start).Take(i - start).Average(f => f.Energy);
            var rise = Math.Clamp((frames[i].Energy - previousEnergy) / Math.Max(frames[i].Energy, threshold), 0, 1);
            strength[i] = frames[i].Energy < threshold ? 0 : .55 * rise + .45 * frames[i].Flux;
        }
        var onsets = new List<double>();
        for (var i = 0; i < frames.Count; i++)
        {
            var local = strength.Skip(Math.Max(0, i - 8)).Take(Math.Min(frames.Count, i + 9) - Math.Max(0, i - 8)).Average();
            if (strength[i] < .18 || strength[i] < local + .09) continue;
            if (i > 0 && strength[i] <= strength[i - 1] || i + 1 < strength.Length && strength[i] < strength[i + 1]) continue;
            // Backtrack to the preceding low-energy frame to place the note at the attack, not its peak.
            var index = i;
            while (index > Math.Max(0, i - 4) && frames[index - 1].Energy < frames[index].Energy * .9) index--;
            var time = frames[index].Time;
            if (onsets.Count == 0 || time - onsets[^1] >= .1) onsets.Add(time);
        }
        return onsets;
    }

    private static double? EstimateTempo(List<double> onsets)
    {
        var intervals = onsets.Zip(onsets.Skip(1)).Select(p => p.Second - p.First).Where(t => t is >= .18 and <= 1.2).Order().ToArray();
        if (intervals.Length < 5) return null;
        var period = intervals[intervals.Length / 2];
        if (intervals.Count(t => Math.Abs(t - period) <= period * .1) < intervals.Length * .75) return null;
        var tempo = 60 / period;
        while (tempo < 70) tempo *= 2;
        while (tempo > 180) tempo /= 2;
        return Math.Round(tempo, 2);
    }

    private static List<ChartNote> BuildPhrases(List<Frame> frames, List<double> onsets, double duration, double bpm)
    {
        var onsetFrames = onsets.Select(t => (int)Math.Round(t * SampleRate / Hop)).ToHashSet();
        var notes = new List<ChartNote>(); var phrase = new List<Frame>();
        var lowPitch = double.MaxValue; var highPitch = double.MinValue;
        foreach (var (frame, index) in frames.Select((f, i) => (f, i)))
        {
            if (phrase.Count > 0 && (!frame.Voiced ||
                onsetFrames.Contains(index) && frame.Time - phrase[0].Time >= .12 ||
                Math.Abs(frame.SmoothedPitch - phrase[^1].SmoothedPitch) >= 3 ||
                frame.Time - phrase[0].Time >= 240 / bpm && highPitch - lowPitch >= .45))
            { AddPhrase(notes, phrase, frame.Time); phrase.Clear(); lowPitch = double.MaxValue; highPitch = double.MinValue; }
            if (frame.Voiced) { phrase.Add(frame); lowPitch = Math.Min(lowPitch, frame.SmoothedPitch); highPitch = Math.Max(highPitch, frame.SmoothedPitch); }
        }
        AddPhrase(notes, phrase, duration);
        return MakeLevel(notes, "Expert");
    }

    private static void AddPhrase(List<ChartNote> notes, List<Frame> frames, double finish)
    {
        if (frames.Count == 0 || finish - frames[0].Time < .07) return;
        var note = new ChartNote { Time = frames[0].Time, Length = finish - frames[0].Time };
        var pitches = frames.Select(f => f.SmoothedPitch).Order().ToArray();
        if (pitches[^1] - pitches[0] < .45)
            note.Pitch = note.EndPitch = Math.Round(pitches[pitches.Length / 2]);
        else
        {
            note.CurvePoints = frames.Select(f => new CurvePoint { Position = (f.Time - note.Time) / note.Length, Pitch = f.SmoothedPitch }).ToList();
            note.Pitch = note.CurvePoints[0].Pitch; note.EndPitch = note.CurvePoints[^1].Pitch;
            note.CurvePoints.Add(new() { Position = 1, Pitch = note.EndPitch });
            note = NotePath.Simplify(note, .08);
        }
        notes.Add(note);
    }

    private static List<ChartNote> BuildMidiNotes(List<Frame> frames, double duration)
    {
        var notes = new List<ChartNote>(); ChartNote? current = null;
        foreach (var frame in frames)
        {
            var pitch = Math.Round(frame.SmoothedPitch);
            if (current is not null && (!frame.Voiced || pitch != current.Pitch))
            { if (current.Length >= .055) notes.Add(current); current = null; }
            if (!frame.Voiced) continue;
            current ??= new() { Time = frame.Time, Pitch = pitch, EndPitch = pitch };
            current.Length = Math.Min(duration, frame.Time + (double)Hop / SampleRate) - current.Time;
        }
        if (current is not null && current.Length >= .055) notes.Add(current);
        return MakeLevel(notes, "Expert");
    }

    public static List<ChartNote> MakeAudioLevel(AudioDraft draft, string difficulty)
    {
        var tolerance = difficulty switch { "Easy" => .12, "Normal" => .08, "Hard" => .06, _ => .04 };
        return MakeLevel(draft.Notes, difficulty).Select(n => NotePath.Simplify(n, tolerance)).ToList();
    }

    private static void Fft(Complex[] values)
    {
        var n = values.Length;
        for (int i = 1, j = 0; i < n; i++)
        {
            var bit = n >> 1; for (; (j & bit) != 0; bit >>= 1) j ^= bit; j ^= bit;
            if (i < j) (values[i], values[j]) = (values[j], values[i]);
        }
        for (var length = 2; length <= n; length <<= 1)
        {
            var rotation = Complex.FromPolarCoordinates(1, -2 * Math.PI / length);
            for (var start = 0; start < n; start += length)
            {
                var factor = Complex.One;
                for (var i = 0; i < length / 2; i++)
                {
                    var even = values[start + i]; var odd = values[start + i + length / 2] * factor;
                    values[start + i] = even + odd; values[start + i + length / 2] = even - odd; factor *= rotation;
                }
            }
        }
    }

    public static List<ChartNote> MakeLevel(List<ChartNote> notes, string difficulty)
    {
        var spacing = difficulty switch { "Easy" => .5, "Normal" => .25, "Hard" => .125, _ => .03 };
        var output = new List<ChartNote>();
        foreach (var note in notes.OrderBy(n => n.Time).ThenBy(n => n.Pitch))
        {
            if (output.Count > 0 && note.Time - output[^1].Time < spacing) continue;
            if (output.Count > 0 && output[^1].Time + output[^1].Length > note.Time)
                output[^1] = NotePath.Slice(output[^1], 0, note.Time - output[^1].Time);
            var copy = NotePath.Clone(note); copy.Id = Guid.NewGuid(); output.Add(copy);
        }
        return output;
    }

    public static List<ChartNote> CropNotes(List<ChartNote> notes, double offset, double start, double end)
    {
        var output = new List<ChartNote>();
        foreach (var n in notes)
        {
            var onset = n.Time + offset; var finish = onset + n.Length;
            var left = Math.Max(start, onset); var right = Math.Min(end, finish);
            if (right <= left) continue;
            var copy = NotePath.Slice(n, Math.Max(0, left - onset), Math.Min(n.Length, right - onset));
            copy.Time = left - start; output.Add(copy);
        }
        return output;
    }
}

public sealed record AudioDraft(List<ChartNote> Notes, List<ChartNote> MidiNotes, List<double> Onsets, double? DetectedBpm);
