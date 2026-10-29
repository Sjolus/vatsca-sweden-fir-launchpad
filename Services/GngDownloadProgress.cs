using System.Globalization;

namespace VatscaUpdateChecker.Services;

internal sealed record GngDownloadProgress(double? Percent, string CompactText, string DetailText,
    double? BytesPerSecond, TimeSpan? Remaining);

/// <summary>Per-file transfer estimates; elapsed time comes from the caller's monotonic clock.</summary>
internal sealed class GngDownloadProgressMeter
{
    private static readonly TimeSpan Window = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan SampleInterval = TimeSpan.FromMilliseconds(250);
    private readonly Queue<(TimeSpan Time, long Bytes)> _samples = new();
    private bool _initialized;
    private long _lastBytes;
    private TimeSpan _lastTime;
    private TimeSpan _lastProgress;

    public void Reset()
    {
        _samples.Clear();
        _initialized = false;
        _lastBytes = 0;
        _lastTime = _lastProgress = TimeSpan.Zero;
    }

    public GngDownloadProgress Sample(long received, long? total, TimeSpan elapsed)
    {
        received = Math.Max(0, received);
        elapsed = elapsed < TimeSpan.Zero ? TimeSpan.Zero : elapsed;
        if (total <= 0) total = null;
        if (!_initialized || received < _lastBytes || elapsed < _lastTime)
        {
            Reset();
            _initialized = true;
            _lastProgress = elapsed;
            _samples.Enqueue((elapsed, received));
        }
        if (received > _lastBytes) _lastProgress = elapsed;
        _lastBytes = received;
        _lastTime = elapsed;

        if (elapsed - _samples.Last().Time >= SampleInterval)
            _samples.Enqueue((elapsed, received));
        // Keep one observation at/before the window so the first interval is measurable.
        // The time spacing also bounds the queue during very frequent byte notifications.
        while (_samples.Count > 1 && (_samples.ElementAt(1).Time <= elapsed - Window || _samples.Count > 24))
            _samples.Dequeue();

        bool stalled = elapsed - _lastProgress >= Window;
        var first = _samples.Peek();
        var seconds = (elapsed - first.Time).TotalSeconds;
        double? speed = stalled ? 0 : seconds >= 1 ? (received - first.Bytes) / seconds : null;
        double? percent = total is { } length ? Math.Clamp(received * 100d / length, 0, 100) : null;
        TimeSpan? remaining = null;
        if (total is { } expected && expected > received && speed is > 0)
        {
            var secondsLeft = (expected - received) / speed.Value;
            if (double.IsFinite(secondsLeft) && secondsLeft <= TimeSpan.FromDays(1).TotalSeconds)
                remaining = TimeSpan.FromSeconds(secondsLeft);
        }

        var (unit, divisor) = Unit(total ?? received);
        string amount = Number(received / divisor);
        string size = total is { } known
            ? $"{amount}/{Number(known / divisor)} {unit}"
            : $"{amount} {unit}";
        string rate = speed is { } measured ? FormatRate(measured) : "Measuring speed…";
        string detailSize = total is { } knownTotal
            ? $"{Number(percent!.Value)}% · {amount} of {Number(knownTotal / divisor)} {unit}"
            : $"{amount} {unit} downloaded · total size unknown";
        string detail = $"{detailSize} · {rate}";
        if (stalled) detail += " · Waiting for data…";
        else if (remaining is { } eta)
            detail += $" · about {FormatRemaining(eta)} left for this file";
        return new(percent, $"{rate} · {size}", detail, speed, remaining);
    }

    private static (string Unit, double Divisor) Unit(double bytes) => bytes >= 1_000_000
        ? ("MB", 1_000_000d) : bytes >= 1_000 ? ("kB", 1_000d) : ("B", 1d);

    private static string FormatRate(double bytesPerSecond)
    {
        var (unit, divisor) = Unit(bytesPerSecond);
        return $"{Number(bytesPerSecond / divisor)} {unit}/s";
    }

    private static string FormatRemaining(TimeSpan remaining) => remaining.TotalSeconds < 60
        ? $"{Math.Max(1, Math.Ceiling(remaining.TotalSeconds)):0} s"
        : remaining.TotalMinutes < 60 ? $"{Math.Ceiling(remaining.TotalMinutes):0} min"
        : $"{Math.Ceiling(remaining.TotalHours):0} h";

    private static string Number(double value) => value.ToString("0.#", CultureInfo.InvariantCulture);
}
