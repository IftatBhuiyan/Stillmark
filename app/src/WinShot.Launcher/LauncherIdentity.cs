namespace WinShot.Launcher
{
    /// <summary>
    /// Build-time identity boundary for the stable launcher. Production packaging must replace both
    /// UNSET trust pins from the approved commercial contract before signing; an authoring build
    /// with either sentinel is deliberately unable to launch a payload through the real verifier.
    /// </summary>
    internal static class LauncherIdentity
    {
        internal const string PackageId = "PROVISIONAL.WinShot";
        internal const string ExecutableName = "WinShot.exe";
        internal const string ProductName = "WinShot";
        internal const string CompanyName = "Iftat Bhuiyan";
        internal const string ExpectedPublisherSubject = "UNSET";
        internal const string ExpectedArtifactSigningIdentityEku = "UNSET";
        internal const string ExpectedReleaseChannel = "UNSET";
        internal const string ExpectedRuntimeIdentifier = "win-x64";
        internal const int ExpectedPaidMajor = 1;
        internal const string ReleaseAttestationPublicKeyDerBase64 = "UNSET";
        internal const string ReleaseAttestationPublicKeySha256 = "UNSET";
    }
}
