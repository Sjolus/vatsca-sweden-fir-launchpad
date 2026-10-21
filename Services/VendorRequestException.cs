using System.Globalization;
using System.Net;
using System.Net.Http;

namespace VatscaUpdateChecker.Services;

public enum VendorService { GitHub, AeroNav, Vatis }
public enum VendorFailureKind { RateLimited, Forbidden, Unavailable, Timeout }

/// <summary>Fixed network diagnostics, without URLs, response bodies or account information.</summary>
public class VendorRequestException : HttpRequestException
{
    public VendorService Vendor { get; }
    public VendorFailureKind FailureKind { get; }
    public DateTimeOffset? RetryAtUtc { get; }

    public VendorRequestException(VendorService vendor, VendorFailureKind failureKind,
        HttpStatusCode? statusCode = null, DateTimeOffset? retryAtUtc = null, string? message = null)
        : base(message ?? SafeMessage(vendor, failureKind, retryAtUtc), null, statusCode)
    { Vendor = vendor; FailureKind = failureKind; RetryAtUtc = retryAtUtc; }

    internal static VendorRequestException FromResponse(HttpResponseMessage response, VendorService vendor,
        DateTimeOffset now)
    {
        bool limited = response.StatusCode == HttpStatusCode.TooManyRequests ||
            response.StatusCode == HttpStatusCode.Forbidden &&
            (response.Headers.RetryAfter is not null ||
             vendor == VendorService.GitHub &&
             (response.Headers.TryGetValues("X-RateLimit-Remaining", out var remaining) && remaining.Any(value => value.Trim() == "0") ||
              response.ReasonPhrase?.Contains("rate limit", StringComparison.OrdinalIgnoreCase) == true));
        if (!limited)
            return new(vendor, response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized
                ? VendorFailureKind.Forbidden : VendorFailureKind.Unavailable, response.StatusCode);

        DateTimeOffset retryAt = now.AddMinutes(5);
        if (response.Headers.RetryAfter?.Date is { } date) retryAt = date;
        else if (response.Headers.RetryAfter?.Delta is { } delay)
            retryAt = now + (delay > TimeSpan.FromDays(1) ? TimeSpan.FromDays(1) : delay < TimeSpan.Zero ? TimeSpan.Zero : delay);
        if (vendor == VendorService.GitHub && response.Headers.TryGetValues("X-RateLimit-Reset", out var resets) &&
            long.TryParse(resets.FirstOrDefault(), NumberStyles.None, CultureInfo.InvariantCulture, out long seconds) &&
            seconds is >= 0 and <= 253402300799)
        {
            var reset = DateTimeOffset.FromUnixTimeSeconds(seconds);
            if (reset > retryAt || response.Headers.RetryAfter is null) retryAt = reset;
        }
        retryAt = retryAt < now.AddSeconds(1) ? now.AddSeconds(1) : retryAt > now.AddDays(1) ? now.AddDays(1) : retryAt;
        return new(vendor, VendorFailureKind.RateLimited, response.StatusCode, retryAt);
    }

    private static string SafeMessage(VendorService vendor, VendorFailureKind kind, DateTimeOffset? retryAt)
    {
        string name = vendor switch { VendorService.GitHub => "GitHub", VendorService.AeroNav => "AeroNav", _ => "vATIS" };
        return kind switch
        {
            VendorFailureKind.RateLimited => retryAt is { } retry
                ? $"{name} is temporarily limiting requests. Try again after {retry.ToLocalTime():HH:mm}."
                : $"{name} is temporarily limiting requests. Try again later.",
            VendorFailureKind.Forbidden => $"{name} refused the request. Try again later or open its website.",
            VendorFailureKind.Timeout => $"{name} did not respond in time. Try again later.",
            _ => $"{name} could not be reached. Try again later."
        };
    }
}
