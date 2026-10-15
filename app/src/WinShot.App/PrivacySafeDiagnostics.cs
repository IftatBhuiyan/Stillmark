using System.Globalization;
using System.Text;
using WinShot.App.Diagnostics;

namespace WinShot.App;

/// <summary>
/// Formats local diagnostics without persisting exception messages, user file paths, or content.
/// Raw exceptions remain available to a debugger; the resident log is intentionally narrower.
/// </summary>
internal static class PrivacySafeDiagnostics
{
    private const int MaxExceptionDepth = 4;
    private const int MaxFramesPerException = 24;

    internal static string Format(Exception exception, DateTimeOffset timestamp)
    {
        ArgumentNullException.ThrowIfNull(exception);

        var text = new StringBuilder();
        text.Append('[')
            .Append(timestamp.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss 'UTC'", CultureInfo.InvariantCulture))
            .AppendLine("]");

        Exception? current = exception;
        for (int depth = 0; current is not null && depth < MaxExceptionDepth; depth++)
        {
            text.Append(depth == 0 ? "Exception: " : "Inner: ")
                .Append(current.GetType().FullName ?? current.GetType().Name)
                .Append(" (HRESULT 0x")
                .Append(current.HResult.ToString("X8", CultureInfo.InvariantCulture))
                .AppendLine(")");

            AppendMethodFrames(text, current.StackTrace);
            current = current.InnerException;
        }

        if (current is not null)
            text.AppendLine("Inner: [additional exceptions omitted]");

        text.AppendLine();
        return text.ToString();
    }

    /// <summary>
    /// Produces a fixed-field summary intended for explicit user review and copy.
    /// Invalid raw values are never echoed; the formatter substitutes a stable placeholder.
    /// </summary>
    internal static string FormatSupportSummary(DiagnosticSupportSummaryInput input) =>
        DiagnosticSupportSummaryFormatter.Format(input);

    private static void AppendMethodFrames(StringBuilder text, string? stackTrace)
    {
        if (string.IsNullOrWhiteSpace(stackTrace))
            return;

        int written = 0;
        foreach (string rawLine in stackTrace.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (written == MaxFramesPerException)
            {
                text.AppendLine("  [additional frames omitted]");
                break;
            }

            string line = rawLine.Trim();
            int sourceIndex = line.IndexOf(" in ", StringComparison.Ordinal);
            if (sourceIndex >= 0)
                line = line[..sourceIndex];

            if (line.Length == 0)
                continue;

            text.Append("  ").AppendLine(line);
            written++;
        }
    }
}
