using System;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;

namespace ExplorerNative
{
    /// <summary>
    /// An AIFF file, presented to Media Foundation as the WAV file it would be.
    ///
    /// **`.aif` and `.aiff` were in the default extension list and never
    /// played.** Windows registers no byte-stream handler for them and Media
    /// Foundation does not recognise the content either (0xC00D36C4), so every
    /// AIFF was handed to another application — found while testing seeking.
    ///
    /// AIFF is uncompressed PCM in a different wrapper: big-endian samples
    /// under a "FORM" header instead of little-endian ones under "RIFF". So
    /// this is a stream that answers with a WAV header of its own making and
    /// then the file's samples, byte-swapped as they are read. Everything after
    /// that — decoding, rate conversion, and seeking, which is exact for WAV —
    /// is Windows' own, unchanged.
    ///
    /// Covered: AIFF, and AIFF-C with no compression ("NONE"/"twos"),
    /// little-endian PCM ("sowt"), 32- and 64-bit float ("fl32"/"fl64"), and
    /// µ-law/A-law, which WAV carries as they are. Anything else — Apple's IMA
    /// ADPCM, for one — is refused, and the file goes to whichever application
    /// owns it, as before.
    /// </summary>
    internal sealed class AiffStream : IStream, IDisposable
    {
        private enum Coding { BigEndian, LittleEndian, Float, Law }

        private readonly object _gate = new();
        private readonly ByteSource _source;
        private readonly bool _ownsSource;
        private readonly byte[] _header;
        private readonly long _dataStart;
        private readonly long _dataLength;
        private readonly int _bytesPerSample;
        private readonly Coding _coding;
        private readonly bool _signed8;
        private long _position;

        private AiffStream(ByteSource source, bool ownsSource, byte[] header, long dataStart, long dataLength,
            int bytesPerSample, Coding coding, bool signed8)
        {
            _source = source;
            _ownsSource = ownsSource;
            _header = header;
            _dataStart = dataStart;
            _dataLength = dataLength;
            _bytesPerSample = bytesPerSample;
            _coding = coding;
            _signed8 = signed8;
            Length = header.Length + dataLength;
        }

        public long Length { get; }

        /// <summary>
        /// The file as WAV, or null when it is not an AIFF this can present.
        /// <paramref name="stream"/> is the player's download when there is one;
        /// otherwise the file is opened here and closed with the stream.
        /// </summary>
        public static AiffStream? TryOpen(string path, IStream? stream)
        {
            ByteSource? source = stream switch
            {
                null => FileBytes.TryOpen(path),
                StreamingSource download => new HeldBytes(download),
                _ => null,
            };
            if (source == null) return null;

            AiffStream? result;
            try { result = Parse(source, stream == null); }
            catch { result = null; }

            if (result == null && stream == null) source.Dispose();
            return result;
        }

