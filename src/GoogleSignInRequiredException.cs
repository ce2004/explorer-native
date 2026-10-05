using System;

namespace ExplorerNative
{
    /// <summary>
    /// The stored sign-in is no longer good and nobody has been asked to renew
    /// it.
    ///
    /// Expected roughly weekly rather than exceptional: Google expires refresh
    /// tokens after seven days for an app that has not been through verification,
    /// and verification for a Drive scope means a third-party security
    /// assessment. So this is an ordinary Tuesday, and the only correct response
    /// is to tell the person and let them decide when to sign in again.
    /// </summary>
    public sealed class GoogleSignInRequiredException : Exception
    {
        public GoogleSignInRequiredException()
            : base("Google Drive needs you to sign in again.") { }
    }
}
