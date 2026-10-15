using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;

namespace ExplorerNative
{
    /// <summary>
    /// Files whose sound Windows can decode, in a container Windows will not
    /// open, rewrapped as one it will: FLAC inside Ogg as plain FLAC, and
    /// FLV's AAC or MP3 as a plain AAC (ADTS) or MP3 stream. (M2TS was tried
    /// and dropped: Windows opens .m2ts itself.) Media Foundation then decodes them as it would any other, with
    /// its own seeking. Tried only after it has refused the file itself (see
    /// TrackDecoder), so nothing that already played goes this way.
    /// </summary>
    internal static class Rewrap
    {
        /// <summary>The file as something Windows reads, or null when it is none of these.</summary>
        public static Stream? TryRewrap(string path, IStream? stream, out string kind)
        {
            kind = "";
            Stream? source = null;
            try
            {
                source = stream != null
                    ? new ComStream(stream)
                    : new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 64 * 1024);
                var head = new byte[200];
                source.Position = 0;
                int got = ReadFully(source, head, head.Length);

                if (got >= 4 && head[0] == 'O' && head[1] == 'g' && head[2] == 'g' && head[3] == 'S')
                {
                    kind = "FLAC in Ogg";
                    var flac = OggFlacToFlac(source);
                    source.Dispose();
                    return flac;
                }
                if (got >= 9 && head[0] == 'F' && head[1] == 'L' && head[2] == 'V')
                {
                    kind = "FLV";
                    var audio = FlvAudio(source);
                    source.Dispose();
                    return audio;
                }
                source.Dispose();
                return null;
            }
            catch
            {
                try { source?.Dispose(); } catch { }
                return null;
            }
        }

        private static int ReadFully(Stream s, byte[] buffer, int count)
        {
            int total = 0;
            while (total < count)
            {
                int n = s.Read(buffer, total, count - total);
                if (n <= 0) break;
                total += n;
            }
            return total;
        }

        // ---- FLAC in Ogg ----

        /// <summary>
        /// The packets of the first Ogg stream, put back together: the first is
        /// 0x7F "FLAC", a version, a count, then "fLaC" and STREAMINFO; the rest
        /// are metadata blocks and then FLAC frames as they are. Joined, that is
        /// a native FLAC file. Held in memory: FLAC in Ogg is rare and a few tens
        /// of megabytes at most.
        /// </summary>
        private static Stream? OggFlacToFlac(Stream source)
        {
            // Held whole, so sized once (the FLAC is about as long as the Ogg),
            // and a file too big to hold is refused rather than taking the PC's memory.
            if (source.Length > MostHeld) return null;
            source.Position = 0;
            var output = new MemoryStream((int)source.Length);
            var packet = new MemoryStream();
            int serial = -1;
            bool first = true;
            var header = new byte[27];
            while (ReadFully(source, header, 27) == 27)
            {
                if (header[0] != 'O' || header[1] != 'g' || header[2] != 'g' || header[3] != 'S') return null;
                int pageSerial = BitConverter.ToInt32(header, 14);
                int segments = header[26];
                var table = new byte[segments];
                if (ReadFully(source, table, segments) < segments) break;
                int bodyLength = 0;
                foreach (var b in table) bodyLength += b;
                var body = new byte[bodyLength];
                if (ReadFully(source, body, bodyLength) < bodyLength) break;
                if (serial == -1) serial = pageSerial;
                if (pageSerial != serial) continue;

                int at = 0;
                foreach (var lace in table)
                {
                    packet.Write(body, at, lace);
                    at += lace;
                    if (lace == 255) continue;
                    var whole = packet.ToArray();
                    packet.SetLength(0);
                    if (first)
                    {
                        if (whole.Length < 13 || whole[0] != 0x7F || whole[1] != 'F' || whole[2] != 'L' ||
                            whole[3] != 'A' || whole[4] != 'C') return null;
                        output.Write(whole, 9, whole.Length - 9);
                        first = false;
                    }
                    else output.Write(whole, 0, whole.Length);
                }
            }
            if (first) return null;
            output.Position = 0;
            return output;
        }

        // ---- FLV ----

        /// <summary>
        /// FLV's audio, out of its tags: MP3 as it is, AAC with an ADTS header
        /// put in front of each frame from the AudioSpecificConfig the first AAC
        /// tag carries. Video tags are passed over. Held in memory: the sound
        /// alone, a few megabytes an hour.
        /// </summary>
        /// <summary>The most of a file rewrapped in memory: 512MB.</summary>
        internal const long MostHeld = 512L * 1024 * 1024;

        /// <summary>
        /// The ADTS profile for an object type. HE-AAC (5) and HE-AAC v2 (29) are
        /// AAC LC with SBR signalled implicitly, so they go as LC (2): "Main",
        /// which the plain arithmetic gave them, Windows' decoder refuses.
        /// </summary>
        internal static int Profile(int objectType) => objectType is 5 or 29 ? 2 : objectType;

