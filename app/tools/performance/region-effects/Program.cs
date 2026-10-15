using System.Text.Json;

namespace WinShot.RegionEffects.Perf;

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
                RegionEffectBenchmark.RunSelfTest();
                DispatcherHeartbeatBenchmark.RunSelfTest();
                Console.WriteLine("PASS_REGION_EFFECT_HARNESS");
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
                return 0;
            }

            int sampleCount = ParseSampleCount(args);
            BenchmarkReport report = RegionEffectBenchmark.Run(sampleCount);
            Console.WriteLine(JsonSerializer.Serialize(
                report,
                BenchmarkJsonContext.Default.BenchmarkReport));
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(
                $"FAIL_REGION_EFFECT_HARNESS:{ex.GetType().Name}");
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
