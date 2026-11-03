using System;
using System.Collections.Concurrent;
using System.IO;

namespace ExplorerNative
{
    /// <summary>
    /// Passwords for protected archives: asked once, kept for the session,
    /// never written anywhere and never logged.
    /// </summary>
    public static class ArchivePasswords
    {
        /// <summary>
        /// Asks for an archive's password: the archive's path, whether the last
        /// one was wrong. Returns null when the person cancels. Set by the
        /// window; called from workers. Null when nobody can be asked.
        /// </summary>
        public static Func<string, bool, string?>? Asker { get; set; }

        private static readonly ConcurrentDictionary<string, string> _known = new(StringComparer.OrdinalIgnoreCase);
        private static readonly ConcurrentDictionary<string, object> _gates = new(StringComparer.OrdinalIgnoreCase);

        private static string Key(string archivePath)
        {
            try { return Path.GetFullPath(archivePath); } catch { return archivePath; }
        }

        /// <summary>The password that opened this archive earlier this session, if any.</summary>
        public static string? Known(string archivePath) =>
            _known.TryGetValue(Key(archivePath), out var password) ? password : null;

        public static void Remember(string archivePath, string password) => _known[Key(archivePath)] = password;

        public static void Forget(string archivePath) => _known.TryRemove(Key(archivePath), out _);

        /// <summary>For the tests.</summary>
        internal static void ForgetAll() => _known.Clear();

        /// <summary>What the prompt says the first time.</summary>
        public static string Question(string archivePath) => $"{Path.GetFileName(archivePath)} needs a password";

        /// <summary>What the prompt says after a wrong one.</summary>
        public static string WrongSaid(string archivePath) =>
            $"That password is not right for {Path.GetFileName(archivePath)}. Type it again";

        /// <summary>
        /// The password to try next, after <paramref name="tried"/> failed (null
        /// when nothing was tried yet). One question per archive at a time: a
        /// second job on the same archive takes the answer the first one got.
        /// </summary>
        /// <returns>Null when nobody can be asked.</returns>
        /// <exception cref="Declined">The person cancelled.</exception>
        public static string? Next(string archivePath, string? tried)
        {
            if (tried != null && Known(archivePath) == tried) Forget(archivePath);
            var asker = Asker;
            if (asker == null) return null;

            lock (_gates.GetOrAdd(Key(archivePath), _ => new object()))
            {
                var known = Known(archivePath);
                if (known != null && known != tried) return known;
                var typed = asker(archivePath, tried != null);
                if (typed == null) throw new Declined();
                // Kept at once, so a second job waiting here does not ask again;
                // forgotten again if it turns out wrong.
                Remember(archivePath, typed);
                return typed;
            }
        }

        /// <summary>The person cancelled the password question. Nothing more is said.</summary>
        public sealed class Declined : OperationCanceledException
        {
            public Declined() : base("No password was given") { }
        }
    }
}