        private static AiffStream? Parse(ByteSource source, bool owns)
        {
            var head = new byte[12];
            if (source.ReadAny(0, head) < 12) return null;
            if (!Is(head, 0, "FORM")) return null;

            bool compressed = Is(head, 8, "AIFC");
            if (!compressed && !Is(head, 8, "AIFF")) return null;

            long formEnd = Math.Min(source.Length, 8 + (long)BigEndian32(head, 4));

            int channels = 0, bits = 0;
            long frames = 0;
            double rate = 0;
            string compression = "NONE";
            long dataStart = -1, dataLength = 0;

            var chunk = new byte[8];
            long at = 12;
            while (at + 8 <= formEnd)
            {
                if (source.ReadAny(at, chunk) < 8) break;
                long size = BigEndian32(chunk, 4);
                long body = at + 8;

                if (Is(chunk, 0, "COMM"))
                {
                    var comm = new byte[Math.Min(size, 64)];
                    if (source.ReadAny(body, comm) < Math.Min(comm.Length, 18)) return null;
                    channels = (comm[0] << 8) | comm[1];
                    frames = BigEndian32(comm, 2);
                    bits = (comm[6] << 8) | comm[7];
                    rate = Extended(comm, 8);
                    if (compressed && comm.Length >= 22)
                        compression = System.Text.Encoding.ASCII.GetString(comm, 18, 4);
                }
                else if (Is(chunk, 0, "SSND"))
                {
                    var ssnd = new byte[8];
                    if (source.ReadAny(body, ssnd) < 8) return null;
                    long offset = BigEndian32(ssnd, 0);
                    dataStart = body + 8 + offset;
                    dataLength = Math.Max(0, Math.Min(size - 8 - offset, source.Length - dataStart));
                }

                // Chunks are padded to an even length.
                at = body + size + (size & 1);
            }

            // Written so NaN fails it: every comparison with NaN is false.
            if (channels <= 0 || channels > 32 || bits <= 0 || !(rate >= 1 && rate <= 1_000_000) || dataStart < 0)
                return null;

            Coding coding;
            int bytes = (bits + 7) / 8;
            bool isFloat = false;
            int formatTag = 0;

            switch (compression)
            {
                case "NONE":
                case "twos":
                    coding = Coding.BigEndian;
                    break;
                case "sowt":
                    coding = Coding.LittleEndian;
                    break;
                case "in24": coding = Coding.BigEndian; bits = 24; bytes = 3; break;
                case "in32": coding = Coding.BigEndian; bits = 32; bytes = 4; break;
                case "fl32":
                case "FL32":
                    coding = Coding.Float; bits = 32; bytes = 4; isFloat = true;
                    break;
                case "fl64":
                case "FL64":
                    coding = Coding.Float; bits = 64; bytes = 8; isFloat = true;
                    break;
                case "ulaw":
                case "ULAW":
                    coding = Coding.Law; bits = 8; bytes = 1; formatTag = 7;
                    break;
                case "alaw":
                case "ALAW":
                    coding = Coding.Law; bits = 8; bytes = 1; formatTag = 6;
                    break;
                default:
                    return null;
            }

            if (bytes > 8) return null;

            // What the header says, unless the file is shorter than that.
            long blockAlign = (long)bytes * channels;
            long wanted = frames * blockAlign;
            if (wanted > 0 && wanted < dataLength) dataLength = wanted;
            dataLength -= dataLength % blockAlign;
            if (dataLength > uint.MaxValue - 100) return null;

            int sampleRate = (int)Math.Round(rate);
            var header = formatTag != 0
                ? LawHeader(formatTag, channels, sampleRate, dataLength)
                : ExtensibleHeader(isFloat, channels, sampleRate, bytes, bits, dataLength);

            return new AiffStream(source, owns, header, dataStart, dataLength, bytes, coding,
                signed8: coding == Coding.BigEndian && bytes == 1 || coding == Coding.LittleEndian && bytes == 1);
        }

        private static byte[] ExtensibleHeader(bool isFloat, int channels, int rate, int bytes, int bits, long data)
        {
            var h = new byte[68];
            int blockAlign = bytes * channels;
            Put(h, 0, "RIFF"); PutLe32(h, 4, (uint)(60 + data)); Put(h, 8, "WAVE");
            Put(h, 12, "fmt "); PutLe32(h, 16, 40);
            PutLe16(h, 20, 0xFFFE);
            PutLe16(h, 22, channels);
            PutLe32(h, 24, (uint)rate);
            PutLe32(h, 28, (uint)(rate * blockAlign));
            PutLe16(h, 32, blockAlign);
            PutLe16(h, 34, bytes * 8);
            PutLe16(h, 36, 22);
            PutLe16(h, 38, isFloat ? bytes * 8 : bits);
            PutLe32(h, 40, channels switch { 1 => 0x4u, 2 => 0x3u, _ => 0u });
            var subtype = isFloat
                ? new Guid("00000003-0000-0010-8000-00AA00389B71")
                : new Guid("00000001-0000-0010-8000-00AA00389B71");
            subtype.ToByteArray().CopyTo(h, 44);
            Put(h, 60, "data"); PutLe32(h, 64, (uint)data);
            return h;
        }

        private static byte[] LawHeader(int tag, int channels, int rate, long data)
        {
            var h = new byte[46];
            Put(h, 0, "RIFF"); PutLe32(h, 4, (uint)(38 + data)); Put(h, 8, "WAVE");
            Put(h, 12, "fmt "); PutLe32(h, 16, 18);
            PutLe16(h, 20, tag);
            PutLe16(h, 22, channels);
            PutLe32(h, 24, (uint)rate);
            PutLe32(h, 28, (uint)(rate * channels));
            PutLe16(h, 32, channels);
            PutLe16(h, 34, 8);
            PutLe16(h, 36, 0);
            Put(h, 38, "data"); PutLe32(h, 42, (uint)data);
            return h;
        }

        public void Read(byte[] buffer, int count, IntPtr bytesReadOut)
        {
            int read = 0;
            try
            {
                long position;
                lock (_gate) position = _position;

                count = (int)Math.Max(0, Math.Min(Math.Min(count, buffer.Length), Length - position));
                if (position < _header.Length && count > 0)
                {
                    read = (int)Math.Min(count, _header.Length - position);
                    Array.Copy(_header, position, buffer, 0, read);
                }

                if (read < count)
                    read += ReadSamples(position + read - _header.Length, buffer.AsSpan(read, count - read));

                lock (_gate)
                {
                    if (_position == position) _position = position + read;
                }
            }
            catch { read = 0; }

            if (bytesReadOut != IntPtr.Zero) Marshal.WriteInt32(bytesReadOut, read);
        }

