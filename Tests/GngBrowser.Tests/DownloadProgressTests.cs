using VatscaUpdateChecker.Services;

internal static class DownloadProgressTests
{
    public static int Run()
    {
        int count = 0;
        void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
            count++;
        }
        var meter = new GngDownloadProgressMeter();
        GngDownloadProgress Sample(long bytes, long? total, double seconds) => meter.Sample(bytes, total, TimeSpan.FromSeconds(seconds));

        var first = Sample(0, 60_000_000, 0);
        Check(first.Percent == 0 && first.BytesPerSecond is null && first.Remaining is null, "Startup does not invent throughput or an ETA.");
        Check(first.CompactText == "Measuring speed… · 0/60 MB", "Startup shows the actual expected transfer size.");
        var early = Sample(500_000, 60_000_000, .2);
        Check(early.BytesPerSecond is null, "A burst shorter than one second does not produce a misleading speed.");
        var moving = Sample(2_000_000, 60_000_000, 1);
        Check(moving.BytesPerSecond == 2_000_000 && moving.Remaining == TimeSpan.FromSeconds(29), "Speed and per-file ETA use received bytes and elapsed time.");
        Check(moving.CompactText == "2 MB/s · 2/60 MB" && moving.DetailText.Contains("29 s left for this file"), "Compact progress prioritizes speed while details retain units and per-file scope.");
        Check(Math.Abs(moving.Percent!.Value - 100d / 30) < .001, "Progress uses the current file's known length.");

        var unknown = Sample(4_000_000, null, 2);
        Check(unknown.Percent is null && unknown.Remaining is null && unknown.BytesPerSecond == 2_000_000, "Unknown length retains throughput but no percentage or ETA.");
        Check(unknown.DetailText.Contains("total size unknown"), "An unavailable total is explained explicitly.");
        Check(Sample(4_000_000, 0, 2.1).Percent is null && Sample(4_000_000, -1, 2.2).Percent is null, "Zero and negative totals remain indeterminate.");

        var slowing = Sample(4_000_000, 60_000_000, 4);
        Check(slowing.BytesPerSecond == 1_000_000, "A quiet transfer lowers the recent speed before declaring a stall.");
        var stalled = Sample(4_000_000, 60_000_000, 7);
        Check(stalled.BytesPerSecond == 0 && stalled.Remaining is null && stalled.DetailText.Contains("Waiting for data"), "Five seconds without bytes clears stale speed and ETA.");
        Check(stalled.CompactText.StartsWith("0 B/s · "), "A stalled transfer never continues displaying an old fast rate.");
        var resumed = Sample(5_000_000, 60_000_000, 8);
        Check(resumed.BytesPerSecond is > 0 && !resumed.DetailText.Contains("Waiting for data"), "New bytes immediately leave the stalled state.");
        Sample(6_000_000, 60_000_000, 9);
        Sample(7_000_000, 60_000_000, 10);
        Sample(8_000_000, 60_000_000, 11);
        Sample(9_000_000, 60_000_000, 12);
        Check(Sample(10_000_000, 60_000_000, 13).BytesPerSecond == 1_000_000, "The rolling window recovers to current speed rather than keeping the whole-transfer average.");

        meter.Reset();
        var secondFile = Sample(0, 100_000_000, 0);
        Check(secondFile.BytesPerSecond is null && secondFile.Percent == 0, "An explicit per-file reset cannot inherit the previous file's statistics.");
        Sample(1_000_000, 100_000_000, 1);
        var counterReset = Sample(0, 100_000_000, 2);
        Check(counterReset.BytesPerSecond is null && counterReset.Remaining is null, "A reset byte counter discards earlier rate history.");
        Check(Sample(200, 100_000_000, 1).BytesPerSecond is null, "A clock reset discards earlier timestamps.");

        meter.Reset();
        Sample(0, 100, 0);
        var fast = Sample(100, 100, .01);
        Check(fast.Percent == 100 && fast.BytesPerSecond is null && fast.Remaining is null, "A tiny completed transfer has no fabricated peak speed or time remaining.");
        Check(fast.CompactText.EndsWith("100/100 B"), "Small files retain readable byte units.");
        var overrun = Sample(120, 100, 1);
        Check(overrun.Percent == 100 && overrun.Remaining is null && overrun.CompactText.EndsWith("120/100 B"), "Stale totals clamp progress without hiding the actual byte count.");

        meter.Reset();
        Sample(0, long.MaxValue, 0);
        var huge = Sample(long.MaxValue, long.MaxValue, 1);
        Check(huge.Percent == 100 && double.IsFinite(huge.BytesPerSecond!.Value), "Large counters do not overflow speed or percentage.");
        var invalid = meter.Sample(-1, -1, TimeSpan.FromSeconds(-2));
        Check(invalid.Percent is null && invalid.BytesPerSecond is null && !invalid.CompactText.Contains('-'), "Invalid negative inputs cannot leak negative values into the display.");

        meter.Reset();
        Sample(0, 10_000_000, 0);
        for (int i = 1; i <= 10_000; i++) Sample(i * 100, 10_000_000, i / 1000d);
        var frequent = Sample(1_100_000, 10_000_000, 11);
        Check(Math.Abs(frequent.BytesPerSecond!.Value - 100_000) < .001, "Frequent byte events retain an accurate recent rate with bounded sample spacing.");
        return count;
    }
}
