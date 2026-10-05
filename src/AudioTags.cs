using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;

namespace ExplorerNative
{
    /// <summary>One line of the song properties sheet.</summary>
    public sealed record SongProperty(string Name, string Value);

    /// <summary>A track's tags as values, for Explorer Native Connect's stat.</summary>
    public sealed record SongSummary(string? Title, string? Artist, string? Album, int? Year, int? Track,
        double? DurationSeconds);

    /// <summary>
    /// What a music file says about itself, read through the Windows property
    /// system — the same store File Explorer's Details pane reads.
    ///
    /// No tag parser of our own, and no library. Windows already knows how to
    /// read ID3, Vorbis comments and MP4 atoms, and asking it means the numbers
    /// here are the numbers Explorer shows for the same file.
    ///
    /// Everything is best-effort: a missing property is a row that is not there,
    /// never an error. A file with no tags at all still has a duration, a sample
    /// rate and a channel count.
    /// </summary>
    public static class AudioTags
    {
        /// <summary>
        /// What was read, as a catalogue id and the sentence for it. Static because
        /// the reading is: a worker started by the player when a track begins, and
        /// another started by the properties sheet when one is asked for. Null
        /// changes nothing, and it is called on whichever worker did the reading.
        /// </summary>
        public static Action<string, string>? Notify;

        private static void Raise(string notificationId, string message)
        {
            var notify = Notify;
            if (notify == null) return;
            try { notify(notificationId, message); } catch { }
        }

        private const uint GPS_BESTEFFORT = 0x40;

        [StructLayout(LayoutKind.Sequential)]
        private struct PropertyKey
        {
            public Guid FormatId;
            public uint Pid;
            public PropertyKey(string guid, uint pid) { FormatId = new Guid(guid); Pid = pid; }
        }

        /// <summary>
        /// Sixteen bytes on 32-bit, twenty-four on 64-bit — the union is two
        /// pointers wide. Never read directly; it is handed straight back to
        /// propsys for conversion and then cleared.
        /// </summary>
        [StructLayout(LayoutKind.Sequential)]
        private struct PropVariant
        {
            public ushort Type;
            public ushort Reserved1, Reserved2, Reserved3;
            public IntPtr Value1;
            public IntPtr Value2;
        }

