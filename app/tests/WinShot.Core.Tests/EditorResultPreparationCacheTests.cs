using WinShot.App.Editor;
using Xunit;

namespace WinShot.Core.Tests;

public sealed class EditorResultPreparationCacheTests
{
    [Fact]
    public void TryCommit_RefusesRevisionDrift()
    {
        var cache = new EditorResultPreparationCache();

        bool committed = cache.TryCommit(
            expectedRevision: 8,
            currentRevision: 9,
            @"C:\synthetic\result.png",
            out PreparedEditorResult? result);

        Assert.False(committed);
        Assert.Null(result);
        Assert.False(cache.TryGet(8, out _));
        Assert.False(cache.TryGet(9, out _));
    }

    [Fact]
    public void TryGet_RequiresTheExactCommittedRevision()
    {
        var cache = new EditorResultPreparationCache();

        Assert.True(cache.TryCommit(
            expectedRevision: 12,
            currentRevision: 12,
            @"C:\synthetic\result.png",
            out PreparedEditorResult? committed));

        Assert.NotNull(committed);
        Assert.False(cache.TryGet(11, out _));
        Assert.True(cache.TryGet(12, out PreparedEditorResult? current));
        Assert.Equal(@"C:\synthetic\result.png", current!.FilePath);
    }

    [Fact]
    public void Invalidate_ClearsTheCommittedResult()
    {
        var cache = new EditorResultPreparationCache();
        Assert.True(cache.TryCommit(
            expectedRevision: 3,
            currentRevision: 3,
            @"C:\synthetic\result.png",
            out _));

        cache.Invalidate();

        Assert.False(cache.TryGet(3, out _));
    }
}
