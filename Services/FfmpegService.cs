using System.Diagnostics;

namespace MySingingMonsterKaraokeStudio.Services;

public sealed class FfmpegService
{
    private readonly string? _explicitExecutable;
    private readonly FfmpegInstaller _installer;
    public string FfmpegPath => _explicitExecutable ?? _installer.ExecutablePath;

    public FfmpegService(string? executable = null, string? installDirectory = null, Func<Stream>? openExecutable = null)
    {
        _explicitExecutable = executable;
        _installer = new FfmpegInstaller(installDirectory, openExecutable);
    }

    public bool IsAvailable => File.Exists(FfmpegPath);

    public Task<string> EnsureInstalledAsync(IProgress<double>? progress = null, CancellationToken cancellationToken = default) =>
        _explicitExecutable is null ? _installer.EnsureInstalledAsync(progress, cancellationToken)
            : File.Exists(_explicitExecutable) ? Task.FromResult(_explicitExecutable)
            : Task.FromException<string>(new FileNotFoundException("The configured audio converter is missing.", _explicitExecutable));

    public Task ConvertToOggAsync(string inputFile, string outputFile, CancellationToken cancellationToken = default) =>
        ConvertAsync(inputFile, outputFile, ["-c:a", "libvorbis", "-q:a", "5"], cancellationToken);

    public Task DecodeForNotesAsync(string inputFile, string outputFile, CancellationToken cancellationToken = default) =>
        ConvertAsync(inputFile, outputFile, ["-ac", "1", "-ar", "8000", "-c:a", "pcm_s16le"], cancellationToken);

    public Task TrimToOggAsync(string inputFile, string outputFile, double start, double end, CancellationToken cancellationToken = default)
    {
        if (!double.IsFinite(start) || !double.IsFinite(end) || start < 0 || end <= start)
            throw new InvalidDataException("Choose a valid start and end time for the trim.");
        if (!string.Equals(Path.GetExtension(outputFile), ".ogg", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(Path.GetFullPath(inputFile), Path.GetFullPath(outputFile), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Trimming must create a separate OGG file; the imported audio cannot be overwritten.");
        return ConvertAsync(inputFile, outputFile, ["-ss", start.ToString(System.Globalization.CultureInfo.InvariantCulture), "-t", (end - start).ToString(System.Globalization.CultureInfo.InvariantCulture), "-c:a", "libvorbis", "-q:a", "5"], cancellationToken);
    }

    private async Task ConvertAsync(string inputFile, string outputFile, string[] options, CancellationToken cancellationToken)
    {
        if (!File.Exists(inputFile)) throw new FileNotFoundException("The imported audio file is missing. Reimport it.", inputFile);
        await EnsureInstalledAsync(cancellationToken: cancellationToken);

        var temporary = Path.Combine(Path.GetDirectoryName(outputFile)!, "conversion-" + Guid.NewGuid().ToString("N") + Path.GetExtension(outputFile));

        var psi = new ProcessStartInfo
        {
            FileName = FfmpegPath,
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            CreateNoWindow = true
        };
        psi.ArgumentList.Add("-y");
        psi.ArgumentList.Add("-nostdin");
        psi.ArgumentList.Add("-hide_banner");
        psi.ArgumentList.Add("-loglevel");
        psi.ArgumentList.Add("error");
        psi.ArgumentList.Add("-i");
        psi.ArgumentList.Add(inputFile);
        psi.ArgumentList.Add("-vn");
        foreach (var option in options) psi.ArgumentList.Add(option);
        psi.ArgumentList.Add(temporary);

        using var process = Process.Start(psi) ?? throw new InvalidOperationException("Unable to start FFmpeg.");
        try
        {
            var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
            var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            var stderr = await stderrTask;
            await stdoutTask;
            if (process.ExitCode != 0) throw new InvalidOperationException($"FFmpeg could not convert the audio.\n{stderr}");
            if (!File.Exists(temporary) || new FileInfo(temporary).Length == 0) throw new IOException("Conversion produced no audio.");
            File.Move(temporary, outputFile, true);
        }
        finally
        {
            if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(CancellationToken.None); }
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
}
