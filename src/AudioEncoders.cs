using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace ExplorerNative
{
    /// <summary>The five formats the converter writes.</summary>
    internal enum AudioFormatKind { Mp3, M4a, Opus, Flac, Wav }

    /// <summary>
    /// One format at one quality: what the converter dialog offers, what is
    /// remembered in Settings (by <see cref="Id"/>), and what the editor saves.
    /// </summary>
    /// <param name="Kbps">Bit rate for the lossy formats, zero for FLAC and WAV.</param>
    /// <param name="WavBits">16 for 16-bit WAV, zero for "keep the original's bit depth".</param>
    internal sealed record AudioFormatChoice(string Id, AudioFormatKind Kind, int Kbps, int WavBits)
    {
        public string Extension => AudioFormats.ExtensionOf(Kind);

        public string FormatName => AudioFormats.NameOf(Kind);

        public string QualityName => Kind switch
        {
            AudioFormatKind.Flac => "lossless",
            AudioFormatKind.Wav => WavBits == 16 ? "16-bit" : "same bit depth as the original",
            _ => $"{Kbps} kilobits per second",
        };

        /// <summary>"MP3, 192 kilobits per second": what is spoken and what the summary says.</summary>
        public string Label => $"{FormatName}, {QualityName}";
    }

    internal static class AudioFormats
    {
        public const string DefaultId = "mp3-192";

        public static readonly IReadOnlyList<AudioFormatChoice> All = new[]
        {
            new AudioFormatChoice("mp3-128", AudioFormatKind.Mp3, 128, 0),
            new AudioFormatChoice("mp3-192", AudioFormatKind.Mp3, 192, 0),
            new AudioFormatChoice("mp3-256", AudioFormatKind.Mp3, 256, 0),
            new AudioFormatChoice("mp3-320", AudioFormatKind.Mp3, 320, 0),
            new AudioFormatChoice("m4a-128", AudioFormatKind.M4a, 128, 0),
            new AudioFormatChoice("m4a-192", AudioFormatKind.M4a, 192, 0),
            new AudioFormatChoice("m4a-256", AudioFormatKind.M4a, 256, 0),
            new AudioFormatChoice("opus-64", AudioFormatKind.Opus, 64, 0),
            new AudioFormatChoice("opus-96", AudioFormatKind.Opus, 96, 0),
            new AudioFormatChoice("opus-128", AudioFormatKind.Opus, 128, 0),
            new AudioFormatChoice("opus-160", AudioFormatKind.Opus, 160, 0),
            new AudioFormatChoice("flac", AudioFormatKind.Flac, 0, 0),
            new AudioFormatChoice("wav-16", AudioFormatKind.Wav, 0, 16),
            new AudioFormatChoice("wav-keep", AudioFormatKind.Wav, 0, 0),
        };

        /// <summary>The formats in the order the dialog lists them.</summary>
        public static readonly IReadOnlyList<AudioFormatKind> Kinds =
            new[] { AudioFormatKind.Mp3, AudioFormatKind.M4a, AudioFormatKind.Opus, AudioFormatKind.Flac, AudioFormatKind.Wav };

        public static IEnumerable<AudioFormatChoice> QualitiesOf(AudioFormatKind kind) => All.Where(c => c.Kind == kind);

        /// <summary>
        /// The remembered choice, or the default for anything this version does
        /// not know (a newer build's id, a hand edit). The setting itself is not
        /// changed: an update never changes a setting.
        /// </summary>
        public static AudioFormatChoice ById(string? id) =>
            All.FirstOrDefault(c => string.Equals(c.Id, id, StringComparison.OrdinalIgnoreCase))
            ?? All.First(c => c.Id == DefaultId);

        public static string ExtensionOf(AudioFormatKind kind) => kind switch
        {
            AudioFormatKind.Mp3 => ".mp3",
            AudioFormatKind.M4a => ".m4a",
            AudioFormatKind.Opus => ".opus",
            AudioFormatKind.Flac => ".flac",
            _ => ".wav",
        };

        public static string NameOf(AudioFormatKind kind) => kind switch
        {
            AudioFormatKind.Mp3 => "MP3",
            AudioFormatKind.M4a => "M4A (AAC)",
            AudioFormatKind.Opus => "Opus",
            AudioFormatKind.Flac => "FLAC",
            _ => "WAV",
        };

        /// <summary>
        /// The format the editor saves a file of this extension in, or null when
        /// the converter cannot write that format and the editor has to ask. A
        /// lossy file is written back at the nearest offered rate at or above
        /// its own, so an edit does not cost more quality than it must.
        /// </summary>
        public static AudioFormatChoice? SameFormatAs(string extension, int? sourceKbps)
        {
            var ext = (extension ?? "").ToLowerInvariant();
            AudioFormatKind? kind = ext switch
            {
                ".mp3" => AudioFormatKind.Mp3,
                ".m4a" => AudioFormatKind.M4a,
                ".opus" => AudioFormatKind.Opus,
                ".flac" => AudioFormatKind.Flac,
                ".wav" => AudioFormatKind.Wav,
                _ => null,
            };
            if (kind == null) return null;

            if (kind == AudioFormatKind.Flac) return ById("flac");
            if (kind == AudioFormatKind.Wav) return ById("wav-keep");

            var offered = QualitiesOf(kind.Value).ToList();
            if (sourceKbps is not > 0)
                return offered[Math.Min(2, offered.Count - 1)];

            return offered.FirstOrDefault(c => c.Kbps >= sourceKbps.Value) ?? offered[^1];
        }
    }

    /// <summary>Something an encoder will not do, said as a sentence for the summary.</summary>
    internal sealed class AudioEncodeException : Exception
    {
        public AudioEncodeException(string message, bool typeRefused = false) : base(message)
        {
            TypeRefused = typeRefused;
        }

        /// <summary>The encoder exists and refused the shape it was offered.</summary>
        public bool TypeRefused { get; }
    }

    /// <summary>Interleaved float in, a file out.</summary>
    internal interface IAudioEncoder : IDisposable
    {
        void Write(float[] interleaved, int frames);

        /// <summary>Writes whatever is buffered and closes the file properly. Not called on a cancel.</summary>
        void Finish();

        /// <summary>
        /// True when the tags could not go inside the file as it was written and
        /// have to be added afterwards through the Windows property system.
        /// </summary>
        bool TagsAfterwards { get; }
    }

    /// <summary>Which encoder writes which format, and the sample shapes each accepts.</summary>
    internal static class AudioEncoders
    {
        /// <summary>
        /// The rate and channel count to hand the encoder for a source of this
        /// shape. MP3 and AAC take only the usual rates; Opus is always 48k;
        /// FLAC and WAV take what they are given.
        /// </summary>
        public static (int Rate, int Channels) ShapeFor(AudioFormatKind kind, int rate, int channels)
        {
            int Usual(params int[] accepted) =>
                accepted.Contains(rate) ? rate : rate > 0 && rate % 11025 == 0 ? 44100 : 48000;

            return kind switch
            {
                AudioFormatKind.Mp3 => (Usual(32000, 44100, 48000), Math.Clamp(channels, 1, 2)),
                AudioFormatKind.M4a => (Usual(44100, 48000), channels is 1 or 2 or 6 ? channels : 2),
                AudioFormatKind.Opus => (48000, Math.Clamp(channels, 1, 2)),
                AudioFormatKind.Flac => (rate, Math.Clamp(channels, 1, 8)),
                _ => (rate, Math.Max(1, channels)),
            };
        }

        /// <summary>The bits per sample a lossless target keeps from a source that stored <paramref name="sourceBits"/>.</summary>
        public static int LosslessBits(AudioFormatChoice choice, int sourceBits)
        {
            if (choice.Kind == AudioFormatKind.Wav && choice.WavBits == 16) return 16;
            if (choice.Kind == AudioFormatKind.Flac) return sourceBits > 16 ? 24 : 16;
            // WAV keeping the original: float sources stay float.
            return sourceBits switch { > 24 => 32, > 16 => 24, _ => 16 };
        }

        private static readonly Dictionary<AudioFormatKind, string?> _why = new();

        /// <summary>
        /// Null when this PC can write the format, otherwise the reason in words.
        /// Asks Media Foundation which encoders are installed, once per run; call
        /// it on a worker. FLAC is always available, with our own encoder when
        /// Windows has none.
        /// </summary>
        public static string? WhyUnavailable(AudioFormatKind kind)
        {
            if (kind is AudioFormatKind.Opus or AudioFormatKind.Wav or AudioFormatKind.Flac) return null;

            lock (_why)
            {
                if (_why.TryGetValue(kind, out var known)) return known;
                string? why = MediaFoundationEncoder.HasEncoder(kind)
                    ? null
                    : $"Windows on this PC has no {AudioFormats.NameOf(kind)} encoder";
                _why[kind] = why;
                return why;
            }
        }

        /// <summary>
        /// Opens the encoder for <paramref name="choice"/> writing to
        /// <paramref name="path"/>. Anything it had to change is added to
        /// <paramref name="notes"/> ("M4A at 256 is not offered; used 192").
        /// Throws <see cref="AudioEncodeException"/> with the reason in words.
        /// </summary>
        public static IAudioEncoder Create(AudioFormatChoice choice, string path, int rate, int channels,
            int sourceBits, AudioTagSet? tags, ICollection<string> notes)
        {
            switch (choice.Kind)
            {
                case AudioFormatKind.Wav:
                    return new WavEncoder(path, rate, channels, LosslessBits(choice, sourceBits), tags);

                case AudioFormatKind.Opus:
                    return new OpusFileEncoder(path, channels, choice.Kbps, tags);

                case AudioFormatKind.Flac:
                {
                    int bits = LosslessBits(choice, sourceBits);
                    if (MediaFoundationEncoder.HasEncoder(AudioFormatKind.Flac))
                    {
                        try { return new MediaFoundationEncoder(path, AudioFormatKind.Flac, rate, channels, 0, bits); }
                        catch (AudioEncodeException) { TryDelete(path); }
                    }
                    return new FlacEncoder(path, rate, channels, bits, tags);
                }

                default:
                {
                    var why = WhyUnavailable(choice.Kind);
                    if (why != null) throw new AudioEncodeException(why);

                    try
                    {
                        return new MediaFoundationEncoder(path, choice.Kind, rate, channels, choice.Kbps, 16);
                    }
                    catch (AudioEncodeException ex) when (ex.TypeRefused)
                    {
                        // The encoder is there and will not take this rate. The
                        // next one down that it will, rather than nothing.
                        TryDelete(path);
                        foreach (var lower in AudioFormats.QualitiesOf(choice.Kind)
                                     .Where(c => c.Kbps < choice.Kbps).OrderByDescending(c => c.Kbps))
                        {
                            try
                            {
                                var encoder = new MediaFoundationEncoder(path, choice.Kind, rate, channels, lower.Kbps, 16);
                                lock (notes)
                                {
                                    var note = $"Windows' {choice.FormatName} encoder does not offer {choice.Kbps} kilobits, " +
                                               $"so {lower.Kbps} was used";
                                    if (!notes.Contains(note)) notes.Add(note);
                                }
                                return encoder;
                            }
                            catch (AudioEncodeException again) when (again.TypeRefused) { TryDelete(path); }
                        }
                        throw;
                    }
                }
            }
        }

        internal static void TryDelete(string path)
        {
            var exact = NameRules.ExactPath(path);
            try { if (File.Exists(exact)) File.Delete(exact); } catch { }
        }

        /// <summary>One float as a signed integer of <paramref name="bits"/>, scaled so 16-bit audio round-trips exactly.</summary>
        internal static int ToInt(float sample, int bits)
        {
            double full = 1L << (bits - 1);
            double v = Math.Round(sample * full);
            if (v >= full) return (int)(full - 1);
            if (v < -full) return (int)-full;
            return (int)v;
        }
    }

    // ---------------------------------------------------------------- WAV

    /// <summary>
    /// Plain RIFF WAVE: 16 or 24-bit PCM, or 32-bit float. Tags go in a LIST
    /// INFO chunk for other programs, and are written again afterwards through
    /// the property system, because Windows' own WAV handler does not read
    /// INFO (measured: a file with one read back no tags at all).
    /// </summary>
    internal sealed class WavEncoder : IAudioEncoder
    {
        private readonly FileStream _file;
        private readonly int _channels, _bits;
        private readonly long _dataSizeAt;
        private long _dataBytes;
        private byte[] _bytes = Array.Empty<byte>();

        public bool TagsAfterwards => true;

        public WavEncoder(string path, int rate, int channels, int bits, AudioTagSet? tags)
        {
            _channels = channels;
            _bits = bits;
            _file = new FileStream(NameRules.ExactPath(path), FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read, 1 << 16);

            using var w = new BinaryWriter(_file, Encoding.ASCII, leaveOpen: true);
            int blockAlign = channels * bits / 8;
            w.Write("RIFF"u8);
            w.Write(0u);                                // patched in Finish
            w.Write("WAVE"u8);
            w.Write("fmt "u8);
            w.Write(16);
            w.Write((short)(bits == 32 ? 3 : 1));       // 3 = IEEE float
            w.Write((short)channels);
            w.Write(rate);
            w.Write(rate * blockAlign);
            w.Write((short)blockAlign);
            w.Write((short)bits);

            var info = InfoChunk(tags);
            if (info.Length > 0) w.Write(info);

            w.Write("data"u8);
            _dataSizeAt = _file.Position;
            w.Write(0u);
        }

        /// <summary>LIST/INFO with the tags Windows reads from a WAV, or nothing.</summary>
        private static byte[] InfoChunk(AudioTagSet? tags)
        {
            if (tags == null || tags.IsEmpty) return Array.Empty<byte>();

            var fields = new List<(string Id, string Text)>();
            if (tags.Title != null) fields.Add(("INAM", tags.Title));
            if (tags.Artist != null) fields.Add(("IART", tags.Artist));
            if (tags.Album != null) fields.Add(("IPRD", tags.Album));
            if (tags.Genre != null) fields.Add(("IGNR", tags.Genre));
            if (tags.Year != null) fields.Add(("ICRD", tags.Year.Value.ToString()));
            if (tags.Track != null) fields.Add(("ITRK", tags.Track.Value.ToString()));

            using var body = new MemoryStream();
            body.Write("INFO"u8);
            foreach (var (id, text) in fields)
            {
                var value = Encoding.UTF8.GetBytes(text + "\0");
                body.Write(Encoding.ASCII.GetBytes(id));
                body.Write(BitConverter.GetBytes(value.Length));
                body.Write(value);
                if ((value.Length & 1) == 1) body.WriteByte(0);
            }

            using var chunk = new MemoryStream();
            chunk.Write("LIST"u8);
            chunk.Write(BitConverter.GetBytes((int)body.Length));
            body.Position = 0;
            body.CopyTo(chunk);
            return chunk.ToArray();
        }

        public void Write(float[] interleaved, int frames)
        {
            int samples = frames * _channels;
            int bytesPer = _bits / 8;
            int need = samples * bytesPer;
            if (_bytes.Length < need) _bytes = new byte[need];

            int at = 0;
            for (int i = 0; i < samples; i++)
            {
                float s = interleaved[i];
                if (_bits == 32)
                {
                    BitConverter.TryWriteBytes(_bytes.AsSpan(at, 4), s);
                    at += 4;
                }
                else
                {
                    int v = AudioEncoders.ToInt(s, _bits);
                    _bytes[at++] = (byte)v;
                    _bytes[at++] = (byte)(v >> 8);
                    if (_bits == 24) _bytes[at++] = (byte)(v >> 16);
                }
            }

            if (_dataBytes + need > uint.MaxValue - 1024)
                throw new AudioEncodeException("it is too long for a WAV file, which stops at 4 gigabytes");

            _file.Write(_bytes, 0, need);
            _dataBytes += need;
        }

        public void Finish()
        {
            if ((_dataBytes & 1) == 1) _file.WriteByte(0);
            long end = _file.Position;

            _file.Position = 4;
            _file.Write(BitConverter.GetBytes((uint)(end - 8)));
            _file.Position = _dataSizeAt;
            _file.Write(BitConverter.GetBytes((uint)_dataBytes));
            _file.Flush(true);
        }

        public void Dispose()
        {
            try { _file.Dispose(); } catch { }
        }
    }

    // ---------------------------------------------------------------- Opus

    /// <summary>
    /// Ogg Opus through Concentus, which the player already uses to decode it.
    /// Always 48k; the tags go into the OpusTags header as Vorbis comments.
    /// </summary>
    internal sealed class OpusFileEncoder : IAudioEncoder
    {
        private readonly FileStream _file;
        private readonly Concentus.Oggfile.OpusOggWriteStream _writer;
        private readonly int _channels;
        private float[] _copy = Array.Empty<float>();

        public bool TagsAfterwards => false;

        public OpusFileEncoder(string path, int channels, int kbps, AudioTagSet? tags)
        {
            _channels = channels;
            _file = new FileStream(NameRules.ExactPath(path), FileMode.CreateNew, FileAccess.Write, FileShare.Read, 1 << 16);

            try
            {
                var encoder = Concentus.OpusCodecFactory.CreateEncoder(
                    48000, channels, Concentus.Enums.OpusApplication.OPUS_APPLICATION_AUDIO, null);
                encoder.Bitrate = kbps * 1000;
                encoder.Complexity = 10;

                var head = new Concentus.Oggfile.OpusTags();
                if (tags != null)
                    foreach (var (key, value) in tags.VorbisFields())
                        head.Fields[key] = value;

                _writer = new Concentus.Oggfile.OpusOggWriteStream(encoder, _file, head, 48000, 5, true);
            }
            catch
            {
                _file.Dispose();
                throw;
            }
        }

        public void Write(float[] interleaved, int frames)
        {
            // Copied, because the writer may keep what it is handed until it has
            // a whole packet, and the caller reuses its buffer.
            int count = frames * _channels;
            if (_copy.Length < count) _copy = new float[count];
            Array.Copy(interleaved, _copy, count);
            _writer.WriteSamples(_copy, 0, count);
        }

        public void Finish()
        {
            _writer.Finish();
            _file.Flush(true);
        }

        public void Dispose()
        {
            try { _file.Dispose(); } catch { }
        }
    }

    // ---------------------------------------------------------------- FLAC

    /// <summary>
    /// A small FLAC encoder of our own, for a Windows with no FLAC encoder:
    /// fixed blocks of 4096, the five fixed predictors, partitioned Rice
    /// residuals, and a Vorbis comment block for the tags. It compresses a
    /// little less than the reference encoder and is exactly as lossless.
    /// </summary>
    internal sealed class FlacEncoder : IAudioEncoder
    {
        private const int BlockSize = 4096;
        private const int MaxPartitionOrder = 8;

        private readonly FileStream _file;
        private readonly int _rate, _channels, _bits;
        private readonly int[][] _block;
        private int _filled;
        private long _totalSamples;
        private long _frameNumber;
        private int _minFrame = int.MaxValue, _maxFrame;
        private readonly IncrementalHash _md5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
        private byte[] _md5Bytes = Array.Empty<byte>();
        private readonly BitWriter _out = new();
        private readonly long _streamInfoAt;
        private int[] _residual = new int[BlockSize];
        private int[] _bestResidual = new int[BlockSize];

        public bool TagsAfterwards => false;

        public FlacEncoder(string path, int rate, int channels, int bits, AudioTagSet? tags)
        {
            if (rate <= 0 || rate > 655350) throw new AudioEncodeException($"FLAC cannot hold audio at {rate} hertz");
            if (channels < 1 || channels > 8) throw new AudioEncodeException($"FLAC cannot hold {channels} channels");

            _rate = rate;
            _channels = channels;
            _bits = bits;
            _block = new int[channels][];
            for (int c = 0; c < channels; c++) _block[c] = new int[BlockSize];

            _file = new FileStream(NameRules.ExactPath(path), FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read, 1 << 16);
            _file.Write("fLaC"u8);

            // STREAMINFO, written now with the counts unknown and again at the end.
            _file.WriteByte(0x00);                      // not last, type 0
            _file.Write(new byte[] { 0, 0, 34 });
            _streamInfoAt = _file.Position;
            _file.Write(StreamInfo());

            // VORBIS_COMMENT, last.
            var comment = VorbisComment(tags);
            _file.WriteByte(0x80 | 4);
            _file.Write(new[] { (byte)(comment.Length >> 16), (byte)(comment.Length >> 8), (byte)comment.Length });
            _file.Write(comment);
        }

        private byte[] StreamInfo()
        {
            var w = new BitWriter();
            w.Write(BlockSize, 16);
            w.Write(BlockSize, 16);
            w.Write(_maxFrame == 0 ? 0u : (uint)_minFrame, 24);
            w.Write((uint)_maxFrame, 24);
            w.Write((uint)_rate, 20);
            w.Write((uint)(_channels - 1), 3);
            w.Write((uint)(_bits - 1), 5);
            w.Write((uint)(_totalSamples >> 32) & 0xF, 4);
            w.Write((uint)_totalSamples, 32);
            var bytes = w.ToArray();
            var all = new byte[34];
            Array.Copy(bytes, all, Math.Min(bytes.Length, 18));
            if (_md5Bytes.Length == 16) Array.Copy(_md5Bytes, 0, all, 18, 16);
            return all;
        }

        private static byte[] VorbisComment(AudioTagSet? tags)
        {
            using var body = new MemoryStream();
            var vendor = Encoding.UTF8.GetBytes("Explorer Native");
            body.Write(BitConverter.GetBytes(vendor.Length));
            body.Write(vendor);

            var fields = tags?.VorbisFields().Select(f => Encoding.UTF8.GetBytes($"{f.Key}={f.Value}")).ToList()
                         ?? new List<byte[]>();
            body.Write(BitConverter.GetBytes(fields.Count));
            foreach (var field in fields)
            {
                body.Write(BitConverter.GetBytes(field.Length));
                body.Write(field);
            }
            return body.ToArray();
        }

        public void Write(float[] interleaved, int frames)
        {
            int at = 0;
            while (at < frames)
            {
                int take = Math.Min(frames - at, BlockSize - _filled);
                for (int i = 0; i < take; i++)
                    for (int c = 0; c < _channels; c++)
                        _block[c][_filled + i] = AudioEncoders.ToInt(interleaved[(at + i) * _channels + c], _bits);

                _filled += take;
                at += take;
                if (_filled == BlockSize) EncodeFrame();
            }
        }

        public void Finish()
        {
            if (_filled > 0) EncodeFrame();

            _md5Bytes = _md5.GetHashAndReset();
            _file.Position = _streamInfoAt;
            _file.Write(StreamInfo());
            _file.Flush(true);
        }

        public void Dispose()
        {
            try { _file.Dispose(); } catch { }
            _md5.Dispose();
        }

        private void HashBlock(int n)
        {
            int bytesPer = _bits / 8;
            var bytes = new byte[n * _channels * bytesPer];
            int at = 0;
            for (int i = 0; i < n; i++)
                for (int c = 0; c < _channels; c++)
                {
                    int v = _block[c][i];
                    for (int b = 0; b < bytesPer; b++) bytes[at++] = (byte)(v >> (8 * b));
                }
            _md5.AppendData(bytes);
        }

        private void EncodeFrame()
        {
            int n = _filled;
            HashBlock(n);

            var w = _out;
            w.Reset();

            // Header.
            w.Write(0xFFF8, 16);                        // sync, fixed block size
            w.Write(0b0111, 4);                         // block size: 16 bits at the end
            w.Write(RateCode(_rate), 4);
            w.Write((uint)(_channels - 1), 4);          // independent channels
            w.Write(_bits == 24 ? 0b110u : 0b100u, 3);
            w.Write(0, 1);
            foreach (var b in Utf8Number(_frameNumber)) w.Write(b, 8);
            w.Write((uint)(n - 1), 16);
            byte crc8 = Crc8(w.Bytes, w.ByteCount);
            w.Write(crc8, 8);

            for (int c = 0; c < _channels; c++) Subframe(w, _block[c], n);

            w.AlignToByte();
            ushort crc16 = Crc16(w.Bytes, w.ByteCount);
            w.Write(crc16, 16);

            _file.Write(w.Bytes, 0, w.ByteCount);
            _minFrame = Math.Min(_minFrame, w.ByteCount);
            _maxFrame = Math.Max(_maxFrame, w.ByteCount);

            _totalSamples += n;
            _frameNumber++;
            _filled = 0;
        }

        private static uint RateCode(int rate) => rate switch
        {
            88200 => 0b0001, 176400 => 0b0010, 192000 => 0b0011, 8000 => 0b0100,
            16000 => 0b0101, 22050 => 0b0110, 24000 => 0b0111, 32000 => 0b1000,
            44100 => 0b1001, 48000 => 0b1010, 96000 => 0b1011,
            _ => 0b0000,                                // from STREAMINFO
        };

        private static IEnumerable<uint> Utf8Number(long v)
        {
            if (v < 0x80) { yield return (uint)v; yield break; }

            int extra = v < 0x800 ? 1 : v < 0x10000 ? 2 : v < 0x200000 ? 3 : v < 0x4000000 ? 4 : 5;
            uint lead = extra switch { 1 => 0xC0u, 2 => 0xE0u, 3 => 0xF0u, 4 => 0xF8u, _ => 0xFCu };
            yield return lead | (uint)(v >> (6 * extra));
            for (int i = extra - 1; i >= 0; i--) yield return 0x80u | (uint)((v >> (6 * i)) & 0x3F);
        }

        private void Subframe(BitWriter w, int[] x, int n)
        {
            bool constant = true;
            for (int i = 1; i < n && constant; i++) constant = x[i] == x[0];
            if (constant)
            {
                w.Write(0, 8);                          // CONSTANT
                w.WriteSigned(x[0], _bits);
                return;
            }

            long bestBits = (long)n * _bits;            // VERBATIM
            int bestOrder = -1;
            int bestPartition = 0;

            for (int order = 0; order <= 4 && order < n; order++)
            {
                Residual(x, n, order, _residual);
                long bits = RiceCost(_residual, n, order, out int partition) + order * _bits;
                if (bits < bestBits)
                {
                    bestBits = bits;
                    bestOrder = order;
                    bestPartition = partition;
                    (_residual, _bestResidual) = (_bestResidual, _residual);
                }
            }

            if (bestOrder < 0)
            {
                w.Write(0b00000010, 8);                 // VERBATIM
                for (int i = 0; i < n; i++) w.WriteSigned(x[i], _bits);
                return;
            }

            w.Write((uint)(0b00010000 | (bestOrder << 1)), 8);      // FIXED, order
            for (int i = 0; i < bestOrder; i++) w.WriteSigned(x[i], _bits);
            WriteResidual(w, _bestResidual, n, bestOrder, bestPartition);
        }

        private static void Residual(int[] x, int n, int order, int[] r)
        {
            for (int i = order; i < n; i++)
            {
                long e = order switch
                {
                    0 => x[i],
                    1 => (long)x[i] - x[i - 1],
                    2 => (long)x[i] - 2L * x[i - 1] + x[i - 2],
                    3 => (long)x[i] - 3L * x[i - 1] + 3L * x[i - 2] - x[i - 3],
                    _ => (long)x[i] - 4L * x[i - 1] + 6L * x[i - 2] - 4L * x[i - 3] + x[i - 4],
                };
                r[i - order] = (int)e;
            }
        }

        private static uint ZigZag(int r) => (uint)((r << 1) ^ (r >> 31));

        /// <summary>The Rice parameter for a partition whose zig-zagged values add up to <paramref name="sum"/>.</summary>
        private static int Parameter(long sum, int count)
        {
            if (count <= 0) return 0;
            long mean = sum / count;
            int k = 0;
            while (k < 30 && (mean >> (k + 1)) > 0) k++;
            return k;
        }

        private static long PartitionCost(long sum, int count, int k) => count * (long)(k + 1) + (sum >> k);

        private static int MaxPartition(int n, int order)
        {
            int p = 0;
            while (p < MaxPartitionOrder && n % (1 << (p + 1)) == 0 && (n >> (p + 1)) > order) p++;
            return p;
        }

        private static long RiceCost(int[] r, int n, int order, out int bestPartition)
        {
            int maxP = MaxPartition(n, order);
            int parts = 1 << maxP;
            var sums = new long[parts];
            int per = n >> maxP;

            for (int p = 0; p < parts; p++)
            {
                int from = p == 0 ? 0 : p * per - order;
                int to = (p + 1) * per - order;
                long s = 0;
                for (int i = from; i < to; i++) s += ZigZag(r[i]);
                sums[p] = s;
            }

            long best = long.MaxValue;
            bestPartition = 0;
            for (int level = maxP; level >= 0; level--)
            {
                int count = 1 << level;
                int size = n >> level;
                long bits = 0;
                bool wide = false;
                for (int p = 0; p < count; p++)
                {
                    int samples = p == 0 ? size - order : size;
                    int k = Parameter(sums[p], samples);
                    if (k > 14) wide = true;
                    bits += PartitionCost(sums[p], samples, k);
                }
                bits += 6 + count * (wide ? 5 : 4);
                if (bits < best) { best = bits; bestPartition = level; }

                // Merge pairs for the next level down.
                if (level > 0)
                    for (int p = 0; p < count / 2; p++) sums[p] = sums[2 * p] + sums[2 * p + 1];
            }
            return best;
        }

        private static void WriteResidual(BitWriter w, int[] r, int n, int order, int partition)
        {
            int count = 1 << partition;
            int size = n >> partition;

            var parameters = new int[count];
            bool wide = false;
            int at = 0;
            for (int p = 0; p < count; p++)
            {
                int samples = p == 0 ? size - order : size;
                long s = 0;
                for (int i = 0; i < samples; i++) s += ZigZag(r[at + i]);
                parameters[p] = Parameter(s, samples);
                if (parameters[p] > 14) wide = true;
                at += samples;
            }

            w.Write(wide ? 1u : 0u, 2);                 // RICE or RICE2
            w.Write((uint)partition, 4);

            at = 0;
            for (int p = 0; p < count; p++)
            {
                int samples = p == 0 ? size - order : size;
                int k = parameters[p];
                w.Write((uint)k, wide ? 5 : 4);
                for (int i = 0; i < samples; i++) w.WriteRice(ZigZag(r[at + i]), k);
                at += samples;
            }
        }

        private static byte Crc8(byte[] data, int length)
        {
            int crc = 0;
            for (int i = 0; i < length; i++)
            {
                crc ^= data[i];
                for (int b = 0; b < 8; b++) crc = (crc & 0x80) != 0 ? ((crc << 1) ^ 0x07) & 0xFF : (crc << 1) & 0xFF;
            }
            return (byte)crc;
        }

        private static readonly ushort[] Crc16Table = MakeCrc16Table();

        private static ushort[] MakeCrc16Table()
        {
            var table = new ushort[256];
            for (int i = 0; i < 256; i++)
            {
                int crc = i << 8;
                for (int b = 0; b < 8; b++) crc = (crc & 0x8000) != 0 ? (crc << 1) ^ 0x8005 : crc << 1;
                table[i] = (ushort)crc;
            }
            return table;
        }

        private static ushort Crc16(byte[] data, int length)
        {
            int crc = 0;
            for (int i = 0; i < length; i++) crc = ((crc << 8) ^ Crc16Table[((crc >> 8) ^ data[i]) & 0xFF]) & 0xFFFF;
            return (ushort)crc;
        }

        /// <summary>Most significant bit first, as FLAC is written.</summary>
        private sealed class BitWriter
        {
            private byte[] _bytes = new byte[1 << 16];
            private int _count;
            private ulong _acc;
            private int _bits;

            public byte[] Bytes => _bytes;
            public int ByteCount => _count;

            public void Reset() { _count = 0; _acc = 0; _bits = 0; }

            public void Write(uint value, int bits)
            {
                if (bits == 0) return;
                ulong mask = bits == 32 ? 0xFFFFFFFFUL : (1UL << bits) - 1;
                _acc = (_acc << bits) | (value & mask);
                _bits += bits;
                while (_bits >= 8)
                {
                    _bits -= 8;
                    Put((byte)(_acc >> _bits));
                }
                _acc &= (1UL << _bits) - 1;
            }

            public void Write(int value, int bits) => Write((uint)value, bits);

            public void WriteSigned(int value, int bits) => Write((uint)value, bits);

            public void WriteRice(uint u, int k)
            {
                uint q = u >> k;
                while (q >= 31) { Write(0u, 31); q -= 31; }
                Write(1u, (int)q + 1);
                if (k > 0) Write(u & ((1u << k) - 1), k);
            }

            public void AlignToByte()
            {
                if (_bits > 0) Write(0u, 8 - _bits);
            }

            private void Put(byte b)
            {
                if (_count == _bytes.Length) Array.Resize(ref _bytes, _bytes.Length * 2);
                _bytes[_count++] = b;
            }

            public byte[] ToArray()
            {
                AlignToByte();
                var copy = new byte[_count];
                Array.Copy(_bytes, copy, _count);
                return copy;
            }
        }
    }

    // ---------------------------------------------------------------- Media Foundation

    /// <summary>
    /// MP3, AAC in M4A, and FLAC through Media Foundation's sink writer and the
    /// encoders Windows ships. Fed 16 or 24-bit PCM at a rate the encoder takes.
    ///
    /// This is encoding, not playback: the rule against Media Foundation
    /// transforms is about the playback chain. Every object is created and used
    /// on the one thread that calls this, and MFStartup is matched by exactly
    /// one MFShutdown in Dispose.
    /// </summary>
    internal sealed class MediaFoundationEncoder : IAudioEncoder
    {
        [ComImport, Guid("44AE0FA8-EA31-4109-8D2E-4CAE4997C555"),
         InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMFMediaType
        {
            void R00(); void R01(); void R02(); void R03(); void R04();
            void R05(); void R06(); void R07(); void R08(); void R09();
            void R10(); void R11(); void R12(); void R13(); void R14();
            void R15(); void R16(); void R17();
            [PreserveSig] int SetUINT32(ref Guid key, uint value);              // 18
            void R19(); void R20();
            [PreserveSig] int SetGUID(ref Guid key, ref Guid value);            // 21
        }

        [ComImport, Guid("045FA593-8799-42B8-BC8D-8968C6453507"),
         InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMFMediaBuffer
        {
            [PreserveSig] int Lock(out IntPtr buffer, out uint maxLength, out uint currentLength);
            [PreserveSig] int Unlock();
            [PreserveSig] int GetCurrentLength(out uint length);
            [PreserveSig] int SetCurrentLength(uint length);
            [PreserveSig] int GetMaxLength(out uint length);
        }

        [ComImport, Guid("C40A00F2-B93A-4D80-AE8C-5A1C634F58E4"),
         InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMFSample
        {
            // IMFAttributes is the first thirty.
            void A00(); void A01(); void A02(); void A03(); void A04();
            void A05(); void A06(); void A07(); void A08(); void A09();
            void A10(); void A11(); void A12(); void A13(); void A14();
            void A15(); void A16(); void A17(); void A18(); void A19();
            void A20(); void A21(); void A22(); void A23(); void A24();
            void A25(); void A26(); void A27(); void A28(); void A29();
            void S30(); void S31(); void S32();
            [PreserveSig] int SetSampleTime(long time);                         // 33
            void S34();
            [PreserveSig] int SetSampleDuration(long duration);                 // 35
            void S36(); void S37(); void S38();
            [PreserveSig] int AddBuffer(IMFMediaBuffer buffer);                 // 39
        }

        [ComImport, Guid("3137f1cd-fe5e-4805-a5d8-fb477448cb3d"),
         InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMFSinkWriter
        {
            [PreserveSig] int AddStream(IMFMediaType targetType, out uint streamIndex);         // 0
            [PreserveSig] int SetInputMediaType(uint streamIndex, IMFMediaType inputType,
                IntPtr encodingParameters);                                                     // 1
            [PreserveSig] int BeginWriting();                                                   // 2
            [PreserveSig] int WriteSample(uint streamIndex, IMFSample sample);                  // 3
            void R04(); void R05(); void R06(); void R07();
            [PreserveSig] int DoFinalize();                                                     // 8
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct RegisterTypeInfo
        {
            public Guid Major;
            public Guid Sub;
        }

        [DllImport("mfplat.dll")] private static extern int MFStartup(int version, int flags);
        [DllImport("mfplat.dll")] private static extern int MFShutdown();
        [DllImport("mfplat.dll")] private static extern int MFCreateMediaType(out IMFMediaType type);
        [DllImport("mfplat.dll")] private static extern int MFCreateSample(out IMFSample sample);
        [DllImport("mfplat.dll")] private static extern int MFCreateMemoryBuffer(uint maxLength, out IMFMediaBuffer buffer);

        [DllImport("mfplat.dll")]
        private static extern int MFTEnumEx(Guid category, uint flags, IntPtr inputType,
            ref RegisterTypeInfo outputType, out IntPtr activates, out uint count);

        [DllImport("mfreadwrite.dll", CharSet = CharSet.Unicode)]
        private static extern int MFCreateSinkWriterFromURL(string url, IntPtr byteStream,
            IntPtr attributes, out IMFSinkWriter writer);

        private const int MF_VERSION = 0x00020070;
        private const uint MFT_ENUM_FLAG_ALL = 0x3F;
        private const int MF_E_TOPO_CODEC_NOT_FOUND = unchecked((int)0xC00D5212);
        private const int MF_E_INVALIDMEDIATYPE = unchecked((int)0xC00D36B4);
        private const int MF_E_UNSUPPORTED_D3D_TYPE = unchecked((int)0xC00D6D76);

        private static readonly Guid AudioEncoderCategory = new("91c64bd0-f91e-4d8c-9276-db248279d975");

        private static Guid MajorType = new("48eba18e-f8c9-4687-bf11-0a74c9f96a8f");
        private static Guid Subtype = new("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");
        private static Guid Audio = new("73647561-0000-0010-8000-00AA00389B71");
        private static Guid Pcm = new("00000001-0000-0010-8000-00AA00389B71");
        private static Guid SamplesPerSecond = new("5faeeae7-0290-4c31-9e8a-c534f68d9dba");
        private static Guid ChannelCount = new("37e48bf5-645e-4c5b-89de-ada9e29b696a");
        private static Guid BitsPerSample = new("f2deb57f-40fa-4764-aa33-ed4f2d1ff669");
        private static Guid BlockAlignment = new("322de230-9eeb-43bd-ab7a-ff412251541d");
        private static Guid BytesPerSecond = new("1aab75c8-cfef-451c-ab95-ac034b8e1731");
        private static Guid AacPayloadType = new("bfbabe79-7434-4d1c-94f0-72a3b9e17188");

        private static Guid SubtypeFor(AudioFormatKind kind) => kind switch
        {
            AudioFormatKind.Mp3 => new Guid("00000055-0000-0010-8000-00AA00389B71"),
            AudioFormatKind.M4a => new Guid("00001610-0000-0010-8000-00AA00389B71"),
            AudioFormatKind.Flac => new Guid("0000F1AC-0000-0010-8000-00AA00389B71"),
            _ => Guid.Empty,
        };

        private static readonly Dictionary<AudioFormatKind, bool> _has = new();

        /// <summary>Whether an audio encoder producing this format is registered. Cached.</summary>
        public static bool HasEncoder(AudioFormatKind kind)
        {
            lock (_has)
            {
                if (_has.TryGetValue(kind, out bool known)) return known;

                bool found = false;
                var subtype = SubtypeFor(kind);
                if (subtype != Guid.Empty)
                {
                    bool started = false;
                    try
                    {
                        started = MFStartup(MF_VERSION, 0) == 0;
                        var output = new RegisterTypeInfo { Major = Audio, Sub = subtype };
                        if (MFTEnumEx(AudioEncoderCategory, MFT_ENUM_FLAG_ALL, IntPtr.Zero, ref output,
                                out IntPtr activates, out uint count) == 0 && activates != IntPtr.Zero)
                        {
                            found = count > 0;
                            for (int i = 0; i < count; i++)
                            {
                                var one = Marshal.ReadIntPtr(activates, i * IntPtr.Size);
                                if (one != IntPtr.Zero) Marshal.Release(one);
                            }
                            Marshal.FreeCoTaskMem(activates);
                        }
                    }
                    catch { found = false; }
                    finally { if (started) MFShutdown(); }
                }

                _has[kind] = found;
                return found;
            }
        }

        private IMFSinkWriter? _writer;
        private uint _stream;
        private readonly int _rate, _channels, _bits;
        private readonly string _format;
        private long _frames;
        private byte[] _pcm = Array.Empty<byte>();
        private int _started;

        public bool TagsAfterwards => true;

        public MediaFoundationEncoder(string path, AudioFormatKind kind, int rate, int channels, int kbps, int bits)
        {
            _rate = rate;
            _channels = channels;
            _bits = bits;
            _format = AudioFormats.NameOf(kind);

            if (MFStartup(MF_VERSION, 0) == 0) _started = 1;

            IMFMediaType? output = null, input = null;
            try
            {
                Check(MFCreateMediaType(out output), "could not describe the output");
                var sub = SubtypeFor(kind);
                output.SetGUID(ref MajorType, ref Audio);
                output.SetGUID(ref Subtype, ref sub);
                output.SetUINT32(ref SamplesPerSecond, (uint)rate);
                output.SetUINT32(ref ChannelCount, (uint)channels);

                switch (kind)
                {
                    case AudioFormatKind.Mp3:
                        output.SetUINT32(ref BytesPerSecond, (uint)(kbps * 1000 / 8));
                        output.SetUINT32(ref BlockAlignment, 1);
                        break;
                    case AudioFormatKind.M4a:
                        output.SetUINT32(ref BitsPerSample, 16);
                        output.SetUINT32(ref BytesPerSecond, (uint)(kbps * 1000 / 8));
                        output.SetUINT32(ref AacPayloadType, 0);
                        break;
                    case AudioFormatKind.Flac:
                        output.SetUINT32(ref BitsPerSample, (uint)bits);
                        break;
                }

                Check(MFCreateMediaType(out input), "could not describe the input");
                input.SetGUID(ref MajorType, ref Audio);
                input.SetGUID(ref Subtype, ref Pcm);
                input.SetUINT32(ref BitsPerSample, (uint)bits);
                input.SetUINT32(ref SamplesPerSecond, (uint)rate);
                input.SetUINT32(ref ChannelCount, (uint)channels);
                input.SetUINT32(ref BlockAlignment, (uint)(bits / 8 * channels));
                input.SetUINT32(ref BytesPerSecond, (uint)(rate * bits / 8 * channels));

                int hr = MFCreateSinkWriterFromURL(NameRules.ExactPath(path), IntPtr.Zero, IntPtr.Zero, out var writer);
                if (hr != 0 || writer == null)
                    throw new AudioEncodeException($"Windows could not create the {_format} file (0x{hr:X8})");
                _writer = writer;

                hr = writer.AddStream(output, out _stream);
                if (hr != 0) throw Refusal(hr, rate, channels, kbps);

                hr = writer.SetInputMediaType(_stream, input, IntPtr.Zero);
                if (hr != 0) throw Refusal(hr, rate, channels, kbps);

                hr = writer.BeginWriting();
                if (hr != 0) throw Refusal(hr, rate, channels, kbps);
            }
            catch
            {
                Dispose();
                throw;
            }
            finally
            {
                if (output != null) try { Marshal.ReleaseComObject(output); } catch { }
                if (input != null) try { Marshal.ReleaseComObject(input); } catch { }
            }
        }

        private AudioEncodeException Refusal(int hr, int rate, int channels, int kbps)
        {
            if (hr == MF_E_TOPO_CODEC_NOT_FOUND)
                return new AudioEncodeException(
                    kbps > 0
                        ? $"Windows' {_format} encoder will not take {rate} hertz, {channels} channels at {kbps} kilobits"
                        : $"Windows' {_format} encoder will not take {rate} hertz, {channels} channels",
                    typeRefused: true);

            if (hr == MF_E_INVALIDMEDIATYPE || hr == MF_E_UNSUPPORTED_D3D_TYPE)
                return new AudioEncodeException(
                    $"Windows' {_format} encoder will not take this shape of audio (0x{hr:X8})", typeRefused: true);

            return new AudioEncodeException($"Windows' {_format} encoder failed (0x{hr:X8})");
        }

        private static void Check(int hr, string what)
        {
            if (hr != 0) throw new AudioEncodeException($"{what} (0x{hr:X8})");
        }

        public void Write(float[] interleaved, int frames)
        {
            var writer = _writer ?? throw new ObjectDisposedException(nameof(MediaFoundationEncoder));
            if (frames <= 0) return;

            int samples = frames * _channels;
            int bytesPer = _bits / 8;
            int length = samples * bytesPer;
            if (_pcm.Length < length) _pcm = new byte[length];

            int at = 0;
            for (int i = 0; i < samples; i++)
            {
                int v = AudioEncoders.ToInt(interleaved[i], _bits);
                _pcm[at++] = (byte)v;
                _pcm[at++] = (byte)(v >> 8);
                if (bytesPer == 3) _pcm[at++] = (byte)(v >> 16);
            }

            Check(MFCreateMemoryBuffer((uint)length, out var buffer), "could not make a buffer");
            IMFSample? sample = null;
            try
            {
                Check(buffer.Lock(out IntPtr into, out _, out _), "could not fill a buffer");
                try { Marshal.Copy(_pcm, 0, into, length); }
                finally { buffer.Unlock(); }
                buffer.SetCurrentLength((uint)length);

                Check(MFCreateSample(out sample), "could not make a sample");
                sample.AddBuffer(buffer);

                long start = _frames * 10_000_000L / _rate;
                long end = (_frames + frames) * 10_000_000L / _rate;
                sample.SetSampleTime(start);
                sample.SetSampleDuration(end - start);

                int hr = writer.WriteSample(_stream, sample);
                if (hr != 0) throw new AudioEncodeException($"Windows' {_format} encoder stopped (0x{hr:X8})");
                _frames += frames;
            }
            finally
            {
                if (sample != null) try { Marshal.ReleaseComObject(sample); } catch { }
                try { Marshal.ReleaseComObject(buffer); } catch { }
            }
        }

        public void Finish()
        {
            var writer = _writer ?? throw new ObjectDisposedException(nameof(MediaFoundationEncoder));
            int hr = writer.DoFinalize();
            if (hr != 0) throw new AudioEncodeException($"Windows could not finish the {_format} file (0x{hr:X8})");
        }

        public void Dispose()
        {
            var writer = Interlocked.Exchange(ref _writer, null);
            if (writer != null) try { Marshal.ReleaseComObject(writer); } catch { }

            // Exactly once, and only if this instance took one. An unbalanced
            // MFShutdown takes Media Foundation down for the whole process.
            if (Interlocked.Exchange(ref _started, 0) == 1)
                try { MFShutdown(); } catch { }
        }
    }
}
