using WinShot.App;
using WinShot.App.Diagnostics;
using Xunit;

namespace WinShot.Core.Tests;

public sealed class DiagnosticSupportSummaryTests
{
    [Fact]
    public void FormatSupportSummary_UsesDeterministicCanonicalFormat()
    {
        var input = new DiagnosticSupportSummaryInput(
            AppVersion: "01.002.0003",
            OsVersion: "10.0.26100.000",
            OsBuild: "026100",
            ProcessArchitecture: "X64",
            OsArchitecture: "Arm64",
            Timestamp: new DateTimeOffset(2026, 7, 28, 18, 15, 42, TimeSpan.FromHours(2)),
            CorrelationId: "{A1B2C3D4-E5F6-4789-ABCD-0123456789AB}");

        string result = PrivacySafeDiagnostics.FormatSupportSummary(input);

        Assert.Equal(
            """
            Stillmark diagnostic summary v1
            GeneratedUtc: 2026-07-28T16:15:42Z
            CorrelationId: a1b2c3d4-e5f6-4789-abcd-0123456789ab
            AppVersion: 1.2.3
            OsVersion: 10.0.26100.0
            OsBuild: 26100
            ProcessArchitecture: x64
            OsArchitecture: arm64

            """.ReplaceLineEndings("\n"),
            result);
    }

    [Fact]
    public void FormatSupportSummary_DoesNotEchoHostilePathOrKeyShapedValues()
    {
        string pathPayload = "C:" + @"\Users\Alice\Pictures\customer-private.png";
        string keyPayload = "api" + "_key=" + new string('K', 48);
        var input = new DiagnosticSupportSummaryInput(
            AppVersion: pathPayload,
            OsVersion: keyPayload,
            OsBuild: pathPayload,
            ProcessArchitecture: keyPayload,
            OsArchitecture: pathPayload,
            Timestamp: DateTimeOffset.UnixEpoch,
            CorrelationId: keyPayload);

        string result = PrivacySafeDiagnostics.FormatSupportSummary(input);

        Assert.Equal(6, CountOccurrences(result, "[unavailable]"));
        Assert.DoesNotContain("Alice", result, StringComparison.Ordinal);
        Assert.DoesNotContain("customer-private", result, StringComparison.Ordinal);
        Assert.DoesNotContain("api_key", result, StringComparison.Ordinal);
        Assert.DoesNotContain(new string('K', 16), result, StringComparison.Ordinal);
        Assert.DoesNotContain(@"\", result, StringComparison.Ordinal);
    }

    [Fact]
    public void FormatSupportSummary_BoundsEveryRawFieldAndTotalOutput()
    {
        string oversized = new('9', 1_000_000);
        var input = new DiagnosticSupportSummaryInput(
            AppVersion: oversized,
            OsVersion: oversized,
            OsBuild: oversized,
            ProcessArchitecture: oversized,
            OsArchitecture: oversized,
            Timestamp: DateTimeOffset.MaxValue,
            CorrelationId: oversized);

        string result = PrivacySafeDiagnostics.FormatSupportSummary(input);

        Assert.Equal(6, CountOccurrences(result, "[unavailable]"));
        Assert.True(result.Length <= DiagnosticSupportSummaryFormatter.MaxSummaryLength);
        Assert.DoesNotContain(new string('9', 32), result, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("0")]
    [InlineData("1.2.3-preview")]
    [InlineData("1.2.3.4.5")]
    [InlineData("1.2.-3")]
    [InlineData("1.2.4294967296")]
    public void FormatSupportSummary_RejectsNonNumericOrOutOfRangeVersions(string? version)
    {
        var input = CreateValidInput() with { AppVersion = version };

        string result = PrivacySafeDiagnostics.FormatSupportSummary(input);

        Assert.Contains("AppVersion: [unavailable]\n", result, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("amd64")]
    [InlineData("x64\nMachineName")]
    public void FormatSupportSummary_RejectsUnknownArchitectureWithoutEchoingIt(string? architecture)
    {
        var input = CreateValidInput() with { ProcessArchitecture = architecture };

        string result = PrivacySafeDiagnostics.FormatSupportSummary(input);

        Assert.Contains("ProcessArchitecture: [unavailable]\n", result, StringComparison.Ordinal);
        if (!string.IsNullOrEmpty(architecture))
            Assert.DoesNotContain(architecture, result, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    [InlineData("not-a-correlation-id")]
    public void FormatSupportSummary_RejectsMissingOrInvalidCorrelationId(string? correlationId)
    {
        var input = CreateValidInput() with { CorrelationId = correlationId };

        string result = PrivacySafeDiagnostics.FormatSupportSummary(input);

        Assert.Contains("CorrelationId: [unavailable]\n", result, StringComparison.Ordinal);
        if (!string.IsNullOrEmpty(correlationId))
            Assert.DoesNotContain(correlationId, result, StringComparison.Ordinal);
    }

    private static DiagnosticSupportSummaryInput CreateValidInput() =>
        new(
            AppVersion: "1.0.0",
            OsVersion: "10.0.26100.0",
            OsBuild: "26100",
            ProcessArchitecture: "x64",
            OsArchitecture: "x64",
            Timestamp: DateTimeOffset.UnixEpoch,
            CorrelationId: "3cb89252-b007-42cc-b7c1-7f52d9a160be");

    private static int CountOccurrences(string value, string fragment)
    {
        int count = 0;
        int index = 0;
        while ((index = value.IndexOf(fragment, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += fragment.Length;
        }

        return count;
    }
}
