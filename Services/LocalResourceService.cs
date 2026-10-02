namespace MySingingMonsterKaraokeStudio.Services;

// Byte ranges let the media player read metadata and seek without loading the whole song.
public static class LocalResourceService
{
    public static LocalResponse Open(string path, string? range = null)
    {
        var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var length = file.Length;
        long start = 0, end = length - 1;
        var partial = !string.IsNullOrEmpty(range);
        if (partial)
        {
            var parts = range!.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase) ? range[6..].Split('-') : [];
            if (parts.Length != 2 || range.Contains(',') || length == 0) return InvalidRange(file, length);
            if (parts[0].Length == 0)
            {
                if (!long.TryParse(parts[1], out var suffix) || suffix <= 0) return InvalidRange(file, length);
                start = Math.Max(0, length - suffix);
            }
            else
            {
                if (!long.TryParse(parts[0], out start) || start < 0 || start >= length) return InvalidRange(file, length);
                if (parts[1].Length > 0 && (!long.TryParse(parts[1], out end) || end < start)) return InvalidRange(file, length);
                end = Math.Min(end, length - 1);
            }
        }
        var count = Math.Max(0, end - start + 1);
        var headers = $"Content-Type: {MimeType(path)}\r\nContent-Length: {count}\r\nAccept-Ranges: bytes\r\nCache-Control: no-store\r\n";
        if (partial) headers += $"Content-Range: bytes {start}-{end}/{length}\r\n";
        return new(new FileSegmentStream(file, start, count), partial ? 206 : 200, partial ? "Partial Content" : "OK", headers);
    }

    private static LocalResponse InvalidRange(FileStream file, long length)
    {
        file.Dispose();
        return new(new MemoryStream(), 416, "Range Not Satisfiable", $"Content-Range: bytes */{length}\r\nContent-Length: 0\r\n");
    }

    private static string MimeType(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".html" => "text/html; charset=utf-8", ".js" => "text/javascript; charset=utf-8", ".css" => "text/css; charset=utf-8",
        ".mp3" => "audio/mpeg", ".wav" => "audio/wav", ".ogg" => "audio/ogg", ".flac" => "audio/flac", ".m4a" => "audio/mp4",
        _ => "application/octet-stream"
    };

    private sealed class FileSegmentStream(FileStream file, long start, long length) : Stream
    {
        private long _position;
        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => length;
        public override long Position { get => _position; set => Seek(value, SeekOrigin.Begin); }
        public override int Read(byte[] buffer, int offset, int count)
        {
            file.Position = start + _position;
            var read = file.Read(buffer, offset, (int)Math.Min(count, length - _position));
            _position += read;
            return read;
        }
        public override long Seek(long offset, SeekOrigin origin)
        {
            var next = origin switch { SeekOrigin.Begin => offset, SeekOrigin.Current => _position + offset, _ => length + offset };
            if (next < 0 || next > length) throw new IOException("Seek outside response.");
            return _position = next;
        }
        protected override void Dispose(bool disposing) { if (disposing) file.Dispose(); base.Dispose(disposing); }
        public override void Flush() { }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}

public sealed record LocalResponse(Stream Content, int Status, string Reason, string Headers);
