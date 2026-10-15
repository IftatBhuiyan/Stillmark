using System.Text.Json;

namespace WinShot.EditorOutput.Perf;

internal static class Program
{
    private const int DefaultSampleCount = 9;

    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            if (args.Length == 1 &&
                string.Equals(args[0], "--self-test", StringComparison.Ordinal))
            {
                HarnessSelfTest.Run();
                Console.WriteLine("PASS_EDITOR_OUTPUT_HARNESS");
                return 0;
            }

            if (args.Length == 1 &&
                string.Equals(
                    args[0],
                    "--dispatcher-heartbeat",
                    StringComparison.Ordinal))
            {
                DispatcherHeartbeatReport heartbeat =
                    DispatcherHeartbeatBenchmark.Run4K();
                Console.WriteLine(JsonSerializer.Serialize(
                    heartbeat,
                    BenchmarkJsonContext.Default.DispatcherHeartbeatReport));
                return heartbeat.GuardrailsPassed ? 0 : 2;
            }

            int sampleCount = ParseSampleCount(args);
            BenchmarkReport report = EditorOutputBenchmark.Run(sampleCount);
            Console.WriteLine(JsonSerializer.Serialize(
                report,
                BenchmarkJsonContext.Default.BenchmarkReport));
            return report.OverallPassed ? 0 : 2;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(
                $"FAIL_EDITOR_OUTPUT_HARNESS:{ex.GetType().Name}");
            return 2;
        }
    }

    private static int ParseSampleCount(string[] args)
    {
        if (args.Length == 0)
            return DefaultSampleCount;
        if (args.Length != 2 ||
            !string.Equals(args[0], "--samples", StringComparison.Ordinal) ||
            !int.TryParse(
                args[1],
                System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture,
                out int sampleCount) ||
            sampleCount is < 3 or > 31)
        {
            throw new ArgumentException(
                "Use no arguments, --self-test, --dispatcher-heartbeat, " +
                "or --samples with a value from 3 through 31.");
        }

        return sampleCount;
    }
}
