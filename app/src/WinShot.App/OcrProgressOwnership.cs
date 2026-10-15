namespace WinShot.App;

/// <summary>
/// Tracks which capture generation owns the OCR progress surface. A stale operation may finish
/// after a newer one starts, but it must never close the newer operation's UI.
/// </summary>
internal sealed class OcrProgressOwnership
{
    private readonly object _gate = new();
    private long? _generation;

    internal bool TryStart(long generation)
    {
        lock (_gate)
        {
            if (_generation == generation)
                return false;

            _generation = generation;
            return true;
        }
    }

    internal bool TryComplete(long generation)
    {
        lock (_gate)
        {
            if (_generation != generation)
                return false;

            _generation = null;
            return true;
        }
    }

    internal bool CancelCurrent()
    {
        lock (_gate)
        {
            if (_generation is null)
                return false;

            _generation = null;
            return true;
        }
    }
}
