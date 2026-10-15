using System.Globalization;
using System.Text;

namespace WinShot.App.Diagnostics;

/// <summary>
/// The deliberately small set of non-content fields that may cross the support review boundary.
/// Values remain raw here so the formatter can fail closed when a caller supplies unexpected data.
/// </summary>
internal sealed record DiagnosticSupportSummaryInput(
    string? AppVersion,
    string? OsVersion,
    string? OsBuild,
    string? ProcessArchitecture,
    string? OsArchitecture,
    DateTimeOffset Timestamp,
    string? CorrelationId);

/// <summary>
/// Formats a deterministic, bounded support summary without reading environment or application
/// state. Only strict version, build, architecture, and GUID shapes are copied to the result.
/// </summary>
internal static class DiagnosticSupportSummaryFormatter
{
    internal const int MaxSummaryLength = 512;

    private const int MaxVersionLength = 43;
    private const int MaxBuildLength = 10;
    private const int MaxArchitectureLength = 8;
    private const int MaxCorrelationIdLength = 38;
    private const string Unavailable = "[unavailable]";

    internal static string Format(DiagnosticSupportSummaryInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        var text = new StringBuilder(capacity: 256);
        text.Append(ProductIdentity.DisplayName).Append(" diagnostic summary v1\n")
            .Append("GeneratedUtc: ")
            .Append(input.Timestamp.UtcDateTime.ToString(
                "yyyy-MM-dd'T'HH:mm:ss'Z'",
                CultureInfo.InvariantCulture))
            .Append('\n')
            .Append("CorrelationId: ")
            .Append(FormatCorrelationId(input.CorrelationId))
            .Append('\n')
            .Append("AppVersion: ")
            .Append(FormatDottedVersion(input.AppVersion))
            .Append('\n')
            .Append("OsVersion: ")
            .Append(FormatDottedVersion(input.OsVersion))
            .Append('\n')
            .Append("OsBuild: ")
            .Append(FormatUnsignedInteger(input.OsBuild, MaxBuildLength))
            .Append('\n')
            .Append("ProcessArchitecture: ")
            .Append(FormatArchitecture(input.ProcessArchitecture))
            .Append('\n')
            .Append("OsArchitecture: ")
            .Append(FormatArchitecture(input.OsArchitecture))
            .Append('\n');

        string result = text.ToString();
        if (result.Length > MaxSummaryLength)
            throw new InvalidOperationException("Diagnostic support summary exceeded its fixed size budget.");

        return result;
    }

    private static string FormatDottedVersion(string? value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > MaxVersionLength)
            return Unavailable;

        string[] segments = value.Split('.');
        if (segments.Length is < 2 or > 4)
            return Unavailable;

        var normalized = new StringBuilder(value.Length);
        for (int index = 0; index < segments.Length; index++)
        {
            string segment = segments[index];
            if (!TryParseUnsignedInteger(segment, 10, out uint component))
                return Unavailable;

            if (index > 0)
                normalized.Append('.');

            normalized.Append(component.ToString(CultureInfo.InvariantCulture));
        }

        return normalized.ToString();
    }

    private static string FormatUnsignedInteger(string? value, int maximumLength) =>
        TryParseUnsignedInteger(value, maximumLength, out uint parsed)
            ? parsed.ToString(CultureInfo.InvariantCulture)
            : Unavailable;

    private static bool TryParseUnsignedInteger(
        string? value,
        int maximumLength,
        out uint parsed)
    {
        parsed = 0;
        return !string.IsNullOrEmpty(value)
            && value.Length <= maximumLength
            && uint.TryParse(
                value,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out parsed);
    }

    private static string FormatArchitecture(string? value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > MaxArchitectureLength)
            return Unavailable;

        if (value.Equals("x86", StringComparison.OrdinalIgnoreCase))
            return "x86";
        if (value.Equals("x64", StringComparison.OrdinalIgnoreCase))
            return "x64";
        if (value.Equals("arm", StringComparison.OrdinalIgnoreCase))
            return "arm";
        if (value.Equals("arm64", StringComparison.OrdinalIgnoreCase))
            return "arm64";

        return Unavailable;
    }

    private static string FormatCorrelationId(string? value)
    {
        if (string.IsNullOrEmpty(value)
            || value.Length > MaxCorrelationIdLength
            || !Guid.TryParse(value, out Guid correlationId)
            || correlationId == Guid.Empty)
        {
            return Unavailable;
        }

        return correlationId.ToString("D", CultureInfo.InvariantCulture);
    }
}
