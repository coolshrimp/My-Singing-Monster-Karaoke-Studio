using System.Security.Cryptography;
using System.IO.Compression;
using System.Net.Http;

namespace MySingingMonsterKaraokeStudio.Services;

public sealed class FfmpegInstaller
{
    public const string Version = "9.0.2";
    public const string ExecutableHash = "3256173F3F8BFFD7DF12227C68ADF68025EDB1832273A9530688A7BB1ED8EDEC";
    public const string DownloadUrl = "https://www.gyan.dev/ffmpeg/builds/packages/ffmpeg-9.0.2-essentials_build.zip";
    public const string ArchiveHash = "60F467265B1E312373DBCD92200C2618A74850F98D3D078E94296BB3FA2047BA";
    public const string MirrorUrl = "https://github.com/GyanD/codexffmpeg/releases/download/9.0.2/ffmpeg-9.0.2-essentials_build.zip";
    private static readonly HttpClient Downloads = new() { Timeout = TimeSpan.FromMinutes(10) };
    private static readonly SemaphoreSlim SetupGate = new(1, 1);
    private readonly Func<Stream>? _openExecutable;
    private readonly Func<CancellationToken, Task<Stream>>? _openArchive;
    public string InstallDirectory { get; }
    public string ExecutablePath => Path.Combine(InstallDirectory, "ffmpeg.exe");

    static FfmpegInstaller() => Downloads.DefaultRequestHeaders.UserAgent.ParseAdd($"MySingingMonsterKaraokeStudio/{typeof(FfmpegInstaller).Assembly.GetName().Version}");

    public FfmpegInstaller(string? installDirectory = null, Func<Stream>? openExecutable = null, Func<CancellationToken, Task<Stream>>? openArchive = null)
    {
        InstallDirectory = installDirectory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MSM Song Studio", "Tools", "FFmpeg", Version);
        _openExecutable = openExecutable; _openArchive = openArchive;
    }

    private static Stream OpenResource(string name) => typeof(FfmpegInstaller).Assembly.GetManifestResourceStream("MySingingMonsterKaraokeStudio.Tools." + name)
        ?? throw new InvalidOperationException("This build does not include the audio converter. Run the complete MSM Song Studio build.");

