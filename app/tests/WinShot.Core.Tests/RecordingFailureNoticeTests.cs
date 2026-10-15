using WinShot.App.Recording;
using Xunit;

namespace WinShot.Core.Tests;

public sealed class RecordingFailureNoticeTests
{
    [Theory]
    [InlineData("start")]
    [InlineData("recording")]
    [InlineData("completion")]
    [InlineData("gif")]
    public void RecoveryNotice_ExposesFileNameButNotSensitivePath(string kind)
    {
        const string sensitivePath =
            @"C:\Users\Alice\Confidential Client\private-recording.mp4";

        string notice = Create(kind, sensitivePath);

        Assert.Contains("private-recording.mp4", notice);
        Assert.DoesNotContain(@"C:\Users", notice, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Confidential Client", notice, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void FailureNotices_DoNotRequireRawNativeOrExceptionText()
    {
        string[] notices =
        [
            RecordingFailureNotice.StartFailure(null),
            RecordingFailureNotice.RecordingFailure(null),
            RecordingFailureNotice.CompletionFailure(null),
            RecordingFailureNotice.GifFailure(null),
        ];

        Assert.All(notices, notice => Assert.False(string.IsNullOrWhiteSpace(notice)));
        Assert.All(notices, notice => Assert.DoesNotContain(@":\", notice));
    }

    private static string Create(string kind, string preservedPath)
    {
        return kind switch
        {
            "start" => RecordingFailureNotice.StartFailure(preservedPath),
            "recording" => RecordingFailureNotice.RecordingFailure(preservedPath),
            "completion" => RecordingFailureNotice.CompletionFailure(preservedPath),
            "gif" => RecordingFailureNotice.GifFailure(preservedPath),
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
    }
}
