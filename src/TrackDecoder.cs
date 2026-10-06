using System;
using System.IO;

namespace ExplorerNative
{
    /// <summary>
    /// A file turned into interleaved float at the device's format, whoever does
    /// the turning.
    ///
    /// There are two implementations and there did not use to be one: everything
    /// went through Media Foundation, because Media Foundation decodes
    /// everything Windows decodes and that has always been the right answer. It
    /// is still the right answer for MP3, AAC, WMA, WAV, ALAC and FLAC.
    ///
    /// It is not an answer at all for Ogg Vorbis and Opus. Measured on this
    /// machine: a real .opus file fails to open with
    /// MF_E_UNSUPPORTED_BYTESTREAM_TYPE in 33ms, and the registry says why —
    /// there is no byte-stream handler registered for .ogg, .oga or .opus, where
    /// .flac and .mp3 both have one. The Web Media Extensions are the documented
    /// fix, are installed here, and make no difference, because that package
    /// registers its handlers inside the package where only other *packaged*
    /// applications can see them.
    ///
    /// So this interface exists to let a second decoder stand beside the first
    /// for exactly the formats the first cannot open. See <see cref="OggDecoder"/>.
    /// </summary>
    internal interface ITrackDecoder : IDisposable
    {
        /// <summary>What the decoder is producing, after any conversion.</summary>
        int SampleRate { get; }
        int Channels { get; }

        /// <summary>Length in seconds, or 0 when the source will not say.</summary>
        double Duration { get; }

        /// <summary>Where the last frames handed out came from, in seconds.</summary>
        double Position { get; }

        /// <summary>
        /// Fills <paramref name="destination"/> with interleaved float frames and
        /// returns how many it managed. Fewer than asked means the file is done.
        /// </summary>
        int Read(float[] destination, int frames);

        /// <summary>Moves to a position in seconds. Anything buffered is dropped.</summary>
        bool SeekTo(double seconds);

        /// <summary>
        /// Asks for a different output format without reopening the file — what
        /// makes moving to a device that mixes at another rate cheap.
        /// </summary>
        bool Reopen(int sampleRate, int channels);
    }

    /// <summary>
    /// Opens a track with whichever decoder can take it.
    ///
    /// Media Foundation first, always, and not out of politeness: it is the one
    /// that hardware-accelerates, that handles every format the platform has
    /// ever added, and that this application chose in the first place. The
    /// managed decoders are for the gap it leaves.
    /// </summary>
    internal static class TrackDecoder
    {
        /// <summary>
        /// Returns an open decoder, or null with <paramref name="why"/> saying
        /// what both of them made of the file.
        /// </summary>
        public static ITrackDecoder? Open(string path,
            System.Runtime.InteropServices.ComTypes.IStream? stream,
            int sampleRate, int channels, out string why)
        {
            var extension = SafeExtension(path);

            // Straight to the managed decoder when Windows is known not to have
            // a handler for this kind of file. Not an optimisation — Media
            // Foundation fails in 33ms — but the diagnostic is much better for
            // it: an Ogg file that is genuinely corrupt should be reported by
            // something that got far enough to look inside it, rather than by
            // the layer that could not identify the container at all.
            if (OggDecoder.Handles(extension) && !AudioFiles.WindowsHasHandlerFor(extension))
            {
                var ogg = new OggDecoder();
                if (ogg.Open(path, stream, sampleRate, channels)) { why = ogg.Diagnostic; return ogg; }

                why = ogg.Diagnostic;
                ogg.Dispose();
                return null;
            }

            var media = new AudioDecoder();
            if (media.Open(path, stream, sampleRate, channels)) { why = media.Diagnostic; return media; }

            string mediaSaid = media.Diagnostic;
            media.Dispose();

            // Second chance for anything the managed side knows, whatever the
            // registry claimed. A handler being registered is not the same as it
            // working, and the file in front of us is the only real test.
            //
            // And for anything that *is* an Ogg file, whatever it is called. An
            // Ogg file named .mp3 — a download given the wrong name, a rip tool's
            // idea of a default — failed with "Windows cannot decode .mp3",
            // because this used to ask the extension and nothing else.
            if (OggDecoder.Handles(extension) || StartsWithOggS(path, stream))
            {
                var ogg = new OggDecoder();
                if (ogg.Open(path, stream, sampleRate, channels)) { why = ogg.Diagnostic; return ogg; }

                why = mediaSaid + "; " + ogg.Diagnostic;
                ogg.Dispose();
                return null;
            }

            why = mediaSaid;
            return null;
        }

        /// <summary>
        /// Whether the file begins with "OggS", the capture pattern every Ogg
        /// page starts with. Asked only after Media Foundation has failed, so it
        /// costs nothing on any file that plays.
        /// </summary>
        private static bool StartsWithOggS(string path,
            System.Runtime.InteropServices.ComTypes.IStream? stream)
        {
            var head = new byte[4];
            int got = 0;

            try
            {
                if (stream != null)
                {
                    stream.Seek(0, 0, IntPtr.Zero);
                    var read = System.Runtime.InteropServices.Marshal.AllocHGlobal(sizeof(int));
                    try
                    {
                        stream.Read(head, head.Length, read);
                        got = System.Runtime.InteropServices.Marshal.ReadInt32(read);
                    }
                    finally
                    {
                        System.Runtime.InteropServices.Marshal.FreeHGlobal(read);
                        try { stream.Seek(0, 0, IntPtr.Zero); } catch { }
                    }
                }
                else
                {
                    using var file = new FileStream(path, FileMode.Open, FileAccess.Read,
                        FileShare.ReadWrite | FileShare.Delete, 16);
                    while (got < head.Length)
                    {
                        int n = file.Read(head, got, head.Length - got);
                        if (n <= 0) break;
                        got += n;
                    }
                }
            }
            catch { return false; }

            return got == 4 && head[0] == (byte)'O' && head[1] == (byte)'g' &&
                   head[2] == (byte)'g' && head[3] == (byte)'S';
        }

        private static string SafeExtension(string path)
        {
            try { return Path.GetExtension(path) ?? ""; }
            catch { return ""; }
        }
    }
}
