namespace WinShot.App.Editor;

internal sealed record PreparedEditorResult(
    long Revision,
    string FilePath);

/// <summary>
/// Keeps the editor's prepared cross-application result bound to the exact document revision
/// that produced it. File existence remains a caller concern because another process may remove
/// retained clipboard backing after a successful commit.
/// </summary>
internal sealed class EditorResultPreparationCache
{
    private PreparedEditorResult? _current;

    internal void Invalidate() => _current = null;

    internal bool TryGet(long revision, out PreparedEditorResult? result)
    {
        if (_current is not null && _current.Revision == revision)
        {
            result = _current;
            return true;
        }

        result = null;
        return false;
    }

    internal bool TryCommit(
        long expectedRevision,
        long currentRevision,
        string filePath,
        out PreparedEditorResult? result)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        if (expectedRevision != currentRevision)
        {
            result = null;
            return false;
        }

        result = new PreparedEditorResult(expectedRevision, filePath);
        _current = result;
        return true;
    }
}