    public async Task<string> EnsureInstalledAsync(IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        await SetupGate.WaitAsync(cancellationToken);
        string? temporary = null;
        string? archiveFile = null;
        try
        {
            Directory.CreateDirectory(InstallDirectory);
            using var setupLock = await AcquireSetupLockAsync(cancellationToken);
            progress?.Report(0);
            if (await IsVerifiedAsync(ExecutablePath, cancellationToken))
            {
                await WriteNoticesAsync(cancellationToken);
                progress?.Report(100);
                return ExecutablePath;
            }

            temporary = Path.Combine(InstallDirectory, "ffmpeg-" + Guid.NewGuid().ToString("N") + ".tmp");
            if (_openExecutable is null)
            {
                archiveFile = Path.Combine(InstallDirectory, "download-" + Guid.NewGuid().ToString("N") + ".tmp");
                if (_openArchive is null) await DownloadArchiveAsync(archiveFile, progress, cancellationToken);
                else
                {
                    using var input = await _openArchive(cancellationToken);
                    await CopyDownloadAsync(input, archiveFile, input.CanSeek ? input.Length : 0, progress, cancellationToken);
                }
                await using (var input = File.OpenRead(archiveFile))
                    if (Convert.ToHexString(await SHA256.HashDataAsync(input, cancellationToken)) != ArchiveHash)
                        throw new InvalidDataException("The FFmpeg download failed verification. Retry setup; the existing installation is unchanged.");
                progress?.Report(78);
                using var archive = ZipFile.OpenRead(archiveFile);
                var entries = archive.Entries.Where(e => e.FullName == "ffmpeg-9.0.2-essentials_build/bin/ffmpeg.exe").ToList();
                if (entries.Count != 1 || entries[0].Length > 128L * 1024 * 1024) throw new InvalidDataException("The converter download has an unexpected layout.");
                using var executable = entries[0].Open();
                await using var destination = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 1024, true);
                await executable.CopyToAsync(destination, cancellationToken);
            }
            else
            {
            using (var input = _openExecutable())
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 1024, useAsync: true))
            {
                var buffer = new byte[1024 * 1024];
                long written = 0;
                var total = input.CanSeek ? input.Length : 0;
                int read;
                while ((read = await input.ReadAsync(buffer.AsMemory(), cancellationToken)) > 0)
                {
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                    written += read;
                    if (total > 0) progress?.Report(written * 90.0 / total);
                }
                await output.FlushAsync(cancellationToken);
            }
            }
            progress?.Report(92);
            if (!await IsVerifiedAsync(temporary, cancellationToken))
                throw new InvalidDataException("Audio converter verification failed. Setup has left the existing installation unchanged.");
            cancellationToken.ThrowIfCancellationRequested();
            await WriteNoticesAsync(cancellationToken);
            File.Move(temporary, ExecutablePath, overwrite: true);
            temporary = null;
            progress?.Report(100);
            return ExecutablePath;
        }
        finally
        {
            if (temporary is not null && File.Exists(temporary)) File.Delete(temporary);
            if (archiveFile is not null && File.Exists(archiveFile)) File.Delete(archiveFile);
            SetupGate.Release();
        }
    }

    private static async Task DownloadArchiveAsync(string file, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        foreach (var url in new[] { MirrorUrl, DownloadUrl })
        {
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromMinutes(10));
                using var response = await Downloads.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
                response.EnsureSuccessStatusCode();
                using var input = await response.Content.ReadAsStreamAsync(timeout.Token);
                await CopyDownloadAsync(input, file, response.Content.Headers.ContentLength ?? 0, progress, timeout.Token);
                return;
            }
            catch (Exception ex) when (url == MirrorUrl && !cancellationToken.IsCancellationRequested && ex is HttpRequestException or IOException or OperationCanceledException)
            { progress?.Report(0); }
        }
    }

    private static async Task CopyDownloadAsync(Stream input, string file, long total, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        if (total > 200L * 1024 * 1024) throw new InvalidDataException("The converter download is unexpectedly large.");
        await using var output = new FileStream(file, FileMode.Create, FileAccess.Write, FileShare.None, 1024 * 1024, true);
        var buffer = new byte[1024 * 1024]; long written = 0;
        while (true)
        {
            using var idle = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            idle.CancelAfter(TimeSpan.FromSeconds(30));
            var read = await input.ReadAsync(buffer, idle.Token);
            if (read == 0) break;
            written += read;
            if (written > 200L * 1024 * 1024) throw new InvalidDataException("The converter download is unexpectedly large.");
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            progress?.Report(total > 0 ? Math.Min(75, written * 75.0 / total) : 10);
        }
    }

    public static async Task<bool> IsVerifiedAsync(string path, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(path)) return false;
        await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, useAsync: true);
        return Convert.ToHexString(await SHA256.HashDataAsync(input, cancellationToken)) == ExecutableHash;
    }

    private async Task<FileStream> AcquireSetupLockAsync(CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try { return new FileStream(Path.Combine(InstallDirectory, "setup.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) when (DateTime.UtcNow < deadline) { await Task.Delay(150, cancellationToken); }
        }
    }

    private async Task WriteNoticesAsync(CancellationToken cancellationToken)
    {
        foreach (var name in new[] { "FFmpeg-LICENSE.txt", "FFmpeg-SOURCE.txt" })
        {
            using var input = OpenResource(name);
            await using var output = new FileStream(Path.Combine(InstallDirectory, name), FileMode.Create, FileAccess.Write, FileShare.Read, 4096, useAsync: true);
            await input.CopyToAsync(output, cancellationToken);
        }
    }
}
