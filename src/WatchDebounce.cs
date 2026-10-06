using System;
using System.Collections.Generic;

namespace ExplorerNative
{
    /// <summary>
    /// When the folder watcher should re-read a folder: once changes have
    /// stopped, and never later than a ceiling after the first one.
    ///
    /// It used to be a timer firing every 600ms for the life of the window,
    /// re-reading whenever a flag was up. A file being downloaded is written
    /// every few milliseconds, so that was a full reload every 600ms for as long
    /// as the download ran: eleven reloads for six seconds of one file, and 26
    /// for five thousand files arriving over fifteen seconds. Each reload lists,
    /// sorts and re-indexes the whole folder on the UI thread.
    ///
    /// Two rules now. A change restarts the wait (<see cref="QuietMilliseconds"/>),
    /// so a burst is read once after it ends. And the wait never runs past
    /// <see cref="MaxWaitMilliseconds"/> from the first change, so a folder that
    /// never stops changing still shows what is in it every few seconds.
    ///
    /// A change to a file the list already shows — its size or date — is not a
    /// reason to re-read the folder at all. Those paths are collected
    /// (<see cref="Take"/>) and the window updates the rows in place, so a
    /// download in progress costs one reload when it appears and nothing after.
    ///
    /// Thread-safe: the watcher's events arrive on pool threads and the window
    /// reads this on its own.
    /// </summary>
    public sealed class WatchDebounce
    {
        public const int DefaultQuietMilliseconds = 500;
        public const int DefaultMaxWaitMilliseconds = 3000;

        public int QuietMilliseconds { get; init; } = DefaultQuietMilliseconds;
        public int MaxWaitMilliseconds { get; init; } = DefaultMaxWaitMilliseconds;

        /// <summary>More than this many changed files in one burst is a reload, not a list of rows.</summary>
        public const int MostInPlace = 256;

        private readonly object _gate = new();
        private long _first = -1;
        private long _last;
        private bool _reload;
        private readonly HashSet<string> _changed = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Something was created, deleted, renamed, or the change list was lost:
        /// the folder has to be read again. True when this starts a new wait, so
        /// the caller knows to arm its timer.
        /// </summary>
        public bool Reload(long now)
        {
            lock (_gate)
            {
                bool started = _first < 0;
                Touch(now);
                _reload = true;
                return started;
            }
        }

        /// <summary>
        /// A file the folder already holds changed. Its row can be updated in
        /// place; too many at once and the whole folder is read instead.
        /// </summary>
        public bool Changed(string path, long now)
        {
            lock (_gate)
            {
                bool started = _first < 0;
                Touch(now);
                if (!_reload)
                {
                    _changed.Add(path);
                    if (_changed.Count > MostInPlace) { _reload = true; _changed.Clear(); }
                }
                return started;
            }
        }

        private void Touch(long now)
        {
            if (_first < 0) _first = now;
            _last = now;
        }

        public bool Pending { get { lock (_gate) return _first >= 0; } }

        /// <summary>
        /// Milliseconds until the wait is over: 0 when it is over now, -1 when
        /// nothing is waiting.
        /// </summary>
        public int DueIn(long now)
        {
            lock (_gate)
            {
                if (_first < 0) return -1;
                long due = Math.Min(_last + QuietMilliseconds, _first + MaxWaitMilliseconds);
                return (int)Math.Clamp(due - now, 0, int.MaxValue);
            }
        }

        /// <summary>
        /// Ends the wait and says what it came to: a full reload, or the files
        /// whose rows want updating (empty when neither).
        /// </summary>
        public (bool Reload, List<string> Changed) Take()
        {
            lock (_gate)
            {
                var changed = _reload ? new List<string>() : new List<string>(_changed);
                bool reload = _reload;
                _first = -1;
                _reload = false;
                _changed.Clear();
                return (reload, changed);
            }
        }

        /// <summary>Forgets everything waiting: the folder was just read anyway.</summary>
        public void Clear()
        {
            lock (_gate)
            {
                _first = -1;
                _reload = false;
                _changed.Clear();
            }
        }
    }
}
