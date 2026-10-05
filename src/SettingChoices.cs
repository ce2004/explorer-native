using System.Collections.Generic;

namespace ExplorerNative
{
    /// <summary>
    /// The numbers a settings drop-down should offer between two bounds.
    ///
    /// Its own file, and public, because it was wrong for a long time in a way
    /// nothing could see. It lived as a private helper inside
    /// <see cref="SettingsForm"/>, which needs a message loop and is not
    /// compiled into the self-test at all, so the one part of that dialog that
    /// is pure arithmetic — and the part that decides whether any of its
    /// controls are usable — was the part with no test on it.
    ///
    /// <para>
    /// What it got wrong: the step was worked out from how *wide* the range was
    /// and then applied flat across the whole of it. "Give the memory back after
    /// paused (minutes)" runs 0 to 1440, so the step came out at 100 and the
    /// list was 0, 100, 200 … 1400, with the saved value of ten inserted as a
    /// one-off in the middle. From a default of ten minutes the only reachable
    /// settings were ten and a hundred: five, fifteen, twenty and thirty — every
    /// figure anybody would actually choose — could not be expressed, and one
    /// press of the down arrow went from ten minutes to an hour and forty.
    /// "Skip by (seconds)" was 1, 10, 25, 50; most of the rest were the same.
    /// </para>
    ///
    /// <para>
    /// Round numbers are round at their own magnitude. Nobody wants 325 minutes
    /// and everybody wants 15, so the step grows with the *value* rather than
    /// with the span. The lists come out longer and every entry in one is a
    /// number a person would pick, which is the trade worth making — these are
    /// drop-down lists, so a typed digit jumps straight into them.
    /// </para>
    /// </summary>
    public static class SettingChoices
    {
        /// <summary>
        /// How far apart the offered values should be, around a given value.
        ///
        /// A ladder rather than a formula, because the thresholds are a judgement
        /// about what people ask for and not a calculation. Same reasoning as
        /// <see cref="AudioPlayer.SpeedLadder"/>: close together where a small
        /// change is worth having, wide apart where it is not.
        /// </summary>
        public static long StepAt(long value) => value switch
        {
            < 30 => 1,
            < 100 => 5,
            < 300 => 10,
            < 1000 => 25,
            < 3000 => 100,
            < 10000 => 250,
            < 30000 => 1000,
            < 100000 => 5000,
            _ => 25000,
        };

        /// <summary>
        /// The ladder for a range, with both ends on it and the value the setting
        /// currently holds.
        ///
        /// <paramref name="current"/> is always included: a value already saved
        /// must never be silently changed just because this list would not have
        /// suggested it. That covers a figure carried over from an older build
        /// and one somebody put in the file by hand.
        /// </summary>
        public static List<int> Between(int min, int max, int current)
        {
            if (max < min) (min, max) = (max, min);

            var values = new SortedSet<int> { min, max };

            long v = min;
            while (v < max)
            {
                // The next multiple of the step, not the next step along, so a
                // range starting at 64 gives 65, 70, 75 rather than 64, 69, 74.
                // A ladder of round numbers has to actually land on them.
                long step = StepAt(v);
                long next = (v / step + 1) * step;
                if (next <= v) next = v + step;
                if (next > max) break;

                values.Add((int)next);
                v = next;
            }

            if (current >= min && current <= max) values.Add(current);
            return new List<int>(values);
        }
    }
}