        [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"),
         InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IPropertyStore
        {
            void GetCount(out uint count);
            void GetAt(uint index, out PropertyKey key);
            [PreserveSig] int GetValue(ref PropertyKey key, out PropVariant value);
            void SetValue(ref PropertyKey key, ref PropVariant value);
            void Commit();
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
        private static extern void SHGetPropertyStoreFromParsingName(
            string path, IntPtr bindContext, uint flags, ref Guid iid,
            [MarshalAs(UnmanagedType.Interface)] out IPropertyStore store);

        [DllImport("propsys.dll", PreserveSig = true)]
        private static extern int PropVariantToStringAlloc(ref PropVariant value, out IntPtr text);

        [DllImport("propsys.dll", PreserveSig = true)]
        private static extern int PropVariantToUInt64(ref PropVariant value, out ulong number);

        [DllImport("ole32.dll", PreserveSig = true)]
        private static extern int PropVariantClear(ref PropVariant value);

        // The format ids are the three that carry everything a music file has to
        // say: the summary block, the music block, and the audio block.
        private const string Summary = "F29F85E0-4FF9-1068-AB91-08002B27B3D9";
        private const string Music = "56A3372E-CE9C-11D2-9F0E-006097C686F6";
        private const string Audio = "64440490-4C8B-11D1-8B70-080036B11A03";

        private static readonly PropertyKey Title = new(Summary, 2);
        private static readonly PropertyKey Artist = new(Music, 2);
        private static readonly PropertyKey AlbumTitle = new(Music, 4);
        private static readonly PropertyKey Year = new(Music, 5);
        private static readonly PropertyKey TrackNumber = new(Music, 7);
        private static readonly PropertyKey Genre = new(Music, 11);
        private static readonly PropertyKey AlbumArtist = new(Music, 13);
        // Not in the music block: PKEY_Music_Composer lives in the media file
        // summary block, and asked for in the other the row never appeared.
        private static readonly PropertyKey Composer = new("64440492-4C8B-11D1-8B70-080036B11A03", 19);

        private static readonly PropertyKey Duration = new(Audio, 3);
        private static readonly PropertyKey Bitrate = new(Audio, 4);
        private static readonly PropertyKey SampleRate = new(Audio, 5);
        private static readonly PropertyKey SampleSize = new(Audio, 6);
        private static readonly PropertyKey Channels = new(Audio, 7);

        /// <summary>
        /// The title the file claims, or null. Used when announcements are set to
        /// prefer the tag over the file name.
        ///
        /// Slow enough to matter on a network share, so this belongs on a worker.
        /// </summary>
        public static string? TitleOf(string path)
        {
            try
            {
                var store = Open(path);
                if (store == null) return null;
                try
                {
                    var artist = Text(store, Artist);
                    var title = Text(store, Title);

                    if (title == null)
                    {
                        // A file with an artist and no title still has tags. Only
                        // the empty pair is a file that says nothing about itself,
                        // and the name on disk is all there is to call it.
                        if (artist == null)
                            Raise("audiofile.tags.none", Path.GetFileName(path) + " has no tags");
                        return null;
                    }

                    Raise("audiofile.tags", artist == null ? title : $"{title} by {artist}");
                    return artist == null ? title : artist + " — " + title;
                }
                finally { Release(store); }
            }
            catch { return null; }
        }

        /// <summary>
        /// Everything worth showing, in the order it is worth hearing. Runs on a
        /// worker; the dialog that shows it does not build until this returns.
        /// </summary>
        public static IReadOnlyList<SongProperty> Read(string path)
        {
            var rows = new List<SongProperty>();

            try { rows.Add(new SongProperty("File", Path.GetFileName(path))); }
            catch { }

            IPropertyStore? store = null;
            try { store = Open(path); } catch { }

            if (store != null)
            {
                try
                {
                    Add(rows, "Title", Text(store, Title));
                    Add(rows, "Artist", Text(store, Artist));
                    Add(rows, "Album artist", Text(store, AlbumArtist));
                    Add(rows, "Album", Text(store, AlbumTitle));
                    Add(rows, "Composer", Text(store, Composer));
                    Add(rows, "Genre", Text(store, Genre));
                    // Guarded like every other number here. A year of zero and a
                    // track zero are both "not set" written as a digit, and a
                    // properties list padded with those is worse than a short one.
                    var year = Number(store, Year);
                    if (year is > 0) Add(rows, "Year", year.Value.ToString());

                    var track = Number(store, TrackNumber);
                    if (track is > 0) Add(rows, "Track", track.Value.ToString());

                    // Duration is stored in hundred-nanosecond units, and spoken
                    // rather than punctuated: "4 minutes 3", not "00:04:03",
                    // which a screen reader reads as a row of numbers.
                    var hundredNanoseconds = Number(store, Duration);
                    if (hundredNanoseconds is > 0)
                        Add(rows, "Length", AudioPlayer.SpokenTime(hundredNanoseconds.Value / 10_000_000.0));

                    var bits = Number(store, Bitrate);
                    if (bits is > 0) Add(rows, "Bitrate", $"{bits.Value / 1000:N0} kilobits per second");

                    var rate = Number(store, SampleRate);
                    if (rate is > 0) Add(rows, "Sample rate", $"{rate.Value:N0} hertz");

                    var depth = Number(store, SampleSize);
                    if (depth is > 0) Add(rows, "Sample size", $"{depth.Value} bit");

                    var channels = Number(store, Channels);
                    if (channels is > 0) Add(rows, "Channels", ChannelWord((int)channels.Value));
                }
                catch { }
                finally { Release(store); }
            }

            // The plain facts, which are worth having even for a file Windows can
            // tell us nothing else about.
            try
            {
                var info = new FileInfo(path);
                if (info.Exists)
                {
                    Add(rows, "Size", SizeFormatter.Format(info.Length, SizeUnitStyle.FullWords));
                    Add(rows, "Modified", info.LastWriteTime.ToString("f"));
                }
                Add(rows, "Folder", Path.GetDirectoryName(path));
            }
            catch { }

            return rows;
        }

        /// <summary>
        /// The few facts a phone shows about a track, as values rather than as
        /// sentences: <see cref="Read"/> speaks the length ("4 minutes 3"), and a
        /// client wants the number. Null when the file says nothing at all.
        /// Runs on a worker, like everything else here.
        /// </summary>
        public static SongSummary? ReadSummary(string path)
        {
            IPropertyStore? store = null;
            try { store = Open(path); } catch { }
            if (store == null) return null;
            try
            {
                var year = Number(store, Year);
                var track = Number(store, TrackNumber);
                var duration = Number(store, Duration);
                var summary = new SongSummary(
                    Text(store, Title), Text(store, Artist), Text(store, AlbumTitle),
                    year is > 0 and < 10000 ? (int)year.Value : null,
                    track is > 0 and < 100000 ? (int)track.Value : null,
                    duration is > 0 ? Math.Round(duration.Value / 10_000_000.0, 3) : null);
                return summary.Title == null && summary.Artist == null && summary.Album == null &&
                       summary.Year == null && summary.Track == null && summary.DurationSeconds == null
                    ? null
                    : summary;
            }
            catch { return null; }
            finally { Release(store); }
        }

        [DllImport("propsys.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
        private static extern int PSGetPropertyKeyFromName(string name, out PropertyKey key);

        /// <summary>
        /// Properties by canonical name ("System.Image.HorizontalSize"), as text, for whichever of them the file
        /// has. Local files only, on a worker: it opens the file through the shell's property handler.
        /// </summary>
        public static Dictionary<string, string> ReadNamed(string path, IEnumerable<string> names)
        {
            var found = new Dictionary<string, string>(StringComparer.Ordinal);
            IPropertyStore? store = null;
            try { store = Open(path); } catch { }
            if (store == null) return found;
            try
            {
                foreach (var name in names)
                {
                    try
                    {
                        if (PSGetPropertyKeyFromName(name, out var key) < 0) continue;
                        var text = Text(store, key);
                        if (!string.IsNullOrWhiteSpace(text)) found[name] = text.Trim();
                    }
                    catch { }
                }
            }
            finally { Release(store); }
            return found;
        }

        private static string ChannelWord(int count) => count switch
        {
            1 => "1, mono",
            2 => "2, stereo",
            6 => "6, 5.1 surround",
            8 => "8, 7.1 surround",
            _ => count.ToString(),
        };

        private static void Add(List<SongProperty> rows, string name, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value)) rows.Add(new SongProperty(name, value.Trim()));
        }

        private static IPropertyStore? Open(string path)
        {
            var iid = typeof(IPropertyStore).GUID;

            // Best effort: a file type with no property handler returns an empty
            // store rather than failing, which is exactly what is wanted here.
            SHGetPropertyStoreFromParsingName(path, IntPtr.Zero, GPS_BESTEFFORT, ref iid, out var store);
            return store;
        }

        private static void Release(IPropertyStore store)
        {
            try { Marshal.ReleaseComObject(store); } catch { }
        }

        private static string? Text(IPropertyStore store, PropertyKey key)
        {
            var pk = key;
            if (store.GetValue(ref pk, out var value) < 0) return null;
            try
            {
                // Handles the multi-valued case too: several artists come back
                // as one "A; B" string rather than as nothing at all.
                if (PropVariantToStringAlloc(ref value, out var text) < 0 || text == IntPtr.Zero) return null;
                try
                {
                    // A property that is not there converts to an empty string
                    // rather than failing, so "absent" and "blank" arrive looking
                    // identical. They are the same thing here, and treating them
                    // as different is how a file with no tags got announced as
                    // " — " instead of by its name.
                    var read = Marshal.PtrToStringUni(text);
                    return string.IsNullOrWhiteSpace(read) ? null : read;
                }
                finally { Marshal.FreeCoTaskMem(text); }
            }
            finally { PropVariantClear(ref value); }
        }

        private const ushort VT_EMPTY = 0;

        private static ulong? Number(IPropertyStore store, PropertyKey key)
        {
            var pk = key;
            if (store.GetValue(ref pk, out var value) < 0) return null;
            try
            {
                // A property that is not there converts to zero rather than
                // failing, so "absent" and "zero" arrive looking identical. The
                // variant type is the only thing that tells them apart, and
                // without this a file with no tags listed "Year: 0".
                if (value.Type == VT_EMPTY) return null;

                return PropVariantToUInt64(ref value, out ulong number) < 0 ? null : number;
            }
            finally { PropVariantClear(ref value); }
        }
    }
}
