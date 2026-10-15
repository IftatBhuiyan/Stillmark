using WinShot.App;
using Xunit;

namespace WinShot.Core.Tests;

public sealed class OcrProgressOwnershipTests
{
    [Fact]
    public void NewGenerationSupersedesOldAndRejectsStaleCompletion()
    {
        var ownership = new OcrProgressOwnership();

        Assert.True(ownership.TryStart(10));
        Assert.True(ownership.TryStart(11));
        Assert.False(ownership.TryComplete(10));
        Assert.True(ownership.TryComplete(11));
        Assert.False(ownership.TryComplete(11));
    }

    [Fact]
    public void CurrentGenerationCannotOpenDuplicateProgress()
    {
        var ownership = new OcrProgressOwnership();

        Assert.True(ownership.TryStart(7));
        Assert.False(ownership.TryStart(7));
        Assert.True(ownership.TryComplete(7));
    }

    [Fact]
    public void CancellationClearsOnlyTheCurrentOwner()
    {
        var ownership = new OcrProgressOwnership();

        Assert.False(ownership.CancelCurrent());
        Assert.True(ownership.TryStart(3));
        Assert.True(ownership.CancelCurrent());
        Assert.False(ownership.TryComplete(3));
        Assert.False(ownership.CancelCurrent());
    }
}
