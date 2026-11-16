using System;

namespace ExplorerNative
{
    /// <summary>
    /// One row. Built once during enumeration, formatted on demand.
    /// MainForm (and the plain classes it calls) know it as Entry.
    /// </summary>
    internal sealed record ListEntry(string Path, string Name, bool IsDir, long Size, DateTime Modified)
    {
        public string? SizeOverride { get; set; }

        /// <summary>
        /// What the Name column says, when that is not simply the file's
        /// name. Search results use it to carry the folder a hit came out of
        /// "Lovesick.flac, in 2019", because a results list gathers names
        /// from all over a tree and three rows called the same thing with no
        /// way to tell them apart is the one thing it must not do.
        /// </summary>
        public string? DisplayOverride { get; set; }

        /// <summary>
        /// The name's key for <see cref="NameRules.CompareNames(string, byte[], string, string, byte[], string)"/>,
        /// while a listing is being sorted, and null otherwise. Made on the
        /// worker, read by the merge on the UI thread, and dropped once the
        /// listing is in order: nothing sorts it again, and it would be about
        /// eighty bytes a row kept for nothing. A null key is never wrong,
        /// only slower, so a reader racing the drop is still right.
        /// </summary>
        public byte[]? NameKey;

        /// <summary>
        /// Type-ahead's view of the row, folded once rather than on every
        /// keystroke: <c>TypeAhead.Fold</c> of what the Name column shows with
        /// extensions on. For plain ASCII that is the name itself and costs
        /// nothing. Made on the worker as a folder is listed, or on first use
        /// for rows made anywhere else.
        /// </summary>
        private string? _folded;

        /// <summary>
        /// How much of <see cref="_folded"/> is the name without its
        /// extension, or -1 until it is asked. A slice rather than a second
        /// string, because folding works a character at a time and a plain
        /// extension folds to itself; <see cref="_foldedStem"/> is for the
        /// rare name where that cannot be shown to hold.
        /// </summary>
        private int _stemLength = -1;
        private string? _foldedStem;

        /// <summary>Folds now, off the UI thread, so the first keystroke does not.</summary>
        public void PrepareTypeAhead()
        {
            var whole = _folded = TypeAhead.Fold(DisplayOverride ?? Name);
            MeasureStem(whole);
        }

        /// <summary>
        /// What type-ahead matches against: <c>TypeAhead.Fold(DisplayNameOf(entry))</c>,
        /// without building either string on the keystroke.
        /// </summary>
        public ReadOnlySpan<char> FoldedDisplayName(bool showExtensions)
        {
            var whole = _folded ??= TypeAhead.Fold(DisplayOverride ?? Name);
            if (showExtensions || DisplayOverride != null || IsDir) return whole;

            if (_stemLength < 0) MeasureStem(whole);
            return _foldedStem ?? whole.AsSpan(0, _stemLength);
        }

        private void MeasureStem(string whole)
        {
            int dot = Name.LastIndexOf('.');
            if (DisplayOverride != null || IsDir || dot <= 0) { _stemLength = whole.Length; return; }

            // Plain ASCII: the fold is the name, and the stem is up to the dot.
            if (ReferenceEquals(whole, Name)) { _stemLength = dot; return; }

            // Otherwise the fold of the name is the fold of its stem followed
            // by the fold of its extension, which for a plain extension is
            // the extension. Checked against folding the stem on its own for
            // 591,000 names, real ones and ones built to break it.
            var extension = Name.AsSpan(dot);
            if (System.Text.Ascii.IsValid(extension) && whole.AsSpan().EndsWith(extension, StringComparison.Ordinal))
            {
                _stemLength = whole.Length - extension.Length;
                return;
            }

            _foldedStem = TypeAhead.Fold(System.IO.Path.GetFileNameWithoutExtension(Name));
            _stemLength = _foldedStem.Length;
        }
    }
}
