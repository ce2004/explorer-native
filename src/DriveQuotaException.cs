using System;

namespace ExplorerNative
{
    /// <summary>
    /// Google has refused for a reason that will not change in the next minute:
    /// the account's daily limit, or its storage being full.
    ///
    /// A type of its own because it is the one Drive failure that must stop the
    /// whole transfer rather than one file. Everything else the upload meets —
    /// a 502, a dropped connection, a slow listing — is per-file and per-moment,
    /// and the right answer is to retry it and carry on with the other nine
    /// hundred. These two are neither. Retrying is pointless, and carrying on
    /// means nine hundred identical failures scrolling past the one sentence
    /// that explains all of them.
    ///
    /// So it is thrown out of the file that met it, through
    /// <see cref="DriveUpload.UploadOne"/>, which lets it past on purpose, and
    /// out of the parallel loop — which abandons the remaining files, correctly,
    /// for the only case where abandoning them is right.
    ///
    /// Nothing is lost by stopping. The paste can be repeated tomorrow with
    /// "fill in what is missing" and only the files that never arrived will go.
    /// </summary>
    public sealed class DriveQuotaException : Exception
    {
        /// <summary>Google's own machine-readable reason, for the log.</summary>
        public string Reason { get; }

        /// <summary>
        /// True when waiting will fix it — a daily limit resets, where a full
        /// account does not fix itself at all.
        /// </summary>
        public bool ResetsOnItsOwn { get; }

        public DriveQuotaException(string reason, string message, bool resetsOnItsOwn)
            : base(message)
        {
            Reason = reason;
            ResetsOnItsOwn = resetsOnItsOwn;
        }

        /// <summary>
        /// The one Google sent, or null when this answer is an ordinary failure.
        ///
        /// Kept beside the reason strings it tests for, so adding one is a change
        /// in a single place rather than three.
        /// </summary>
        public static DriveQuotaException? From(int status, string? body)
        {
            if (status != 403) return null;

            var reason = DriveClient.ErrorReason(body);
            return reason switch
            {
                "dailyLimitExceeded" => new DriveQuotaException(reason,
                    DriveClient.Explain(status, body ?? ""), resetsOnItsOwn: true),

                "storageQuotaExceeded" or "quotaExceeded" => new DriveQuotaException(reason,
                    DriveClient.Explain(status, body ?? ""), resetsOnItsOwn: false),

                _ => null,
            };
        }
    }
}
