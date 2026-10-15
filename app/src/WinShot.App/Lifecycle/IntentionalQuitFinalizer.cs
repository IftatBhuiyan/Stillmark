namespace WinShot.App.Lifecycle;

/// <summary>
/// Guarantees that an explicit user Quit reaches its terminal cleanup even if active media work
/// faults, is canceled, or exceeds its bounded drain deadline.
/// </summary>
internal static class IntentionalQuitFinalizer
{
    internal static async Task RunAsync(
        Func<Task<bool>> drain,
        Action<Exception> logFailure,
        Action finalize)
    {
        ArgumentNullException.ThrowIfNull(drain);
        ArgumentNullException.ThrowIfNull(logFailure);
        ArgumentNullException.ThrowIfNull(finalize);

        try
        {
            if (!await drain())
            {
                TryLog(
                    logFailure,
                    new TimeoutException(
                        "Active work did not finish within the intentional-Quit deadline."));
            }
        }
        catch (Exception ex)
        {
            TryLog(logFailure, ex);
        }
        finally
        {
            finalize();
        }
    }

    private static void TryLog(
        Action<Exception> logFailure,
        Exception failure)
    {
        try
        {
            logFailure(failure);
        }
        catch
        {
            // Diagnostic failure must not prevent a user-requested terminal cleanup.
        }
    }
}
