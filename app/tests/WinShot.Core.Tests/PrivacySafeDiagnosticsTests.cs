using WinShot.App;
using Xunit;

namespace WinShot.Core.Tests;

public sealed class PrivacySafeDiagnosticsTests
{
    [Fact]
    public void Format_OmitsMessageAndSourcePathButKeepsDiagnosticIdentity()
    {
        const string secretMessage =
            @"Failed to save C:\Users\Alice\Pictures\customer-recovery-code.png via \\fileserver\private";
        Exception exception;
        try
        {
            ThrowFixture(secretMessage);
            throw new InvalidOperationException("unreachable");
        }
        catch (Exception ex)
        {
            exception = ex;
        }

        string result = PrivacySafeDiagnostics.Format(
            exception,
            new DateTimeOffset(2026, 7, 28, 12, 34, 56, TimeSpan.Zero));

        Assert.Contains("[2026-07-28 12:34:56 UTC]", result);
        Assert.Contains("System.InvalidOperationException", result);
        Assert.Contains("HRESULT 0x80131509", result);
        Assert.Contains(nameof(ThrowFixture), result);
        Assert.DoesNotContain(secretMessage, result);
        Assert.DoesNotContain(@"C:\Users\Alice", result);
        Assert.DoesNotContain(@"\\fileserver", result);
        Assert.DoesNotContain("customer-recovery-code.png", result);
    }

    [Fact]
    public void Format_BoundsInnerExceptionDepth()
    {
        Exception exception = new InvalidOperationException("depth-5");
        for (int i = 0; i < 5; i++)
            exception = new ApplicationException($"depth-{i}", exception);

        string result = PrivacySafeDiagnostics.Format(exception, DateTimeOffset.UnixEpoch);

        Assert.Equal(4, CountOccurrences(result, "HRESULT 0x"));
        Assert.Contains("[additional exceptions omitted]", result);
        Assert.DoesNotContain("depth-", result);
    }

    private static void ThrowFixture(string message) => throw new InvalidOperationException(message);

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
