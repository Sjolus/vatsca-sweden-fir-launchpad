using System.Net;
using System.Net.Http.Headers;
using VatscaUpdateChecker.Services;

internal static class MetadataTests
{
    internal static async Task RunAsync()
    {
        var now = DateTimeOffset.Parse("2026-10-21T12:00:00Z");
        int calls = 0;
        using (var client = new HttpClient(new Handler((_, _) =>
        { calls++; return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("version-" + calls) }); })))
        {
            var reader = new VendorMetadataReader(client, () => now);
            var page = new Uri("https://files.aero-nav.com/ESAA");
            Require(await reader.GetAsync(page, VendorService.AeroNav) == "version-1");
            Require(await reader.GetAsync(page, VendorService.AeroNav) == "version-1" && calls == 1);
            now = now.AddMinutes(10);
            Require(await reader.GetAsync(page, VendorService.AeroNav) == "version-2" && calls == 2);
        }
        Console.WriteLine("PASS metadata: public page cache expires");

        calls = 0;
        var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using (var client = new HttpClient(new Handler(async (_, _) =>
        { calls++; await gate.Task; return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("[]") }; })))
        {
            var reader = new VendorMetadataReader(client);
            var page = new Uri("https://api.github.com/repos/example/tool/releases");
            using var cancellation = new CancellationTokenSource();
            var first = reader.GetAsync(page, VendorService.GitHub, cancellation.Token);
            var second = reader.GetAsync(page, VendorService.GitHub);
            cancellation.Cancel();
            await Reject<OperationCanceledException>(() => first);
            gate.SetResult(true);
            Require(await second == "[]" && calls == 1);
        }
        Console.WriteLine("PASS metadata: concurrent checks share work and cancellation is local");

        calls = 0;
        using (var client = new HttpClient(new Handler((_, _) =>
        {
            calls++;
            var response = new HttpResponseMessage(HttpStatusCode.Forbidden) { Content = new StringContent("SECRET response") };
            response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromMinutes(2));
            return Task.FromResult(response);
        })))
        {
            var reader = new VendorMetadataReader(client, () => now);
            var error = await Reject<VendorRequestException>(() => reader.GetAsync(new("https://api.github.com/repos/example/a/releases"), VendorService.GitHub));
            Require(error.FailureKind == VendorFailureKind.RateLimited && error.StatusCode == HttpStatusCode.Forbidden && !error.ToString().Contains("SECRET"));
            await Reject<VendorRequestException>(() => reader.GetAsync(new("https://api.github.com/repos/example/b/releases"), VendorService.GitHub));
            Require(calls == 1);
            now = now.AddMinutes(3);
            await Reject<VendorRequestException>(() => reader.GetAsync(new("https://api.github.com/repos/example/a/releases"), VendorService.GitHub));
            Require(calls == 2);
        }
        Console.WriteLine("PASS metadata: quota cooldown blocks requests and exposes safe typed status");

        calls = 0;
        var longer = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var shorter = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        using (var client = new HttpClient(new Handler((request, _) =>
        {
            calls++;
            return request.RequestUri!.AbsolutePath.EndsWith("/long", StringComparison.Ordinal) ? longer.Task :
                request.RequestUri.AbsolutePath.EndsWith("/short", StringComparison.Ordinal) ? shorter.Task :
                Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("[]") });
        })))
        {
            var reader = new VendorMetadataReader(client, () => now);
            var first = reader.GetAsync(new("https://api.github.com/repos/example/long"), VendorService.GitHub);
            var second = reader.GetAsync(new("https://api.github.com/repos/example/short"), VendorService.GitHub);
            Require(calls == 2);
            var laterDeadline = now.AddMinutes(10);
            var longResponse = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            longResponse.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromMinutes(10));
            longer.SetResult(longResponse);
            Require((await Reject<VendorRequestException>(() => first)).RetryAtUtc == laterDeadline);
            var shortResponse = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            shortResponse.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromMinutes(2));
            shorter.SetResult(shortResponse);
            await Reject<VendorRequestException>(() => second);
            now = now.AddMinutes(3);
            var blocked = await Reject<VendorRequestException>(() => reader.GetAsync(new("https://api.github.com/repos/example/next"), VendorService.GitHub));
            Require(blocked.RetryAtUtc == laterDeadline && calls == 2);
            now = now.AddMinutes(8);
            Require(await reader.GetAsync(new("https://api.github.com/repos/example/next"), VendorService.GitHub) == "[]" && calls == 3);
        }
        Console.WriteLine("PASS metadata: later-arriving shorter cooldown cannot replace the longer deadline");

        calls = 0;
        using (var client = new HttpClient(new Handler((_, _) =>
        { calls++; return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(calls == 1 ? "bad" : "valid") }); })))
        {
            var reader = new VendorMetadataReader(client, validate: (_, body) => body == "valid");
            await Reject<InvalidDataException>(() => reader.GetAsync(new("https://files.aero-nav.com/ESAA"), VendorService.AeroNav));
            Require(await reader.GetAsync(new("https://files.aero-nav.com/ESAA"), VendorService.AeroNav) == "valid" && calls == 2);
        }
        Console.WriteLine("PASS metadata: malformed public pages are not cached");

        using (var client = new HttpClient(new Handler((request, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        { RequestMessage = new(HttpMethod.Get, "https://example.invalid/elsewhere"), Content = new StringContent("[]") }))))
        {
            await Reject<InvalidDataException>(() => new VendorMetadataReader(client).GetAsync(new("https://files.aero-nav.com/ESAA"), VendorService.AeroNav));
        }
        using (var client = new HttpClient(new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StringContent(new string('x', 8 * 1024 * 1024 + 1)) }))))
        {
            await Reject<InvalidDataException>(() => new VendorMetadataReader(client).GetAsync(new("https://files.aero-nav.com/ESAA"), VendorService.AeroNav));
        }
        Console.WriteLine("PASS metadata: unexpected redirects and oversized pages fail closed");
    }

    private static void Require(bool condition) { if (!condition) throw new Exception("Metadata regression failed."); }
    private static async Task<T> Reject<T>(Func<Task> action) where T : Exception
    { try { await action(); } catch (T error) { return error; } throw new Exception("Expected " + typeof(T).Name); }
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handle) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        { var response = await handle(request, token); response.RequestMessage ??= request; return response; }
    }
}
