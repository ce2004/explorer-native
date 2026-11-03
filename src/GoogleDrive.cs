using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace ExplorerNative
{
    /// <summary>
    /// Google Drive as a drive letter, for the rest of the application to use
    /// without knowing it is there.
    ///
    /// Below this is a real file system on WinFsp (4.3.0; see DriveMount): a
    /// drive every program reads and writes, whose changes all go to Google.
    /// Enumeration, tags, robocopy, the shell context menu and the media engine
    /// keep working unchanged, and so do other programs' saves, moves and
    /// renames — which a Cloud Files sync root never sent anywhere.
    ///
    /// Folders fill in as they are opened, by any program: one Drive listing
    /// each. See <see cref="Populate"/>, which pays that cost up front on
    /// navigation.
    /// </summary>
    /// <summary>
    /// A file in Drive, read straight from Google by byte range — what the
    /// player downloads a track through, rather than through the letter. The
    /// letter would answer the same reads from this process's own provider,
    /// holding a second copy in its own cache, and a read of an entry on the letter left
    /// pending when a process ends leaves it unable to finish exiting.
    /// </summary>
    internal sealed class DriveRangeSource : IRangeSource
    {
        private readonly DriveClient _client;
        private readonly string _id;

        public DriveRangeSource(DriveClient client, string id, long length)
        {
            _client = client;
            _id = id;
            Length = length;
        }

        public long Length { get; }

        /// <summary>
        /// Four, the measured best for 4MB ranges: one stream is about 1MB/s and
        /// it scales nearly linearly to four, then flattens.
        /// </summary>
        public int Streams => 4;

        public async Task<int> ReadAsync(long offset, Memory<byte> into, CancellationToken token)
        {
            int want = (int)Math.Min(into.Length, Length - offset);
            if (want <= 0) return 0;
            var bytes = await _client.ReadRange(_id, offset, want, token).ConfigureAwait(false);
            int got = Math.Min(bytes.Length, want);
            bytes.AsSpan(0, got).CopyTo(into.Span);
            return got;
        }

        public void Dispose() { }
    }

    /// <summary>
    /// Told about each file a copy inside Drive makes: when it is asked for,
    /// and when Google has made it, with its size.
    /// </summary>
    public sealed class DriveCopyReport
    {
        public Action<string>? OnStarting { get; init; }
        public Action<string, long>? OnCopied { get; init; }

        /// <summary>
        /// A file or folder inside a folder being copied that Drive would not
        /// copy, with why. Set, the copy carries on with the rest and the
        /// caller lists these; unset, the first one ends the copy as before.
        /// One file whose owner had turned copying off used to stop a whole
        /// folder part way, everything after it left behind.
        /// </summary>
        public Action<string, string>? OnFailed { get; init; }

        public void Starting(string name) { try { OnStarting?.Invoke(name); } catch { } }
        public void Copied(string name, long bytes) { try { OnCopied?.Invoke(name, bytes); } catch { } }
        public void Failed(string name, string why) { try { OnFailed?.Invoke(name, why); } catch { } }

        /// <summary>
        /// Whether a failure inside a folder copy is one to note and carry on
        /// past, noting it if so. Never a cancellation, and never a full
        /// account or a spent day, which stop the whole copy.
        /// </summary>
        internal static bool CarriesOn(Exception error, DriveCopyReport? report, object gate, string name,
            string why, CancellationToken token)
        {
            if (report?.OnFailed == null) return false;
            if (error is OperationCanceledException && token.IsCancellationRequested) return false;
            if (error is DriveQuotaException) return false;
            lock (gate) report.Failed(name, why + error.Message);
            return true;
        }
    }

    public sealed class GoogleDrive : IDisposable
    {
        /// <summary>
        /// Downloaded Drive audio held in memory, across every file. A request
        /// to Google costs about 700ms whatever is in it, so reads are served
        /// from here rather than the network.
        /// </summary>
        private const int CacheMegabytes = 1024;

        /// <summary>
        /// How long a Drive file may sit unread before its download and memory
        /// are given back: long enough to copy, switch windows and paste.
        /// </summary>
        private const int CacheIdleSeconds = 120;

        private readonly object _gate = new();
        private DriveMount? _mount;
        private GoogleAuth? _auth;

        static GoogleDrive()
        {
            DriveSizeIndex.Log = Note;
            // Signed out: a refresh still running, or the unmount after it, must not write a copy back.
            DriveSizeIndex.MaySave = () => HasSavedSignIn;
        }

        /// <summary>
        /// The Drive API itself. Held because the write operations need it, and
        /// they are addressed by path rather than by id — only this class knows
        /// how to get from one to the other.
        /// </summary>
        private DriveClient? _client;

        /// <summary>
        /// The Drive API while Drive is mounted, otherwise null. The folder
        /// monitor talks to Drive through this, never through the letter.
        /// </summary>
        internal DriveClient? Client { get { lock (_gate) return _mount != null ? _client : null; } }

        /// <summary>
        /// Raised when the stored sign-in has expired and something tried to use
        /// it. Roughly weekly, by Google's design — see
        /// <see cref="GoogleSignInRequiredException"/>.
        /// </summary>
        public event Action? SignInRequired;

        /// <summary>
        /// Raised when Drive stops answering, and again when it comes back.
        /// Once per change, never per file.
        /// </summary>
        public event Action<DriveState>? ConnectionChanged;

        private Action<string, string>? _notification;

        /// <summary>
        /// Everything this layer has to say to a person, as (notification id,
        /// the sentence). The id decides whether anybody hears it and where; the
        /// sentence is built here because this is where the values are.
        ///
        /// Two things about the thread it arrives on. Several of these are raised
        /// from inside a request the drive is answering for Windows, which is
        /// holding up whoever is listing a folder or reading a track — so a
        /// handler must hand the message on and return, never block,
        /// never speak inline. And the raise itself is wrapped, because a handler
        /// that throws in there would fail the read that happened to be underway.
        ///
        /// Subscribing is what turns any of it on: with nobody listening the
        /// layers below are handed nothing and build no strings at all.
        /// </summary>
        public event Action<string, string>? Notification
        {
            add { _notification += value; Rewire(); }
            remove { _notification -= value; Rewire(); }
        }

        private Action<string, string>? Forward =>
            _notification == null ? null : Say;

        private static readonly AsyncLocal<Action<string, string>?> _sayTo = new();

        /// <summary>
        /// Until disposed, what this call flow would say on the PC goes to <paramref name="to"/> instead (the web
        /// app: the phone hears the real reason, and the PC stays quiet).
        /// </summary>
        public static IDisposable SayingTo(Action<string, string> to)
        {
            var before = _sayTo.Value;
            _sayTo.Value = to;
            return new SayRestore(before);
        }

        private sealed class SayRestore(Action<string, string>? before) : IDisposable
        {
            public void Dispose() => _sayTo.Value = before;
        }

        private void Say(string id, string message)
        {
            if (_sayTo.Value is { } to)
            {
                try { to(id, message); } catch { }
                return;
            }
            var handler = _notification;
            if (handler == null) return;
            try { handler(id, message); } catch { }
        }

        /// <summary>
        /// Pushes the forwarder down, or takes it away again. Called whenever the
        /// subscription changes, because a mount that is already up would
        /// otherwise stay silent until the next one.
        /// </summary>
        private void Rewire()
        {
            var forward = Forward;
            lock (_gate)
            {
                if (_mount != null) _mount.Notify = forward;
                if (_client != null) _client.Notify = forward;
                if (_auth != null) _auth.Notify = forward;
            }
        }

        /// <summary>
        /// How a byte count is spelled in these messages — the same preference
        /// every other size in the application obeys, read once at mount.
        /// </summary>
        private SizeUnitStyle _units = SizeUnitStyle.FullWords;

        /// <summary>
        /// The point at which "nearly full" is worth saying. A gigabyte is about
        /// one more album at the sizes in this Drive, and an upload that runs out
        /// of quota fails on whichever chunk crosses the line rather than being
        /// refused up front.
        /// </summary>
        private const long QuotaLowBytes = 1024L * 1024 * 1024;

        /// <summary>
        /// How often an upload may report its progress. Measured: a gigabyte is
        /// 128 chunks, one about every 570ms — a message each is not a progress
        /// report, it is something talking over the top of everything else.
        /// </summary>
        private const long UploadReportMs = 5000;

        /// <summary>
        /// Drive's own storage quota, in bytes, or zero when it is not known yet.
        ///
        /// Read from the same About response that proves the token at mount, so
        /// they cost nothing, and handed to the mount, which reports them to
        /// Windows as the drive's size and free space and keeps them up to date.
        /// </summary>
        public long QuotaUsed { get; private set; }

        /// <summary>Total bytes the account may hold, or zero when not known.</summary>
        public long QuotaLimit { get; private set; }

        // A Connection property polled the health state and nothing ever polled
        // it: the state is pushed out through ConnectionChanged instead, which
        // is what the tray listens to.
        private DriveHealth? _health;

        /// <summary>True once the sign-in has expired and not yet been renewed.</summary>
        public bool NeedsSignIn { get; private set; }

        /// <summary>The letter, or null when nothing is mounted.</summary>
        public string? Letter { get { lock (_gate) return _mount?.Letter; } }

        public bool Mounted => Letter != null;

        /// <summary>For the suite: the account's storage, as About would say it.</summary>
        internal void SetQuotaForTests(long used, long limit) { QuotaUsed = used; QuotaLimit = limit; }

        /// <summary>For the suite: a mount over the fake Drive, with no sign-in and no letter.</summary>
        internal void UseForTests(DriveMount mount, DriveClient client)
        {
            lock (_gate) { _mount = mount; _client = client; }
            mount.ShowTrash = () => ShowTrash;
            Rewire();
        }

        /// <summary>Whether the letter shows Google Drive's Trash folder (Preferences, Google Drive).</summary>
        public bool ShowTrash { get; private set; } = true;

        /// <summary>
        /// Shows or hides the Trash folder. The root's listing is forgotten, so
        /// the next visit lists it again with the folder put in or taken out.
        /// </summary>
        public void SetShowTrash(bool show)
        {
            if (ShowTrash == show) return;
            ShowTrash = show;
            string? root;
            lock (_gate) { root = _mount?.Letter ?? _mount?.Root; if (show) _client?.WarmTrash(); }
            if (root != null) Forget(root.TrimEnd('\\') + "\\");
        }

        private string _status = "Not connected.";

        /// <summary>
        /// Plain English, for the Preferences page — and, when
        /// EXPLORERNATIVE_DRIVELOG names a file, for that file too.
        ///
        /// A mount that fails leaves no window, no dialog and one balloon that is
        /// gone in seconds, which is not enough to work with when the failure is
        /// on somebody else's machine.
        /// </summary>
        public string Status
        {
            get => _status;
            private set
            {
                _status = value;
                try
                {
                    var path = Environment.GetEnvironmentVariable("EXPLORERNATIVE_DRIVELOG");
                    if (!string.IsNullOrEmpty(path))
                        File.AppendAllText(path, $"{DateTime.Now:HH:mm:ss.fff}  {value}{Environment.NewLine}");
                }
                catch { }
            }
        }

        /// <summary>
        /// Where the credentials live: beside settings.json, never in the build
        /// or install tree, so a rebuild cannot lose the sign-in.
        /// </summary>
        public static string CredentialsDirectory => Settings.AppDataDir;

        /// <summary>The file the refresh token is kept in, for the scope in force.</summary>
        private static string TokenPath =>
            Path.Combine(SafeDirectory(), $"google-drive-token-v{GoogleAuth.ScopeVersion}.dat");

        /// <summary>For the timing benchmark in the suite, which signs in the way a mount does.</summary>
        internal static string SavedTokenPath => TokenPath;

        /// <summary>
        /// Whether there is a saved sign-in on this computer.
        ///
        /// The presence of the token file, not whether it still works — asking
        /// Google that costs a round trip, and this is read while building a
        /// preferences page. A token that has been revoked at the other end
        /// looks signed in here and says so when it is next used, which is the
        /// right moment to find out.
        /// </summary>
        public static bool HasSavedSignIn
        {
            get { try { return File.Exists(TokenPath); } catch { return false; } }
        }

        /// <summary>
        /// Forgets the saved sign-in on this computer. True when there was one.
        ///
        /// Deliberately local. It does not call Google's revoke endpoint,
        /// because "sign out of this computer" and "withdraw this application's
        /// access to my account" are different things and a button that did the
        /// second while saying the first would be a surprise on somebody's other
        /// machine. The account page is named on the preferences page for anyone
        /// who wants the second.
        /// </summary>
        public static bool ForgetSignIn()
        {
            try
            {
                ForgetSavedSizes(keepAccount: null);
                if (!File.Exists(TokenPath)) return false;
                File.Delete(TokenPath);
                return true;
            }
            catch { return false; }
        }

        /// <summary>
        /// The volume's own name for a signed-in account, "\\GoogleDrive\conner":
        /// the server is always GoogleDrive and the share is the account's name
        /// before the @, cut down to what a share name may hold. Explorer shows
        /// it as "Google Drive" anyway (<see cref="LabelTheDrive"/>).
        /// </summary>
        internal static string VolumeName(string account)
        {
            var user = account.Split('@')[0];
            var share = new string(user.Where(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.').ToArray());
            if (share.Length == 0) share = "drive";
            if (share.Length > 60) share = share[..60];
            return @"\\GoogleDrive\" + share;
        }

        /// <summary>
        /// Where test builds of 4.3.0 held files saved on the letter before they
        /// went up. Nothing is held on disk now (DriveMount.Writes.cs); the
        /// folder is removed at start when it is empty.
        /// </summary>
        internal static string OldHoldingFolder => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ExplorerNative", "Google Drive uploads");

        /// <summary>
        /// What File Explorer calls the drive. A network drive is shown by its
        /// share and server ("conner (\\GoogleDrive) (G:)") unless Explorer's own
        /// key for it says otherwise, which is what this sets.
        /// </summary>
        private static void LabelTheDrive(string root)
        {
            try
            {
                var name = "##" + root.TrimStart('\\').Replace('\\', '#');
                using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Explorer\MountPoints2\" + name);
                key?.SetValue("_LabelFromReg", "Google Drive");
            }
            catch { }
        }
        /// <summary>The same trace, for the upload and other callers outside this class.</summary>
        internal static void Trace(string line) => Note(line);

        private static void Note(string line)
        {
            try
            {
                var path = Environment.GetEnvironmentVariable("EXPLORERNATIVE_DRIVELOG");
                if (!string.IsNullOrEmpty(path))
                    File.AppendAllText(path, $"{DateTime.Now:HH:mm:ss.fff}  {line}{Environment.NewLine}");
            }
            catch { }
        }

        private static string SafeDirectory()
        {
            try
            {
                Directory.CreateDirectory(Settings.AppDataDir);
                return Settings.AppDataDir;
            }
            catch
            {
                return Path.GetTempPath();
            }
        }

        /// <summary>
        /// Signs in if needed and puts Drive on a letter. Returns false with
        /// <see cref="Status"/> set to why, because every failure here is
        /// something the person can act on — no credentials, no letter free, or a
        /// consent they declined.
        /// </summary>
        /// <param name="quietly">A retry after the network failed: "Connecting" is not said again.</param>
        /// <param name="mayConsent">
        /// A deliberate connect (Connect, OK in Preferences), the only kind that
        /// may open Google's sign-in page. The start-up mount and the retries
        /// after a network failure pass false: with no saved sign-in they say so
        /// instead of putting a browser in front of somebody who asked for nothing.
        /// </param>
        public async Task<bool> Mount(Settings settings, CancellationToken token = default, bool quietly = false,
            bool mayConsent = true)
        {
            // One at a time. Two in parallel — OK pressed and a settings reload a
            // moment apart — both chose the same root, and the second tore the
            // first one's registration down while it was being populated.
            var lifetime = _lifetime;
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime.Token);

            // Connect pressed again while a sign-in waits in the browser (a tab
            // closed by mistake, a page behind another window): the new press
            // replaces the waiting one. It used to queue behind it silently for
            // up to thirty minutes and then open a browser out of nowhere.
            CancellationTokenSource? consent = null;
            if (mayConsent)
            {
                consent = new CancellationTokenSource();
                var waiting = Interlocked.Exchange(ref _consentWait, consent);
                try { waiting?.Cancel(); } catch { }
            }

            try { await _mountGate.WaitAsync(linked.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { return false; }

            var consentToken = consent?.Token ?? CancellationToken.None;
            try
            {
                if (linked.IsCancellationRequested) return false;

                // Already up. Reconnecting is what OK on the Drive page means when
                // the sign-in has expired, and it used to mean mounting again from
                // scratch — whose first step, choosing a root, found the live
                // mount's own directory, unregistered its drive and stood a
                // second provider up over it to clear it out. The mount stays; the
                // sign-in is renewed underneath it.
                if (Mounted)
                {
                    if (!NeedsSignIn) return true;
                    var (renewed, otherAccount, fresh) =
                        await RenewSignIn(linked.Token, mayConsent, consentToken).ConfigureAwait(false);
                    if (!otherAccount || fresh == null) return renewed;

                    // Signed in again as somebody else. Every entry on the letter, the
                    // path-to-id map and the search graph belong to the old
                    // account, and its ids sent with the new token fail
                    // everywhere, so the letter is mounted again from scratch.
                    // TearDown, not Unmount: Unmount cancels _lifetime, and with
                    // it the token this mount runs on.
                    //
                    // The old sign-in is retired first, so nothing of the old
                    // account's (an unsent save, a size refresh, a read-ahead) can
                    // go out as the new one: a file saved to the root of the letter
                    // used to land in the other account's My Drive, said as saved.
                    // Its unsent files are kept on this PC instead (AccountGone).
                    GoogleAuth? old;
                    DriveMount? going;
                    lock (_gate) { old = _auth; going = _mount; _otherAccountsSwept = false; }
                    if (old != null) { old.SignInLapsed -= OnSignInLapsed; old.Retire(); }
                    if (going != null) going.AccountGone = true;
                    TearDown();

                    // Only now is the new account's sign-in put where MountFresh reads it.
                    if (GoogleAuth.Credentials(SafeDirectory()) is { } credentials)
                        new GoogleAuth(credentials.ClientId, credentials.ClientSecret, TokenPath).AdoptFrom(fresh);
                    if (linked.IsCancellationRequested) return false;
                    return await MountFresh(settings, linked.Token, quietly: true).ConfigureAwait(false);
                }

                return await MountFresh(settings, linked.Token, quietly, mayConsent, consentToken).ConfigureAwait(false);
            }
            finally
            {
                if (consent != null) Interlocked.CompareExchange(ref _consentWait, null, consent);
                _mountGate.Release();
            }
        }

        private readonly SemaphoreSlim _mountGate = new(1, 1);

        /// <summary>The deliberate connect whose browser sign-in a newer press may call off. Cancelled, never disposed.</summary>
        private CancellationTokenSource? _consentWait;

        /// <summary>
        /// Whether a connect that failed is worth trying again by itself: the
        /// network, and not a sign-in somebody left unfinished in the browser.
        /// Retrying that one opened Google's page again every half hour.
        /// </summary>
        internal static bool RetryableConnectFailure(Exception ex, bool cancelled, bool consentStarted,
            bool consentGranted) =>
            !cancelled && ex is not GoogleSignInRequiredException && !(consentStarted && !consentGranted) &&
            DriveHealth.LooksLikeNetwork(ex);

        /// <summary>The connection state last passed on to ConnectionChanged.</summary>
        private DriveState? _lastSaid;

        /// <summary>
        /// Cancelled by <see cref="Unmount"/>, so a mount still on its way up when
        /// Drive is switched off does not arrive afterwards. Replaced rather than
        /// reset, and never disposed — see the note on cancellation sources.
        /// </summary>
        private CancellationTokenSource _lifetime = new();

        private bool _disposed;

        /// <summary>
        /// Asks for consent again, and keeps the live mount with every entry on
        /// the letter and every cache where it is when the same account comes
        /// back. The consent goes through a scratch sign-in, never the live one:
        /// a different account chosen in Google's chooser must not reach the
        /// client the old account's unsent saves go out on. OtherAccount is true
        /// then, with that sign-in in Fresh, and the caller mounts again from scratch.
        /// </summary>
        private async Task<(bool Ok, bool OtherAccount, GoogleAuth? Fresh)> RenewSignIn(CancellationToken token,
            bool mayConsent, CancellationToken consentToken)
        {
            GoogleAuth? auth;
            lock (_gate) auth = _auth;
            if (auth == null) return (false, false, null);
            if (GoogleAuth.Credentials(SafeDirectory()) is not { } credentials)
            {
                Status = "No Google client ID and secret yet. Enter them in Preferences, Google Drive, then connect.";
                return (false, false, null);
            }

            var scratchPath = Path.Combine(Path.GetTempPath(), "en-renew-" + Guid.NewGuid().ToString("N") + ".dat");
            // Consent for this request alone; see GoogleAuth.ConsentScope.
            try
            {
                // A sign-in saved meanwhile is tried before any browser.
                try { if (File.Exists(TokenPath)) File.Copy(TokenPath, scratchPath); } catch { }
                var scratch = new GoogleAuth(credentials.ClientId, credentials.ClientSecret, scratchPath)
                    { AllowConsent = false, Notify = Forward };
                var client = new DriveClient(scratch) { Notify = Forward, Units = _units };

                string user;
                var asking = CancellationTokenSource.CreateLinkedTokenSource(token, consentToken);
                using (mayConsent ? GoogleAuth.ConsentScope() : null)
                    (user, _, _) = await client.Whoami(asking.Token).ConfigureAwait(false);
                bool other;
                lock (_gate)
                {
                    other = _account != null && !string.Equals(_account, user, StringComparison.OrdinalIgnoreCase);
                    // The old account's sizes are left for TearDown to save
                    // under that account's own name.
                    if (!other) _account = user;
                }
                if (scratch.ConsentGranted) Say("drive.signin.done", $"Signed in to Google Drive as {user}");
                if (other) return (false, true, scratch);

                auth.AdoptFrom(scratch);
                NeedsSignIn = false;
                Interlocked.Exchange(ref _lapseSaid, 0);
                Status = $"{user} on {Letter}";
                return (true, false, null);
            }
            catch (Exception ex)
            {
                Status = consentToken.IsCancellationRequested && !token.IsCancellationRequested
                    ? "Not connected."
                    : $"Could not sign in again: {GoogleNet.Reason(ex)}";
                return (false, false, null);
            }
            finally { try { File.Delete(scratchPath); } catch { } }
        }

        /// <summary>
        /// A request found the sign-in gone. Said once, however many ask.
        /// </summary>
        private int _lapseSaid;

        private void OnSignInLapsed()
        {
            // Once, when several requests lapse together.
            if (Interlocked.Exchange(ref _lapseSaid, 1) != 0) return;
            NeedsSignIn = true;
            Status = "Sign-in expired. Reconnect from Preferences.";
            try { SignInRequired?.Invoke(); } catch { }
        }

        private async Task<bool> MountFresh(Settings settings, CancellationToken token, bool quietly = false,
            bool mayConsent = true, CancellationToken consentToken = default)
        {
            _units = settings.SizeUnits;
            FailedOnNetwork = false;

            // The drive is a WinFsp volume (4.3.0), so WinFsp has to be there
            // before anything else is worth doing. The tray offers to install it.
            bool winFspLoads = WinFsp.TryLoad(out var winFspProblem);
            // Installed but not loading: installing again would change nothing, so
            // say what is wrong instead of asking to install.
            NeedsWinFsp = !winFspLoads && !WinFsp.Installed;
            if (!winFspLoads)
            {
                Status = NeedsWinFsp
                    ? "Google Drive needs WinFsp, a free driver that lets it show as a real drive. " +
                      "Explorer Native can install it for you."
                    : "WinFsp is installed but would not load" +
                      (string.IsNullOrWhiteSpace(winFspProblem) ? "." : ": " + winFspProblem.TrimEnd('.') + ".");
                Note("WinFsp: " + winFspProblem);
                if (NeedsWinFsp) Say("drive.winfsp.missing", "Google Drive needs WinFsp, a free driver. Explorer Native can install it for you");
                return false;
            }

            if (!quietly) Say("drive.mounting", "Connecting to Google Drive");

            // The client ID and secret entered in Preferences. Nothing is compiled in.
            var client = GoogleAuth.Credentials(SafeDirectory());
            if (client == null)
            {
                Status = "No Google client ID and secret yet. Enter them in Preferences, Google Drive, " +
                         "then connect.";

                Say("drive.credentials.missing",
                    "No Google client ID and secret yet. Enter them in Preferences, Google Drive");
                return false;
            }

            GoogleAuth? auth = null;
            try
            {
                // Consent is allowed here and nowhere else: this is somebody
                // asking to connect, so a browser opening is the expected answer
                // rather than an ambush from inside a directory listing.
                auth = new GoogleAuth(client.Value.ClientId, client.Value.ClientSecret, TokenPath)
                { AllowConsent = false, Notify = Forward };
                auth.SignInLapsed += OnSignInLapsed;
                var drive = new DriveClient(auth) { Notify = Forward, Units = _units };

                // Hooked before the first request, so the very first failure
                // counts and a mount that never comes up says why.
                _health = drive.Health;
                _health.Changed += _ =>
                {
                    Note(drive.Health.Sentence);

                    // The state as it is now, not as it was when this was raised:
                    // two streams changing it at once can deliver their events in
                    // the other order, and the last word must be the true one.
                    // And each change said once.
                    var now = drive.Health.State;
                    lock (_gate)
                    {
                        if (_lastSaid == now) return;
                        _lastSaid = now;
                    }
                    try { ConnectionChanged?.Invoke(now); } catch { }
                };

                // Proves the token before anything is registered. A drive left
                // behind by a sign-in that then failed is worse than no mount.
                // The one request allowed to open a browser: a deliberate
                // connect. Nothing the mount does afterwards may.
                // Not at start-up, nor on a retry: those never open a browser
                // (with no saved sign-in they say it has expired instead). A newer
                // Connect press calls the browser wait off (consentToken).
                string user;
                long used, limit;
                var asking = CancellationTokenSource.CreateLinkedTokenSource(token, consentToken);
                using (mayConsent ? GoogleAuth.ConsentScope() : null)
                    (user, used, limit) = await drive.Whoami(asking.Token);

                // Only when the browser was actually used. A mount that refreshed
                // a stored token in silence is not a sign-in, and saying so on
                // every start would be an echo of nothing having happened.
                if (auth.ConsentGranted) Say("drive.signin.done", $"Signed in to Google Drive as {user}");

                // The quota was already read here and thrown away — it is in the
                // same response that proves the token, so it costs nothing.
                QuotaUsed = used;
                QuotaLimit = limit;

                // In the log as raw bytes as well as words: Google's own figure,
                // which is what the drive reports to Windows as its size.
                Note($"quota: {used} used of {limit} bytes");

                if (limit > 0)
                {
                    Say("drive.quota",
                        $"{SizeFormatter.Format(used, _units)} used of {SizeFormatter.Format(limit, _units)}");

                    long free = Math.Max(0, limit - used);
                    if (free < QuotaLowBytes)
                        Say("drive.quota.low",
                            $"Google Drive is nearly full: {SizeFormatter.Format(free, _units)} left");
                }

                long budget = CacheMegabytes * 1024L * 1024;
                var root = VolumeName(user);
                Note($"mounting {root}");
                LabelTheDrive(root);

                ShowTrash = settings.ShowDriveTrash;

                // Drive switched off and on again quickly: the old volume, same
                // name, may still be sending what it held. It goes first.
                await Teardown.WaitAsync(token).ConfigureAwait(false);

                var mount = new DriveMount(drive, root, budget, Note)
                {
                    Notify = Forward,
                    OnLetterMoved = (from, to) =>
                    {
                        Status = Status.Replace(from, to, StringComparison.OrdinalIgnoreCase);
                        LetterMoved?.Invoke(from, to);
                    },
                    IdleRelease = TimeSpan.FromSeconds(CacheIdleSeconds),
                    ShowTrash = () => ShowTrash,
                    QuotaUsed = used,
                    QuotaLimit = limit,
                };

                // Start lists the root, creates the volume and then takes a
                // letter, and any of those can throw — no free letter is the
                // likeliest, and it happens last. The catch below calls Unmount,
                // which disposes _mount; on a first mount that is still null, so
                // this one is disposed here or its volume would outlive the mount.
                try
                {
                    mount.Start("root", assignLetter: true,
                        preferred: PreferredLetter(settings.GoogleDriveLetter), token: token);

                    // A big trash takes over a minute to list; started now, Trash opens at once later.
                    if (ShowTrash) drive.WarmTrash();
                }
                catch
                {
                    try { mount.Dispose(); } catch { }
                    throw;
                }
                // Decided under the lock that Unmount takes after cancelling, so
                // one of the two always sees the other: either this sees the
                // cancellation and takes its own mount down, or Unmount finds the
                // mount installed and takes it down.
                bool abandoned;
                DriveMount? replaced = null;
                lock (_gate)
                {
                    abandoned = token.IsCancellationRequested;
                    if (!abandoned)
                    {
                        replaced = _mount;
                        _mount = mount;

                        // From here on a dead token fails fast instead of opening
                        // a browser out of a request the drive is answering.
                        auth.AllowConsent = false;
                        _auth = auth;
                        _client = drive;
                        _account = user;
                    }
                }

                // Outside the lock: disposing waits for callbacks in flight, and a
                // callback can be waiting for the lock itself (the health event).
                try { replaced?.Dispose(); } catch { }

                if (abandoned)
                {
                    try { mount.Dispose(); } catch { }
                    Status = "Not connected.";
                    return false;
                }

                NeedsSignIn = false;
                Interlocked.Exchange(ref _lapseSaid, 0);

                Status = $"{user} on {mount.Letter}";
                if (settings.FolderSizes == FolderSizeMode.Automatic) WarmSizes(mount.Letter ?? mount.Root);
                return true;
            }
            catch (Exception ex)
            {
                // TearDown first: it sets "Not connected.", and the reason must
                // be the last word, because the tray and Preferences read it.
                TearDown();
                // Called off by a newer Connect press: that one is connecting now.
                bool cancelled = token.IsCancellationRequested || consentToken.IsCancellationRequested;
                bool unfinished = auth?.ConsentStarted == true && !auth.ConsentGranted;
                Status = cancelled
                    ? "Not connected."
                    : unfinished
                        ? "Google sign-in was not finished."
                        : $"Could not connect: {GoogleNet.Reason(ex)}";
                FailedOnNetwork = RetryableConnectFailure(ex, cancelled, auth?.ConsentStarted == true,
                    auth?.ConsentGranted == true);
                return false;
            }
        }

        /// <summary>
        /// The last connect failed because Google could not be reached (no
        /// network yet, a name that would not resolve), not over the sign-in:
        /// worth trying again on its own, which opens no browser.
        /// </summary>
        public bool FailedOnNetwork { get; private set; }

        /// <summary>The last connect stopped because WinFsp is not installed (or would not load).</summary>
        public bool NeedsWinFsp { get; private set; }

        /// <summary>Whether the letter is there now: false while it is hidden because Google cannot be reached.</summary>
        public bool LetterShown { get { lock (_gate) return _mount?.LetterShown ?? false; } }

        /// <summary>Files saved on the letter by other programs that are still on their way to Google.</summary>
        public int UploadsPending
        {
            get { lock (_gate) return (_mount?.UploadsPending ?? 0) + _goingAway.Sum(m => m.UploadsPending); }
        }

        /// <summary>The names of those files, for the question before quitting, a mount just disconnected and still sending them included.</summary>
        public IReadOnlyList<string> UploadNames
        {
            get
            {
                lock (_gate)
                    return (_mount?.UploadNames ?? Array.Empty<string>())
                        .Concat(_goingAway.SelectMany(m => m.UploadNames)).ToList();
            }
        }

        /// <summary>
        /// Moves a mounted Drive to the letter Preferences now names, without
        /// unmounting. Returns the old and new letters when it moved, otherwise
        /// null — not mounted, already there, or no letter free.
        /// </summary>
        public event Action<string, string>? LetterMoved;

        public (string From, string To)? MoveLetter(string configured)
        {
            DriveMount? mount;
            lock (_gate) mount = _mount;
            var before = mount?.Letter;
            if (mount == null || before == null) return null;

            try { mount.MoveLetter(PreferredLetter(configured)); }
            catch (Exception ex)
            {
                Note("moving the drive letter failed: " + ex.Message);
                return null;
            }

            var after = mount.Letter;
            if (after == null || string.Equals(before, after, StringComparison.OrdinalIgnoreCase)) return null;

            Status = Status.Replace(before, after, StringComparison.OrdinalIgnoreCase);
            return (before, after);
        }

        private static char PreferredLetter(string configured) =>
            string.IsNullOrWhiteSpace(configured)
                ? 'G'
                : char.ToUpperInvariant(configured.Trim()[0]);

        /// <summary>
        /// Whether the letter setting itself changed, read the way the mount reads
        /// it ("g", "G:" and blank are all G). Only then does OK move the drive:
        /// on its fallback letter (G: held by a stick) every OK took the letter
        /// away and put it back, or moved it lower.
        /// </summary>
        internal static bool LetterSettingChanged(string? before, string? after) =>
            PreferredLetter(before ?? "") != PreferredLetter(after ?? "");

        /// <summary>
        /// Takes Drive off its letter, and stops any mount still on its way up.
        /// </summary>
        public void Unmount()
        {
            // Cancelled first and outside the lock; see the matching decision in
            // MountFresh.
            var old = _disposed ? _lifetime : Interlocked.Exchange(ref _lifetime, new CancellationTokenSource());
            try { old.Cancel(); } catch { }

            // Disconnect and switching Drive off call this on the UI thread, and
            // taking a mount down sends what programs saved and closed for up to
            // a minute (DrainFor). So the mount is taken off at once (Mounted is
            // false from here) and goes away on a worker; Dispose and the next
            // mount wait for it, and the question before quitting still sees
            // what it is sending.
            var (mount, sizes) = Detach();
            if (mount == null && sizes == null) return;
            lock (_gate)
            {
                if (mount != null) _goingAway.Add(mount);
                _teardown = _teardown.ContinueWith(_ => Finish(mount, sizes), TaskScheduler.Default);
            }
        }

        /// <summary>Mounts taken off by <see cref="Unmount"/> that are still sending what they hold.</summary>
        private readonly List<DriveMount> _goingAway = new();

        /// <summary>Those mounts going away, one after another.</summary>
        private Task _teardown = Task.CompletedTask;

        /// <summary>For the suite: the mounts <see cref="Unmount"/> handed to a worker, finished.</summary>
        internal Task TeardownForTests { get { lock (_gate) return _teardown; } }

        private Task Teardown { get { lock (_gate) return _teardown; } }

        /// <summary>Takes a mount down where the caller is: already on a worker, or quitting.</summary>
        private void TearDown()
        {
            var (old, sizes) = Detach();
            Finish(old, sizes);
        }

        /// <summary>Takes the mount and its size indexes out of use. Quick: nothing is sent or saved.</summary>
        private (DriveMount? Old, Dictionary<string, DriveSizeIndex>? Sizes) Detach()
        {
            DriveMount? old;
            Dictionary<string, DriveSizeIndex>? sizes;
            lock (_gate)
            {
                old = _mount;
                _mount = null;
                _folders = null;
                sizes = _sizes;
                _sizes = null;
            }
            // Here, not after the mount has gone: a mount made since may already
            // have said "user on G:", and the old one finishing must not undo it.
            Status = "Not connected.";
            return (old, sizes);
        }

        /// <summary>Saves the size indexes and lets the mount go, sending what it holds. Slow.</summary>
        private void Finish(DriveMount? old, Dictionary<string, DriveSizeIndex>? sizes)
        {
            // Changes the feed brought in since the last save, so the next start begins from here.
            if (sizes != null)
                foreach (var index in sizes.Values)
                    try { index.SaveNow(); } catch { }

            // Outside the lock. Dispose waits up to five seconds for callbacks
            // still in flight, and a callback whose request fails raises the
            // health event, which takes this lock: held here, that callback
            // could not finish, the wait ran out, and the provider disconnected
            // under a read — the one thing the wait exists to prevent.
            try { old?.Dispose(); } catch { }
            if (old == null) return;
            lock (_gate) _goingAway.Remove(old);

            // Only when there was something to take down. A mount failing on its
            // way up comes through here too, and a connection that never came up
            // disconnecting is not news.
            Say("drive.unmounted", "Google Drive disconnected");
        }

        /// <summary>
        /// Fills in a folder before it is listed, and says whether it did
        /// anything.
        ///
        /// Belt and braces rather than the mechanism: the drive lists a folder
        /// itself the first time any program opens it. Kept because navigation
        /// knows a folder is about to be listed and can pay the listing up front,
        /// a page at a time, and because it costs nothing when the drive got
        /// there first.
        ///
        /// Safe to call for any path. Anything that is not on the mounted letter
        /// returns false without touching the network.
        /// </summary>
        public bool Populate(string path, Action<IReadOnlyList<DrivePlaced>>? onPlaced = null,
            bool interactive = true)
        {
            DriveMount? mount;
            lock (_gate) mount = _mount;
            if (mount == null || string.IsNullOrEmpty(path)) return false;

            try
            {
                if (!Owns(mount, path)) return false;
                return mount.Populate(path, onPlaced, interactive);
            }
            catch (GoogleSignInRequiredException)
            {
                // Weekly, by Google's design. Already said by the sign-in itself
                // (SignInLapsed), once; saying it here too could come after a
                // reconnect that had just succeeded.
                return false;
            }
            catch (Exception ex)
            {
                // A folder that will not populate still lists, just emptily. That
                // is a worse folder, not a broken application — but an empty
                // folder that is not empty is exactly the thing nobody can tell
                // by looking, so it is worth one sentence.
                // A deadline is "took too long", not the framework's "operation was canceled".
                Say("drive.folder.failed", $"Could not read {Path.GetFileName(path)}: " +
                    (ex is OperationCanceledException ? "Google Drive took too long to answer" : ex.Message));
                return false;
            }
        }

        private static bool Owns(DriveMount mount, string path)
        {
            var full = Path.GetFullPath(path);

            // Both halves anchored on a path boundary. A bare StartsWith on the
            // root claimed GoogleDrive-2 for the GoogleDrive mount — the two sit
            // side by side in the same folder whenever PickRoot has had to step
            // aside — so deleting a leftover through the file manager was sent to
            // Drive, where the path is not known, and refused. The map then
            // correctly answered "not part of the mounted Drive" about a path this
            // method had just claimed, which is the two halves disagreeing.
            return (mount.Letter != null && DriveMount.IsAtOrUnder(full, mount.Letter)) ||
                   DriveMount.IsAtOrUnder(full, mount.Root);
        }

        /// <summary>
        /// Clears up what Explorer Native 4.2 and earlier could leave behind
        /// after a crash: a Cloud Files sync root and a subst letter onto it
        /// (see <see cref="LegacyCloudFiles"/>). Runs on every start, whether or
        /// not Drive is switched on, so somebody who turned it off is not left
        /// with the wreckage.
        /// </summary>
        public static void SweepOrphans(Settings settings)
        {
            _ = settings;
            try { LegacyCloudFiles.Sweep(Note); } catch { }
            try { if (Directory.Exists(OldHoldingFolder) && !Directory.EnumerateFileSystemEntries(OldHoldingFolder).Any()) Directory.Delete(OldHoldingFolder); } catch { }
        }
        /// <summary>Whether this path lives on the mounted drive.</summary>
        /// <summary>
        /// Watches what the mount downloads for <paramref name="paths"/> — how a
        /// copy out of the drive learns how far it has really got. Null when
        /// nothing is mounted.
        /// </summary>
        internal DriveMount.FetchWatch? WatchFetches(IEnumerable<string> paths)
        {
            DriveMount? mount;
            lock (_gate) mount = _mount;
            return mount?.Watch(paths);
        }

        /// <summary>
        /// One file on the letter, copied to <paramref name="destination"/> straight
        /// from Google rather than through Windows' cloud layer: streamed, resumed
        /// from the byte it got to when the connection breaks, checked against
        /// Drive's size and checksum, and put in place only when whole. Null when
        /// it worked, otherwise why not.
        ///
        /// For a copy out of Drive that robocopy gave up on (4.2.7). Once one read
        /// of a file has timed out, Windows refuses that file with error 379 ("not
        /// supported by the cloud sync provider") until the app restarts, so
        /// robocopy's own retries can never succeed; this does not go through
        /// Windows at all.
        /// </summary>
        internal async Task<string?> DownloadOutAsync(string source, string destination,
            Action<long>? moved, CancellationToken token)
        {
            // A few tries: this is the last chance the file gets, on a line bad
            // enough that Windows already gave up on it. A refusal is final.
            string? why = null;
            for (int attempt = 0; attempt < 4; attempt++)
            {
                if (attempt > 0) await Task.Delay(TimeSpan.FromSeconds(2 * attempt), token).ConfigureAwait(false);
                try { why = await DownloadOutOnceAsync(source, destination, moved, token).ConfigureAwait(false); }
                catch (DriveClient.DriveStatusException ex) when (ex.Status < 500 && ex.Status != 429) { return GoogleNet.Reason(ex); }
                catch (DriveClient.DriveStatusException ex) { why = GoogleNet.Reason(ex); continue; }
                if (why == null) return null;
            }
            return why;
        }

        private async Task<string?> DownloadOutOnceAsync(string source, string destination,
            Action<long>? moved, CancellationToken token)
        {
            DriveMount? mount;
            DriveClient? client;
            lock (_gate) { mount = _mount; client = _client; }
            if (mount == null || client == null || !Owns(mount, source)) return "it is not on Google Drive";
            var id = mount.IdFor(source);
            if (id == null) return "Google Drive could not say which file it is";

            // Its read-ahead from the failed copy stops, so the line is this download's.
            try { mount.ReleaseFile(id); } catch { }

            var folder = Path.GetDirectoryName(destination);
            var partial = Path.Combine(folder ?? "", "." + Path.GetFileName(destination) + ".partial-" +
                                                    Guid.NewGuid().ToString("N")[..8]);
            try
            {
                var remote = await client.FileNow(id, token).ConfigureAwait(false);
                if (remote == null) return "it is no longer on Google Drive";
                if (remote.IsGoogleDocument) return "it is a Google document, which has no file to copy";
                if (folder != null) Directory.CreateDirectory(folder);

                using var md5 = remote.Md5 != null
                    ? System.Security.Cryptography.IncrementalHash.CreateHash(System.Security.Cryptography.HashAlgorithmName.MD5)
                    : null;
                await using (var file = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                                 1 << 16, FileOptions.Asynchronous))
                {
                    // Reported as bytes so far in this attempt, so a retried
                    // attempt is not counted on top of the one before.
                    long got = 0;
                    if (remote.Size > 0)
                        await client.DownloadTo(id, 0, remote.Size, file, block =>
                        {
                            md5?.AppendData(block.Span);
                            got += block.Length;
                            moved?.Invoke(got);
                        }, token).ConfigureAwait(false);
                }

                if (new FileInfo(partial).Length != remote.Size)
                    return "the download came out the wrong size";
                if (md5 != null && !string.Equals(Convert.ToHexString(md5.GetHashAndReset()), remote.Md5,
                        StringComparison.OrdinalIgnoreCase))
                    return "the download did not match Google Drive's copy";

                if (remote.Modified > DateTime.MinValue) File.SetLastWriteTimeUtc(partial, remote.Modified);
                File.Move(partial, destination, overwrite: true);
                Note($"copied {Path.GetFileName(source)} straight from Google ({remote.Size:N0} bytes)");
                return null;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (DriveClient.DriveStatusException) { throw; }
            catch (Exception ex) { return GoogleNet.Reason(ex); }
            finally
            {
                try { if (File.Exists(partial)) File.Delete(partial); } catch { }
            }
        }

        public bool Owns(string path)
        {
            DriveMount? mount;
            lock (_gate) mount = _mount;
            if (mount == null || string.IsNullOrEmpty(path)) return false;
            try { return Owns(mount, path); } catch { return false; }
        }

        /// <summary>
        /// Starts downloading files that are about to be read by somebody else.
        /// See <see cref="DriveMount.Warm"/> for why this is worth doing at all.
        ///
        /// Safe to call with any mixture of paths: anything not on the mounted
        /// letter is ignored without touching the network, so a selection of
        /// local files costs nothing. Returns how many were taken on.
        ///
        /// Never throws and never blocks. It is called from the clipboard path on
        /// the UI thread, and a copy that failed because warming did would be a
        /// far worse bug than the freeze this is fixing.
        /// </summary>
        public void Warm(IEnumerable<string> paths)
        {
            DriveMount? mount;
            lock (_gate) mount = _mount;
            if (mount == null || paths == null) return;

            // Copied before it leaves this thread. The caller's array is the
            // pane's current selection, which changes the moment the next key is
            // pressed, and reading it from a worker is a race for the sake of
            // nothing.
            var snapshot = paths.Where(p => Owns(mount, p)).ToArray();
            if (snapshot.Length == 0) return;

            // Nothing is waited for. Warm itself is bounded and quick, but it is
            // called from the clipboard path on the UI thread, and this
            // application's rule about that is not negotiable: a copy that got
            // slower because of a speed-up would be a poor trade.
            Task.Run(() =>
            {
                try { mount.Warm(snapshot); }
                catch (Exception ex) { Note("warm failed: " + ex.Message); }
            });
        }

        /// <summary>
        /// Everything at or under <paramref name="folderPath"/> whose name
        /// contains <paramref name="term"/>, as paths on the drive letter.
        ///
        /// **Two requests, whatever the size of the account.** Walking the tree
        /// is the obvious way and it is hopeless: populating a cloud folder is a
        /// network listing at about 1.3 seconds, and a library is hundreds of
        /// folders, so a recursive search would be several minutes of nothing
        /// happening. Instead Google is asked to do the searching — one query for
        /// the matches and one for the folder graph — and the answer is then
        /// narrowed to this subtree here, out of memory. See
        /// <see cref="DriveSearch"/>.
        ///
        /// The folders that turn out to *hold* something are populated, and only
        /// those: a search of two thousand tracks that finds four of them costs
        /// the listings for the handful of folders they are in, not for the tree.
        /// That is also what makes the paths real — see
        /// <see cref="DriveMount.ChildPathForId"/> for why the name Drive returns
        /// cannot be used to build one.
        ///
        /// Returns null when this is not a folder on the mounted letter, which is
        /// the caller's signal to search the filesystem instead.
        /// </summary>
        internal async Task<SearchResult?> SearchAsync(
            string folderPath, string term, int most, CancellationToken token)
        {
            DriveMount? mount;
            DriveClient? client;
            lock (_gate) { mount = _mount; client = _client; }

            if (mount == null || client == null || string.IsNullOrEmpty(folderPath)) return null;
            if (!Owns(mount, folderPath)) return null;

            // The root of the letter is the account root, which has no
            // entry on the letter and therefore no id in the map.
            var rootId = mount.IdFor(folderPath) ?? (IsMountRoot(mount, folderPath) ? "root" : null);
            if (rootId == null) return null;

            var query = SearchQuery.Parse(term);

            // The list of shared drives holds drives, and a search there is a
            // search of their names — searching inside all of them at once is
            // minutes of Drive listing on an account with many (see Query).
            // The trash the same way: a search of what is in it, by name. Drive's
            // own search leaves trashed files out, and inside a trashed folder
            // there is nothing it would find.
            if (rootId == DriveClient.SharedDrivesId || InTrash(mount, folderPath))
            {
                // A listing that ran out of time is a failure to say, not the
                // Escape that every other cancellation in a search means.
                try { await Task.Run(() => mount.Populate(folderPath), token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (!token.IsCancellationRequested)
                {
                    throw new IOException("Google Drive took too long to list it");
                }
                var named = new List<SearchHit>();
                if (mount.TryListing(folderPath, out var drives))
                    foreach (var d in drives)
                        if (query.Matches(d.Name, "", d.IsFolder, d.IsFolder ? -1 : d.Size, d.Modified))
                            named.Add(new SearchHit(Path.Combine(folderPath, d.Name), d.Name, d.IsFolder,
                                d.IsFolder ? -1 : d.Size, d.Modified));
                return new SearchResult(named, false);
            }

            // Inside a shared drive, everything is asked of that drive alone.
            var driveId = mount.DriveIdFor(folderPath);

            // ConfigureAwait(false) on every await in this method, and it is not
            // decoration. This is called from the clipboard-and-keystroke side of
            // the application, so without it each continuation resumes on the UI
            // thread — and what runs after them is Populate, which is a
            // *synchronous* Drive listing at about 1.3 seconds each. A search
            // across five folders would have been six seconds of frozen window,
            // which is the one rule this codebase does not bend.
            // From the size index when it holds the whole drive: Google's own
            // name search matches only the start of a word ("mix" never finds
            // "Remix", "World" never finds "HelloWorld"), while the same search
            // in a folder on the PC matches any part of a name.
            var indexed = await FoundInIndex(client, query, driveId, token).ConfigureAwait(false);
            var found = indexed == null
                ? await client.FindByName(query.DriveHints, token, most, driveId, query.DriveClauses).ConfigureAwait(false)
                : indexed.Count > most ? indexed.GetRange(0, most) : indexed;
            var folders = await FolderGraph(client, driveId, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();

            // A hit in a folder the kept graph has never heard of is most likely
            // in a folder made in the last few minutes; ask once more, fresh,
            // rather than dropping it. (Folders shared in from other people are
            // never in the graph, so this happens at most once per graph.)
            var parentOf = DriveSearch.ParentGraph(folders);

            // "root" is an alias Drive takes in a query and never the id it puts
            // in a `parents` array, so searching from the letter itself has to
            // ask what that alias actually is before any chain can be recognised
            // as having reached the top.
            var realRootId = rootId == "root"
                ? await client.RootId(token).ConfigureAwait(false)
                : rootId;

            bool Unknown(string? myDrive) => found.Exists(f =>
                f.ParentId != null && f.ParentId != realRootId && f.ParentId != myDrive &&
                f.ParentId != driveId && !parentOf.ContainsKey(f.ParentId));
            if (Unknown(null) && FolderGraphAge(driveId) > TimeSpan.FromSeconds(30) &&
                // Items sitting in My Drive itself have it as a parent, and it is
                // not a folder in the graph; asked only when it could matter.
                (driveId != null || rootId == "root" || Unknown(await client.RootId(token).ConfigureAwait(false))))
            {
                ForgetFolderGraph();
                folders = await FolderGraph(client, driveId, token).ConfigureAwait(false);
                parentOf = DriveSearch.ParentGraph(folders);
            }

            var hits = new List<SearchHit>();

            // Against what was actually asked for. It used to compare against a
            // literal that happened to match FindByName's default, which is the
            // sort of agreement that holds until somebody changes one of them.
            bool truncated = found.Count >= most;

            // Google was asked for any one word; the whole rule (every word, in
            // the name or the folders under the searched one, patterns, words
            // left out) is applied here, before any folder is listed for a hit
            // that would not be shown.
            var matches = DriveSearch.Answering(query, found, realRootId, parentOf, DriveSearch.NameGraph(folders));

            // Folders already walked down, so a second hit in the same place
            // costs nothing. The value is the path on the letter, or null for one
            // that could not be resolved — remembered either way, so a folder
            // that is not there is not looked for again once per hit inside it.
            var resolved = new Dictionary<string, string?>(StringComparer.Ordinal);

            // Every chain first, out of memory, so the folders they pass through
            // can be listed four at a time rather than one hit at a time. A Doc
            // or a Sheet has no bytes anywhere, so it is not placed on the letter
            // and a result pointing at one would not open.
            var chains = new Dictionary<string, List<string>?>(StringComparer.Ordinal);
            foreach (var match in matches)
                if (!match.IsGoogleDocument && match.ParentId != null && !chains.ContainsKey(match.ParentId))
                    chains[match.ParentId] = DriveSearch.ChainUnder(realRootId, match.ParentId, parentOf);

            // One folder on the way that will not list (a blip, the deadline)
            // costs the hits below it, not the whole search: it threw out of
            // here and every hit was lost to "Could not search". Each folder is
            // tried once, and the hits that could not be placed are said.
            var unlisted = new System.Collections.Concurrent.ConcurrentDictionary<string, string>(
                StringComparer.OrdinalIgnoreCase);
            var populateOrNote = DriveSearch.NotingFailures(
                folder => mount.Populate(folder), unlisted,
                ex => token.IsCancellationRequested || ex is GoogleSignInRequiredException
                    ? null
                    : SearchListingProblem(ex));

            await DriveSearch.PopulateAhead(
                folderPath, chains.Values.Where(c => c != null).Select(c => (IReadOnlyList<string>)c!),
                populateOrNote,
                mount.ChildPathForId,
                resolved, 4, token).ConfigureAwait(false);
            int unplaced = 0;

            foreach (var match in matches)
            {
                token.ThrowIfCancellationRequested();
                if (hits.Count >= most) { truncated = true; break; }

                if (match.IsGoogleDocument) continue;

                var chain = match.ParentId != null && chains.TryGetValue(match.ParentId, out var known)
                    ? known
                    : DriveSearch.ChainUnder(realRootId, match.ParentId, parentOf);
                if (chain == null) continue;   // somewhere else in the account

                var parentPath = DriveSearch.PathForChain(
                    folderPath, chain,
                    populateOrNote,
                    mount.ChildPathForId,
                    resolved);
                var path = parentPath == null ? null : mount.ChildPathForId(parentPath, match.Id);
                if (path == null)
                {
                    unplaced++;
                    continue;
                }

                hits.Add(new SearchHit(
                    path, Path.GetFileName(path), match.IsFolder,
                    match.IsFolder ? -1 : Math.Max(0, match.Size),
                    match.Modified == default ? default : match.Modified.ToLocalTime()));
            }

            if (!unlisted.IsEmpty && unplaced > 0)
            {
                var (folder, why) = unlisted.First();
                var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(folder));
                if (string.IsNullOrEmpty(name)) name = "Google Drive";

                // Nothing could be shown at all: a failure, said as one, rather
                // than "nothing matching", which would be untrue.
                if (hits.Count == 0)
                    throw new IOException($"Google Drive would not list {name} ({why})");

                Say("drive.folder.failed",
                    $"Could not read {name}: {why}. " +
                    $"{unplaced} search result{(unplaced == 1 ? "" : "s")} could not be shown");
            }

            return new SearchResult(hits, truncated);
        }

        /// <summary>Why a folder would not list, in words, for a search that goes on without it.</summary>
        private static string SearchListingProblem(Exception ex) =>
            ex is OperationCanceledException ? "Google Drive took too long" : GoogleNet.Reason(ex);

        /// <summary>
        /// How long the folder graph is reused between searches.
        ///
        /// Measured on this account: 1347 folders, 2.9 of the 3.9 seconds a whole
        /// search costs. It is also the part that barely changes — folders are
        /// made far more rarely than files — so a second search a minute later
        /// paying for it again is the wrong trade. Five minutes rather than the
        /// session, because a folder created in the meantime should turn up
        /// without restarting the application.
        /// </summary>
        private static readonly TimeSpan FolderGraphLife = TimeSpan.FromMinutes(5);

        /// <summary>The folder graphs, per drive ("" for My Drive), and when each was read.</summary>
        private Dictionary<string, (List<DriveFound> Folders, DateTime At)>? _folders;

        /// <summary>Drops the kept folder graphs, so the next search reads them fresh (F5 in results).</summary>
        internal void ForgetFolderGraph()
        {
            lock (_gate) _folders = null;
        }

        /// <summary>How old the kept graph for a drive is (zero when there is none).</summary>
        private TimeSpan FolderGraphAge(string? driveId)
        {
            lock (_gate)
                return _folders != null && _folders.TryGetValue(driveId ?? "", out var known)
                    ? DateTime.UtcNow - known.At
                    : TimeSpan.Zero;
        }

        /// <summary>
        /// Every folder in the account, from the last few minutes if it was
        /// asked for then. See <see cref="FolderGraphLife"/>.
        /// </summary>
        private async Task<List<DriveFound>> FolderGraph(DriveClient client, string? driveId, CancellationToken token)
        {
            var key = driveId ?? "";
            lock (_gate)
            {
                if (_folders != null && _folders.TryGetValue(key, out var known) &&
                    DateTime.UtcNow - known.At < FolderGraphLife)
                    return known.Folders;
            }

            // Outside the lock: this is a network request, and holding the gate
            // across one would stall every other Drive operation in the
            // application behind it. Two searches starting together may both
            // fetch, which costs one extra request and is the cheaper mistake.
            var folders = await client.AllFolders(token, driveId: driveId).ConfigureAwait(false);

            lock (_gate)
            {
                _folders ??= new Dictionary<string, (List<DriveFound>, DateTime)>(StringComparer.Ordinal);
                _folders[key] = (folders, DateTime.UtcNow);
            }

            return folders;
        }

        /// <summary>The size indexes, per drive ("" for My Drive). See <see cref="DriveSizeIndex"/>.</summary>
        private Dictionary<string, DriveSizeIndex>? _sizes;

        /// <summary>
        /// Everything in the drive's size index whose name holds any of the
        /// query's words anywhere in it, as a search's candidates; null when no
        /// index holds the whole drive yet, and Google is asked instead.
        /// </summary>
        private async Task<List<DriveFound>?> FoundInIndex(DriveClient client, SearchQuery query, string? driveId,
            CancellationToken token)
        {
            DriveSizeIndex? index = null;
            CancellationToken lifetime;
            lock (_gate)
            {
                _sizes?.TryGetValue(driveId ?? "", out index);
                lifetime = _lifetime.Token;
            }
            if (index == null || !index.Built) return null;

            // Changes made a moment ago (a file just saved), as a size waits for them.
            var refresh = index.Refresh(client, lifetime);
            try { await refresh.WaitAsync(DriveSizeIndex.AnswerWithin, token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception) { }
            if (!refresh.IsCompleted)
                _ = refresh.ContinueWith(t => Note($"folder sizes: {t.Exception?.GetBaseException().Message}"),
                    CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);

            var items = index.Snapshot();
            if (items == null) return null;
            var hints = query.DriveHints.Select(SearchQuery.FoldName).Where(h => h.Length > 0).ToArray();
            var found = new List<DriveFound>();
            foreach (var item in items)
            {
                token.ThrowIfCancellationRequested();
                if (item.Name.Length == 0) continue;
                if (hints.Length > 0)
                {
                    var name = SearchQuery.FoldName(item.Name);
                    if (!Array.Exists(hints, h => name.Contains(h, StringComparison.Ordinal))) continue;
                }
                // Google's documents and shortcuts are not on the letter: marked as documents, they are left out.
                var mime = item.IsFolder ? DriveClient.FolderMimeType
                    : item.Counted ? "application/octet-stream" : "application/vnd.google-apps.document";
                found.Add(new DriveFound(item.Id, item.Name, mime, item.IsFolder ? -1 : item.Size,
                    new DateTime(item.ModifiedTicks, DateTimeKind.Utc), item.Parent));
            }
            return found;
        }

        /// <summary>Who the mount is signed in as, from Whoami. Keys the saved size indexes.</summary>
        private string? _account;

        /// <summary>Where the size indexes are saved between runs. One encrypted file per account and drive.</summary>
        internal static string SavedSizesDirectory => Path.Combine(Settings.AppDataDir, "DriveSizes");

        /// <summary>The account part of a saved index's name: a hash, so the file name does not give the address away.</summary>
        internal static string AccountKey(string account)
        {
            var hash = System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(account.Trim().ToLowerInvariant()));
            return Convert.ToHexString(hash, 0, 8).ToLowerInvariant();
        }

        internal static string SavedSizesPath(string account, string? driveId) =>
            Path.Combine(SavedSizesDirectory, $"{AccountKey(account)}-{(string.IsNullOrEmpty(driveId) ? "mydrive" : driveId)}.bin");

        /// <summary>
        /// Deletes saved size indexes: every one (sign-out), or every one not
        /// belonging to <paramref name="keepAccount"/> (another account signed in).
        /// Only this one folder's own files are looked at.
        /// </summary>
        internal static void ForgetSavedSizes(string? keepAccount)
        {
            try
            {
                var dir = SavedSizesDirectory;
                if (!Directory.Exists(dir)) return;
                var keep = keepAccount == null ? null : AccountKey(keepAccount) + "-";
                foreach (var file in Directory.GetFiles(dir))
                {
                    if (keep != null && Path.GetFileName(file).StartsWith(keep, StringComparison.Ordinal) &&
                        file.EndsWith(".bin", StringComparison.Ordinal)) continue;
                    try { File.Delete(file); } catch { }
                }
            }
            catch { }
        }

        private bool _otherAccountsSwept;

        /// <summary>
        /// How big a folder on the letter is, from Drive's own listing of the
        /// whole account rather than a walk of its entries on the letter — which would be
        /// one Drive listing per folder and populate the tree as a side effect.
        /// The first answer builds the index; the rest are lookups, refreshed
        /// through the changes feed. Null when it cannot be told.
        /// </summary>
        public async Task<FolderSizeResult?> FolderSizeAsync(string path, CancellationToken token = default)
        {
            DriveMount? mount;
            DriveClient? client;
            CancellationToken lifetime;
            lock (_gate) { mount = _mount; client = _client; lifetime = _lifetime.Token; }
            if (mount == null || client == null || !Owns(mount, path)) return null;
            if (mount.IsSharedDrivesFolder(path)) return await SharedDrivesTotalAsync(mount, path, token).ConfigureAwait(false);
            if (InTrash(mount, path)) return null;      // the size index covers what is not trashed

            var id = mount.FolderIdFor(path) ?? mount.IdFor(path);
            if (id == null) return null;
            var driveId = mount.DriveIdFor(path);
            if (driveId == null && mount.IsSharedDriveRoot(path)) driveId = id;

            DriveSizeIndex index;
            string? account;
            bool sweep = false;
            lock (_gate)
            {
                account = _account;
                if (account != null && !_otherAccountsSwept) { _otherAccountsSwept = true; sweep = true; }
                _sizes ??= new Dictionary<string, DriveSizeIndex>(StringComparer.Ordinal);
                if (!_sizes.TryGetValue(driveId ?? "", out index!))
                    _sizes[driveId ?? ""] = index = new DriveSizeIndex(driveId,
                        account == null ? null : SavedSizesPath(account, driveId));
            }
            // A different account signed in since the last run: its sizes are no use to this one.
            if (sweep) ForgetSavedSizes(keepAccount: account);

            // Built once, the sizes held answer straight away when the feed is
            // slow, and the feed catches up behind them.
            var refresh = index.Refresh(client, lifetime);
            try
            {
                if (index.Built) await refresh.WaitAsync(DriveSizeIndex.AnswerWithin, token).ConfigureAwait(false);
                else await refresh.WaitAsync(token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (TimeoutException) { }
            catch (Exception ex) { Note($"folder sizes: {ex.Message}"); }

            // A refresh left running is watched, so a failure is noted rather than unobserved.
            if (!refresh.IsCompleted)
                _ = refresh.ContinueWith(t => Note($"folder sizes: {t.Exception?.GetBaseException().Message}"),
                    CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);

            return index.SizeOf(id);
        }

        /// <summary>
        /// Makes the next size question ask Drive's changes feed, so a folder on
        /// screen catches up with changes made here, on the web or on a phone.
        /// </summary>
        public void ExpireSizesIfIdle()
        {
            lock (_gate)
                if (_sizes != null)
                    foreach (var index in _sizes.Values) index.ExpireIfIdle();
        }

        /// <summary>
        /// The Shared drives folder's size: every shared drive's own total added
        /// up (Conner: it should show all of them together). Null until each
        /// drive can be told, so a part is never shown as the whole.
        /// </summary>
        private async Task<FolderSizeResult?> SharedDrivesTotalAsync(DriveMount mount, string path, CancellationToken token)
        {
            if (!mount.TryListing(path, out var drives))
            {
                try { await Task.Run(() => mount.Populate(path), token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch (Exception ex) { Note($"folder sizes: {ex.Message}"); return null; }
                if (!mount.TryListing(path, out drives)) return null;
            }

            var each = await Task.WhenAll(drives.Where(d => d.IsFolder)
                .Select(d => FolderSizeAsync(Path.Combine(path, d.Name), token))).ConfigureAwait(false);
            if (each.Any(s => s == null)) return null;

            long bytes = 0, files = 0, folders = 0;
            foreach (var s in each)
            {
                bytes += s!.Value.Bytes;
                files += s.Value.Files;
                folders += s.Value.Folders + 1;
            }
            return new FolderSizeResult(bytes, (int)Math.Min(int.MaxValue, files), (int)Math.Min(int.MaxValue, folders), true);
        }

        /// <summary>
        /// Starts the size index for My Drive as soon as Drive is on the letter,
        /// so the Size column is ready by the time a folder is opened instead of
        /// waiting on a listing of the whole account then.
        /// </summary>
        private void WarmSizes(string root)
        {
            _ = Task.Run(async () =>
            {
                try { await FolderSizeAsync(root).ConfigureAwait(false); }
                catch (Exception ex) { Note($"folder sizes: {ex.Message}"); }
            });
        }

        /// <summary>Whether a path is the mounted letter itself rather than something in it.</summary>
        private static bool IsMountRoot(DriveMount mount, string path)
        {
            try
            {
                var full = Path.GetFullPath(path).TrimEnd('\\');
                var letter = mount.Letter?.TrimEnd('\\');
                return letter != null && string.Equals(full, letter, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(full, mount.Root.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        /// <summary>
        /// What a Drive folder contains, from the listing that populating it
        /// already fetched — no filesystem walk and no network.
        ///
        /// This is the difference between a Drive folder opening instantly and
        /// taking half a minute. Enumerating entries on the letter costs about fifteen
        /// milliseconds each and never caches: a folder of 2118 tracks measured
        /// **33 seconds**, on every visit, against 310ms for a local folder of
        /// twice the size. Every one of those facts — the name, the size, the
        /// modified time — was already in the Drive listing that placed the
        /// files, and was being thrown away.
        ///
        /// False when nothing has populated the folder yet, which is the
        /// caller's signal to go the ordinary way.
        /// </summary>
        public bool TryListing(string path, out IReadOnlyList<DrivePlaced> entries)
        {
            entries = Array.Empty<DrivePlaced>();

            DriveMount? mount;
            lock (_gate) mount = _mount;
            if (mount == null || string.IsNullOrEmpty(path)) return false;

            try { return Owns(mount, path) && mount.TryListing(path, out entries); }
            catch { return false; }
        }

        /// <summary>
        /// The names already in a Drive folder, as they are placed on disk, or
        /// null when this is not a folder on the mounted drive.
        ///
        /// Fetches the folder if nobody has looked in it yet, which costs the one
        /// Drive listing that navigating into it would have cost. That is the
        /// point: the caller is about to decide whether to replace, skip or rename
        /// something in there, and the honest way to answer "is it already there"
        /// is to know what is there.
        ///
        /// **Not to be called on the UI thread.** It is a network request when the
        /// folder is cold, and free when it is not.
        /// </summary>
        public HashSet<string>? NamesIn(string folderPath)
        {
            if (string.IsNullOrEmpty(folderPath) || !Owns(folderPath)) return null;

            // Patient: the answer decides what gets replaced or renamed.
            Populate(folderPath, interactive: false);
            if (!TryListing(folderPath, out var entries)) return null;

            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in entries) names.Add(entry.Name);
            return names;
        }

        /// <summary>
        /// Drops what is remembered about a folder, so the next visit asks Drive
        /// again. This is what Refresh has to mean on a drive whose contents are
        /// somewhere else — re-walking the entries on the letter would re-read a listing
        /// that is just as stale, thirty seconds more slowly.
        /// </summary>
        /// <summary>
        /// A track on the letter as something to read from Google directly, or
        /// null when it is not one. Its size comes from the entry on the letter, which
        /// reads no data.
        /// </summary>
        public IRangeSource? OpenRange(string path)
        {
            DriveMount? mount;
            DriveClient? client;
            lock (_gate) { mount = _mount; client = _client; }
            if (mount == null || client == null || string.IsNullOrEmpty(path) || !Owns(mount, path)) return null;

            try
            {
                long length;
                if (mount.Describe(path) is { } known)
                {
                    if (known.Folder) return null;
                    length = known.Size;
                }
                else
                {
                    var info = new FileInfo(path);
                    if (!info.Exists) return null;
                    length = info.Length;
                }
                var id = IdOrRelist(mount, path, CancellationToken.None).GetAwaiter().GetResult();
                return id == null ? null : new DriveRangeSource(client, id, length);
            }
            catch
            {
                return null;
            }
        }

        public void Forget(string path)
        {
            DriveMount? mount;
            lock (_gate)
            {
                mount = _mount;

                // A refresh is somebody saying they think this is out of date,
                // and the folder graph a search runs on is part of what they
                // mean. Cheap to drop and one request to rebuild.
                _folders = null;
                if (_sizes != null) foreach (var index in _sizes.Values) index.Expire();
            }
            if (mount == null || string.IsNullOrEmpty(path)) return;

            try { if (Owns(mount, path)) mount.Forget(path); } catch { }
        }

        /// <summary>
        /// Sends something on the drive to the Drive trash and takes its
        /// entry on the letter away.
        ///
        /// Trash, not delete. The recycle bin exists because people change their
        /// minds, and a file manager that destroys irrecoverably from one
        /// keystroke will one day be the reason something is gone. It is
        /// recoverable from drive.google.com like anything else somebody deleted.
        /// </summary>
        /// <summary>
        /// The Drive link for something on the drive letter, optionally making it
        /// open for anybody who has the link first.
        ///
        /// Null when the path is not on Drive or has no id we can find, and the
        /// caller says so — the same shape as <see cref="TrashPath"/>, which is
        /// the other command that has to turn a path back into a Drive id.
        /// </summary>
        public async Task<string?> LinkFor(
            string path, bool makePublic, CancellationToken token = default)
        {
            DriveMount? mount;
            DriveClient? client;
            lock (_gate) { mount = _mount; client = _client; }
            if (mount == null || client == null || !Owns(path)) return null;

            var id = await IdOrRelist(mount, path, token).ConfigureAwait(false);
            if (id == null)
            {
                Say("drive.link.failed",
                    $"Could not get a link for {Path.GetFileName(path)}: " +
                    "Google Drive does not have a file by that name here");
                return null;
            }

            try
            {
                return await client.ShareLink(id, makePublic, token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Note($"link for {path} failed: {ex.Message}");
                Say("drive.link.failed", $"Could not get a link: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// A path's Drive id, re-listing its folder once if the map has never
        /// heard of it.
        ///
        /// The ids are built as a folder is populated, so a folder populated by
        /// an earlier run has entries on the letter on disk with nothing here to match
        /// them to. The file is plainly there — somebody has it selected — so
        /// asking Drive for the parent again is the answer rather than reporting
        /// that it is not part of the mounted drive, which is both untrue and
        /// impossible to act on.
        ///
        /// Written out three times before this existed, in TrashPath, in the
        /// rename and here.
        ///
        /// The listing is a network round trip of up to the entry on the letter deadline,
        /// and the callers are commands started from the window: run inline it was
        /// that long with the window not answering, because an async method runs
        /// on its caller's thread until its first await. It goes to a worker, and
        /// giving up on it is possible even though the listing itself carries on.
        /// </summary>
        /// <summary>
        /// The Drive id the mount already knows for a path on the letter, or
        /// null. Only the map: nothing is listed and nothing is read.
        /// </summary>
        internal string? KnownIdOf(string path)
        {
            DriveMount? mount;
            lock (_gate) mount = _mount;
            return mount?.IdFor(path);
        }

        /// <summary>What a file on the letter is now (id, size, date), from the mount's map only; null if unknown.
        /// A download held under an older stamp is of a file since replaced or changed.</summary>
        internal string? StampOf(string path)
        {
            DriveMount? mount;
            lock (_gate) mount = _mount;
            if (mount == null) return null;
            var known = mount.Describe(path);
            return $"{mount.IdFor(path)}|{known?.Size}|{known?.ModifiedUtc.Ticks}";
        }

        private static async Task<string?> IdOrRelist(DriveMount mount, string path, CancellationToken token)
        {
            var id = mount.IdFor(path);
            if (id != null) return id;

            var parent = Path.GetDirectoryName(path);
            if (string.IsNullOrEmpty(parent)) return null;

            Note($"no Drive id for {path}; re-listing {parent}");
            await Task.Run(() =>
            {
                mount.Forget(parent);
                try { mount.Populate(parent, interactive: false); }
                catch (Exception ex) { Note($"re-listing {parent} failed: {ex.Message}"); }
            }).WaitAsync(token).ConfigureAwait(false);

            return mount.IdFor(path);
        }

        /// <summary>
        /// Whether a folder on the drive has nothing in it, asked of Drive rather
        /// than of the listing — which leaves out Google documents and shortcuts,
        /// so a folder holding only those looked empty and was trashed with them.
        /// False when it cannot be told.
        /// </summary>
        public async Task<bool> IsEmptyOnDrive(string path, CancellationToken token = default)
        {
            DriveMount? mount;
            DriveClient? client;
            lock (_gate) { mount = _mount; client = _client; }
            if (mount == null || client == null || !Owns(path)) return false;

            var id = await IdOrRelist(mount, path, token).ConfigureAwait(false);
            if (id == null) return false;

            try
            {
                var children = await client.ListChildren(id, token, max: 1, driveId: mount.DriveIdFor(path),
                    partial: true).ConfigureAwait(false);
                return children.Count == 0;
            }
            catch { return false; }
        }

        /// <summary>
        /// Whether everything Drive holds under a folder — asked of Drive, not of
        /// the listing — is at <paramref name="copiedTo"/> with the same size.
        /// What a move out of the drive asks before it trashes the original.
        ///
        /// False for a Google document or shortcut, which is not a file here; for
        /// anything this mount never placed, which robocopy cannot have copied;
        /// for a copy missing or of another size; and whenever it cannot be told.
        /// Null when the tree is too big to check.
        /// </summary>
        public async Task<bool?> VerifyCopied(string path, string copiedTo, CancellationToken token = default)
        {
            DriveMount? mount;
            DriveClient? client;
            lock (_gate) { mount = _mount; client = _client; }
            if (mount == null || client == null || !Owns(path)) return false;

            var id = await IdOrRelist(mount, path, token).ConfigureAwait(false);
            if (id == null) return false;
            var driveId = mount.DriveIdFor(path);

            var pending = new Stack<(string Id, string Local, string Copy)>();
            pending.Push((id, path, copiedTo));
            int folders = 0;

            try
            {
                while (pending.Count > 0)
                {
                    if (++folders > 2000) return null;
                    token.ThrowIfCancellationRequested();
                    var (folderId, local, copy) = pending.Pop();
                    // Exact: a PC folder ending in a dot ("Backup.") is another
                    // folder to .NET's plain calls, which drop the dot, and the
                    // copy read as missing.
                    if (!Directory.Exists(NameRules.ExactPath(copy))) return false;

                    // The whole folder: a child past the first 100,000 that never
                    // arrived must make this false, or the move trashes it.
                    var children = await client.ListChildren(folderId, token, max: int.MaxValue, driveId: driveId)
                        .ConfigureAwait(false);
                    foreach (var child in children)
                    {
                        if (child.IsGoogleDocument) return false;

                        var placed = mount.ChildPathForId(local, child.Id);
                        if (placed == null) return false;
                        var arrived = Path.Combine(copy, Path.GetFileName(placed));

                        if (child.IsFolder) pending.Push((child.Id, placed, arrived));
                        else
                        {
                            var file = new FileInfo(NameRules.ExactPath(arrived));
                            if (!file.Exists || file.Length != child.Size) return false;
                        }
                    }
                }
                return true;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch { return false; }
        }

        /// <summary>
        /// Whether a folder directly holds anything the letter does not show —
        /// Google documents and shortcuts — asked of Drive. True when it cannot
        /// be told, so the caller says so rather than claiming a complete copy.
        /// </summary>
        public async Task<bool> HasDocumentsIn(string path, CancellationToken token = default)
        {
            DriveMount? mount;
            DriveClient? client;
            lock (_gate) { mount = _mount; client = _client; }
            if (mount == null || client == null || !Owns(path)) return true;

            var id = await IdOrRelist(mount, path, token).ConfigureAwait(false);
            if (id == null) return true;

            try
            {
                var children = await client.ListChildren(id, token, max: int.MaxValue, driveId: mount.DriveIdFor(path))
                    .ConfigureAwait(false);
                return children.Any(c => c.IsGoogleDocument);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch { return true; }
        }

        /// <summary>How recent a listing <see cref="DriveNameOf"/> will name things from.</summary>
        public static readonly TimeSpan FreshListing = TimeSpan.FromSeconds(30);

        /// <summary>
        /// Lists a folder again unless its listing is recent. Once per source
        /// folder of a paste, rather than a name request per item.
        /// </summary>
        public void RelistUnlessFresh(string path)
        {
            DriveMount? mount;
            lock (_gate) mount = _mount;
            if (mount == null || string.IsNullOrEmpty(path) || !Owns(mount, path)) return;
            try
            {
                if (mount.TryListing(path, out _, FreshListing)) return;
                mount.Forget(path);
                NamesIn(path);
            }
            catch { }
        }

        /// <summary>What Drive calls the item at a path, or null when it cannot be told.</summary>
        public async Task<string?> DriveNameOf(string path, CancellationToken token = default)
        {
            DriveMount? mount;
            DriveClient? client;
            lock (_gate) { mount = _mount; client = _client; }
            if (mount == null || client == null || !Owns(path)) return null;

            // From the listing the folder was placed from, which already holds it:
            // a request per item was most of a second each across a big paste.
            // Only a recent one — the name is passed to Drive explicitly, and an
            // old listing put back a rename made on the web since.
            var parent = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(path));
            var leaf = Path.GetFileName(Path.TrimEndingDirectorySeparator(path));
            if (!string.IsNullOrEmpty(parent) && mount.TryListing(parent, out var listed, FreshListing))
                foreach (var item in listed)
                    if (item.DriveName != null && string.Equals(item.Name, leaf, StringComparison.OrdinalIgnoreCase))
                        return item.DriveName;

            var id = await IdOrRelist(mount, path, token).ConfigureAwait(false);
            if (id == null) return null;
            try { return await client.NameOf(id, token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch { return null; }
        }

        /// <summary>
        /// For each path, why it cannot be taken out of Drive — which a move
        /// off the letter does, by copying and then trashing. Asked before the
        /// copy, so a move that is not allowed does not leave a copy behind and
        /// report a failure; it is refused, and says to copy instead.
        /// </summary>
        public async Task<List<string>> WhyNotRemovable(IReadOnlyList<string> paths, CancellationToken token = default)
        {
            var problems = new List<string>();
            DriveMount? mount;
            DriveClient? client;
            lock (_gate) { mount = _mount; client = _client; }
            if (mount == null || client == null) return problems;

            using var lanes = new SemaphoreSlim(4);
            var checks = paths.Select(async path =>
            {
                var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(path));
                await lanes.WaitAsync(token).ConfigureAwait(false);
                try
                {
                    if (IsDriveItself(mount, path))
                        return $"{name}: a shared drive cannot be moved; copy it instead";
                    var id = await IdOrRelist(mount, path, token).ConfigureAwait(false);
                    if (id == null) return $"{name}: Google Drive does not have it any more";
                    var rights = await client.RightsOf(id, token).ConfigureAwait(false);
                    return rights.CanTrash
                        ? null
                        : $"{name}: you do not have permission to remove it from Google Drive, so it can only be copied";
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch (Exception ex)
                {
                    return $"{name}: Google Drive would not say whether it can be moved ({ex.Message})";
                }
                finally { lanes.Release(); }
            }).ToList();

            foreach (var answer in await Task.WhenAll(checks).ConfigureAwait(false))
                if (answer != null) problems.Add(answer);
            return problems;
        }

        public async Task<bool> TrashPath(string path, CancellationToken token = default)
        {
            DriveMount? mount;
            DriveClient? client;
            lock (_gate) { mount = _mount; client = _client; }
            if (mount == null || client == null || !Owns(path)) return false;

            var name = Path.GetFileName(path);

            if (InTrash(mount, path))
            {
                Say("drive.trash.failed", mount.IsTrashFolder(path)
                    ? $"Could not delete {name}: it is Google Drive's trash. Use Empty Google Drive trash instead"
                    : $"Could not move {name} to the trash: it is already there. Use Delete permanently instead");
                return false;
            }

            if (IsDriveItself(mount, path))
            {
                Say("drive.trash.failed",
                    $"Could not delete {name}: shared drives are removed from drive.google.com, not from here");
                return false;
            }

            // The map may not know this path, which is recoverable rather than
            // fatal: the ids are built when a folder is populated, and a folder
            // that was populated by an earlier run, or whose listing was dropped by
            // a refresh, has entries on the letter on disk with nothing here to match them
            // to. Asking Drive for the parent again rebuilds exactly the entries
            // needed — and "it is not part of the mounted Drive" is both untrue and
            // impossible to act on.
            // Still on its way up (a recording a moment ago, a big file
            // streaming): Drive has no id for it yet, so it is deleted the way the
            // letter deletes a new file, and whatever its upload makes is
            // trashed when it lands. It used to be refused as "Google Drive does
            // not have a file by that name here", and then landed anyway.
            if (mount.IsNewNotLanded(path, out var open))
            {
                if (open)
                {
                    Say("drive.trash.failed", $"Could not delete {name}: another program is still saving it");
                    return false;
                }
                await Task.Run(() => mount.RemoveOne(path), token).ConfigureAwait(false);
                Say("drive.trash", $"{name} deleted before it reached Google Drive");
                return true;
            }

            var clock = System.Diagnostics.Stopwatch.StartNew();
            var id = await IdOrRelist(mount, path, token).ConfigureAwait(false);
            long idMs = clock.ElapsedMilliseconds;

            if (id == null)
            {
                Note($"no Drive id known for {path}");
                Say("drive.trash.failed",
                    $"Could not delete {name}: Google Drive does not have a file by that name here");
                return false;
            }

            try
            {
                await client.Trash(id, token, retry: true).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // Rethrown, not swallowed: the caller collects failures and counts
                // them. This only names the one file, which the count cannot.
                Say("drive.trash.failed", $"Could not delete {name}: {ex.Message}");
                throw;
            }

            long trashMs = clock.ElapsedMilliseconds - idMs;

            // Only after Drive has agreed. Removing the entry on the letter first would
            // hide a file that is still there if the trash call then failed.
            mount.RemoveOne(path);
            Note($"trash {name}: id {idMs}ms, Drive {trashMs}ms, letter {clock.ElapsedMilliseconds - idMs - trashMs}ms");
            Say("drive.trash", $"{name} moved to the Google Drive trash");
            return true;
        }

        /// <summary>Why a file saved on the letter a moment ago cannot be moved or copied yet.</summary>
        private const string StillSaving = "it is still being saved to Google Drive. Try again in a moment";

        /// <summary>
        /// Makes a folder inside a folder on the drive, and gives back the local
        /// path of the entry on the letter for it.
        ///
        /// This is the half of "upload a folder" that was missing, and the reason
        /// folders used to be refused: sending a tree means creating every
        /// directory in it and knowing which Drive id each one got, because the
        /// files below it are addressed by that id and nothing else.
        ///
        /// **A folder of that name already there is used, not duplicated.** Drive
        /// would allow a second one — names are not unique in an account — and the
        /// result would be two folders that NTFS can only tell apart by calling
        /// one of them "album (2)", with somebody's album split across both.
        /// Pasting a folder onto a folder of the same name means merge everywhere
        /// else in this application, and it means merge here.
        /// </summary>
        public async Task<string?> CreateFolderIn(
            string parentFullPath, string name, bool announce, CancellationToken token = default)
        {
            DriveMount? mount;
            DriveClient? client;
            lock (_gate) { mount = _mount; client = _client; }
            if (mount == null || client == null || !Owns(parentFullPath)) return null;
            if (InTrash(mount, parentFullPath))
            {
                Say("drive.upload.failed", $"Could not create {name}: nothing can be put in the Google Drive trash");
                return null;
            }

            // What is in the parent has to be known before anything can be said
            // about whether the folder is already there. A parent nobody had opened
            // had no entries in the map, so "is there one of this name" was always
            // no and uploading into it made a second folder beside the first.
            // Nothing when it is already populated, which the folders this upload
            // made itself are.
            // Patient, and checked: a fast-fail listing during a blip left the
            // parent unknown, ChildFolderId said no, and a duplicate was made.
            var clock = System.Diagnostics.Stopwatch.StartNew();
            await Task.Run(() =>
            {
                try { mount.Populate(parentFullPath, interactive: false); } catch { }
            }, token).ConfigureAwait(false);
            long listMs = clock.ElapsedMilliseconds;
            if (!mount.TryListing(parentFullPath, out _))
            {
                Say("drive.upload.failed",
                    $"Could not create {name}: Google Drive would not say what is already in {Path.GetFileName(Path.TrimEndingDirectorySeparator(parentFullPath))}");
                return null;
            }

            // Already there. The entry on the letter is on disk and the id is in the map,
            // so there is nothing to create and nothing to place — only a path to
            // hand back.
            var existing = mount.ChildFolderId(parentFullPath, name);
            if (existing != null)
            {
                var known = Path.Combine(parentFullPath, DriveMount.Sanitise(name));
                Note($"merging into existing Drive folder {known}");
                return known;
            }

            var parentId = mount.FolderIdFor(parentFullPath);
            if (parentId == null)
            {
                Note($"no Drive folder known for {parentFullPath}");
                return null;
            }

            string id;
            try
            {
                id = await client.CreateFolder(name, parentId, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                Say("drive.upload.failed", $"Could not create {name}: {ex.Message}");
                return null;
            }

            if (string.IsNullOrEmpty(id)) return null;

            // Populated on purpose: it was made a moment ago and it is empty, and
            // that is a Drive listing the first visit does not have to pay for to
            // be told what is already known.
            long createMs = clock.ElapsedMilliseconds - listMs;
            var placed = mount.PlaceOne(parentFullPath,
                new DriveEntry(id, name, DriveClient.FolderMimeType, 0, DateTime.UtcNow),
                populated: true);
            Note($"folder {name}: waited for parent listing {listMs}ms, Drive {createMs}ms, " +
                 $"letter {clock.ElapsedMilliseconds - listMs - createMs}ms");

            if (placed == null) return null;

            if (announce) Say("drive.folder.created", $"Created {placed} in Google Drive");
            return Path.Combine(parentFullPath, placed);
        }

        /// <summary>
        /// Makes an empty file in a folder on the drive.
        ///
        /// "New file" on the drive letter used to be `File.Create` on a path
        /// inside the drive: a real local file that Drive had never heard of,
        /// which the pane could not show — it is built from the Drive listing, and
        /// the file is not in it — and which the next mount deleted, because
        /// clearing the drive is how a leftover tree is dealt
        /// with. So the file was announced as created, was invisible immediately,
        /// and was gone for good by the next launch, along with anything anybody
        /// had typed into it.
        /// </summary>
        public async Task<string?> CreateFileIn(
            string folderPath, string name, CancellationToken token = default)
        {
            DriveMount? mount;
            DriveClient? client;
            lock (_gate) { mount = _mount; client = _client; }
            if (mount == null || client == null || !Owns(folderPath)) return null;
            if (InTrash(mount, folderPath))
            {
                Say("drive.upload.failed", $"Could not create {name}: nothing can be put in the Google Drive trash");
                return null;
            }

            var parentId = mount.FolderIdFor(folderPath);
            if (parentId == null)
            {
                Note($"no Drive folder known for {folderPath}");
                return null;
            }

            string id;
            var now = DateTime.UtcNow;
            var created = new DateTime(now.Ticks - now.Ticks % TimeSpan.TicksPerMillisecond, DateTimeKind.Utc);
            try
            {
                id = await client.CreateEmptyFile(name, parentId, token, created).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                Say("drive.upload.failed", $"Could not create {name}: {ex.Message}");
                return null;
            }

            if (string.IsNullOrEmpty(id)) return null;

            // The file exists in Drive whether or not its entry on the letter could be
            // made. Null here said "could not create", and the next F7 made a
            // second one.
            return mount.PlaceOne(folderPath,
                       new DriveEntry(id, name, "application/octet-stream", 0, created))
                   ?? name;
        }

        /// <summary>
        /// Renames something on the drive, in the account and on the letter.
        ///
        /// F2 used to be a plain <c>File.Move</c>, which renames the *entry on the letter*
        /// and tells Drive nothing. The account keeps the old name, so the next
        /// listing puts the old name back — and the renamed entry on the letter is left on
        /// disk under a name no listing mentions, invisible to the pane and in the
        /// way of the file it was made from. "Renamed to X" was announced every
        /// time, and nothing was.
        ///
        /// Drive first and the entry on the letter afterwards. A rename cannot lose
        /// anything either way round, and this order means a failure half way
        /// leaves the letter disagreeing with the account for one refresh rather
        /// than the account disagreeing with itself.
        /// </summary>
        /// <param name="interactive">
        /// Somebody pressed F2 and is waiting: two quick tries rather than a
        /// transfer's patience. A rename inside a paste leaves it false.
        /// </param>
        public async Task<string?> RenameOnDrive(
            string path, string newName, CancellationToken token = default, bool interactive = false)
        {
            DriveMount? mount;
            DriveClient? client;
            lock (_gate) { mount = _mount; client = _client; }
            if (mount == null || client == null || !Owns(path)) return null;

            var folder = Path.GetDirectoryName(path);
            if (string.IsNullOrEmpty(folder)) return null;

            if (InTrash(mount, path))
            {
                Say("rename.failed", mount.IsTrashFolder(path)
                    ? $"Could not rename {Path.GetFileName(path)}: it is Google Drive's trash"
                    : $"Could not rename {Path.GetFileName(path)}: {TrashRefusal}");
                return null;
            }

            if (IsDriveItself(mount, path))
            {
                Say("rename.failed",
                    $"Could not rename {Path.GetFileName(path)}: shared drives are renamed from drive.google.com");
                return null;
            }

            // Still on its way up: Drive has nothing to rename yet, and saying it
            // has no such file was not true.
            if (mount.IsNewNotLanded(path, out _))
            {
                Say("rename.failed",
                    $"Could not rename {Path.GetFileName(path)}: it is still being saved to Google Drive. Try again in a moment");
                return null;
            }

            // The same recovery TrashPath makes, and for the same reason.
            var id = await IdOrRelist(mount, path, token).ConfigureAwait(false);

            if (id == null)
            {
                Say("rename.failed",
                    $"Could not rename {Path.GetFileName(path)}: Google Drive does not have a file by that name here");
                return null;
            }

            // Read before anything is removed: afterwards the entry on the letter is gone
            // and with it the only local record of how big the file was.
            bool isFolder;
            long size = 0;
            var modified = DateTime.UtcNow;
            try
            {
                // A rename does not change a file's modified time in Drive, and
                // an entry on the letter put back with "now" read as changed
                // contents at the next listing.
                if (mount.Describe(path) is { } known)
                    (isFolder, size, modified) = (known.Folder, known.Size, known.ModifiedUtc);
                else
                {
                    isFolder = Directory.Exists(path);
                    if (!isFolder)
                    {
                        var info = new FileInfo(path);
                        size = info.Length;
                        modified = info.LastWriteTimeUtc;
                    }
                }
            }
            catch { isFolder = false; }

            await client.MoveOrRename(id, newName, null, null, token, retry: true, interactive: interactive)
                .ConfigureAwait(false);

            // Moved in place first: a populated folder keeps everything under it
            // and needs no new listing. Only when that is refused, RemoveOne takes
            // the entry on the letter and its subtree away and PlaceOne puts it back under
            // the new name with the same id.
            var placed = mount.MoveLocal(path, folder, newName);
            if (placed == null)
            {
                mount.RemoveOne(path);
                placed = mount.PlaceOne(folder, new DriveEntry(
                    id, newName,
                    isFolder ? DriveClient.FolderMimeType : "application/octet-stream",
                    isFolder ? 0 : size,
                    modified));
            }

            Say("drive.renamed", $"Renamed to {placed ?? newName}");
            return placed ?? newName;
        }

        /// <summary>
        /// Copies a local file up into a folder on the drive, and makes it appear
        /// there without re-listing the whole folder.
        /// </summary>
        /// <param name="announce">
        /// Whether this one file is worth a sentence of its own. True for a
        /// handful, false for a tree: a folder of two thousand tracks would
        /// otherwise post two thousand messages through the UI thread that is at
        /// that moment drawing the progress dialog, and say each of them over the
        /// top of the last. What speaks for a bulk upload is the dialog, which
        /// reads out every tenth of the whole job.
        /// </param>
        /// <param name="replace">
        /// Take the place of a file of the same name already in the folder, rather
        /// than sitting beside it. Drive allows two files in one folder to share a
        /// name, so "replace" here is genuinely two steps — put the new one up,
        /// then trash the old — and they happen in that order because the other
        /// one loses the file whenever the upload fails.
        /// </param>
        /// <returns>
        /// The name the file was actually placed under on the drive letter, or
        /// null when it did not go. That is not always the name it was given:
        /// Drive allows two files in one folder to share a name and NTFS does not,
        /// so a second "song.flac" becomes "song (2).flac" on this machine — and
        /// "Saved as song (2).flac" is the whole value of saying anything about it,
        /// which the caller cannot say without being told.
        /// </returns>
        public async Task<string?> UploadInto(
            string localPath, string destinationFolder,
            Action<UploadProgress>? progress = null, bool announce = true,
            bool replace = false, CancellationToken token = default)
        {
            DriveMount? mount;
            DriveClient? client;
            lock (_gate) { mount = _mount; client = _client; }
            if (mount == null || client == null || !Owns(destinationFolder)) return null;

            var name = Path.GetFileName(localPath);
            if (InTrash(mount, destinationFolder))
            {
                // Like every other failure here: a bulk upload says it once, in its summary.
                if (announce) Say("drive.upload.failed", $"Could not upload {name}: nothing can be put in the Google Drive trash");
                return null;
            }

            // Looked up *before* the upload, because afterwards there are two
            // files of this name in Drive and the path map only remembers the one
            // placed most recently. Asked for even when nothing is being replaced
            // costs nothing; asked for afterwards would trash the file just
            // uploaded.
            var wanted = DriveMount.Sanitise(name);
            var target = Path.Combine(destinationFolder, wanted);

            // A file replaces a file. A folder of the same name is not what
            // Overwrite means for a file, and replacing it sent the folder and
            // everything in it to the Drive trash; the upload keeps both instead.
            string? replacing = replace && mount.FolderIdFor(target) == null ? mount.IdFor(target) : null;

            var parentId = mount.FolderIdFor(destinationFolder);
            if (parentId == null)
            {
                Note($"no Drive folder known for {destinationFolder}");
                Say("drive.upload.failed",
                    $"Could not upload {name}: {Path.GetFileName(destinationFolder)} is not part of the mounted Drive");
                return null;
            }

            if (announce) Say("drive.upload.start", $"Uploading {name} to Google Drive");

            var clock = System.Diagnostics.Stopwatch.StartNew();
            string id;
            try
            {
                id = await client.Upload(localPath, name, parentId,
                    announce ? Throttled(progress, name) : progress, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Somebody pressed the button on the transfer dialog. Not a
                // failure, and worth being a different sentence from one — but
                // only once, by whoever asked for the upload, rather than once per
                // file that happened to be in flight when the button was pressed.
                if (announce) Say("drive.upload.cancelled", $"Upload of {name} cancelled");
                throw;
            }
            catch (Exception ex)
            {
                // Rethrown: the caller collects the failures and reports the
                // count. This names the file while the reason is still to hand —
                // and only when this file is one of a few, because a tree whose
                // whole destination has gone away is one problem said two thousand
                // times, over the top of the summary that would explain it.
                if (announce) Say("drive.upload.failed", $"Could not upload {name}: {DriveUpload.Reason(ex)}");
                throw;
            }

            if (string.IsNullOrEmpty(id))
            {
                if (announce)
                    Say("drive.upload.failed", $"Could not upload {name}: Drive gave back no file id");
                return null;
            }

            long size = 0;
            try { size = new FileInfo(localPath).Length; } catch { }

            // Stamped as Drive was told to stamp it; see DriveClient.UploadStamp.
            long sendMs = clock.ElapsedMilliseconds;
            var entry = new DriveEntry(id, name, "application/octet-stream", size, DriveClient.UploadStamp(localPath));
            var placed = mount.PlaceOne(destinationFolder, entry);
            Note($"upload {name}: Drive {sendMs}ms, letter {clock.ElapsedMilliseconds - sendMs}ms");

            // Only now is the old copy expendable: the new one is in Drive, with
            // an id, and nothing that happens next can take it away.
            if (replacing != null)
                placed = await Replace(
                    mount, client, destinationFolder, wanted, placed, entry, replacing, token).ConfigureAwait(false);

            // The bytes are in Drive either way. An entry on the letter that could not be
            // made is a local display problem, not a failed upload, and reporting
            // it as one would have the caller upload it again.
            return placed ?? wanted;
        }

        /// <summary>
        /// Trashes the copy that was there before, and gives its name to the one
        /// that has just arrived.
        ///
        /// The renaming half is not cosmetic. While the old entry on the letter still held
        /// the name, the new one had to be placed as "song (2).flac" — that is
        /// exactly what <c>PlaceOne</c> is for, and it is what keeps the path map
        /// honest about which id is which. Left there, "replace" would produce a
        /// folder holding one file with a number after its name and nothing to say
        /// why. Both names are dropped and the entry placed again, which is local
        /// work only: <c>RemoveOne</c> deletes an entry on the letter and forgets an id, and
        /// nothing it does can reach the account.
        /// </summary>
        private async Task<string?> Replace(
            DriveMount mount, DriveClient client, string folder,
            string wanted, string? placed, DriveEntry entry, string replacing,
            CancellationToken token)
        {
            try
            {
                await client.Trash(replacing, token, retry: true).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                // The upload worked and the tidying did not, which is worth
                // saying plainly: what is in Drive now is two files of that name,
                // not one, and nobody would guess that from "uploaded".
                Say("drive.upload.failed",
                    $"Uploaded {wanted}, but the copy that was already there could not be removed: {ex.Message}");
                return placed;
            }

            mount.RemoveOne(Path.Combine(folder, wanted));
            if (placed != null && !string.Equals(placed, wanted, StringComparison.OrdinalIgnoreCase))
                mount.RemoveOne(Path.Combine(folder, placed));

            return mount.PlaceOne(folder, entry);
        }

        /// <summary>
        /// The caller's progress callback, with a spoken one alongside it that
        /// fires far less often.
        ///
        /// The dialog wants every chunk — it is drawing a bar and a rate — and a
        /// screen reader wants almost none of them: measured, a gigabyte is 128
        /// chunks about 570ms apart, so one message each would talk for the whole
        /// upload and over everything else. The caller's callback is passed
        /// through untouched; only the sentence is rationed.
        /// </summary>
        private Action<UploadProgress>? Throttled(Action<UploadProgress>? progress, string name)
        {
            if (_notification == null) return progress;

            long last = -UploadReportMs;
            var clock = System.Diagnostics.Stopwatch.StartNew();

            return p =>
            {
                progress?.Invoke(p);

                long now = clock.ElapsedMilliseconds;
                if (now - last < UploadReportMs) return;
                last = now;

                Say("drive.upload.progress",
                    $"{name}, {(int)p.Percent} percent of {SizeFormatter.Format(p.BytesTotal, _units)}");
            };
        }

        /// <summary>
        /// Moves something already on the drive, under <paramref name="newName"/>
        /// when that is given — one request either way, since a name and a
        /// parent are both only properties.
        /// </summary>
        public async Task<bool> MoveOnDrive(string fromPath, string toFolder, string? newName = null,
            CancellationToken token = default)
        {
            DriveMount? mount;
            DriveClient? client;
            lock (_gate) { mount = _mount; client = _client; }
            if (mount == null || client == null || !Owns(fromPath) || !Owns(toFolder)) return false;

            if (InTrash(mount, toFolder))
                throw new InvalidOperationException("nothing can be moved into the Google Drive trash; delete it instead");
            if (InTrash(mount, fromPath))
                throw new InvalidOperationException("it is in the Google Drive trash; restore it first");

            if (IsDriveItself(mount, fromPath))
                throw new InvalidOperationException(
                    "shared drives cannot be moved; move what is inside one instead");

            if (mount.IsNewNotLanded(fromPath, out _))
                throw new InvalidOperationException(StillSaving);

            // Through the relisting lookup, as trash and rename are: a path whose
            // folder listing was dropped is still a file somebody has selected.
            var id = await IdOrRelist(mount, fromPath, token).ConfigureAwait(false)
                     ?? throw new InvalidOperationException("Google Drive does not have a file by that name here");
            var toId = mount.FolderIdFor(toFolder)
                       ?? await IdOrRelist(mount, toFolder, token).ConfigureAwait(false)
                       ?? throw new InvalidOperationException("Google Drive does not know the destination folder");
            var fromId = mount.FolderIdFor(Path.GetDirectoryName(fromPath) ?? "");

            // Without the folder it is leaving, the request only adds one: the
            // item would be in both places.
            if (string.IsNullOrEmpty(fromId))
                throw new InvalidOperationException("Google Drive would not say which folder it is in");

            // Asked first, and said plainly. Drive refuses a move the account may
            // not make, but a shared item is the one place where what is allowed
            // is not obvious from here: a viewer, a commenter, a contributor in
            // a shared drive, an owner who has locked the file down.
            // Both at once: two round trips that do not depend on each other.
            var rightsAsked = client.RightsOf(id, token);
            var intoAsked = client.RightsOf(toId, token);
            await Task.WhenAll(rightsAsked, intoAsked).ConfigureAwait(false);
            var rights = await rightsAsked.ConfigureAwait(false);
            var into = await intoAsked.ConfigureAwait(false);
            if (!into.CanAddChildren)
                throw new InvalidOperationException("you do not have permission to add anything to that folder");
            bool sameDrive = string.Equals(rights.DriveId, into.DriveId, StringComparison.Ordinal);
            if (sameDrive ? !rights.CanMoveWithinDrive : !rights.CanMoveOutOfDrive)
                throw new InvalidOperationException(sameDrive
                    ? "you do not have permission to move it; copy it instead"
                    : $"you do not have permission to move it out of {(rights.DriveId == null ? "My Drive" : "its shared drive")}; copy it instead");

            // Read before the move, because afterwards the entry on the letter is gone
            // and with it the only local record of how big the file was.
            // With no new name Drive keeps its own, and that is the name the
            // moved row carries: the name on disk is the cleaned-up one
            // ("AC_DC.flac" for "AC:DC.flac"), and recording that as Drive's
            // made a later copy or rename of it send the wrong name to Drive.
            var onDisk = Path.GetFileName(Path.TrimEndingDirectorySeparator(fromPath));
            var name = newName ?? mount.DriveNameAt(fromPath) ?? onDisk;
            long size = 0;
            bool isFolder = false;
            var modified = DateTime.UtcNow;
            try
            {
                // A move keeps the date, as a rename does.
                if (mount.Describe(fromPath) is { } known)
                    (isFolder, size, modified) = (known.Folder, known.Size, known.ModifiedUtc);
                else
                {
                    isFolder = Directory.Exists(fromPath);
                    if (!isFolder)
                    {
                        var info = new FileInfo(fromPath);
                        size = info.Length;
                        modified = info.LastWriteTimeUtc;
                    }
                }
            }
            catch { }

            // Retried when Google says "not now": one 429 in a move of three
            // hundred files failed that file outright.
            await client.MoveOrRename(id, newName, fromId, toId, token, retry: true).ConfigureAwait(false);

            // Moved in place when the mount can, keeping a populated folder's
            // contents. Otherwise removed and put where it went: removing the
            // source alone would take the file off the drive as far as this
            // machine is concerned, in a folder that will not be asked about again.
            if (mount.MoveLocal(fromPath, toFolder, name) == null)
            {
                mount.RemoveOne(fromPath);
                mount.PlaceOne(toFolder, new DriveEntry(
                    id, name,
                    isFolder ? "application/vnd.google-apps.folder" : "application/octet-stream",
                    isFolder ? 0 : size,
                    modified));
            }

            // Nothing on the drive moved by itself, and the entry on the letter
            // disappearing from the folder is all there would otherwise be to
            // show for it.
            Say("drive.moved", $"Moved {name} to {Path.GetFileName(toFolder)}");
            return true;
        }

        /// <summary>
        /// Copies something that is already on the drive into another folder on
        /// it, without a byte leaving Google.
        ///
        /// A file is one `files.copy` — see <see cref="DriveClient.Copy"/>. A
        /// folder is not, because Drive will not copy one: it is a new folder and
        /// then the same question about everything inside it, which is why this
        /// lives here rather than in the client. The listing it walks is Drive's
        /// own rather than the entries on the letter, so a folder nobody has opened on
        /// this machine copies exactly as well as one that is on screen.
        ///
        /// <paramref name="name"/> is what to call it at the destination, already
        /// made unique by the caller, or null to keep Drive's own name — two
        /// files with one name in one folder is
        /// legal in Drive and impossible on a drive letter, where the entry on the letter
        /// for the second would have nowhere to go.
        ///
        /// Returns the number of files and folders that arrived, so a folder copy
        /// can report what it did rather than "1 item".
        /// </summary>
        public async Task<int> CopyOnDrive(string fromPath, string toFolder, string? name,
            CancellationToken token = default, DriveCopyReport? report = null)
        {
            DriveMount? mount;
            DriveClient? client;
            lock (_gate) { mount = _mount; client = _client; }
            if (mount == null || client == null || !Owns(fromPath) || !Owns(toFolder)) return 0;

            // The whole list of shared drives is not a thing to duplicate; one
            // shared drive is, and copies as a folder of what is in it.
            if (mount.IsSharedDrivesFolder(fromPath)) return 0;
            if (mount.IsTrashFolder(fromPath)) return 0;
            if (InTrash(mount, toFolder))
                throw new InvalidOperationException("nothing can be copied into the Google Drive trash");
            if (mount.IsNewNotLanded(fromPath, out _))
                throw new InvalidOperationException(StillSaving);

            var id = await IdOrRelist(mount, fromPath, token).ConfigureAwait(false)
                     ?? throw new InvalidOperationException("Google Drive does not have a file by that name here");
            var intoId = mount.FolderIdFor(toFolder)
                         ?? await IdOrRelist(mount, toFolder, token).ConfigureAwait(false)
                         ?? throw new InvalidOperationException("Google Drive does not know the destination folder");
            var fromDrive = mount.DriveIdFor(fromPath);

            bool isFolder;
            try { isFolder = mount.Describe(fromPath)?.Folder ?? Directory.Exists(fromPath); }
            catch { isFolder = false; }

            // Both asked at once: each is a round trip, and neither needs the other.
            var intoAsked = client.RightsOf(intoId, token);
            var itselfAsked = isFolder ? null : client.RightsOf(id, token);
            await Task.WhenAll(itselfAsked == null ? new Task[] { intoAsked } : new Task[] { intoAsked, itselfAsked })
                .ConfigureAwait(false);

            var into = await intoAsked.ConfigureAwait(false);
            if (!into.CanAddChildren)
                throw new InvalidOperationException("you do not have permission to add anything to that folder");

            // A folder's own answer is about what is inside it, and each file
            // inside is refused by Drive on its own if it has to be.
            if (itselfAsked != null && !(await itselfAsked.ConfigureAwait(false)).CanCopy)
                throw new InvalidOperationException("its owner has not allowed it to be copied");

            if (!isFolder)
            {
                var shown = name ?? Path.GetFileName(fromPath);
                report?.Starting(shown);
                var single = await client.Copy(id, name, intoId, token).ConfigureAwait(false);
                if (string.IsNullOrEmpty(single.Id)) return 0;
                report?.Copied(shown, Math.Max(0, single.Size));

                // Placed, so it is on screen without a fresh listing — the same
                // bookkeeping MoveOnDrive does, and for the same reason: the
                // destination folder has already been populated and will not be
                // asked about again.
                mount.PlaceOne(toFolder, single);
                return 1;
            }

            // A folder: make it, then everything under it. The new folder has to
            // exist before anything can be copied into it, so this cannot be done
            // in parallel with its own contents — but Drive is doing all the real
            // work and none of it is our link.
            // Listed before anything is made, and everything made is remembered
            // and never copied. Copying a folder into itself, or into a folder
            // inside it, otherwise finds its own copy among the things to copy —
            // and copies that, into itself, until Drive or the quota gives out.
            var children = await client.ListChildren(id, token, max: int.MaxValue, driveId: fromDrive).ConfigureAwait(false);
            var made = new HashSet<string>(StringComparer.Ordinal);

            // Drive's own name when none was chosen: the letter's is cleaned up.
            name ??= await client.NameOf(id, token).ConfigureAwait(false);
            var folderId = await client.CreateFolder(name, intoId, token).ConfigureAwait(false);
            if (string.IsNullOrEmpty(folderId)) return 0;
            made.Add(folderId);

            var placed = mount.PlaceOne(toFolder, new DriveEntry(
                folderId, name, DriveClient.FolderMimeType, 0, DateTime.UtcNow));

            int count = 1;
            var copiedInto = placed != null ? Path.Combine(toFolder, placed) : null;
            var gate = new object();

            count += await CopyFilesInto(client, mount, children, folderId, copiedInto, made, gate, token, report)
                .ConfigureAwait(false);

            foreach (var child in children)
            {
                token.ThrowIfCancellationRequested();
                if (!child.IsFolder || made.Contains(child.Id)) continue;

                // One folder that will not copy is said and passed over; the
                // folders after it still go.
                try
                {
                    count += await CopyFolderById(client, mount, child.Id, child.Name,
                        folderId, copiedInto, made, child.DriveId, token, report, gate).ConfigureAwait(false);
                }
                catch (Exception ex) when (DriveCopyReport.CarriesOn(ex, report, gate, child.Name,
                                               "the folder could not be copied: ", token)) { }
            }

            return count;
        }

        /// <summary>Files copied at once within one folder.</summary>
        private const int CopyStreams = 4;

        /// <summary>
        /// The files (not folders) of a listing, copied into
        /// <paramref name="intoId"/> <see cref="CopyStreams"/> at a time.
        ///
        /// One at a time, a folder of two hundred tracks was two hundred round
        /// trips end to end with Google doing all the work and the link idle.
        /// The folders stay in order, after the files: each has to exist before
        /// anything goes into it. What is shared is serialised under
        /// <paramref name="gate"/> — the report, whose listeners count and were
        /// written for one caller, and the placing, which reads a folder's names
        /// to choose a free one. A copy already sent when another fails or the
        /// copy is cancelled is let finish and placed, because Drive has made it.
        /// </summary>
        private static async Task<int> CopyFilesInto(DriveClient client, DriveMount mount,
            List<DriveEntry> children, string intoId, string? intoPath, HashSet<string> made,
            object gate, CancellationToken token, DriveCopyReport? report)
        {
            var files = children.Where(child => !child.IsFolder && !made.Contains(child.Id)).ToList();
            if (files.Count == 0) return 0;

            int count = 0;
            await Parallel.ForEachAsync(files,
                new ParallelOptions { MaxDegreeOfParallelism = CopyStreams, CancellationToken = token },
                async (child, _) =>
                {
                    lock (gate) report?.Starting(child.Name);
                    DriveEntry copy;
                    try { copy = await client.Copy(child.Id, child.Name, intoId, token).ConfigureAwait(false); }
                    catch (Exception ex) when (DriveCopyReport.CarriesOn(ex, report, gate, child.Name, "", token))
                    {
                        return;
                    }
                    if (string.IsNullOrEmpty(copy.Id)) return;

                    lock (gate)
                    {
                        report?.Copied(child.Name, Math.Max(0, child.Size));
                        if (intoPath != null) mount.PlaceOne(intoPath, copy);
                    }
                    Interlocked.Increment(ref count);
                }).ConfigureAwait(false);

            return count;
        }

        /// <summary>
        /// How many files a copy inside Drive will make and how big they are,
        /// from Drive's own listings — the copy itself says nothing until each
        /// file is done, so this is what a percentage is measured against.
        /// Four folders are listed at a time. <paramref name="looking"/> is
        /// told each folder as it is read.
        /// </summary>
        public async Task<(int Files, long Bytes)> MeasureOnDrive(IReadOnlyList<string> paths,
            Action<string>? looking, CancellationToken token)
        {
            DriveMount? mount;
            DriveClient? client;
            lock (_gate) { mount = _mount; client = _client; }
            if (mount == null || client == null) return (0, 0);

            int files = 0;
            long bytes = 0;
            // Trashed carries down: inside a trashed folder everything is trashed
            // with it, and listed as not trashed the Trash folder measured empty.
            var pending = new List<(string Id, string? Drive, string Name, bool Trashed)>();

            foreach (var path in paths)
            {
                token.ThrowIfCancellationRequested();
                var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(path));
                var known = mount.Describe(path);
                bool folder;
                try { folder = known?.Folder ?? Directory.Exists(path); } catch { folder = false; }
                if (!folder)
                {
                    files++;
                    try { bytes += known?.Size ?? new FileInfo(path).Length; } catch { }
                    continue;
                }
                var id = await IdOrRelist(mount, path, token).ConfigureAwait(false);
                if (id != null) pending.Add((id, mount.DriveIdFor(path), name, InTrash(mount, path)));
            }

            while (pending.Count > 0)
            {
                token.ThrowIfCancellationRequested();
                var batch = pending.Take(4).ToList();
                pending.RemoveRange(0, batch.Count);
                foreach (var item in batch) looking?.Invoke(item.Name);

                var listings = await Task.WhenAll(batch.Select(item =>
                    client.ListChildren(item.Id, token, max: int.MaxValue, driveId: item.Drive, trashed: item.Trashed)))
                    .ConfigureAwait(false);

                for (int i = 0; i < batch.Count; i++)
                    foreach (var child in listings[i])
                    {
                        if (child.IsFolder) pending.Add((child.Id, child.DriveId ?? batch[i].Drive, child.Name, batch[i].Trashed));
                        else { files++; bytes += Math.Max(0, child.Size); }
                    }
            }
            return (files, bytes);
        }

        /// <summary>
        /// The same again one level down, by id rather than by path.
        ///
        /// Below the top of the copy there is no local path to work from — the
        /// folders being made have never existed on this machine — so the
        /// recursion carries the Drive id it is copying into and the local path
        /// only as far as the entries on the letter have actually been placed.
        /// </summary>
        private static async Task<int> CopyFolderById(DriveClient client, DriveMount mount,
            string fromId, string name, string intoId, string? intoPath, HashSet<string> made,
            string? fromDrive, CancellationToken token, DriveCopyReport? report, object gate)
        {
            var children = await client.ListChildren(fromId, token, max: int.MaxValue, driveId: fromDrive).ConfigureAwait(false);

            var folderId = await client.CreateFolder(name, intoId, token).ConfigureAwait(false);
            if (string.IsNullOrEmpty(folderId)) return 0;
            made.Add(folderId);

            string? here = null;
            if (intoPath != null)
            {
                string? placed;
                lock (gate)
                    placed = mount.PlaceOne(intoPath, new DriveEntry(
                        folderId, name, DriveClient.FolderMimeType, 0, DateTime.UtcNow));
                if (placed != null) here = Path.Combine(intoPath, placed);
            }

            int count = 1;
            count += await CopyFilesInto(client, mount, children, folderId, here, made, gate, token, report)
                .ConfigureAwait(false);

            foreach (var child in children)
            {
                token.ThrowIfCancellationRequested();
                if (!child.IsFolder || made.Contains(child.Id)) continue;

                try
                {
                    count += await CopyFolderById(client, mount, child.Id, child.Name,
                        folderId, here, made, child.DriveId, token, report, gate).ConfigureAwait(false);
                }
                catch (Exception ex) when (DriveCopyReport.CarriesOn(ex, report, gate, child.Name,
                                               "the folder could not be copied: ", token)) { }
            }

            return count;
        }

        /// <summary>
        /// Whether a path is the list of shared drives or one of the drives in it
        /// — neither of which is a file Drive will trash, rename or move.
        /// </summary>
        private static bool IsDriveItself(DriveMount mount, string path) =>
            mount.IsSharedDrivesFolder(path) || mount.IsSharedDriveRoot(path) || mount.IsTrashFolder(path);

        // ---------------- the trash ----------------

        /// <summary>Whether a path is the Trash folder on the letter.</summary>
        public bool IsTrashFolder(string path)
        {
            DriveMount? mount;
            lock (_gate) mount = _mount;
            try { return mount != null && Owns(mount, path) && mount.IsTrashFolder(path); } catch { return false; }
        }

        /// <summary>Whether a path is something inside the Trash folder, at any depth.</summary>
        public bool IsInTrash(string path)
        {
            DriveMount? mount;
            lock (_gate) mount = _mount;
            try { return mount != null && Owns(mount, path) && mount.IsInTrash(path); } catch { return false; }
        }

        /// <summary>The Trash folder or anything in it: nothing can be written there.</summary>
        private static bool InTrash(DriveMount mount, string path) => mount.IsTrashFolder(path) || mount.IsInTrash(path);

        private const string TrashRefusal = "it is in the Google Drive trash. Restore it first";

        /// <summary>
        /// Puts something in the Trash folder back where it was. Only for items
        /// directly in the Trash folder: inside a trashed folder, the folder is
        /// what was trashed, and restoring it brings everything in it back.
        /// </summary>
        public async Task<bool> RestoreFromTrash(string path, CancellationToken token = default)
        {
            var (mount, client, id, name) = await TrashItem(path, "restore", token).ConfigureAwait(false);
            if (mount == null || client == null || id == null) return false;
            IReadOnlyList<string> backIn;
            try { backIn = await client.Untrash(id, token).ConfigureAwait(false); }
            catch (Exception ex)
            {
                Say("drive.restore.failed", $"Could not restore {name}: {ex.Message}");
                throw;
            }
            mount.RemoveOne(path);
            lock (_gate) _folders = null;

            // The folder it went back to shows it on the next visit, listed
            // already or not. Drive has agreed by now, so nothing here may make
            // the restore read as failed.
            try
            {
                foreach (var parent in backIn)
                {
                    if (parent == await client.RootId(token).ConfigureAwait(false)) mount.ForgetKey("");
                    mount.ForgetFolderId(parent);
                }
            }
            catch (Exception ex) { Note($"restore {name}: could not refresh its folder: {ex.Message}"); }
            Say("drive.restored", $"Restored {name}");
            return true;
        }

        /// <summary>
        /// The web app's Undo: puts back what it trashed a moment ago, by the Drive ids it had. Returns how many
        /// came back. Untrashing something not in the trash changes nothing.
        /// </summary>
        public async Task<int> UntrashIds(IReadOnlyList<string> ids, CancellationToken token = default)
        {
            DriveMount? mount;
            DriveClient? client;
            lock (_gate) { mount = _mount; client = _client; }
            if (mount == null || client == null) return 0;
            int back = 0;
            foreach (var id in ids)
            {
                IReadOnlyList<string> backIn;
                try { backIn = await client.Untrash(id, token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch (Exception ex)
                {
                    Say("drive.restore.failed", $"Could not restore it: {ex.Message}");
                    continue;
                }
                back++;
                try
                {
                    foreach (var parent in backIn)
                    {
                        if (parent == await client.RootId(token).ConfigureAwait(false)) mount.ForgetKey("");
                        mount.ForgetFolderId(parent);
                    }
                }
                catch (Exception ex) { Note($"undo trash {id}: could not refresh its folder: {ex.Message}"); }
            }
            if (back > 0)
            {
                lock (_gate) _folders = null;
                var top = (mount.Letter ?? mount.Root).TrimEnd('\\') + "\\";
                try { mount.Forget(Path.Combine(top, DriveClient.TrashName)); } catch { }
                Say("drive.restored", back == 1 ? "Restored it" : $"Restored {back} items");
            }
            return back;
        }

        /// <summary>
        /// Deletes something in the Trash folder for good. The caller has asked;
        /// this only refuses anything that is not in the trash, so nothing
        /// outside it can ever be deleted permanently from here.
        /// </summary>
        public async Task<bool> DeleteForever(string path, CancellationToken token = default)
        {
            var (mount, client, id, name) = await TrashItem(path, "delete", token).ConfigureAwait(false);
            if (mount == null || client == null || id == null) return false;
            try { await client.DeleteForever(id, token).ConfigureAwait(false); }
            catch (Exception ex)
            {
                Say("drive.trash.failed", $"Could not delete {name}: {ex.Message}");
                throw;
            }
            mount.RemoveOne(path);
            Say("drive.deleted", $"Deleted {name} permanently");
            return true;
        }

        /// <summary>Deletes everything in My Drive's trash for good. The caller has asked.</summary>
        public async Task<bool> EmptyTrash(CancellationToken token = default)
        {
            DriveMount? mount;
            DriveClient? client;
            lock (_gate) { mount = _mount; client = _client; }
            if (mount == null || client == null) return false;
            try { await client.EmptyTrash(token).ConfigureAwait(false); }
            catch (Exception ex)
            {
                Say("drive.trash.failed", $"Could not empty the Google Drive trash: {ex.Message}");
                throw;
            }
            var top = (mount.Letter ?? mount.Root).TrimEnd('\\') + "\\";
            try { mount.Forget(Path.Combine(top, DriveClient.TrashName)); } catch { }
            Say("drive.trash.emptied", "Google Drive trash emptied");
            return true;
        }

        /// <summary>One item directly in the Trash folder, by id, or nulls with the reason said.</summary>
        private async Task<(DriveMount? Mount, DriveClient? Client, string? Id, string Name)> TrashItem(
            string path, string verb, CancellationToken token)
        {
            DriveMount? mount;
            DriveClient? client;
            lock (_gate) { mount = _mount; client = _client; }
            var name = Path.GetFileName(path.TrimEnd('\\'));
            string failed = verb == "restore" ? "drive.restore.failed" : "drive.trash.failed";
            if (mount == null || client == null || !Owns(path)) return (null, null, null, name);
            if (!mount.IsInTrash(path))
            {
                Say(failed, $"Could not {verb} {name}: it is not in the Google Drive trash");
                return (null, null, null, name);
            }
            var parent = Path.GetDirectoryName(path.TrimEnd('\\'));
            if (!mount.IsTrashFolder(parent))
            {
                Say(failed, $"Could not {verb} {name} on its own: it is inside a trashed folder. " +
                            $"{(verb == "restore" ? "Restore" : "Delete")} the folder instead");
                return (null, null, null, name);
            }
            var id = await IdOrRelist(mount, path, token).ConfigureAwait(false);
            if (id == null)
            {
                Say(failed, $"Could not {verb} {name}: Google Drive does not have it in the trash any more");
                return (null, null, null, name);
            }
            return (mount, client, id, name);
        }

        public void Dispose()
        {
            // For good: a mount that finishes after this would leave a letter and a
            // drive behind a process that is on its way out.
            _disposed = true;
            try { _lifetime.Cancel(); } catch { }
            // A mount disconnected a moment ago may still be sending; the process
            // must not leave with that volume half taken down.
            try { Teardown.Wait(DriveMount.DrainFor + TimeSpan.FromSeconds(15)); } catch { }
            TearDown();
        }
    }
}