        private static Stream? FlvAudio(Stream source)
        {
            source.Position = 0;
            var head = new byte[9];
            if (ReadFully(source, head, 9) < 9) return null;
            int dataOffset = head[5] << 24 | head[6] << 16 | head[7] << 8 | head[8];
            source.Position = dataOffset;

            var output = new MemoryStream();
            int objectType = 0, frequency = 0, channels = 0;
            var tag = new byte[15];
            while (ReadFully(source, tag, 15) == 15)
            {
                int type = tag[4];
                int size = tag[5] << 16 | tag[6] << 8 | tag[7];
                if (size <= 0) continue;
                var data = new byte[size];
                if (ReadFully(source, data, size) < size) break;
                if (type != 8) continue;

                int format = data[0] >> 4;
                if (format == 2 || format == 14)
                    output.Write(data, 1, size - 1);
                else if (format == 10 && size > 2)
                {
                    if (data[1] == 0 && size > 3)
                    {
                        objectType = data[2] >> 3;
                        frequency = (data[2] & 7) << 1 | data[3] >> 7;
                        channels = data[3] >> 3 & 15;
                    }
                    else if (objectType > 0 && data[1] != 0)
                    {
                        int length = size - 2 + 7;
                        output.WriteByte(0xFF);
                        output.WriteByte(0xF1);
                        output.WriteByte((byte)(((Profile(objectType) - 1) & 3) << 6 | (frequency & 15) << 2 | (channels >> 2 & 1)));
                        output.WriteByte((byte)((channels & 3) << 6 | length >> 11 & 3));
                        output.WriteByte((byte)(length >> 3 & 0xFF));
                        output.WriteByte((byte)((length & 7) << 5 | 0x1F));
                        output.WriteByte(0xFC);
                        output.Write(data, 2, size - 2);
                    }
                }
            }
            if (output.Length == 0) return null;
            output.Position = 0;
            return output;
        }
    }

    /// <summary>A read-only managed stream as the COM IStream Media Foundation reads through, with its own lock.</summary>
    internal sealed class ManagedIStream : IStream, IDisposable
    {
        private readonly object _gate = new();
        private Stream? _inner;
        private long _position;

        public ManagedIStream(Stream inner) { _inner = inner; }

        public void Read(byte[] buffer, int count, IntPtr bytesReadOut)
        {
            int read = 0;
            try
            {
                lock (_gate)
                {
                    if (_inner != null && count > 0)
                    {
                        _inner.Position = _position;
                        while (read < count)
                        {
                            int n = _inner.Read(buffer, read, count - read);
                            if (n <= 0) break;
                            read += n;
                        }
                        _position += read;
                    }
                }
            }
            catch { }
            if (bytesReadOut != IntPtr.Zero) Marshal.WriteInt32(bytesReadOut, read);
        }

        public void Seek(long move, int origin, IntPtr newPositionOut)
        {
            lock (_gate)
            {
                long length = _inner?.Length ?? 0;
                long from = origin switch { 1 => _position, 2 => length, _ => 0 };
                _position = Math.Clamp(from + move, 0, length);
                if (newPositionOut != IntPtr.Zero) Marshal.WriteInt64(newPositionOut, _position);
            }
        }

        public void Stat(out STATSTG stat, int flags)
        {
            lock (_gate) stat = new STATSTG { type = 2, cbSize = _inner?.Length ?? 0, grfMode = 0 };
        }

        public void Dispose()
        {
            lock (_gate)
            {
                try { _inner?.Dispose(); } catch { }
                _inner = null;
            }
        }

        public void Clone(out IStream copy) => throw new NotSupportedException();
        public void Commit(int flags) => throw new NotSupportedException();
        public void CopyTo(IStream target, long count, IntPtr read, IntPtr written) => throw new NotSupportedException();
        public void LockRegion(long offset, long count, int lockType) => throw new NotSupportedException();
        public void Revert() => throw new NotSupportedException();
        public void SetSize(long newSize) => throw new NotSupportedException();
        public void UnlockRegion(long offset, long count, int lockType) => throw new NotSupportedException();
        public void Write(byte[] buffer, int count, IntPtr written) => throw new NotSupportedException();
    }

    /// <summary>A decoder that also owns the stream it reads through, and lets it go with itself.</summary>
    internal sealed class OwningDecoder : ITrackDecoder
    {
        private readonly ITrackDecoder _inner;
        private readonly IDisposable _owned;
        public OwningDecoder(ITrackDecoder inner, IDisposable owned) { _inner = inner; _owned = owned; }
        public int SampleRate => _inner.SampleRate;
        public int Channels => _inner.Channels;
        public double Duration => _inner.Duration;
        public double Position => _inner.Position;
        public int Read(float[] destination, int frames) => _inner.Read(destination, frames);
        public bool SeekTo(double seconds) => _inner.SeekTo(seconds);
        public bool Reopen(int sampleRate, int channels) => _inner.Reopen(sampleRate, channels);
        public void Dispose()
        {
            try { _inner.Dispose(); } catch { }
            try { _owned.Dispose(); } catch { }
        }
    }
}
