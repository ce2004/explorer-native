using System;

namespace ExplorerNative
{
    /// <summary>
    /// Accumulates typed letters into a search prefix, the way every Windows
    /// list does: keep typing and the prefix grows, pause and it starts again.
    ///
    /// The control's own incremental search cannot do this here. A virtual
    /// ListView owns no items to search, so single letters were all that ever
    /// worked — typing "sy" in a folder jumped to something beginning with "s"
    /// and then to something beginning with "y", which in a folder of thousands
    /// is worse than useless.
    /// </summary>
    public sealed class TypeAheadBuffer
    {
        private string _text = "";
        private long _lastKeyAt;

        /// <summary>
        /// How long a pause ends the current word.
        ///
        /// A quarter of a second, which is deliberately quicker than the shell's
        /// own second. A short window means a letter typed on its own acts on its
        /// own — the single-letter cycling that walks the S entries stays
        /// responsive instead of waiting to see whether more is coming — while
        /// letters typed as one burst still make a word. Adjustable, because the
        /// right value is entirely a matter of how fast someone types.
        /// </summary>
        public int TimeoutMilliseconds { get; set; } = DefaultTimeoutMilliseconds;

        public const int DefaultTimeoutMilliseconds = 250;

        /// <summary>
        /// The word, or nothing if the pause has already ended it.
        ///
        /// The expiry used to happen only inside <see cref="Accept"/>, which
        /// means it is a decision made on the *next* keystroke and there is no
        /// such thing as the word having ended until then. Anything asking "is a
        /// word being typed right now" got the answer yes for ever.
        ///
        /// That is how Space stopped selecting. Space is part of a name while a
        /// word is being typed — "Program Files" has one — and toggles the row
        /// otherwise, so it is exactly the question above. Type three letters,
        /// pause, and the buffer still said a word was in progress: every Space
        /// from then on went into a search nobody was running instead of
        /// selecting the file, until something else reset it.
        /// </summary>
        public string Current(long nowMilliseconds)
        {
            if (Expired(nowMilliseconds)) _text = "";
            return _text;
        }

        private bool Expired(long nowMilliseconds) =>
            _text.Length > 0 && nowMilliseconds - _lastKeyAt > TimeoutMilliseconds;

        /// <summary>Adds a character and returns the prefix to search for.</summary>
        public string Accept(char c, long nowMilliseconds)
        {
            if (Expired(nowMilliseconds)) _text = "";
            _lastKeyAt = nowMilliseconds;
            _text += c;
            return _text;
        }

        public void Reset() => _text = "";
    }

    public static class TypeAhead
    {
        /// <summary>
        /// The row a search prefix should move to, or -1 for no match.
        ///
        /// Two behaviours, both inherited from how Windows lists have always
        /// worked, and both worth keeping because they are what a person's hands
        /// already expect:
        ///
        /// The same letter over and over cycles through everything starting with
        /// it — "s", "s", "s" walks the S entries — so it must always move on.
        ///
        /// A growing prefix is a refinement — "s", "sy", "sys" — so it starts
        /// from the current row and stays put while that row still matches,
        /// rather than jumping away and back.
        ///
        /// Indexed, so the caller need not materialise a list of names on every
        /// keystroke — in a folder of a hundred thousand rows that allocation
        /// would cost more than the search.
        /// </summary>
        public static int Find(int count, Func<int, string?> nameAt, string query, int currentIndex)
        {
            if (count <= 0 || nameAt == null || string.IsNullOrEmpty(query)) return -1;

            // Both sides without their accents, so "e" reaches "Éclair" and
            // "étoile" — which the list sorts among the E names (see
            // NameRules.CompareNames), so the jump lands where the eye and the
            // ear expect the E names to start.
            var folded = Fold(query);
            bool cycling = IsAllOneCharacter(folded, out int first);
            string prefix = cycling ? folded[..first] : folded;

            // Cycling must leave the current row; refining may stay on it.
            int start = cycling ? currentIndex + 1 : currentIndex;
            if (start < 0 || start >= count) start = 0;

            for (int step = 0; step < count; step++)
            {
                int index = (start + step) % count;
                var name = nameAt(index);
                if (name != null && Fold(name).StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    return index;
            }

            return -1;
        }

        /// <summary>
        /// True for "s", "ss", "sss" — a single character is the same case as a
        /// repeat of one, because a lone letter has always meant "next one of
        /// these", not "the first one of these".
        ///
        /// A character is a whole code point, not a UTF-16 unit: an emoji is two
        /// units, and "🎶🎶" compared unit by unit was not a repeat, so it
        /// searched for "🎶🎶" and said nothing starts with it.
        /// <paramref name="firstLength"/> is how long the first one is.
        /// </summary>
        private static bool IsAllOneCharacter(string query, out int firstLength)
        {
            // A lone surrogate is not a character, but it is still a key: one unit.
            if (!System.Text.Rune.TryGetRuneAt(query, 0, out var firstRune))
            {
                firstLength = 1;
                for (int k = 1; k < query.Length; k++)
                    if (query[k] != query[0]) return false;
                return true;
            }
            firstLength = firstRune.Utf16SequenceLength;
            var upper = System.Text.Rune.ToUpperInvariant(firstRune);

            for (int i = firstLength; i < query.Length;)
            {
                if (!System.Text.Rune.TryGetRuneAt(query, i, out var rune)) return false;
                if (System.Text.Rune.ToUpperInvariant(rune) != upper) return false;
                i += rune.Utf16SequenceLength;
            }
            return true;
        }

        /// <summary>
        /// A name with its accents taken off: "Éclair" is "Eclair", "Ärger" is
        /// "Arger". Plain ASCII, which is nearly every name, comes back as it is
        /// without allocating, because this runs per row on every keystroke.
        ///
        /// Decomposed by Windows itself (NormalizeString), not by
        /// string.Normalize: the application runs with InvariantGlobalization,
        /// where string.Normalize leaves everything above ASCII untouched —
        /// measured, "Éclair" comes back unchanged.
        /// </summary>
        public static string Fold(string text)
        {
            int i = 0;
            while (i < text.Length && text[i] < 0x80) i++;
            if (i == text.Length) return text;

            var decomposed = Decompose(text);
            var kept = new System.Text.StringBuilder(decomposed.Length);
            foreach (char c in decomposed)
            {
                var category = char.GetUnicodeCategory(c);
                if (category is System.Globalization.UnicodeCategory.NonSpacingMark
                    or System.Globalization.UnicodeCategory.SpacingCombiningMark
                    or System.Globalization.UnicodeCategory.EnclosingMark) continue;
                kept.Append(c);
            }
            return kept.ToString();
        }

        private static string Decompose(string text)
        {
            const int NormalizationD = 2;
            try
            {
                int size = NormalizeString(NormalizationD, text, text.Length, null, 0);
                for (int attempt = 0; attempt < 3 && size > 0; attempt++)
                {
                    var buffer = new char[size];
                    int written = NormalizeString(NormalizationD, text, text.Length, buffer, buffer.Length);
                    if (written > 0) return new string(buffer, 0, written);

                    // ERROR_INSUFFICIENT_BUFFER: the first answer was an estimate.
                    if (System.Runtime.InteropServices.Marshal.GetLastWin32Error() != 122) break;
                    size = -written;
                    if (size <= buffer.Length) size = buffer.Length * 2;
                }
            }
            catch { }
            return text;
        }

        [System.Runtime.InteropServices.DllImport("kernel32.dll",
            CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
        private static extern int NormalizeString(int form, string source, int sourceLength,
            char[]? destination, int destinationLength);
    }
}
