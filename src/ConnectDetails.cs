using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace ExplorerNative
{
    /// <summary>A name and a value, in the order they are worth hearing.</summary>
    public sealed record NameValue(string Name, string Value);

    /// <summary>
    /// Any byte source read in 256 KB blocks, remembered. A Drive range source answers each read with a request
    /// costing most of a second whatever its size, and the parsers and Media Foundation read in small pieces;
    /// this turns thirty small reads of a header into one request.
    /// </summary>
    internal sealed class CachedBytes : IRangeSource
    {
        private const int Block = 256 * 1024;
        private const int MostBlocks = 128;

        private readonly IRangeSource _inner;
        private readonly CancellationToken _token;
        private readonly Dictionary<long, byte[]> _blocks = new();
        private readonly Queue<long> _order = new();
        private readonly object _gate = new();

        public CachedBytes(IRangeSource inner, CancellationToken token)
        {
            _inner = inner;
            _token = token;
            Length = inner.Length;
        }

        public long Length { get; }
        public int Streams => 1;

        private byte[] BlockAt(long index)
        {
            lock (_gate)
                if (_blocks.TryGetValue(index, out var held)) return held;
            _token.ThrowIfCancellationRequested();
            long at = index * Block;
            var bytes = new byte[(int)Math.Min(Block, Length - at)];
            int filled = 0;
            while (filled < bytes.Length)
            {
                int got = _inner.ReadAsync(at + filled, bytes.AsMemory(filled), _token).GetAwaiter().GetResult();
                if (got <= 0) break;
                filled += got;
            }
            if (filled < bytes.Length) Array.Resize(ref bytes, filled);
            lock (_gate)
            {
                _blocks[index] = bytes;
                _order.Enqueue(index);
                while (_order.Count > MostBlocks) _blocks.Remove(_order.Dequeue());
            }
            return bytes;
        }

        /// <summary>Up to <paramref name="into"/>'s length from <paramref name="offset"/>; fewer only at the end.</summary>
        public int Read(long offset, Span<byte> into)
        {
            int done = 0;
            while (done < into.Length && offset + done < Length)
            {
                long at = offset + done;
                var block = BlockAt(at / Block);
                int from = (int)(at % Block);
                if (from >= block.Length) break;
                int take = Math.Min(block.Length - from, into.Length - done);
                block.AsSpan(from, take).CopyTo(into[done..]);
                done += take;
            }
            return done;
        }

        public byte[] Get(long offset, int count)
        {
            if (offset < 0 || offset >= Length || count <= 0) return Array.Empty<byte>();
            var bytes = new byte[(int)Math.Min(count, Length - offset)];
            int got = Read(offset, bytes);
            return got == bytes.Length ? bytes : bytes[..got];
        }

        /// <summary>Big pieces (a moov, a picture) are read straight through rather than through the cache.</summary>
        public byte[] GetLarge(long offset, int count)
        {
            if (count <= 4 * Block) return Get(offset, count);
            if (offset < 0 || offset >= Length) return Array.Empty<byte>();
            var bytes = new byte[(int)Math.Min(count, Length - offset)];
            int filled = 0;
            while (filled < bytes.Length)
            {
                _token.ThrowIfCancellationRequested();
                int got = _inner.ReadAsync(offset + filled, bytes.AsMemory(filled, Math.Min(4 << 20, bytes.Length - filled)), _token).GetAwaiter().GetResult();
                if (got <= 0) break;
                filled += got;
            }
            return filled == bytes.Length ? bytes : bytes[..filled];
        }

        public System.Threading.Tasks.Task<int> ReadAsync(long offset, Memory<byte> into, CancellationToken token) =>
            System.Threading.Tasks.Task.FromResult(Read(offset, into.Span));

        public void Dispose() { }
    }

    /// <summary>A seekable read-only stream over <see cref="CachedBytes"/>, for the text counter and the hash.</summary>
    internal sealed class CachedStream : Stream
    {
        private readonly CachedBytes _bytes;
        private long _position;

        public CachedStream(CachedBytes bytes) => _bytes = bytes;

        public override int Read(byte[] buffer, int offset, int count)
        {
            int got = _bytes.Read(_position, buffer.AsSpan(offset, count));
            _position += got;
            return got;
        }

        public override long Seek(long offset, SeekOrigin origin) =>
            _position = Math.Clamp(origin switch { SeekOrigin.Current => _position + offset, SeekOrigin.End => Length + offset, _ => offset }, 0, Length);

        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => _bytes.Length;
        public override long Position { get => _position; set => _position = Math.Clamp(value, 0, Length); }
        public override void Flush() { }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>What a media file says about itself, gathered from whichever readers can say it.</summary>
    internal sealed class MediaFacts
    {
        public string? Container;
        public double? Duration;
        public long? Bitrate;
        public long? OverallBitrate;
        public readonly List<Dictionary<string, object?>> Audio = new();
        public readonly List<Dictionary<string, object?>> Video = new();
        public readonly TagList Tags = new();
        public readonly List<Dictionary<string, object?>> Chapters = new();
        public readonly List<NameValue> Extra = new();

        public Dictionary<string, object?> AudioStream(int index)
        {
            while (Audio.Count <= index) Audio.Add(new Dictionary<string, object?>());
            return Audio[index];
        }

        public Dictionary<string, object?> VideoStream(int index)
        {
            while (Video.Count <= index) Video.Add(new Dictionary<string, object?>());
            return Video[index];
        }

        public void Chapter(string title, double start) =>
            Chapters.Add(new Dictionary<string, object?> { ["title"] = title, ["startSeconds"] = Math.Round(start, 3) });

        public void Say(string name, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value) && !Extra.Any(e => e.Name == name)) Extra.Add(new NameValue(name, value.Trim()));
        }
    }

    /// <summary>
    /// Tags in one fixed order, the most asked-for first, whatever order the file kept them in. Unknown ones
    /// follow under their own names. The first value given for a name wins.
    /// </summary>
    internal sealed class TagList
    {
        public static readonly string[] Order =
        {
            "Title", "Artist", "Album artist", "Album", "Genre", "Year", "Track", "Disc", "Composer", "Conductor",
            "Comment", "Lyrics", "BPM", "Key", "ISRC", "Publisher", "Copyright", "Encoder",
            "ReplayGain track gain", "ReplayGain track peak", "ReplayGain album gain", "ReplayGain album peak",
            "Rating", "Cover art",
        };

        private readonly Dictionary<string, string> _values = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<string> _others = new();

        public int Count => _values.Count;

        public void Add(string name, string? value)
        {
            if (string.IsNullOrWhiteSpace(value) || string.IsNullOrWhiteSpace(name)) return;
            value = value.Replace("\0", " ").Trim();
            if (name == "Lyrics" && value.Length > 2000) value = value[..2000];
            if (value.Length > 4000) value = value[..4000];
            if (_values.ContainsKey(name)) return;
            _values[name] = value;
            if (!Order.Contains(name)) _others.Add(name);
        }

        public bool Has(string name) => _values.ContainsKey(name);

        /// <summary>"3" and "12" as "3 of 12"; a "3/12" left as the file wrote it becomes the same.</summary>
        public void Numbered(string name, string? number, string? total)
        {
            if (string.IsNullOrWhiteSpace(number)) return;
            number = number.Trim();
            int slash = number.IndexOf('/');
            if (slash > 0) { total ??= number[(slash + 1)..]; number = number[..slash]; }
            Add(name, string.IsNullOrWhiteSpace(total) || total.Trim() == "0" ? number : $"{number} of {total.Trim()}");
        }

        public List<NameValue> ToList() =>
            Order.Where(_values.ContainsKey).Select(n => new NameValue(n, _values[n]))
                .Concat(_others.Select(n => new NameValue(n, _values[n]))).ToList();

        /// <summary>A Vorbis comment (FLAC, Ogg) or an APE/Matroska-style name, under its common name.</summary>
        public void AddFieldName(string field, string value, Dictionary<string, string> pending)
        {
            switch (field.ToUpperInvariant())
            {
                case "TITLE": Add("Title", value); break;
                case "ARTIST": Add("Artist", value); break;
                case "ALBUMARTIST": case "ALBUM ARTIST": case "ALBUM_ARTIST": Add("Album artist", value); break;
                case "ALBUM": Add("Album", value); break;
                case "GENRE": Add("Genre", value); break;
                case "DATE": case "YEAR": case "DATE_RELEASED": case "ORIGINALDATE": Add("Year", value); break;
                case "TRACKNUMBER": case "PART_NUMBER": pending["track"] = value; break;
                case "TRACKTOTAL": case "TOTALTRACKS": pending["tracks"] = value; break;
                case "DISCNUMBER": pending["disc"] = value; break;
                case "DISCTOTAL": case "TOTALDISCS": pending["discs"] = value; break;
                case "COMPOSER": Add("Composer", value); break;
                case "CONDUCTOR": Add("Conductor", value); break;
                case "COMMENT": case "DESCRIPTION": Add("Comment", value); break;
                case "LYRICS": case "UNSYNCEDLYRICS": Add("Lyrics", value); break;
                case "BPM": Add("BPM", value); break;
                case "KEY": case "INITIALKEY": Add("Key", value); break;
                case "ISRC": Add("ISRC", value); break;
                case "PUBLISHER": case "LABEL": case "ORGANIZATION": Add("Publisher", value); break;
                case "COPYRIGHT": Add("Copyright", value); break;
                case "ENCODER": case "ENCODED_BY": case "ENCODED-BY": case "ENCODEDBY": Add("Encoder", value); break;
                case "REPLAYGAIN_TRACK_GAIN": Add("ReplayGain track gain", value); break;
                case "REPLAYGAIN_TRACK_PEAK": Add("ReplayGain track peak", value); break;
                case "REPLAYGAIN_ALBUM_GAIN": Add("ReplayGain album gain", value); break;
                case "REPLAYGAIN_ALBUM_PEAK": Add("ReplayGain album peak", value); break;
                case "RATING": Add("Rating", value); break;
                case "METADATA_BLOCK_PICTURE": break;
                default:
                    if (field.StartsWith("CHAPTER", StringComparison.OrdinalIgnoreCase)) break;
                    Add(field.Length > 1 ? char.ToUpperInvariant(field[0]) + field[1..].ToLowerInvariant() : field, value);
                    break;
            }
        }

        public void FinishNumbers(Dictionary<string, string> pending)
        {
            pending.TryGetValue("track", out var t); pending.TryGetValue("tracks", out var tt);
            pending.TryGetValue("disc", out var d); pending.TryGetValue("discs", out var dt);
            Numbered("Track", t, tt);
            Numbered("Disc", d, dt);
        }
    }

    /// <summary>
    /// The readers behind <c>/api/stat</c>'s <c>media</c>, <c>text</c>, <c>image</c> and <c>archive</c>: Media
    /// Foundation's source reader for the streams, and small parsers for what it does not say (FLAC STREAMINFO and
    /// its MD5, Vorbis comments and Opus heads, ID3v2 and the Xing/LAME header, MP4 atoms and chapters, Matroska
    /// EBML). Each reads only headers and tails, through <see cref="CachedBytes"/>, so a Drive file costs a few
    /// ranged requests and never a placeholder read. A parser that fails leaves what it had; nothing here throws
    /// except cancellation.
    /// </summary>
    internal static class ConnectDetails
    {
        // MARK: Small helpers

        private static uint Be32(ReadOnlySpan<byte> b, int at) => (uint)(b[at] << 24 | b[at + 1] << 16 | b[at + 2] << 8 | b[at + 3]);
        private static ulong Be64(ReadOnlySpan<byte> b, int at) => (ulong)Be32(b, at) << 32 | Be32(b, at + 4);
        private static int Be16(ReadOnlySpan<byte> b, int at) => b[at] << 8 | b[at + 1];
        private static uint Le32(ReadOnlySpan<byte> b, int at) => BitConverter.ToUInt32(b[at..(at + 4)]);
        private static string Ascii(ReadOnlySpan<byte> b) => Encoding.ASCII.GetString(b);

        public static string Layout(int channels) => channels switch
        {
            1 => "mono", 2 => "stereo", 3 => "2.1", 4 => "quad", 6 => "5.1", 8 => "7.1", _ => $"{channels} channels",
        };

        /// <summary>A picture's shape from its first bytes: "600×600 JPEG".</summary>
        public static string Picture(ReadOnlySpan<byte> data, string? mime)
        {
            var (w, h, format) = ImageSize(data);
            format ??= mime switch { "image/jpeg" or "image/jpg" => "JPEG", "image/png" => "PNG", _ => mime?.Replace("image/", "").ToUpperInvariant() };
            return w > 0 ? $"yes, {w}×{h} {format}".Trim() : $"yes, {format}".TrimEnd(',', ' ');
        }

        /// <summary>Width, height and format of a PNG, JPEG, GIF, BMP or WebP from its header.</summary>
        public static (int W, int H, string? Format) ImageSize(ReadOnlySpan<byte> d)
        {
            try
            {
                if (d.Length >= 24 && d[0] == 0x89 && d[1] == (byte)'P' && d[2] == (byte)'N' && d[3] == (byte)'G')
                    return ((int)Be32(d, 16), (int)Be32(d, 20), "PNG");
                if (d.Length >= 10 && d[0] == (byte)'G' && d[1] == (byte)'I' && d[2] == (byte)'F')
                    return (d[6] | d[7] << 8, d[8] | d[9] << 8, "GIF");
                if (d.Length >= 26 && d[0] == (byte)'B' && d[1] == (byte)'M')
                    return (BitConverter.ToInt32(d[18..22]), Math.Abs(BitConverter.ToInt32(d[22..26])), "BMP");
                if (d.Length >= 30 && Ascii(d[..4]) == "RIFF" && Ascii(d[8..12]) == "WEBP")
                {
                    var chunk = Ascii(d[12..16]);
                    if (chunk == "VP8X") return (1 + (d[24] | d[25] << 8 | d[26] << 16), 1 + (d[27] | d[28] << 8 | d[29] << 16), "WebP");
                    if (chunk == "VP8 ") return ((d[26] | d[27] << 8) & 0x3FFF, (d[28] | d[29] << 8) & 0x3FFF, "WebP");
                    if (chunk == "VP8L") { uint bits = Le32(d, 21); return ((int)(bits & 0x3FFF) + 1, (int)((bits >> 14) & 0x3FFF) + 1, "WebP"); }
                }
                if (d.Length >= 4 && d[0] == 0xFF && d[1] == 0xD8)
                {
                    int i = 2;
                    while (i + 9 < d.Length)
                    {
                        if (d[i] != 0xFF) { i++; continue; }
                        byte marker = d[i + 1];
                        if (marker == 0xD8 || marker == 0x01 || (marker >= 0xD0 && marker <= 0xD7)) { i += 2; continue; }
                        int length = Be16(d, i + 2);
                        if (marker is >= 0xC0 and <= 0xCF && marker != 0xC4 && marker != 0xC8 && marker != 0xCC)
                            return (Be16(d, i + 7), Be16(d, i + 5), "JPEG");
                        i += 2 + length;
                    }
                    return (0, 0, "JPEG");
                }
            }
            catch { }
            return (0, 0, null);
        }

        // MARK: Media, the front door

        public static readonly string[] VideoExtensions = { ".mp4", ".m4v", ".mov", ".mkv", ".avi", ".webm", ".3gp", ".wmv", ".mpg", ".mpeg", ".ts", ".m2ts" };

        /// <summary>Everything known about a media file, as the <c>media</c> section.</summary>
        public static Dictionary<string, object?>? Media(CachedBytes bytes, string path, CancellationToken token)
        {
            var m = new MediaFacts();
            var ext = Path.GetExtension(path).ToLowerInvariant();
            var head = bytes.Get(0, 64);
            string sniff = head.Length >= 12 ? Ascii(head.AsSpan(4, 4)) : "";

            // The parsers first, since they know the container; each is cheap and fails alone.
            void Try(Action parse) { try { parse(); } catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; } catch { } }

            if (StartsWith(head, "fLaC") || (StartsWith(head, "ID3") && ext == ".flac")) Try(() => Flac(bytes, m));
            else if (StartsWith(head, "OggS")) Try(() => Ogg(bytes, m));
            else if (sniff is "ftyp" or "moov" or "mdat" or "wide" or "free" or "skip") Try(() => Mp4(bytes, m, path, token));
            else if (head.Length >= 4 && head[0] == 0x1A && head[1] == 0x45 && head[2] == 0xDF && head[3] == 0xA3) Try(() => Mkv(bytes, m, token));
            else if (StartsWith(head, "ID3") || ext == ".mp3" || (head.Length > 2 && head[0] == 0xFF && (head[1] & 0xE0) == 0xE0)) Try(() => Mp3(bytes, m));

            // Media Foundation for the streams, where the file's own header did not already say.
            Try(() => MediaFoundation(bytes, m, token));

            if (m.Container == null && m.Audio.Count == 0 && m.Video.Count == 0 && m.Tags.Count == 0) return null;

            if (m.Duration is > 0 && m.OverallBitrate == null)
                m.OverallBitrate = (long)Math.Round(bytes.Length * 8 / m.Duration.Value);
            if (m.Bitrate == null)
            {
                long sum = m.Audio.Concat(m.Video).Select(s => s.TryGetValue("bitrate", out var b) && b is long l ? l : 0).Sum();
                m.Bitrate = sum > 0 ? sum : m.OverallBitrate;
            }

            var result = new Dictionary<string, object?>();
            if (m.Container != null) result["container"] = m.Container;
            if (m.Duration is > 0) result["durationSeconds"] = Math.Round(m.Duration.Value, 3);
            if (m.Bitrate is > 0) result["bitrate"] = m.Bitrate;
            if (m.OverallBitrate is > 0) result["overallBitrate"] = m.OverallBitrate;
            if (m.Audio.Count > 0) result["audio"] = m.Audio.Where(a => a.Count > 0).ToList();
            if (m.Video.Count > 0) result["video"] = m.Video.Where(v => v.Count > 0).ToList();
            if (m.Tags.Count > 0) result["tags"] = m.Tags.ToList();
            if (m.Chapters.Count > 0) result["chapters"] = m.Chapters.OrderBy(c => (double)c["startSeconds"]!).ToList();
            if (m.Extra.Count > 0) result["extra"] = m.Extra;
            return result;
        }

        private static bool StartsWith(byte[] b, string ascii) =>
            b.Length >= ascii.Length && Ascii(b.AsSpan(0, ascii.Length)) == ascii;

        private static void Set(Dictionary<string, object?> d, string key, object? value)
        {
            if (value == null || (value is string s && s.Length == 0) || d.ContainsKey(key)) return;
            d[key] = value;
        }

        // MARK: FLAC

        private static void Flac(CachedBytes b, MediaFacts m)
        {
            long at = 0;
            var head = b.Get(0, 10);
            if (StartsWith(head, "ID3")) at = 10 + Syncsafe(head, 6);
            if (Ascii(b.Get(at, 4)) != "fLaC") return;
            m.Container = "FLAC";
            at += 4;
            var audio = m.AudioStream(0);
            audio["codec"] = "FLAC";
            audio["lossless"] = true;
            var pending = new Dictionary<string, string>();
            for (int blocks = 0; blocks < 64 && at + 4 <= b.Length; blocks++)
            {
                var h = b.Get(at, 4);
                if (h.Length < 4) break;
                bool last = (h[0] & 0x80) != 0;
                int type = h[0] & 0x7F, length = h[1] << 16 | h[2] << 8 | h[3];
                at += 4;
                if (type == 0 && length >= 34)
                {
                    var s = b.Get(at, 34);
                    int rate = s[10] << 12 | s[11] << 4 | s[12] >> 4;
                    int channels = ((s[12] >> 1) & 7) + 1;
                    int bits = ((s[12] & 1) << 4 | s[13] >> 4) + 1;
                    long samples = (long)(s[13] & 0xF) << 32 | Be32(s, 14);
                    audio["sampleRate"] = rate;
                    audio["channels"] = channels;
                    audio["channelLayout"] = Layout(channels);
                    audio["bitsPerSample"] = bits;
                    if (samples > 0 && rate > 0) m.Duration = samples / (double)rate;
                    m.Say("Total samples", samples.ToString("N0", CultureInfo.InvariantCulture));
                    m.Say("Block size", $"{Be16(s, 0)} to {Be16(s, 2)} samples");
                    var md5 = Convert.ToHexString(s.AsSpan(18, 16)).ToLowerInvariant();
                    m.Say("MD5", md5 == new string('0', 32) ? "not set" : md5);
                }
                else if (type == 4) VorbisComments(b.GetLarge(at, length), 0, m, pending);
                else if (type == 6) FlacPicture(b.GetLarge(at, Math.Min(length, 4096)), m);
                at += length;
                if (last) break;
            }
            m.Tags.FinishNumbers(pending);
            if (m.Duration is > 0) audio["bitrate"] = (long)Math.Round((b.Length - 0) * 8 / m.Duration.Value);
        }

        /// <summary>A FLAC PICTURE block (or its base64 in a Vorbis comment): only the header and the first bytes matter.</summary>
        private static void FlacPicture(ReadOnlySpan<byte> p, MediaFacts m)
        {
            try
            {
                int i = 4;
                int mimeLength = (int)Be32(p, i); i += 4;
                var mime = Ascii(p.Slice(i, mimeLength)); i += mimeLength;
                int descLength = (int)Be32(p, i); i += 4 + descLength;
                int w = (int)Be32(p, i), h = (int)Be32(p, i + 4); i += 16;
                i += 4;
                var (pw, ph, format) = i < p.Length ? ImageSize(p[i..]) : (0, 0, null);
                if (pw == 0) { pw = w; ph = h; }
                format ??= mime.Replace("image/", "").ToUpperInvariant();
                m.Tags.Add("Cover art", pw > 0 ? $"yes, {pw}×{ph} {format}" : $"yes, {format}");
            }
            catch { }
        }

        private static void VorbisComments(ReadOnlySpan<byte> c, int at, MediaFacts m, Dictionary<string, string> pending)
        {
            try
            {
                int vendor = (int)Le32(c, at); at += 4;
                m.Say("Vendor", Encoding.UTF8.GetString(c.Slice(at, vendor)));
                at += vendor;
                int count = (int)Le32(c, at); at += 4;
                var chapters = new SortedDictionary<string, (string? Time, string? Name)>(StringComparer.Ordinal);
                for (int k = 0; k < count && at + 4 <= c.Length; k++)
                {
                    int length = (int)Le32(c, at); at += 4;
                    if (length < 0 || at + length > c.Length) break;
                    var entry = Encoding.UTF8.GetString(c.Slice(at, length));
                    at += length;
                    int eq = entry.IndexOf('=');
                    if (eq <= 0) continue;
                    string name = entry[..eq], value = entry[(eq + 1)..];
                    if (name.Equals("METADATA_BLOCK_PICTURE", StringComparison.OrdinalIgnoreCase))
                    {
                        try { FlacPicture(Convert.FromBase64String(value.Length > 8000 ? value[..8000] : value), m); } catch { }
                        continue;
                    }
                    if (name.StartsWith("CHAPTER", StringComparison.OrdinalIgnoreCase) && name.Length >= 10)
                    {
                        var key = name.Substring(7, 3);
                        chapters.TryGetValue(key, out var ch);
                        if (name.Length == 10) ch.Time = value; else if (name[10..].Equals("NAME", StringComparison.OrdinalIgnoreCase)) ch.Name = value;
                        chapters[key] = ch;
                        continue;
                    }
                    m.Tags.AddFieldName(name, value, pending);
                }
                foreach (var (_, ch) in chapters)
                    if (ch.Time != null && TimeSpan.TryParse(ch.Time, CultureInfo.InvariantCulture, out var t))
                        m.Chapter(ch.Name ?? "", t.TotalSeconds);
            }
            catch { }
        }

        // MARK: Ogg (Opus, Vorbis)

        /// <summary>The first packets of an Ogg stream, reassembled across pages.</summary>
        private static List<byte[]> OggPackets(CachedBytes b, int want, int most)
        {
            var packets = new List<byte[]>();
            var current = new MemoryStream();
            long at = 0;
            while (packets.Count < want && at + 27 <= b.Length && current.Length < most)
            {
                var h = b.Get(at, 27);
                if (Ascii(h.AsSpan(0, 4)) != "OggS") break;
                int segments = h[26];
                var table = b.Get(at + 27, segments);
                long data = at + 27 + segments;
                foreach (var lace in table)
                {
                    if (lace > 0) current.Write(b.Get(data, lace));
                    data += lace;
                    if (lace < 255)
                    {
                        packets.Add(current.ToArray());
                        current = new MemoryStream();
                        if (packets.Count >= want) break;
                    }
                }
                at = at + 27 + segments + table.Sum(x => (long)x);
            }
            return packets;
        }

        /// <summary>The granule position of the last page, from the file's tail.</summary>
        private static long LastGranule(CachedBytes b)
        {
            int span = (int)Math.Min(b.Length, 128 * 1024);
            var tail = b.Get(b.Length - span, span);
            for (int i = tail.Length - 27; i >= 0; i--)
                if (tail[i] == 'O' && tail[i + 1] == 'g' && tail[i + 2] == 'g' && tail[i + 3] == 'S')
                    return BitConverter.ToInt64(tail, i + 6);
            return -1;
        }

        private static void Ogg(CachedBytes b, MediaFacts m)
        {
            var packets = OggPackets(b, 2, 16 << 20);
            if (packets.Count == 0) return;
            var id = packets[0];
            var audio = m.AudioStream(0);
            var pending = new Dictionary<string, string>();
            long granule = LastGranule(b);
            if (StartsWith(id, "OpusHead"))
            {
                m.Container = "Ogg Opus";
                int channels = id[9], preskip = id[10] | id[11] << 8;
                uint inputRate = Le32(id, 12);
                audio["codec"] = "Opus";
                audio["sampleRate"] = 48000;
                audio["channels"] = channels;
                audio["channelLayout"] = Layout(channels);
                audio["vbr"] = true;
                audio["lossless"] = false;
                m.Say("Pre-skip", $"{preskip} samples");
                if (inputRate > 0) m.Say("Original sample rate", $"{inputRate} Hz");
                short gain = (short)(id[16] | id[17] << 8);
                if (gain != 0) m.Say("Output gain", $"{gain / 256.0:0.##} dB");
                if (granule > preskip) m.Duration = (granule - preskip) / 48000.0;
                if (packets.Count > 1 && StartsWith(packets[1], "OpusTags")) VorbisComments(packets[1], 8, m, pending);
            }
            else if (id.Length >= 30 && id[0] == 1 && Ascii(id.AsSpan(1, 6)) == "vorbis")
            {
                m.Container = "Ogg Vorbis";
                int channels = id[11];
                uint rate = Le32(id, 12);
                int max = BitConverter.ToInt32(id, 16), nominal = BitConverter.ToInt32(id, 20), min = BitConverter.ToInt32(id, 24);
                audio["codec"] = "Vorbis";
                audio["sampleRate"] = (int)rate;
                audio["channels"] = channels;
                audio["channelLayout"] = Layout(channels);
                audio["lossless"] = false;
                if (nominal > 0) audio["bitrate"] = (long)nominal;
                audio["vbr"] = !(max > 0 && max == min && max == nominal);
                if (granule > 0 && rate > 0) m.Duration = granule / (double)rate;
                if (packets.Count > 1 && packets[1].Length > 7 && packets[1][0] == 3) VorbisComments(packets[1], 7, m, pending);
            }
            else if (StartsWith(id, "\u007fFLAC"))
            {
                m.Container = "Ogg FLAC";
                audio["codec"] = "FLAC";
                audio["lossless"] = true;
            }
            m.Tags.FinishNumbers(pending);
            if (m.Duration is > 0 && !audio.ContainsKey("bitrate")) audio["bitrate"] = (long)Math.Round(b.Length * 8 / m.Duration.Value);
        }

        // MARK: MP3

        private static int Syncsafe(ReadOnlySpan<byte> b, int at) => b[at] << 21 | b[at + 1] << 14 | b[at + 2] << 7 | b[at + 3];

        private static string Id3Text(ReadOnlySpan<byte> f)
        {
            if (f.Length == 0) return "";
            var body = f[1..];
            string s = f[0] switch
            {
                1 => body.Length >= 2 && body[0] == 0xFE && body[1] == 0xFF
                        ? Encoding.BigEndianUnicode.GetString(body[2..])
                        : Encoding.Unicode.GetString(body.Length >= 2 && body[0] == 0xFF && body[1] == 0xFE ? body[2..] : body),
                2 => Encoding.BigEndianUnicode.GetString(body),
                3 => Encoding.UTF8.GetString(body),
                _ => Encoding.Latin1.GetString(body),
            };
            return s.Replace('\0', '/').Trim('/', ' ');
        }

        /// <summary>A text terminated per its encoding, and where what follows it starts.</summary>
        private static (string Text, int Next) Id3Terminated(ReadOnlySpan<byte> b, int at, byte encoding)
        {
            bool wide = encoding is 1 or 2;
            int i = at;
            while (i + (wide ? 1 : 0) < b.Length && !(b[i] == 0 && (!wide || b[i + 1] == 0))) i += wide ? 2 : 1;
            var raw = b[at..Math.Min(i, b.Length)];
            string text = encoding switch
            {
                1 => raw.Length >= 2 && raw[0] == 0xFE ? Encoding.BigEndianUnicode.GetString(raw[2..]) : Encoding.Unicode.GetString(raw.Length >= 2 && raw[0] == 0xFF ? raw[2..] : raw),
                2 => Encoding.BigEndianUnicode.GetString(raw),
                3 => Encoding.UTF8.GetString(raw),
                _ => Encoding.Latin1.GetString(raw),
            };
            return (text, i + (wide ? 2 : 1));
        }

        private static long Id3v2(CachedBytes b, MediaFacts m, Dictionary<string, string> pending)
        {
            var h = b.Get(0, 10);
            if (!StartsWith(h, "ID3")) return 0;
            int version = h[3];
            int size = Syncsafe(h, 6);
            long end = 10 + size + ((h[5] & 0x10) != 0 ? 10 : 0);
            var tag = b.GetLarge(10, Math.Min(size, 16 << 20));
            int i = 0;
            if ((h[5] & 0x40) != 0 && version >= 3 && tag.Length >= 4) i += version == 4 ? Syncsafe(tag, 0) : (int)Be32(tag, 0) + 4;
            m.Say("ID3", $"v2.{version}");
            while (i + (version == 2 ? 6 : 10) <= tag.Length)
            {
                string id;
                int length;
                if (version == 2) { id = Ascii(tag.AsSpan(i, 3)); length = tag[i + 3] << 16 | tag[i + 4] << 8 | tag[i + 5]; i += 6; }
                else { id = Ascii(tag.AsSpan(i, 4)); length = version == 4 ? Syncsafe(tag, i + 4) : (int)Be32(tag, i + 4); i += 10; }
                if (id[0] == 0 || length <= 0 || i + length > tag.Length) break;
                var f = tag.AsSpan(i, length);
                i += length;
                switch (id)
                {
                    case "TIT2": case "TT2": m.Tags.Add("Title", Id3Text(f)); break;
                    case "TPE1": case "TP1": m.Tags.Add("Artist", Id3Text(f)); break;
                    case "TPE2": case "TP2": m.Tags.Add("Album artist", Id3Text(f)); break;
                    case "TALB": case "TAL": m.Tags.Add("Album", Id3Text(f)); break;
                    case "TCON": case "TCO": m.Tags.Add("Genre", Id3Text(f)); break;
                    case "TDRC": case "TYER": case "TYE": m.Tags.Add("Year", Id3Text(f)); break;
                    case "TRCK": case "TRK": pending["track"] = Id3Text(f); break;
                    case "TPOS": case "TPA": pending["disc"] = Id3Text(f); break;
                    case "TCOM": case "TCM": m.Tags.Add("Composer", Id3Text(f)); break;
                    case "TPE3": case "TP3": m.Tags.Add("Conductor", Id3Text(f)); break;
                    case "TBPM": case "TBP": m.Tags.Add("BPM", Id3Text(f)); break;
                    case "TKEY": case "TKE": m.Tags.Add("Key", Id3Text(f)); break;
                    case "TSRC": case "TRC": m.Tags.Add("ISRC", Id3Text(f)); break;
                    case "TPUB": case "TPB": m.Tags.Add("Publisher", Id3Text(f)); break;
                    case "TCOP": case "TCR": m.Tags.Add("Copyright", Id3Text(f)); break;
                    case "TSSE": case "TSS": case "TENC": case "TEN": m.Tags.Add("Encoder", Id3Text(f)); break;
                    case "COMM": case "COM": case "USLT": case "ULT":
                        if (f.Length > 4)
                        {
                            var (_, next) = Id3Terminated(f, 4, f[0]);
                            var text = next < f.Length ? Id3Terminated(f, next, f[0]).Text : "";
                            m.Tags.Add(id is "USLT" or "ULT" ? "Lyrics" : "Comment", text);
                        }
                        break;
                    case "TXXX": case "TXX":
                        if (f.Length > 1)
                        {
                            var (name, next) = Id3Terminated(f, 1, f[0]);
                            var value = next < f.Length ? Id3Terminated(f, next, f[0]).Text : "";
                            m.Tags.AddFieldName(name, value, pending);
                        }
                        break;
                    case "POPM": case "POP":
                        {
                            int z = f.IndexOf((byte)0);
                            if (z >= 0 && z + 1 < f.Length) m.Tags.Add("Rating", $"{f[z + 1]} of 255");
                        }
                        break;
                    case "APIC": case "PIC":
                        if (f.Length > 4)
                        {
                            string? mime;
                            int next;
                            if (version == 2) { mime = "image/" + Ascii(f.Slice(1, 3)).ToLowerInvariant(); next = 4; }
                            else { var t = Id3Terminated(f, 1, 0); mime = t.Text; next = t.Next; }
                            next += 1;
                            next = Id3Terminated(f, next, f[0]).Next;
                            if (next < f.Length) m.Tags.Add("Cover art", Picture(f[next..], mime));
                        }
                        break;
                    default:
                        if (id[0] == 'T' && id != "TXXX") m.Tags.Add(id, Id3Text(f));
                        break;
                }
            }
            return end;
        }

        private static readonly int[,] Mp3Bitrates =
        {
            { 0, 32, 64, 96, 128, 160, 192, 224, 256, 288, 320, 352, 384, 416, 448 },   // MPEG1 layer I
            { 0, 32, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320, 384 },      // MPEG1 layer II
            { 0, 32, 40, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320 },       // MPEG1 layer III
            { 0, 32, 48, 56, 64, 80, 96, 112, 128, 144, 160, 176, 192, 224, 256 },      // MPEG2 layer I
            { 0, 8, 16, 24, 32, 40, 48, 56, 64, 80, 96, 112, 128, 144, 160 },           // MPEG2 layers II, III
        };

        private static void Mp3(CachedBytes b, MediaFacts m)
        {
            var pending = new Dictionary<string, string>();
            long at = Id3v2(b, m, pending);
            var scan = b.Get(at, 64 * 1024);
            int s = -1;
            for (int i = 0; i + 4 < scan.Length; i++)
                if (scan[i] == 0xFF && (scan[i + 1] & 0xE0) == 0xE0 && ((scan[i + 1] >> 1) & 3) != 0 && (scan[i + 2] >> 4) is not (0 or 15) && ((scan[i + 2] >> 2) & 3) != 3)
                { s = i; break; }
            if (s < 0) { m.Tags.FinishNumbers(pending); return; }
            var f = scan.AsSpan(s);
            int versionBits = (f[1] >> 3) & 3, layerBits = (f[1] >> 1) & 3;
            bool mpeg1 = versionBits == 3;
            string version = versionBits switch { 3 => "MPEG-1", 2 => "MPEG-2", _ => "MPEG-2.5" };
            int layer = 4 - layerBits;
            int[] rates = versionBits switch { 3 => new[] { 44100, 48000, 32000 }, 2 => new[] { 22050, 24000, 16000 }, _ => new[] { 11025, 12000, 8000 } };
            int rate = rates[(f[2] >> 2) & 3];
            int row = mpeg1 ? layer - 1 : (layer == 1 ? 3 : 4);
            int kbps = Mp3Bitrates[row, f[2] >> 4];
            int mode = f[3] >> 6;
            int channels = mode == 3 ? 1 : 2;
            int samplesPerFrame = layer == 1 ? 384 : layer == 2 || mpeg1 ? 1152 : 576;
            m.Container = "MPEG audio";
            var audio = m.AudioStream(0);
            audio["codec"] = layer == 3 ? "MP3" : $"MPEG layer {layer}";
            audio["codecProfile"] = $"{version} layer {new[] { "", "I", "II", "III" }[layer]}";
            audio["sampleRate"] = rate;
            audio["channels"] = channels;
            audio["channelLayout"] = mode switch { 0 => "stereo", 1 => "joint stereo", 2 => "dual channel", _ => "mono" };
            audio["lossless"] = false;

            int side = mpeg1 ? (channels == 1 ? 17 : 32) : (channels == 1 ? 9 : 17);
            int x = 4 + side;
            long frames = 0;
            bool vbr = false;
            if (f.Length > x + 8 && (Ascii(f.Slice(x, 4)) is "Xing" or "Info"))
            {
                vbr = Ascii(f.Slice(x, 4)) == "Xing";
                uint flags = Be32(f, x + 4);
                int p = x + 8;
                if ((flags & 1) != 0) { frames = Be32(f, p); p += 4; }
                if ((flags & 2) != 0) p += 4;
                if ((flags & 4) != 0) p += 100;
                if ((flags & 8) != 0) p += 4;
                m.Say("Header", vbr ? "Xing (variable bitrate)" : "Info (constant bitrate)");
                if (f.Length > p + 24)
                {
                    var encoder = Ascii(f.Slice(p, 9)).TrimEnd('\0', ' ');
                    if (encoder.StartsWith("LAME", StringComparison.Ordinal) || encoder.StartsWith("Lavc", StringComparison.Ordinal) || encoder.StartsWith("Lavf", StringComparison.Ordinal))
                    {
                        m.Say("Encoder", encoder);
                        int method = f[p + 9] & 0x0F;
                        m.Say("LAME mode", method switch { 1 or 8 => "CBR", 2 or 9 => "ABR", 3 => "VBR (old)", 4 or 5 or 6 => "VBR", _ => $"method {method}" });
                        int lowpass = f[p + 10];
                        if (lowpass > 0) m.Say("Lowpass", $"{lowpass * 100} Hz");
                        int delay = f[p + 21] << 4 | f[p + 22] >> 4, padding = (f[p + 22] & 0xF) << 8 | f[p + 23];
                        m.Say("Encoder delay", $"{delay} samples");
                        m.Say("Padding", $"{padding} samples");
                        if (method is 2 or 9 && f[p + 20] > 0) m.Say("ABR bitrate", $"{f[p + 20]} kbps");
                        m.Tags.Add("Encoder", encoder);
                    }
                }
            }
            else if (f.Length > 36 + 18 && Ascii(f.Slice(36, 4)) == "VBRI")
            {
                vbr = true;
                frames = Be32(f, 36 + 14);
                m.Say("Header", "VBRI (Fraunhofer, variable bitrate)");
            }
            audio["vbr"] = vbr;

            long audioBytes = b.Length - at - s;
            var tail = b.Get(b.Length - 128, 128);
            if (StartsWith(tail, "TAG"))
            {
                audioBytes -= 128;
                m.Tags.Add("Title", Encoding.Latin1.GetString(tail, 3, 30).TrimEnd('\0', ' '));
                m.Tags.Add("Artist", Encoding.Latin1.GetString(tail, 33, 30).TrimEnd('\0', ' '));
                m.Tags.Add("Album", Encoding.Latin1.GetString(tail, 63, 30).TrimEnd('\0', ' '));
                m.Tags.Add("Year", Encoding.Latin1.GetString(tail, 93, 4).TrimEnd('\0', ' '));
            }
            if (frames > 0 && rate > 0) m.Duration = frames * (double)samplesPerFrame / rate;
            else if (kbps > 0) m.Duration = audioBytes * 8.0 / (kbps * 1000);
            audio["bitrate"] = vbr && m.Duration is > 0 ? (long)Math.Round(audioBytes * 8 / m.Duration.Value) : kbps * 1000L;
            if (frames > 0) m.Say("Frames", frames.ToString("N0", CultureInfo.InvariantCulture));
            m.Tags.FinishNumbers(pending);
        }

        // MARK: MP4

        private readonly record struct Box(string Type, long Start, long Size, int Header);

        private static List<Box> Boxes(ReadOnlySpan<byte> d, int from, int to)
        {
            var list = new List<Box>();
            int i = from;
            while (i + 8 <= to)
            {
                long size = Be32(d, i);
                // Latin-1, not ASCII: iTunes item names start with a copyright sign.
                var type = Encoding.Latin1.GetString(d.Slice(i + 4, 4));
                int header = 8;
                if (size == 1 && i + 16 <= to) { size = (long)Be64(d, i + 8); header = 16; }
                else if (size == 0) size = to - i;
                if (size < header || i + size > to) break;
                list.Add(new Box(type, i, size, header));
                i += (int)size;
            }
            return list;
        }

        private static Box? Find(ReadOnlySpan<byte> d, Box parent, string type, int skip = 0)
        {
            foreach (var box in Boxes(d, (int)parent.Start + parent.Header + skip, (int)(parent.Start + parent.Size)))
                if (box.Type == type) return box;
            return null;
        }

        private static Box? Path4(ReadOnlySpan<byte> d, Box root, params string[] types)
        {
            Box? at = root;
            foreach (var t in types)
            {
                if (at == null) return null;
                at = Find(d, at.Value, t, at.Value.Type == "meta" ? 4 : 0);
            }
            return at;
        }

        private static string Lang(int packed) =>
            packed == 0 || packed == 0x7FFF ? "" : new string(new[] { (char)(((packed >> 10) & 31) + 0x60), (char)(((packed >> 5) & 31) + 0x60), (char)((packed & 31) + 0x60) });

        private static void Mp4(CachedBytes b, MediaFacts m, string path, CancellationToken token)
        {
            // The top level, a header at a time, skipping mdat without reading it.
            long at = 0;
            Box? moov = null;
            long moovAt = 0;
            string brand = "";
            for (int n = 0; n < 64 && at + 8 <= b.Length; n++)
            {
                var h = b.Get(at, 16);
                if (h.Length < 8) break;
                long size = Be32(h, 0);
                var type = Ascii(h.AsSpan(4, 4));
                if (size == 1 && h.Length >= 16) size = (long)Be64(h, 8);
                else if (size == 0) size = b.Length - at;
                if (size < 8) break;
                if (type == "ftyp") brand = Ascii(b.Get(at + 8, 4));
                if (type == "moov") { moovAt = at; moov = new Box(type, 0, size, 8); break; }
                at += size;
            }
            // An audiobook is an M4B by its brand or, as ffmpeg and most taggers write them, by its name alone.
            if (brand.Trim() == "M4A" && Path.GetExtension(path).Equals(".m4b", StringComparison.OrdinalIgnoreCase)) brand = "M4B ";
            m.Container = brand.Trim() switch
            {
                "M4B" => "MPEG-4 audiobook (M4B)", "M4A" => "MPEG-4 audio (M4A)", "M4V" => "MPEG-4 video (M4V)",
                "qt" => "QuickTime", "3gp4" or "3gp5" or "3gp6" => "3GPP", _ => "MPEG-4",
            };
            if (moov == null || moov.Value.Size > 64 << 20) return;
            var d = b.GetLarge(moovAt, (int)moov.Value.Size);
            if (d.Length < moov.Value.Size) return;
            var root = new Box("moov", 0, d.Length, 8);

            var mvhd = Find(d, root, "mvhd");
            if (mvhd != null)
            {
                int p = (int)mvhd.Value.Start + 8;
                bool v1 = d[p] == 1;
                long scale = Be32(d, p + (v1 ? 20 : 12));
                long duration = v1 ? (long)Be64(d, p + 24) : Be32(d, p + 16);
                if (scale > 0) m.Duration = duration / (double)scale;
            }

            var chapterTracks = new HashSet<uint>();
            var tracks = new List<(uint Id, Box Trak)>();
            foreach (var trak in Boxes(d, 8, d.Length).Where(x => x.Type == "trak"))
            {
                var tkhd = Find(d, trak, "tkhd");
                uint id = 0;
                if (tkhd != null) { int p = (int)tkhd.Value.Start + 8; id = Be32(d, p + (d[p] == 1 ? 20 : 12)); }
                tracks.Add((id, trak));
                var chap = Path4(d, trak, "tref", "chap");
                if (chap != null)
                    for (long q = chap.Value.Start + 8; q + 4 <= chap.Value.Start + chap.Value.Size; q += 4) chapterTracks.Add(Be32(d, (int)q));
            }

            foreach (var (id, trak) in tracks)
            {
                token.ThrowIfCancellationRequested();
                var hdlr = Path4(d, trak, "mdia", "hdlr");
                string handler = hdlr != null ? Ascii(d.AsSpan((int)hdlr.Value.Start + 16, 4)) : "";
                var mdhd = Path4(d, trak, "mdia", "mdhd");
                long scale = 0;
                string language = "";
                if (mdhd != null)
                {
                    int p = (int)mdhd.Value.Start + 8;
                    bool v1 = d[p] == 1;
                    scale = Be32(d, p + (v1 ? 20 : 12));
                    language = Lang(Be16(d, p + (v1 ? 32 : 20)));
                }
                var stbl = Path4(d, trak, "mdia", "minf", "stbl");
                var stsd = stbl != null ? Find(d, stbl.Value, "stsd") : null;
                if (stsd == null) continue;
                int e = (int)stsd.Value.Start + 16;
                var fourcc = Ascii(d.AsSpan(e + 4, 4));

                if (chapterTracks.Contains(id) && handler is "text" or "sbtl")
                {
                    QuickTimeChapters(b, d, stbl!.Value, scale, m, token);
                    continue;
                }
                if (handler == "soun")
                {
                    var a = m.AudioStream(m.Audio.Count);
                    a["codec"] = fourcc switch { "mp4a" => "AAC", "alac" => "ALAC", "ac-3" => "AC-3", "ec-3" => "E-AC-3", "Opus" => "Opus", "fLaC" => "FLAC", ".mp3" => "MP3", _ => fourcc.Trim() };
                    int channels = Be16(d, e + 24);
                    a["channels"] = channels;
                    a["channelLayout"] = Layout(channels);
                    a["bitsPerSample"] = Be16(d, e + 26);
                    a["sampleRate"] = (int)(Be32(d, e + 32) >> 16);
                    a["lossless"] = fourcc is "alac" or "fLaC";
                    if (language.Length > 0 && language != "und") a["language"] = language;
                    if (fourcc == "alac")
                    {
                        var box = Boxes(d, e + 36, e + (int)Be32(d, e)).FirstOrDefault(x => x.Type == "alac");
                        if (box.Size >= 36) { int q = (int)box.Start + 12; a["bitsPerSample"] = (int)d[q + 5]; a["sampleRate"] = (int)Be32(d, q + 20); }
                    }
                    if (fourcc == "mp4a") Esds(d, e + 36, e + (int)Be32(d, e), a);
                }
                else if (handler == "vide")
                {
                    var v = m.VideoStream(m.Video.Count);
                    v["codec"] = fourcc switch { "avc1" or "avc3" => "H.264", "hvc1" or "hev1" => "HEVC (H.265)", "av01" => "AV1", "vp09" => "VP9", "mp4v" => "MPEG-4 Visual", "jpeg" or "mjpa" => "Motion JPEG", "apcn" or "apch" or "apcs" or "apco" or "ap4h" => "ProRes", _ => fourcc.Trim() };
                    v["width"] = Be16(d, e + 32);
                    v["height"] = Be16(d, e + 34);
                    var tkhd = Find(d, trak, "tkhd");
                    if (tkhd != null)
                    {
                        int p = (int)tkhd.Value.Start + 8;
                        int matrix = p + (d[p] == 1 ? 52 : 40);
                        int a = (int)Be32(d, matrix), bb = (int)Be32(d, matrix + 4);
                        int rotation = (a, bb) switch { (0, 0x10000) => 90, (-0x10000, 0) => 180, (0, -0x10000) => 270, _ => 0 };
                        if (rotation != 0) v["rotation"] = rotation;
                    }
                    var stts = Find(d, stbl!.Value, "stts");
                    if (stts != null && scale > 0 && Be32(d, (int)stts.Value.Start + 12) > 0)
                    {
                        long delta = Be32(d, (int)stts.Value.Start + 20);
                        if (delta > 0) v["frameRate"] = Math.Round(scale / (double)delta, 3);
                    }
                    if (fourcc is "hvc1" or "hev1" or "av01" or "vp09")
                    {
                        var colr = Boxes(d, e + 86, e + (int)Be32(d, e)).FirstOrDefault(x => x.Type == "colr");
                        if (colr.Size >= 18 && Ascii(d.AsSpan((int)colr.Start + 8, 4)) == "nclx")
                        {
                            int transfer = Be16(d, (int)colr.Start + 14);
                            v["hdr"] = transfer is 16 or 18;
                        }
                    }
                }
            }

            // iTunes metadata, and Nero chapters.
            // There can be more than one udta: Windows writes its own, and a tagger adds another.
            var udtas = Boxes(d, 8, d.Length).Where(x => x.Type == "udta").ToList();
            Box? chpl = null;
            var ilsts = new List<Box>();
            foreach (var u in udtas)
            {
                if (Path4(d, u, "meta", "ilst") is { } found) ilsts.Add(found);
                chpl ??= Find(d, u, "chpl");
            }
            if (Path4(d, root, "meta", "ilst") is { } top) ilsts.Add(top);
            foreach (var ilst in ilsts) Ilst(d, ilst, m);
            if (chpl != null && m.Chapters.Count == 0)
            {
                // Version and flags; version 1 then has four reserved bytes; then a one-byte count.
                int q = (int)chpl.Value.Start + 8 + 4;
                int end = (int)(chpl.Value.Start + chpl.Value.Size);
                if (d[(int)chpl.Value.Start + 8] == 1) q += 4;
                int count = d[q]; q += 1;
                for (int k = 0; k < count && q + 9 <= end; k++)
                {
                    long time = (long)Be64(d, q);
                    int length = d[q + 8];
                    if (q + 9 + length > end) break;
                    m.Chapter(Encoding.UTF8.GetString(d, q + 9, length), time / 10_000_000.0);
                    q += 9 + length;
                }
            }
        }

        /// <summary>The AAC profile and bitrate from an esds box among a sample entry's children.</summary>
        private static void Esds(byte[] d, int from, int to, Dictionary<string, object?> a)
        {
            var esds = Boxes(d, from, Math.Min(to, d.Length)).FirstOrDefault(x => x.Type == "esds");
            if (esds.Size == 0) return;
            int i = (int)esds.Start + 12, end = (int)(esds.Start + esds.Size);
            int Length(ref int p) { int n = 0; for (int k = 0; k < 4; k++) { byte c = d[p++]; n = n << 7 | (c & 0x7F); if ((c & 0x80) == 0) break; } return n; }
            while (i + 2 < end)
            {
                byte tag = d[i++];
                int length = Length(ref i);
                if (tag == 3) { i += 3; continue; }
                if (tag == 4)
                {
                    byte objectType = d[i];
                    long avg = Be32(d, i + 9), max = Be32(d, i + 5);
                    if (avg > 0) a["bitrate"] = avg;
                    a["vbr"] = max > 0 && max != avg;
                    if (objectType == 0x6B || objectType == 0x69) a["codec"] = "MP3";
                    i += 13;
                    continue;
                }
                if (tag == 5 && length >= 2)
                {
                    int aot = d[i] >> 3;
                    if (aot == 31) aot = 32 + ((d[i] & 7) << 3 | d[i + 1] >> 5);
                    // Implicitly signalled SBR is not in the config at all; Media Foundation's profile level may say more.
                    a["codecProfile"] = aot switch { 1 => "AAC Main", 2 => "AAC-LC", 3 => "AAC-SSR", 4 => "AAC-LTP", 5 => "HE-AAC", 29 => "HE-AAC v2", 23 => "AAC-LD", 39 => "AAC-ELD", 42 => "xHE-AAC (USAC)", _ => $"AAC (object type {aot})" };
                    return;
                }
                i += length;
            }
        }

        private static void Ilst(byte[] d, Box ilst, MediaFacts m)
        {
            var pending = new Dictionary<string, string>();
            foreach (var item in Boxes(d, (int)ilst.Start + 8, (int)(ilst.Start + ilst.Size)))
            {
                string name = item.Type, mean = "", free = "";
                var boxes = Boxes(d, (int)item.Start + 8, (int)(item.Start + item.Size));
                foreach (var sub in boxes)
                {
                    if (sub.Type == "name") free = Encoding.UTF8.GetString(d, (int)sub.Start + 12, (int)sub.Size - 12);
                    if (sub.Type == "mean") mean = Encoding.UTF8.GetString(d, (int)sub.Start + 12, (int)sub.Size - 12);
                }
                var data = boxes.FirstOrDefault(x => x.Type == "data");
                if (data.Size < 16) continue;
                int p = (int)data.Start + 16, length = (int)data.Size - 16;
                uint kind = Be32(d, (int)data.Start + 8) & 0xFFFFFF;
                string Text() => Encoding.UTF8.GetString(d, p, length);
                switch (name)
                {
                    case "©nam": m.Tags.Add("Title", Text()); break;
                    case "©ART": m.Tags.Add("Artist", Text()); break;
                    case "aART": m.Tags.Add("Album artist", Text()); break;
                    case "©alb": m.Tags.Add("Album", Text()); break;
                    case "©gen": m.Tags.Add("Genre", Text()); break;
                    case "©day": m.Tags.Add("Year", Text()); break;
                    case "©wrt": m.Tags.Add("Composer", Text()); break;
                    case "©cmt": m.Tags.Add("Comment", Text()); break;
                    case "©lyr": m.Tags.Add("Lyrics", Text()); break;
                    case "©too": m.Tags.Add("Encoder", Text()); break;
                    case "cprt": m.Tags.Add("Copyright", Text()); break;
                    case "tmpo": if (length >= 2) m.Tags.Add("BPM", Be16(d, p).ToString()); break;
                    case "trkn": if (length >= 6) m.Tags.Numbered("Track", Be16(d, p + 2).ToString(), Be16(d, p + 4).ToString()); break;
                    case "disk": if (length >= 6) m.Tags.Numbered("Disc", Be16(d, p + 2).ToString(), Be16(d, p + 4).ToString()); break;
                    case "covr": m.Tags.Add("Cover art", Picture(d.AsSpan(p, Math.Min(length, 4096)), kind == 14 ? "image/png" : "image/jpeg")); break;
                    case "----": if (free.Length > 0 && kind == 1) m.Tags.AddFieldName(free, Text(), pending); break;
                    default: if (kind == 1 && length > 0) m.Tags.Add(name.Replace("©", ""), Text()); break;
                }
            }
            m.Tags.FinishNumbers(pending);
        }

        /// <summary>A QuickTime chapter track: its samples are the titles, its time-to-sample table the starts.</summary>
        private static void QuickTimeChapters(CachedBytes b, byte[] d, Box stbl, long scale, MediaFacts m, CancellationToken token)
        {
            if (scale <= 0) return;
            var stts = Find(d, stbl, "stts");
            var stsz = Find(d, stbl, "stsz");
            var stsc = Find(d, stbl, "stsc");
            var stco = Find(d, stbl, "stco") ?? Find(d, stbl, "co64");
            if (stts == null || stsz == null || stco == null) return;

            var starts = new List<double>();
            int p = (int)stts.Value.Start + 12;
            uint entries = Be32(d, p); p += 4;
            long t = 0;
            for (uint k = 0; k < entries && starts.Count < 1000; k++, p += 8)
            {
                uint count = Be32(d, p), delta = Be32(d, p + 4);
                for (uint c = 0; c < count && starts.Count < 1000; c++) { starts.Add(t / (double)scale); t += delta; }
            }

            int z = (int)stsz.Value.Start + 12;
            uint fixedSize = Be32(d, z), sampleCount = Be32(d, z + 4);
            long Size(int i) => fixedSize != 0 ? fixedSize : Be32(d, z + 8 + 4 * i);

            bool wide = stco.Value.Type == "co64";
            int o = (int)stco.Value.Start + 12;
            uint chunks = Be32(d, o); o += 4;
            long Chunk(int i) => wide ? (long)Be64(d, o + 8 * i) : Be32(d, o + 4 * i);

            var perChunk = new List<(uint First, uint Samples)>();
            if (stsc != null)
            {
                int s = (int)stsc.Value.Start + 12;
                uint n = Be32(d, s); s += 4;
                for (uint k = 0; k < n; k++, s += 12) perChunk.Add((Be32(d, s), Be32(d, s + 4)));
            }
            if (perChunk.Count == 0) perChunk.Add((1, 1));

            int sample = 0;
            for (int c = 0; c < chunks && sample < sampleCount && sample < starts.Count; c++)
            {
                uint inThis = perChunk.Last(x => x.First <= c + 1).Samples;
                long offset = Chunk(c);
                for (uint k = 0; k < inThis && sample < sampleCount && sample < starts.Count; k++)
                {
                    token.ThrowIfCancellationRequested();
                    long size = Size(sample);
                    var text = b.Get(offset, (int)Math.Min(size, 1024));
                    if (text.Length >= 2)
                    {
                        int length = Math.Min(Be16(text, 0), text.Length - 2);
                        bool utf16 = length >= 2 && text[2] == 0xFE && text[3] == 0xFF;
                        m.Chapter(utf16 ? Encoding.BigEndianUnicode.GetString(text, 4, length - 2) : Encoding.UTF8.GetString(text, 2, length), starts[sample]);
                    }
                    offset += size;
                    sample++;
                }
            }
        }

        // MARK: Matroska

        private static (long Value, int Length) Vint(ReadOnlySpan<byte> d, int at, bool keepMarker)
        {
            byte first = d[at];
            int length = 1;
            while (length <= 8 && (first & (0x80 >> (length - 1))) == 0) length++;
            if (length > 8 || at + length > d.Length) return (-1, 1);
            long value = keepMarker ? first : first & (0xFF >> length);
            bool allOnes = (first & (0xFF >> length)) == (0xFF >> length);
            for (int k = 1; k < length; k++) { value = value << 8 | d[at + k]; if (d[at + k] != 0xFF) allOnes = false; }
            if (!keepMarker && allOnes) return (long.MaxValue, length);    // unknown size
            return (value, length);
        }

        private readonly record struct Element(long Id, long DataStart, long Size);

        private static List<Element> Elements(ReadOnlySpan<byte> d, long from, long to)
        {
            var list = new List<Element>();
            long i = from;
            while (i + 2 <= to && i < d.Length)
            {
                var (id, a) = Vint(d, (int)i, true);
                if (id < 0 || i + a >= d.Length) break;
                var (size, s) = Vint(d, (int)(i + a), false);
                if (size < 0) break;
                long start = i + a + s;
                if (size == long.MaxValue) size = to - start;
                list.Add(new Element(id, start, size));
                if (start + size > to) break;
                i = start + size;
            }
            return list;
        }

        private static ulong Uint(ReadOnlySpan<byte> d, Element e) { ulong v = 0; for (int k = 0; k < e.Size && k < 8; k++) v = v << 8 | d[(int)e.DataStart + k]; return v; }
        private static double Float(ReadOnlySpan<byte> d, Element e) => e.Size == 4
            ? BitConverter.Int32BitsToSingle((int)Uint(d, e)) : BitConverter.Int64BitsToDouble((long)Uint(d, e));
        private static string Str(ReadOnlySpan<byte> d, Element e) => Encoding.UTF8.GetString(d.Slice((int)e.DataStart, (int)Math.Min(e.Size, d.Length - e.DataStart))).TrimEnd('\0');

        private static void Mkv(CachedBytes b, MediaFacts m, CancellationToken token)
        {
            var head = b.Get(0, 4096);
            var top = Elements(head, 0, head.Length);
            if (top.Count == 0 || top[0].Id != 0x1A45DFA3) return;
            var doc = Elements(head, top[0].DataStart, top[0].DataStart + top[0].Size).FirstOrDefault(e => e.Id == 0x4282);
            string docType = doc.Size > 0 ? Str(head, doc) : "matroska";
            m.Container = docType == "webm" ? "WebM" : "Matroska";
            if (top.Count < 2 || top[1].Id != 0x18538067) return;
            long segment = top[1].DataStart;

            // The segment's children, a header at a time; the ones worth reading are fetched whole.
            var wanted = new Dictionary<long, long>();     // id -> absolute position
            long at = segment;
            for (int n = 0; n < 256 && at + 12 < b.Length; n++)
            {
                token.ThrowIfCancellationRequested();
                var h = b.Get(at, 12);
                var (id, a) = Vint(h, 0, true);
                if (id < 0) break;
                var (size, s) = Vint(h, a, false);
                if (id == 0x114D9B74 && size < 1 << 20)
                {
                    var seek = b.Get(at + a + s, (int)size);
                    foreach (var entry in Elements(seek, 0, seek.Length).Where(e => e.Id == 0x4DBB))
                    {
                        long target = 0, where = -1;
                        foreach (var f in Elements(seek, entry.DataStart, entry.DataStart + entry.Size))
                        {
                            if (f.Id == 0x53AB) target = (long)Uint(seek, f);
                            if (f.Id == 0x53AC) where = (long)Uint(seek, f);
                        }
                        if (where >= 0 && !wanted.ContainsKey(target)) wanted[target] = segment + where;
                    }
                }
                else if (id is 0x1549A966 or 0x1654AE6B or 0x1043A770 or 0x1254C367) wanted[id] = at;
                if (id == 0x1F43B675 || size == long.MaxValue) break;     // the clusters: what follows is found by SeekHead
                at += a + s + size;
            }

            byte[]? Load(long id)
            {
                if (!wanted.TryGetValue(id, out var where)) return null;
                var h = b.Get(where, 12);
                var (_, a) = Vint(h, 0, true);
                var (size, s) = Vint(h, a, false);
                if (size <= 0 || size > 16 << 20) return null;
                return b.GetLarge(where + a + s, (int)size);
            }

            double scale = 1_000_000;
            var info = Load(0x1549A966);
            if (info != null)
            {
                foreach (var e in Elements(info, 0, info.Length))
                {
                    if (e.Id == 0x2AD7B1) scale = Uint(info, e);
                    else if (e.Id == 0x4489) m.Duration = Float(info, e) * scale / 1e9;
                    else if (e.Id == 0x4D80) m.Say("Muxing application", Str(info, e));
                    else if (e.Id == 0x5741) m.Say("Writing application", Str(info, e));
                    else if (e.Id == 0x7BA9) m.Tags.Add("Title", Str(info, e));
                }
                // Duration was read before the scale when the scale came later in the element.
                foreach (var e in Elements(info, 0, info.Length)) if (e.Id == 0x4489) m.Duration = Float(info, e) * scale / 1e9;
            }

            var tracks = Load(0x1654AE6B);
            if (tracks != null)
                foreach (var entry in Elements(tracks, 0, tracks.Length).Where(e => e.Id == 0xAE))
                {
                    var fields = Elements(tracks, entry.DataStart, entry.DataStart + entry.Size);
                    ulong type = 0; string codec = "", language = "";
                    foreach (var f in fields)
                    {
                        if (f.Id == 0x83) type = Uint(tracks, f);
                        if (f.Id == 0x86) codec = Str(tracks, f);
                        if (f.Id == 0x22B59C && language.Length == 0) language = Str(tracks, f);
                        if (f.Id == 0x22B59D) language = Str(tracks, f);
                    }
                    if (type == 2)
                    {
                        var a = m.AudioStream(m.Audio.Count);
                        a["codec"] = MkvCodec(codec);
                        a["lossless"] = codec is "A_FLAC" or "A_ALAC" or "A_WAVPACK4" or "A_TTA1" || codec.StartsWith("A_PCM", StringComparison.Ordinal);
                        if (language.Length > 0 && language != "und") a["language"] = language;
                        var audio = fields.FirstOrDefault(f => f.Id == 0xE1);
                        if (audio.Size > 0)
                            foreach (var f in Elements(tracks, audio.DataStart, audio.DataStart + audio.Size))
                            {
                                if (f.Id == 0xB5) a["sampleRate"] = (int)Math.Round(Float(tracks, f));
                                if (f.Id == 0x9F) { int c = (int)Uint(tracks, f); a["channels"] = c; a["channelLayout"] = Layout(c); }
                                if (f.Id == 0x6264) a["bitsPerSample"] = (int)Uint(tracks, f);
                            }
                    }
                    else if (type == 1)
                    {
                        var v = m.VideoStream(m.Video.Count);
                        v["codec"] = MkvCodec(codec);
                        var duration = fields.FirstOrDefault(f => f.Id == 0x23E383);
                        if (duration.Size > 0) { var ns = Uint(tracks, duration); if (ns > 0) v["frameRate"] = Math.Round(1e9 / ns, 3); }
                        var video = fields.FirstOrDefault(f => f.Id == 0xE0);
                        if (video.Size > 0)
                            foreach (var f in Elements(tracks, video.DataStart, video.DataStart + video.Size))
                            {
                                if (f.Id == 0xB0) v["width"] = (int)Uint(tracks, f);
                                if (f.Id == 0xBA) v["height"] = (int)Uint(tracks, f);
                                if (f.Id == 0x55B0)
                                    foreach (var c in Elements(tracks, f.DataStart, f.DataStart + f.Size))
                                        if (c.Id == 0x55BA) v["hdr"] = Uint(tracks, c) is 16 or 18;
                            }
                    }
                }

            var chapters = Load(0x1043A770);
            if (chapters != null)
            {
                void Atoms(long from, long to)
                {
                    foreach (var e in Elements(chapters, from, to))
                    {
                        if (e.Id == 0x45B9) { Atoms(e.DataStart, e.DataStart + e.Size); continue; }
                        if (e.Id != 0xB6) continue;
                        double start = 0; string title = "";
                        foreach (var f in Elements(chapters, e.DataStart, e.DataStart + e.Size))
                        {
                            if (f.Id == 0x91) start = Uint(chapters, f) / 1e9;
                            if (f.Id == 0x80 && title.Length == 0)
                                foreach (var g in Elements(chapters, f.DataStart, f.DataStart + f.Size))
                                    if (g.Id == 0x85) title = Str(chapters, g);
                        }
                        m.Chapter(title, start);
                    }
                }
                Atoms(0, chapters.Length);
            }

            var tags = Load(0x1254C367);
            if (tags != null)
            {
                var pending = new Dictionary<string, string>();
                void Simple(long from, long to)
                {
                    foreach (var e in Elements(tags, from, to))
                    {
                        if (e.Id == 0x7373) { Simple(e.DataStart, e.DataStart + e.Size); continue; }
                        if (e.Id != 0x67C8) continue;
                        string name = "", value = "";
                        foreach (var f in Elements(tags, e.DataStart, e.DataStart + e.Size))
                        {
                            if (f.Id == 0x45A3) name = Str(tags, f);
                            if (f.Id == 0x4487) value = Str(tags, f);
                        }
                        if (name.Length > 0 && value.Length > 0 && name != "DURATION") m.Tags.AddFieldName(name, value, pending);
                        else if (name == "BPS" || name == "DURATION") { }
                    }
                }
                Simple(0, tags.Length);
                m.Tags.FinishNumbers(pending);
            }
        }

        private static string MkvCodec(string id) => id switch
        {
            "A_AAC" or "A_AAC/MPEG4/LC" or "A_AAC/MPEG2/LC" => "AAC", "A_OPUS" => "Opus", "A_VORBIS" => "Vorbis", "A_FLAC" => "FLAC",
            "A_MPEG/L3" => "MP3", "A_AC3" => "AC-3", "A_EAC3" => "E-AC-3", "A_DTS" => "DTS", "A_TRUEHD" => "TrueHD", "A_ALAC" => "ALAC",
            "V_MPEG4/ISO/AVC" => "H.264", "V_MPEGH/ISO/HEVC" => "HEVC (H.265)", "V_AV1" => "AV1", "V_VP9" => "VP9", "V_VP8" => "VP8",
            "V_MPEG4/ISO/ASP" => "MPEG-4 Visual", "V_MPEG2" => "MPEG-2", "V_MJPEG" => "Motion JPEG",
            _ when id.StartsWith("A_PCM", StringComparison.Ordinal) => "PCM",
            _ => id,
        };

        // MARK: Media Foundation

        [ComImport, Guid("70AE66F2-C809-4E4F-8915-BDCB406B7993"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMFSourceReader
        {
            [PreserveSig] int GetStreamSelection(uint streamIndex, out int selected);
            [PreserveSig] int SetStreamSelection(uint streamIndex, int selected);
            [PreserveSig] int GetNativeMediaType(uint streamIndex, uint typeIndex, out IntPtr type);
            [PreserveSig] int GetCurrentMediaType(uint streamIndex, out IntPtr type);
            [PreserveSig] int SetCurrentMediaType(uint streamIndex, IntPtr reserved, IntPtr type);
            [PreserveSig] int SetCurrentPosition(ref Guid format, IntPtr position);
            [PreserveSig] int ReadSample(uint streamIndex, uint flags, out uint actual, out uint streamFlags, out long timestamp, out IntPtr sample);
            [PreserveSig] int Flush(uint streamIndex);
            [PreserveSig] int GetServiceForStream(uint streamIndex, ref Guid service, ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object instance);
            [PreserveSig] int GetPresentationAttribute(uint streamIndex, ref Guid attribute, out PropVariant value);
        }

        /// <summary>The IMFAttributes getters, counted out: 4 GetUINT32, 5 GetUINT64, 7 GetGUID.</summary>
        [ComImport, Guid("44AE0FA8-EA31-4109-8D2E-4CAE4997C555"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMFMediaType
        {
            void R00(); void R01(); void R02(); void R03();
            [PreserveSig] int GetUINT32(ref Guid key, out uint value);
            [PreserveSig] int GetUINT64(ref Guid key, out ulong value);
            void R06();
            [PreserveSig] int GetGUID(ref Guid key, out Guid value);
        }

        [StructLayout(LayoutKind.Explicit)]
        private struct PropVariant
        {
            [FieldOffset(0)] public ushort Type;
            [FieldOffset(8)] public long Long;
        }

        [ComImport, Guid("AD4C1B00-4BF7-422F-9175-756693D9130D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMFByteStream { }

        [DllImport("mfplat.dll")] private static extern int MFStartup(int version, int flags);
        [DllImport("mfplat.dll")] private static extern int MFCreateMFByteStreamOnStream(System.Runtime.InteropServices.ComTypes.IStream stream, out IMFByteStream bytes);
        [DllImport("mfreadwrite.dll")] private static extern int MFCreateSourceReaderFromByteStream(IMFByteStream stream, IntPtr attributes, out IMFSourceReader reader);
        [DllImport("ole32.dll")] private static extern int PropVariantClear(ref PropVariant value);

        private static Guid MT_MAJOR = new("48EBA18E-F8C9-4687-BF11-0A74C9F96A8F");
        private static Guid MT_SUBTYPE = new("F7E34C9A-42E8-4714-B74B-CB29D72C35E5");
        private static Guid MT_CHANNELS = new("37E48BF5-645E-4C5B-89DE-ADA9E29B696A");
        private static Guid MT_RATE = new("5FAEEAE7-0290-4C31-9E8A-C534F68D9DBA");
        private static Guid MT_BITS = new("F2DEB57F-40FA-4764-AA33-ED4F2D1FF669");
        private static Guid MT_AVG_BYTES = new("1AAB75C8-CFEF-451C-AB95-AC034B8E1731");
        private static Guid MT_FRAME_SIZE = new("1652C33D-D6B2-4012-B834-72030849A37D");
        private static Guid MT_FRAME_RATE = new("C459A2E8-3D2C-4E44-B132-FEE5156C7BB0");
        private static Guid MT_AVG_BITRATE = new("20332624-FB0D-4D9E-BD0D-CBF6786C102E");
        private static Guid MT_ROTATION = new("C380465D-2271-428C-9B83-ECEA3B4A85C1");
        private static Guid MT_TRANSFER = new("5FB0FCE9-BE5C-4935-A811-EC838F8EED93");
        private static Guid MT_AAC_PROFILE = new("7632F0E6-9538-4D61-ACDA-EA29C8C14456");
        private static Guid PD_DURATION = new("6C990D33-BB8E-477A-8598-0D5D96FCD88A");
        private static Guid PD_AUDIO_BITRATE = new("6C990D35-BB8E-477A-8598-0D5D96FCD88A");
        private static Guid PD_VIDEO_BITRATE = new("6C990D36-BB8E-477A-8598-0D5D96FCD88A");
        private static readonly Guid Audio = new("73647561-0000-0010-8000-00AA00389B71");
        private static readonly Guid Video = new("73646976-0000-0010-8000-00AA00389B71");
        private const uint MediaSource = 0xFFFFFFFF;

        private static string AudioCodec(Guid subtype) => D1(subtype) switch
        {
            0x0001 => "PCM", 0x0003 => "PCM (float)", 0x0055 => "MP3", 0x0050 => "MPEG audio", 0x1610 or 0x1600 => "AAC", 0xF1AC => "FLAC",
            0x6C61 => "ALAC", 0x704F => "Opus", 0x0160 or 0x0161 => "WMA", 0x0162 => "WMA Pro", 0x0163 => "WMA Lossless",
            0x0092 => "AC-3", 0x2000 => "AC-3", 0x2001 => "DTS", 0x0011 => "IMA ADPCM", 0x0002 => "ADPCM", 0x0006 => "A-law", 0x0007 => "µ-law",
            _ => subtype == new Guid("E06D802C-DB46-11CF-B4D1-00805F6CBBEA") ? "AC-3" : subtype == new Guid("A7FB87AF-2D02-42FB-A4D4-05CD93843BDD") ? "E-AC-3" : Fourcc(D1(subtype)),
        };

        private static string VideoCodec(Guid subtype)
        {
            var f = Fourcc(D1(subtype));
            return f.ToUpperInvariant() switch
            {
                "H264" or "AVC1" => "H.264", "HEVC" or "HVC1" or "H265" => "HEVC (H.265)", "AV01" => "AV1", "VP90" => "VP9", "VP80" => "VP8",
                "MP4V" or "MP4S" or "M4S2" => "MPEG-4 Visual", "WVC1" => "VC-1", "WMV3" => "WMV 9", "MJPG" => "Motion JPEG", "MPG2" => "MPEG-2",
                _ => f,
            };
        }

        /// <summary>A media subtype's first field: the WAVE format tag or FOURCC it was made from.</summary>
        private static uint D1(Guid g) => BitConverter.ToUInt32(g.ToByteArray(), 0);

        private static string Fourcc(uint v)
        {
            var s = Encoding.ASCII.GetString(BitConverter.GetBytes(v)).TrimEnd('\0', ' ');
            return s.All(c => c >= 32 && c < 127) && s.Length > 0 ? s : $"0x{v:X}";
        }

        private static void MediaFoundation(CachedBytes b, MediaFacts m, CancellationToken token)
        {
            MFStartup(0x00020070, 0);
            using var stream = new RangeStream(b);
            if (MFCreateMFByteStreamOnStream(stream, out var bytes) != 0 || bytes == null) return;
            IMFSourceReader? reader;
            try { if (MFCreateSourceReaderFromByteStream(bytes, IntPtr.Zero, out reader) != 0 || reader == null) return; }
            finally { try { Marshal.ReleaseComObject(bytes); } catch { } }
            try
            {
                if (reader.GetPresentationAttribute(MediaSource, ref PD_DURATION, out var pv) == 0)
                {
                    if (m.Duration is not > 0 && pv.Long > 0) m.Duration = pv.Long / 1e7;
                    PropVariantClear(ref pv);
                }
                long audioBitrate = 0, videoBitrate = 0;
                if (reader.GetPresentationAttribute(MediaSource, ref PD_AUDIO_BITRATE, out pv) == 0) { audioBitrate = (uint)pv.Long; PropVariantClear(ref pv); }
                if (reader.GetPresentationAttribute(MediaSource, ref PD_VIDEO_BITRATE, out pv) == 0) { videoBitrate = (uint)pv.Long; PropVariantClear(ref pv); }

                int audioIndex = 0, videoIndex = 0;
                for (uint i = 0; i < 32; i++)
                {
                    token.ThrowIfCancellationRequested();
                    if (reader.GetNativeMediaType(i, 0, out var ptr) != 0 || ptr == IntPtr.Zero) break;
                    var type = (IMFMediaType)Marshal.GetObjectForIUnknown(ptr);
                    Marshal.Release(ptr);
                    try
                    {
                        type.GetGUID(ref MT_MAJOR, out var major);
                        type.GetGUID(ref MT_SUBTYPE, out var subtype);
                        uint U(Guid key) => type.GetUINT32(ref key, out var v) == 0 ? v : 0;
                        if (major == Audio)
                        {
                            var a = m.AudioStream(audioIndex++);
                            Set(a, "codec", AudioCodec(subtype));
                            if (U(MT_RATE) is var rate and > 0) Set(a, "sampleRate", (int)rate);
                            if (U(MT_CHANNELS) is var ch and > 0) { Set(a, "channels", (int)ch); Set(a, "channelLayout", Layout((int)ch)); }
                            if (U(MT_BITS) is var bits and > 0 && D1(subtype) is 1 or 3 or 0xF1AC or 0x6C61 or 0x0163) Set(a, "bitsPerSample", (int)bits);
                            long rateBits = U(MT_AVG_BYTES) * 8L;
                            if (rateBits <= 0 && audioIndex == 1) rateBits = audioBitrate;
                            if (rateBits > 0) Set(a, "bitrate", rateBits);
                            Set(a, "lossless", D1(subtype) is 1 or 3 or 0xF1AC or 0x6C61 or 0x0163);
                            if (D1(subtype) is 0x1610 or 0x1600 && U(MT_AAC_PROFILE) is var profile and > 0)
                            {
                                // Implicit SBR is invisible in the file's config; the decoder's profile is the one that knows.
                                if (profile is 0x2C or 0x2D or 0x2E or 0x2F) a["codecProfile"] = "HE-AAC";
                                else if (profile is 0x30 or 0x31 or 0x32 or 0x33) a["codecProfile"] = "HE-AAC v2";
                                else Set(a, "codecProfile", "AAC-LC");
                            }
                            m.Container ??= "Media Foundation";
                        }
                        else if (major == Video)
                        {
                            var v = m.VideoStream(videoIndex++);
                            Set(v, "codec", VideoCodec(subtype));
                            if (type.GetUINT64(ref MT_FRAME_SIZE, out var size) == 0) { Set(v, "width", (int)(size >> 32)); Set(v, "height", (int)(size & 0xFFFFFFFF)); }
                            if (type.GetUINT64(ref MT_FRAME_RATE, out var fr) == 0 && (fr & 0xFFFFFFFF) > 0)
                                Set(v, "frameRate", Math.Round((fr >> 32) / (double)(fr & 0xFFFFFFFF), 3));
                            long vb = U(MT_AVG_BITRATE);
                            if (vb <= 0 && videoIndex == 1) vb = videoBitrate;
                            if (vb > 0) Set(v, "bitrate", vb);
                            if (U(MT_ROTATION) is var rotation and > 0) Set(v, "rotation", (int)rotation);
                            if (type.GetUINT32(ref MT_TRANSFER, out var transfer) == 0) Set(v, "hdr", transfer is 15 or 16);
                        }
                    }
                    finally { try { Marshal.ReleaseComObject(type); } catch { } }
                }
            }
            finally { try { Marshal.ReleaseComObject(reader); } catch { } }
        }

        // MARK: Text

        public static readonly string[] TextExtensions =
        {
            ".txt", ".md", ".markdown", ".csv", ".tsv", ".log", ".ini", ".cfg", ".conf", ".json", ".xml", ".yaml", ".yml", ".toml",
            ".html", ".htm", ".css", ".js", ".ts", ".tsx", ".jsx", ".cs", ".c", ".h", ".cpp", ".hpp", ".py", ".rb", ".go", ".rs",
            ".java", ".kt", ".swift", ".m", ".sh", ".bat", ".cmd", ".ps1", ".psm1", ".sql", ".srt", ".vtt", ".lrc", ".cue", ".nfo",
            ".rtf", ".tex", ".php", ".lua", ".pl", ".r", ".vb", ".fs", ".csproj", ".sln", ".props", ".targets", ".gitignore", ".editorconfig",
            ".eel", ".jsfx", ".rpp", ".vws", ".lst", ".m3u", ".m3u8", ".pls", ".reg", ".inf",
        };

        public static string? Language(string extension) => extension.ToLowerInvariant() switch
        {
            ".cs" => "C#", ".c" or ".h" => "C", ".cpp" or ".hpp" or ".cc" => "C++", ".py" => "Python", ".js" or ".jsx" => "JavaScript",
            ".ts" or ".tsx" => "TypeScript", ".java" => "Java", ".kt" => "Kotlin", ".swift" => "Swift", ".go" => "Go", ".rs" => "Rust",
            ".rb" => "Ruby", ".php" => "PHP", ".lua" => "Lua", ".pl" => "Perl", ".r" => "R", ".vb" => "Visual Basic", ".fs" => "F#",
            ".sh" => "Shell", ".bat" or ".cmd" => "Batch", ".ps1" or ".psm1" => "PowerShell", ".sql" => "SQL", ".html" or ".htm" => "HTML",
            ".css" => "CSS", ".json" => "JSON", ".xml" or ".csproj" or ".props" or ".targets" => "XML", ".yaml" or ".yml" => "YAML",
            ".toml" => "TOML", ".md" or ".markdown" => "Markdown", ".tex" => "LaTeX", ".m" => "Objective-C or MATLAB",
            ".eel" or ".jsfx" => "EEL2 (JSFX)", _ => null,
        };

        /// <summary>Whether the start of a file looks like text: a BOM, or no zero bytes and few controls.</summary>
        public static bool LooksLikeText(ReadOnlySpan<byte> head)
        {
            if (head.Length == 0) return false;
            if (head.Length >= 2 && ((head[0] == 0xFF && head[1] == 0xFE) || (head[0] == 0xFE && head[1] == 0xFF))) return true;
            if (head.Length >= 3 && head[0] == 0xEF && head[1] == 0xBB && head[2] == 0xBF) return true;
            int controls = 0;
            foreach (var c in head)
            {
                if (c == 0) return false;
                if (c < 9 || (c > 13 && c < 32 && c != 27)) controls++;
            }
            return controls * 100 < head.Length;
        }

        /// <summary>
        /// Counts a text file as it streams past, never holding it: the encoding from its BOM, or from whether its
        /// bytes are valid UTF-8, then lines, words, characters and the rest, decoded a block at a time.
        /// </summary>
        public static Dictionary<string, object?> TextStats(Func<Stream> open, string extension, CancellationToken token)
        {
            // The encoding: a BOM, else a whole pass checking UTF-8 validity (bytes only, cheap), else 1252.
            string encodingName;
            Encoding encoding;
            int bomLength = 0;
            using (var probe = open())
            {
                var head = new byte[4];
                int n = probe.Read(head, 0, 4);
                if (n >= 3 && head[0] == 0xEF && head[1] == 0xBB && head[2] == 0xBF) { encodingName = "UTF-8 with BOM"; encoding = new UTF8Encoding(false); bomLength = 3; }
                else if (n >= 2 && head[0] == 0xFF && head[1] == 0xFE) { encodingName = "UTF-16 LE"; encoding = new UnicodeEncoding(false, false); bomLength = 2; }
                else if (n >= 2 && head[0] == 0xFE && head[1] == 0xFF) { encodingName = "UTF-16 BE"; encoding = new UnicodeEncoding(true, false); bomLength = 2; }
                else
                {
                    probe.Position = 0;
                    var (valid, ascii) = CheckUtf8(probe, token);
                    if (ascii) { encodingName = "ASCII"; encoding = new UTF8Encoding(false); }
                    else if (valid) { encodingName = "UTF-8"; encoding = new UTF8Encoding(false); }
                    else { encodingName = "Windows-1252"; encoding = CodePages1252(); }
                }
            }

            long lines = 0, words = 0, characters = 0, nonSpace = 0, paragraphs = 0, blank = 0, nonAscii = 0, tabs = 0;
            long crlf = 0, lf = 0, cr = 0, longest = 0, current = 0;
            bool inWord = false, lineHasText = false, inParagraph = false, pendingCr = false, any = false;

            void EndLine()
            {
                lines++;
                longest = Math.Max(longest, current);
                if (lineHasText) { if (!inParagraph) paragraphs++; inParagraph = true; }
                else { blank++; inParagraph = false; }
                current = 0;
                lineHasText = false;
            }

            using (var stream = open())
            {
                stream.Position = bomLength;
                using var reader = new StreamReader(stream, encoding, detectEncodingFromByteOrderMarks: false, bufferSize: 64 * 1024);
                var buffer = new char[64 * 1024];
                int got;
                while ((got = reader.Read(buffer, 0, buffer.Length)) > 0)
                {
                    token.ThrowIfCancellationRequested();
                    for (int i = 0; i < got; i++)
                    {
                        char c = buffer[i];
                        any = true;
                        if (pendingCr)
                        {
                            pendingCr = false;
                            if (c == '\n') { crlf++; continue; }
                            cr++;
                        }
                        if (c == '\r') { EndLine(); pendingCr = true; inWord = false; continue; }
                        if (c == '\n') { EndLine(); lf++; inWord = false; continue; }
                        if (char.IsLowSurrogate(c)) continue;       // one character, counted at its high half
                        characters++;
                        current++;
                        if (c == '\t') tabs++;
                        if (c > 127) nonAscii++;
                        if (char.IsWhiteSpace(c)) inWord = false;
                        else
                        {
                            nonSpace++;
                            lineHasText = true;
                            if (!inWord) { words++; inWord = true; }
                        }
                    }
                }
            }
            if (pendingCr) cr++;
            if (any && (current > 0 || lineHasText)) EndLine();
            else if (any && lines == 0) EndLine();

            int kinds = (crlf > 0 ? 1 : 0) + (lf > 0 ? 1 : 0) + (cr > 0 ? 1 : 0);
            var result = new Dictionary<string, object?>
            {
                ["encoding"] = encodingName,
                ["bom"] = bomLength > 0,
                ["lineEndings"] = kinds == 0 ? "none" : kinds > 1 ? "mixed" : crlf > 0 ? "CRLF" : lf > 0 ? "LF" : "CR",
                ["lines"] = lines,
                ["words"] = words,
                ["characters"] = characters,
                ["charactersNoSpaces"] = nonSpace,
                ["paragraphs"] = paragraphs,
                ["blankLines"] = blank,
                ["longestLine"] = longest,
                ["nonAscii"] = nonAscii,
                ["tabs"] = tabs,
            };
            var language = Language(extension);
            if (language != null) result["language"] = language;
            return result;
        }

        private static Encoding CodePages1252()
        {
            try
            {
                Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
                return Encoding.GetEncoding(1252);
            }
            catch { return Encoding.Latin1; }
        }

        /// <summary>Whether a stream is valid UTF-8 all through, and whether it is plain ASCII.</summary>
        private static (bool Valid, bool Ascii) CheckUtf8(Stream s, CancellationToken token)
        {
            var buffer = new byte[64 * 1024];
            int need = 0;
            bool ascii = true;
            int got;
            while ((got = s.Read(buffer, 0, buffer.Length)) > 0)
            {
                token.ThrowIfCancellationRequested();
                for (int i = 0; i < got; i++)
                {
                    byte c = buffer[i];
                    if (need > 0)
                    {
                        if ((c & 0xC0) != 0x80) return (false, false);
                        need--;
                        continue;
                    }
                    if (c < 0x80) continue;
                    ascii = false;
                    if ((c & 0xE0) == 0xC0 && c >= 0xC2) need = 1;
                    else if ((c & 0xF0) == 0xE0) need = 2;
                    else if ((c & 0xF8) == 0xF0 && c <= 0xF4) need = 3;
                    else return (false, false);
                }
            }
            return (need == 0, ascii);
        }

        // MARK: Archives

        /// <summary>A zip's entries from its central directory, read from the tail alone.</summary>
        public static Dictionary<string, object?>? Zip(CachedBytes b)
        {
            int span = (int)Math.Min(b.Length, 70 * 1024);
            var tail = b.Get(b.Length - span, span);
            int e = -1;
            for (int i = tail.Length - 22; i >= 0; i--)
                if (tail[i] == 'P' && tail[i + 1] == 'K' && tail[i + 2] == 5 && tail[i + 3] == 6) { e = i; break; }
            if (e < 0) return null;
            long entries = BitConverter.ToUInt16(tail, e + 10);
            long cdSize = BitConverter.ToUInt32(tail, e + 12);
            long cdOffset = BitConverter.ToUInt32(tail, e + 16);
            // Zip64: the locator sits just before, pointing at the 64-bit record.
            if ((entries == 0xFFFF || cdOffset == 0xFFFFFFFF) && e >= 20 && BitConverter.ToUInt32(tail, e - 20) == 0x07064B50)
            {
                long record = (long)BitConverter.ToUInt64(tail, e - 20 + 8);
                var r = b.Get(record, 56);
                if (r.Length >= 56 && BitConverter.ToUInt32(r, 0) == 0x06064B50)
                {
                    entries = (long)BitConverter.ToUInt64(r, 32);
                    cdSize = (long)BitConverter.ToUInt64(r, 40);
                    cdOffset = (long)BitConverter.ToUInt64(r, 48);
                }
            }
            long uncompressed = 0, files = 0;
            if (cdSize > 0 && cdSize < 64 << 20)
            {
                var cd = b.GetLarge(cdOffset, (int)cdSize);
                int p = 0;
                while (p + 46 <= cd.Length && BitConverter.ToUInt32(cd, p) == 0x02014B50)
                {
                    long size = BitConverter.ToUInt32(cd, p + 24);
                    int nameLength = BitConverter.ToUInt16(cd, p + 28), extraLength = BitConverter.ToUInt16(cd, p + 30), commentLength = BitConverter.ToUInt16(cd, p + 32);
                    if (size == 0xFFFFFFFF)
                        for (int x = p + 46 + nameLength; x + 4 <= p + 46 + nameLength + extraLength; x += 4 + BitConverter.ToUInt16(cd, x + 2))
                            if (BitConverter.ToUInt16(cd, x) == 1) { size = (long)BitConverter.ToUInt64(cd, x + 4); break; }
                    bool folder = nameLength > 0 && cd[p + 46 + nameLength - 1] == '/';
                    if (!folder) { uncompressed += size; files++; }
                    p += 46 + nameLength + extraLength + commentLength;
                }
            }
            return new Dictionary<string, object?> { ["format"] = "zip", ["entries"] = entries, ["files"] = files, ["uncompressedSize"] = uncompressed };
        }
    }
}
