using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace ExplorerNative
{
    /// <summary>
    /// The sound in a Matroska or AVI file.
    ///
    /// Both are written here, byte by byte, around a 440Hz tone: nothing in
    /// the build can write either container, and a test that needs somebody's
    /// video library passes on one machine.
    ///
    /// The AVI carries plain PCM. The MKV carries AAC, made by Windows' own
    /// encoder as an ADTS stream and unwrapped into Matroska blocks — AAC is
    /// what Matroska files actually hold. PCM in Matroska is the one pairing
    /// Windows refuses to turn into float (0xC00D36B4); it is rare, and such a
    /// file is handed to the application that owns it like any other that
    /// will not play.
    /// </summary>
    internal static class VideoContainerTests
    {
        private static Action<string, bool, string?> _check = null!;

        private static void Check(string what, bool ok, string? detail = null) => _check(what, ok, detail);

        private const int Rate = 48000, Channels = 2, Seconds = 3;

        /// <param name="writeAac">Writes a 48kHz stereo tone of three seconds as ADTS AAC.</param>
        public static void RunAll(Action<string, bool, string?> check, Func<string> newTempDir, Action<string> cleanup,
            Func<string, bool> writeAac)
        {
            _check = check;
            Console.WriteLine("The sound in Matroska and AVI files:");

            foreach (var extension in new[] { ".mkv", ".avi" })
            {
                Check($"{extension} is treated as audio",
                    AudioFiles.IsAudio("clip" + extension, AudioFiles.DefaultExtensions));
                Check($"Windows has a {extension} byte-stream handler",
                    AudioFiles.WindowsHasHandlerFor(extension));
            }

            var dir = newTempDir();
            try
            {
                var pcm = Tone();
                var mkv = Path.Combine(dir, "tone.mkv");
                var avi = Path.Combine(dir, "tone.avi");
                File.WriteAllBytes(avi, Avi(pcm));

                var adts = Path.Combine(dir, "tone.aac");
                if (!writeAac(adts))
                {
                    Check("Media Foundation can write AAC for the Matroska test", false);
                    return;
                }
                var (aacFrames, config) = Adts(File.ReadAllBytes(adts));
                Check($"the AAC stream has frames ({aacFrames.Count})", aacFrames.Count > 100 && config != null,
                    aacFrames.Count.ToString());
                if (config == null) return;
                File.WriteAllBytes(mkv, Matroska(aacFrames, config));

                foreach (var path in new[] { mkv, avi })
                {
                    var what = Path.GetExtension(path);
                    var decoder = TrackDecoder.Open(path, null, Rate, Channels, out string why);
                    try
                    {
                        Check($"{what} opens", decoder != null, why);
                        if (decoder == null) continue;

                        Check($"{what} knows how long it is ({decoder.Duration:0.00}s)",
                            Math.Abs(decoder.Duration - Seconds) < 0.25, decoder.Duration.ToString("0.000"));

                        var block = new float[4096 * decoder.Channels];
                        long frames = 0;
                        float peak = 0;
                        for (int empty = 0; frames < Rate * Seconds && empty < 3;)
                        {
                            int got = decoder.Read(block, 4096);
                            if (got <= 0) { empty++; continue; }
                            frames += got;
                            for (int i = 0; i < got * decoder.Channels; i++)
                                peak = Math.Max(peak, Math.Abs(block[i]));
                        }

                        Check($"{what} decodes all of it ({frames:N0} frames)",
                            frames >= Rate * Seconds * 95 / 100, frames.ToString());
                        Check($"{what} comes out as the tone, not silence (peak {peak:0.###})",
                            peak > 0.4f && peak < 0.6f, peak.ToString("0.####"));
                    }
                    finally { decoder?.Dispose(); }
                }
            }
            finally { cleanup(dir); }

            Console.WriteLine();
        }

        /// <summary>Half-scale 440Hz, 16-bit stereo.</summary>
        private static byte[] Tone()
        {
            var bytes = new byte[Rate * Seconds * Channels * 2];
            for (int i = 0; i < Rate * Seconds; i++)
            {
                short v = (short)(Math.Sin(2 * Math.PI * 440 * i / Rate) * 16384);
                for (int c = 0; c < Channels; c++)
                    BitConverter.TryWriteBytes(bytes.AsSpan((i * Channels + c) * 2), v);
            }
            return bytes;
        }

        // ---------------- AVI ----------------

        private static byte[] Avi(byte[] pcm)
        {
            int align = Channels * 2, perSecond = Rate * align;

            var movi = new MemoryStream();
            var index = new MemoryStream();
            Write(movi, "movi");
            for (int at = 0; at < pcm.Length; at += perSecond)
            {
                int length = Math.Min(perSecond, pcm.Length - at);
                // Stream 0, which is the only one: "01wb" named a stream
                // that does not exist and Windows refused the file.
                Index(index, "00wb", 0x10, (int)movi.Position, length);
                Chunk(movi, "00wb", pcm.AsSpan(at, length).ToArray());
            }

            var avih = new MemoryStream();
            Int(avih, 0); Int(avih, perSecond); Int(avih, 0); Int(avih, 0x10);
            Int(avih, (pcm.Length + perSecond - 1) / perSecond); Int(avih, 0); Int(avih, 1); Int(avih, perSecond);
            Int(avih, 0); Int(avih, 0); for (int i = 0; i < 4; i++) Int(avih, 0);

            var strh = new MemoryStream();
            Write(strh, "auds"); Int(strh, 0); Int(strh, 0); Short(strh, 0); Short(strh, 0); Int(strh, 0);
            Int(strh, align); Int(strh, perSecond); Int(strh, 0); Int(strh, pcm.Length / align);
            Int(strh, perSecond); Int(strh, -1); Int(strh, align);
            for (int i = 0; i < 4; i++) Short(strh, 0);

            var strf = new MemoryStream();
            Short(strf, 1); Short(strf, Channels); Int(strf, Rate); Int(strf, perSecond);
            Short(strf, align); Short(strf, 16); Short(strf, 0);

            var strl = new MemoryStream();
            Write(strl, "strl"); Chunk(strl, "strh", strh.ToArray()); Chunk(strl, "strf", strf.ToArray());

            var hdrl = new MemoryStream();
            Write(hdrl, "hdrl"); Chunk(hdrl, "avih", avih.ToArray()); Chunk(hdrl, "LIST", strl.ToArray());

            var body = new MemoryStream();
            Write(body, "AVI ");
            Chunk(body, "LIST", hdrl.ToArray());
            Chunk(body, "LIST", movi.ToArray());
            Chunk(body, "idx1", index.ToArray());

            var file = new MemoryStream();
            Chunk(file, "RIFF", body.ToArray());
            return file.ToArray();
        }

        private static void Index(Stream s, string id, int flags, int offset, int size)
        {
            Write(s, id); Int(s, flags); Int(s, offset); Int(s, size);
        }

        private static void Chunk(Stream s, string id, byte[] data)
        {
            Write(s, id);
            Int(s, data.Length);
            s.Write(data);
            if (data.Length % 2 == 1) s.WriteByte(0);
        }

        private static void Write(Stream s, string fourcc) => s.Write(Encoding.ASCII.GetBytes(fourcc));
        private static void Int(Stream s, int v) => s.Write(BitConverter.GetBytes(v));
        private static void Short(Stream s, int v) => s.Write(BitConverter.GetBytes((short)v));

        // ---------------- Matroska ----------------

        /// <summary>
        /// Raw AAC frames out of an ADTS stream, and the AudioSpecificConfig
        /// Matroska wants in their place.
        /// </summary>
        private static (List<byte[]> Frames, byte[]? Config) Adts(byte[] data)
        {
            var frames = new List<byte[]>();
            byte[]? config = null;
            int at = 0;
            while (at + 7 <= data.Length)
            {
                if (data[at] != 0xFF || (data[at + 1] & 0xF6) != 0xF0) { at++; continue; }
                int header = (data[at + 1] & 1) == 0 ? 9 : 7;
                int profile = (data[at + 2] >> 6) & 3;
                int frequency = (data[at + 2] >> 2) & 15;
                int channels = ((data[at + 2] & 1) << 2) | (data[at + 3] >> 6);
                int length = ((data[at + 3] & 3) << 11) | (data[at + 4] << 3) | (data[at + 5] >> 5);
                if (length < header || at + length > data.Length) break;
                config ??= new[]
                {
                    (byte)(((profile + 1) << 3) | (frequency >> 1)),
                    (byte)(((frequency & 1) << 7) | (channels << 3)),
                };
                frames.Add(data.AsSpan(at + header, length - header).ToArray());
                at += length;
            }
            return (frames, config);
        }

        private static byte[] Matroska(List<byte[]> frames, byte[] config)
        {
            double msPerFrame = 1024 * 1000.0 / Rate;

            var file = new MemoryStream();
            file.Write(Element(0x1A45DFA3, Concat(
                Uint(0x4286, 1), Uint(0x42F7, 1), Uint(0x42F2, 4), Uint(0x42F3, 8),
                Text(0x4282, "matroska"), Uint(0x4287, 4), Uint(0x4285, 2))));

            var info = Element(0x1549A966, Concat(
                Uint(0x2AD7B1, 1_000_000), Float(0x4489, frames.Count * msPerFrame),
                Text(0x4D80, "ExplorerNative tests"), Text(0x5741, "ExplorerNative tests")));

            var tracks = Element(0x1654AE6B, Element(0xAE, Concat(
                Uint(0xD7, 1), Uint(0x73C5, 1), Uint(0x83, 2), Text(0x86, "A_AAC"),
                Element(0x63A2, config),
                Element(0xE1, Concat(Float(0xB5, Rate), Uint(0x9F, Channels))))));

            // A cluster a second, each frame a SimpleBlock timed from its cluster.
            var clusters = new MemoryStream();
            int i = 0;
            while (i < frames.Count)
            {
                long clusterMs = (long)(i * msPerFrame);
                var parts = new List<byte[]> { Uint(0xE7, (ulong)clusterMs) };
                while (i < frames.Count && (long)(i * msPerFrame) - clusterMs < 1000)
                {
                    short offset = (short)((long)(i * msPerFrame) - clusterMs);
                    var block = new byte[4 + frames[i].Length];
                    block[0] = 0x81;                                  // track 1
                    block[1] = (byte)(offset >> 8);
                    block[2] = (byte)offset;
                    block[3] = 0x80;                                  // keyframe
                    frames[i].CopyTo(block, 4);
                    parts.Add(Element(0xA3, block));
                    i++;
                }
                clusters.Write(Element(0x1F43B675, Concat(parts.ToArray())));
            }

            file.Write(Element(0x18538067, Concat(info, tracks, clusters.ToArray())));
            return file.ToArray();
        }

        private static byte[] Element(uint id, byte[] data)
        {
            var s = new MemoryStream();
            var idBytes = BitConverter.GetBytes(id);
            Array.Reverse(idBytes);
            int skip = 0;
            while (skip < 3 && idBytes[skip] == 0) skip++;
            s.Write(idBytes, skip, 4 - skip);

            // Always eight bytes of size: 0x01 and seven of length.
            s.WriteByte(0x01);
            for (int shift = 48; shift >= 0; shift -= 8) s.WriteByte((byte)((ulong)data.LongLength >> shift));
            s.Write(data);
            return s.ToArray();
        }

        private static byte[] Uint(uint id, ulong value)
        {
            var bytes = new List<byte>();
            do { bytes.Insert(0, (byte)value); value >>= 8; } while (value > 0);
            return Element(id, bytes.ToArray());
        }

        private static byte[] Float(uint id, double value)
        {
            var bytes = BitConverter.GetBytes(value);
            Array.Reverse(bytes);
            return Element(id, bytes);
        }

        private static byte[] Text(uint id, string value) => Element(id, Encoding.ASCII.GetBytes(value));

        private static byte[] Concat(params byte[][] parts)
        {
            var s = new MemoryStream();
            foreach (var part in parts) s.Write(part);
            return s.ToArray();
        }
    }
}
