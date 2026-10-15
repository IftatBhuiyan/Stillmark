namespace WinShot.App.Lifecycle;

internal readonly record struct RecoveryLauncherMetadataEvidence(
    string? ProductName,
    string? CompanyName,
    int FileMajorPart,
    int FileMinorPart,
    int FileBuildPart,
    int FilePrivatePart);

internal static class RecoveryLauncherMetadataPolicy
{
    internal static bool IsAllowed(
        RecoveryLauncherMetadataEvidence evidence,
        string expectedProductName,
        string expectedCompanyName,
        Version expectedProductVersion,
        int minimumSecurityEpoch)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedProductName);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedCompanyName);
        ArgumentNullException.ThrowIfNull(expectedProductVersion);
        if (minimumSecurityEpoch <= 0)
            throw new ArgumentOutOfRangeException(nameof(minimumSecurityEpoch));

        return string.Equals(
                evidence.ProductName,
                expectedProductName,
                StringComparison.Ordinal)
            && string.Equals(
                evidence.CompanyName,
                expectedCompanyName,
                StringComparison.Ordinal)
            && evidence.FileMajorPart == expectedProductVersion.Major
            && evidence.FileMinorPart == expectedProductVersion.Minor
            && evidence.FileBuildPart == Math.Max(0, expectedProductVersion.Build)
            && evidence.FilePrivatePart >= minimumSecurityEpoch;
    }
}