        /// <summary>
        /// Sample bytes from <paramref name="offset"/> into the data, converted.
        /// Read from the start of the sample the offset falls in, so a request
        /// that begins part way through a sample still swaps whole samples.
        /// </summary>
        private int ReadSamples(long offset, Span<byte> into)
        {
            int partial = (int)(offset % _bytesPerSample);
            long from = offset - partial;
            int length = partial + into.Length;
            length += (_bytesPerSample - length % _bytesPerSample) % _bytesPerSample;
            length = (int)Math.Min(length, _dataLength - from);
            if (length <= partial) return 0;

            Span<byte> raw = length <= 256 ? stackalloc byte[length] : new byte[length];
            int got = 0;
            while (got < length)
            {
                int n = _source.ReadAny(_dataStart + from + got, raw.Slice(got));
                if (n <= 0) break;
                got += n;
            }
            got -= got % _bytesPerSample;
            if (got <= partial) return 0;

            Convert(raw.Slice(0, got));

            int usable = Math.Min(into.Length, got - partial);
            raw.Slice(partial, usable).CopyTo(into);
            return usable;
        }

        private void Convert(Span<byte> data)
        {
            switch (_coding)
            {
                case Coding.Law:
                    return;

                case Coding.LittleEndian:
                    if (_signed8) for (int i = 0; i < data.Length; i++) data[i] ^= 0x80;
                    return;

                default:
                    if (_bytesPerSample == 1)
                    {
                        // AIFF's 8-bit samples are signed; WAV's are not.
                        for (int i = 0; i < data.Length; i++) data[i] ^= 0x80;
                        return;
                    }

                    for (int i = 0; i < data.Length; i += _bytesPerSample)
                        data.Slice(i, _bytesPerSample).Reverse();
                    return;
            }
        }

        public void Seek(long move, int origin, IntPtr newPositionOut)
        {
            lock (_gate)
            {
                long from = origin switch
                {
                    1 => _position,     // STREAM_SEEK_CUR
                    2 => Length,        // STREAM_SEEK_END
                    _ => 0,             // STREAM_SEEK_SET
                };

                _position = Math.Clamp(from + move, 0, Length);
                if (newPositionOut != IntPtr.Zero) Marshal.WriteInt64(newPositionOut, _position);
            }
        }

        public void Stat(out STATSTG stat, int flags)
        {
            stat = new STATSTG
            {
                type = 2,               // STGTY_STREAM
                cbSize = Length,
                grfMode = 0,            // STGM_READ
            };
        }

        public void Dispose()
        {
            if (_ownsSource) _source.Dispose();
        }

        // ---------- bytes ----------

        private static bool Is(byte[] data, int at, string ascii)
        {
            for (int i = 0; i < 4; i++)
                if (data[at + i] != (byte)ascii[i]) return false;
            return true;
        }

        private static long BigEndian32(byte[] data, int at) =>
            (uint)((data[at] << 24) | (data[at + 1] << 16) | (data[at + 2] << 8) | data[at + 3]);

        /// <summary>The 80-bit IEEE extended float AIFF stores its sample rate as.</summary>
        private static double Extended(byte[] data, int at)
        {
            int exponent = ((data[at] & 0x7F) << 8) | data[at + 1];
            ulong mantissa = 0;
            for (int i = 0; i < 8; i++) mantissa = (mantissa << 8) | data[at + 2 + i];
            if (exponent == 0 && mantissa == 0) return 0;
            double value = mantissa * Math.Pow(2, exponent - 16383 - 63);
            return (data[at] & 0x80) != 0 ? -value : value;
        }

        private static void Put(byte[] data, int at, string ascii)
        {
            for (int i = 0; i < ascii.Length; i++) data[at + i] = (byte)ascii[i];
        }

        private static void PutLe16(byte[] data, int at, int value)
        {
            data[at] = (byte)value;
            data[at + 1] = (byte)(value >> 8);
        }

        private static void PutLe32(byte[] data, int at, uint value)
        {
            for (int i = 0; i < 4; i++) data[at + i] = (byte)(value >> (8 * i));
        }

        // Read only, as LocalByteStream is and for the same reason.
        public void Clone(out IStream copy) => throw new NotSupportedException();
        public void Commit(int flags) => throw new NotSupportedException();
        public void CopyTo(IStream target, long count, IntPtr read, IntPtr written) => throw new NotSupportedException();
        public void LockRegion(long offset, long count, int lockType) => throw new NotSupportedException();
        public void Revert() => throw new NotSupportedException();
        public void SetSize(long newSize) => throw new NotSupportedException();
        public void UnlockRegion(long offset, long count, int lockType) => throw new NotSupportedException();
        public void Write(byte[] buffer, int count, IntPtr written) => throw new NotSupportedException();
    }
}
