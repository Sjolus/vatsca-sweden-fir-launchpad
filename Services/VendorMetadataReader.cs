using System.IO;
using System.Net.Http;
using System.Text;

namespace VatscaUpdateChecker.Services;

/// <summary>Small public version pages. Package review/downloads use their own current metadata checks.</summary>
internal sealed class VendorMetadataReader(HttpClient client, Func<DateTimeOffset>? utcNow = null,
    Func<Uri, string, bool>? validate = null)
{
    private readonly Func<DateTimeOffset> _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
    private readonly object _gate = new();
    private readonly Dictionary<string, (string Body, DateTimeOffset Until)> _cached = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Task<string>> _pending = new(StringComparer.Ordinal);
    private readonly Dictionary<VendorService, VendorRequestException> _cooldowns = new();

    internal async Task<string> GetAsync(Uri uri, VendorService vendor, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        string key = uri.AbsoluteUri;
        Task<string> pending;
        TaskCompletionSource<string>? start = null;
        lock (_gate)
        {
            if (_cached.TryGetValue(key, out var entry) && _utcNow() < entry.Until) return entry.Body;
            if (_cooldowns.TryGetValue(vendor, out var limited) && _utcNow() < limited.RetryAtUtc) throw limited;
            if (!_pending.TryGetValue(key, out pending!))
            {
                start = new(TaskCreationOptions.RunContinuationsAsynchronously);
                pending = start.Task;
                _pending[key] = pending;
            }
        }
        if (start is not null) _ = FetchAsync(uri, vendor, start);
        return await pending.WaitAsync(token).ConfigureAwait(false);
    }

    private async Task FetchAsync(Uri uri, VendorService vendor, TaskCompletionSource<string> completion)
    {
        string key = uri.AbsoluteUri;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.UserAgent.ParseAdd("SwedenFirLaunchpad/2.5");
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) throw VendorRequestException.FromResponse(response, vendor, _utcNow());
            if (response.RequestMessage?.RequestUri is { } final && final != uri)
                throw new InvalidDataException("The version page redirected to an unexpected address.");
            const int limit = 8 * 1024 * 1024;
            if (response.Content.Headers.ContentLength > limit) throw new InvalidDataException("The version page is too large.");
            using var buffer = new MemoryStream();
            using var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            byte[] chunk = new byte[65536];
            int count;
            while ((count = await stream.ReadAsync(chunk, timeout.Token).ConfigureAwait(false)) > 0)
            {
                if (buffer.Length + count > limit) throw new InvalidDataException("The version page is too large.");
                buffer.Write(chunk, 0, count);
            }
            string body = Encoding.UTF8.GetString(buffer.GetBuffer(), 0, checked((int)buffer.Length));
            if (validate is not null && !validate(uri, body)) throw new InvalidDataException("The version page could not be read.");
            lock (_gate)
            {
                _cached[key] = (body, _utcNow().AddMinutes(10));
                _pending.Remove(key);
                completion.TrySetResult(body);
            }
        }
        catch (Exception ex)
        {
            Exception safe = ex switch
            {
                VendorRequestException => ex,
                OperationCanceledException => new VendorRequestException(vendor, VendorFailureKind.Timeout),
                HttpRequestException => new VendorRequestException(vendor, VendorFailureKind.Unavailable),
                _ => ex
            };
            lock (_gate)
            {
                if (safe is VendorRequestException { FailureKind: VendorFailureKind.RateLimited, RetryAtUtc: not null } limited &&
                    (!_cooldowns.TryGetValue(vendor, out var previous) || previous.RetryAtUtc < limited.RetryAtUtc))
                    _cooldowns[vendor] = limited;
                _pending.Remove(key);
                completion.TrySetException(safe);
            }
            _ = completion.Task.Exception;
        }
    }
}
