namespace ExplorerNative
{
    /// <summary>
    /// The OAuth client this application ships with, so that connecting Google
    /// Drive is one button rather than a trip to the Cloud console.
    ///
    /// **A desktop client's secret is not a secret, and Google says so.** For an
    /// installed application it ships inside the executable where anybody can
    /// read it; what actually secures the flow is PKCE and the redirect being
    /// loopback, neither of which depends on hiding this string.
    /// <see cref="GoogleAuth"/> already carries that argument at length. The
    /// thing worth protecting is the *refresh token*, and that one is encrypted
    /// to the Windows account.
    ///
    /// What embedding it really costs is the project's OAuth user cap: everybody
    /// who signs in with this client counts against one hundred, for the life of
    /// the project, and that figure cannot be reset. For an application handed
    /// to a few people that is a hundred against three. For one published to
    /// strangers it would be the wrong trade, and the answer then is for each
    /// person to bring their own client — which is what
    /// <see cref="GoogleAuth.ReadClientJson"/> is still there for, and why a
    /// file on disk still wins over this.
    ///
    /// A file in <see cref="GoogleDrive.CredentialsDirectory"/> takes
    /// precedence. That ordering matters: it is what lets one person point the
    /// application at their own Cloud project without rebuilding it, and it
    /// means the developer's own setup is unchanged by this file existing.
    ///
    /// The two values live in BuiltInCredentials.Values.cs, which is not in the
    /// repository: CI writes it from repository secrets, and a build without it
    /// compiles build\BuiltInCredentials.Placeholder.cs instead.
    /// </summary>
    internal static partial class BuiltInCredentials
    {
        /// <summary>
        /// Whether anything was actually compiled in. False for a build whose
        /// placeholders were never filled, which is the state this file is in
        /// when it is first written — so the application says "no credentials"
        /// rather than sending Google the literal word.
        /// </summary>
        public static bool Present =>
            ClientId.Length > 0 && !ClientId.StartsWith("__", System.StringComparison.Ordinal) &&
            ClientSecret.Length > 0 && !ClientSecret.StartsWith("__", System.StringComparison.Ordinal);
    }
}
