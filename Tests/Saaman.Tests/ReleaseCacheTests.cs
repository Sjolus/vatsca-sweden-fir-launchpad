using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using VatscaUpdateChecker.Services;

internal static class ReleaseCacheTests
{
    internal static async Task<int> RunAsync()
    {
        int passed = 0;
        await Check("release cache is shared per client and expires", async () =>
        {
            DateTimeOffset now = DateTimeOffset.Parse("2026-10-21T12:00:00Z");
            string version = "1.3.37";
            using var handler = new Handler(_ => Task.FromResult(Catalog(version)));
            using var client = new HttpClient(handler);
            var first = new SaamanReleaseService(client, () => now);
            var second = new SaamanReleaseService(client, () => now);
            Equal("1.3.37", (await first.GetLatestAsync())!.Version);
            version = "1.3.38";
            Equal("1.3.37", (await second.GetLatestAsync())!.Version);
            Equal(1, handler.Requests);
            now = now.AddMinutes(11);
            Equal("1.3.38", (await second.GetLatestAsync())!.Version);
            Equal(2, handler.Requests);
        });
        await Check("empty published catalog is cached", async () =>
        {
            using var handler = new Handler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("[]") }));
            using var client = new HttpClient(handler);
            var service = new SaamanReleaseService(client);
            Equal(true, await service.GetLatestAsync() is null);
            Equal(true, await service.GetLatestAsync() is null);
            Equal(1, handler.Requests);
        });
        await Check("concurrent checks share a request and cancellation only cancels its caller", async () =>
        {
            var release = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var handler = new Handler(_ => release.Task);
            using var client = new HttpClient(handler);
            var first = new SaamanReleaseService(client);
            var second = new SaamanReleaseService(client);
            using var cancelled = new CancellationTokenSource();
            var waiting = first.GetLatestAsync(cancelled.Token);
            var other = second.GetLatestAsync();
            Equal(1, handler.Requests);
            cancelled.Cancel();
            await Throws<OperationCanceledException>(async () => await waiting);
            release.SetResult(Catalog("1.3.37"));
            Equal("1.3.37", (await other)!.Version);
            Equal("1.3.37", (await first.GetLatestAsync())!.Version);
            Equal(1, handler.Requests);
        });
        await Check("a pre-cancelled check sends no request", async () =>
        {
            using var handler = new Handler(_ => Task.FromResult(Catalog("1.3.37")));
            using var client = new HttpClient(handler);
            var service = new SaamanReleaseService(client);
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            await Throws<OperationCanceledException>(() => service.GetLatestAsync(cancelled.Token));
            Equal(0, handler.Requests);
        });
        await Check("rate-limit reset is shared and blocks further requests until expiry", async () =>
        {
            DateTimeOffset now = DateTimeOffset.Parse("2026-10-21T12:00:00Z");
            DateTimeOffset reset = now.AddMinutes(10);
            bool limited = true;
            using var handler = new Handler(_ =>
            {
                var response = limited ? new HttpResponseMessage(HttpStatusCode.Forbidden) : Catalog("1.3.37");
                if (limited)
                {
                    response.Headers.Add("X-RateLimit-Remaining", "0");
                    response.Headers.Add("X-RateLimit-Reset", reset.ToUnixTimeSeconds().ToString());
                }
                return Task.FromResult(response);
            });
            using var client = new HttpClient(handler);
            var first = new SaamanReleaseService(client, () => now);
            var second = new SaamanReleaseService(client, () => now);
            var error = await Throws<SaamanReleaseUnavailableException>(() => first.GetLatestAsync());
            Equal(reset, error.RetryAtUtc);
            Equal(true, error.Message.Contains("Try again after", StringComparison.Ordinal));
            Equal(false, error.Message.Contains("403", StringComparison.Ordinal));
            await Throws<SaamanReleaseUnavailableException>(() => second.GetLatestAsync());
            Equal(1, handler.Requests);
            now = reset.AddSeconds(1);
            limited = false;
            Equal("1.3.37", (await second.GetLatestAsync())!.Version);
            Equal(2, handler.Requests);
        });
        await Check("Retry-After delta and date are honored", async () =>
        {
            foreach (bool dateHeader in new[] { false, true })
            {
                DateTimeOffset now = DateTimeOffset.Parse("2026-10-21T12:00:00Z");
                DateTimeOffset retry = now.AddMinutes(2);
                using var handler = new Handler(_ =>
                {
                    var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
                    response.Headers.RetryAfter = dateHeader ? new RetryConditionHeaderValue(retry) : new RetryConditionHeaderValue(TimeSpan.FromMinutes(2));
                    return Task.FromResult(response);
                });
                using var client = new HttpClient(handler);
                var service = new SaamanReleaseService(client, () => now);
                Equal(retry, (await Throws<SaamanReleaseUnavailableException>(() => service.GetLatestAsync())).RetryAtUtc);
                await Throws<SaamanReleaseUnavailableException>(() => service.GetLatestAsync());
                Equal(1, handler.Requests);
            }
        });
        await Check("ordinary forbidden requests are not mislabeled as rate limits", async () =>
        {
            using var handler = new Handler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden)));
            using var client = new HttpClient(handler);
            var service = new SaamanReleaseService(client);
            var error = await Throws<SaamanReleaseUnavailableException>(() => service.GetLatestAsync());
            Equal(true, error.RetryAtUtc is null);
            Equal(true, error.Message.Contains("refused", StringComparison.Ordinal));
            await Throws<SaamanReleaseUnavailableException>(() => service.GetLatestAsync());
            Equal(2, handler.Requests);
        });
        await Check("invalid catalogs are never cached", async () =>
        {
            bool invalid = true;
            using var handler = new Handler(_ => Task.FromResult(invalid
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") } : Catalog("1.3.37")));
            using var client = new HttpClient(handler);
            var service = new SaamanReleaseService(client);
            await Throws<InvalidDataException>(() => service.GetLatestAsync());
            invalid = false;
            Equal("1.3.37", (await service.GetLatestAsync())!.Version);
            Equal(2, handler.Requests);
        });
        return passed;

        async Task Check(string name, Func<Task> test)
        {
            await test();
            passed++;
            Console.WriteLine("PASS " + name);
        }
    }

    private static HttpResponseMessage Catalog(string version) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(JsonSerializer.Serialize(new[]
        {
            new
            {
                tag_name = "v" + version, draft = false, prerelease = false, published_at = "2026-10-20T12:00:00Z",
                assets = new[] { "SAAMAN.dll", "LICENSE.txt", "THIRD-PARTY-NOTICES.txt", "SHA256SUMS.txt" }.Select(name => new
                {
                    name, state = "uploaded", size = 1, digest = "sha256:" + new string('a', 64),
                    browser_download_url = $"https://github.com/Kaskobiii/SAAMAN/releases/download/v{version}/{name}"
                }).ToArray()
            }
        }))
    };

    private static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"Expected {expected}, got {actual}.");
    }

    private static async Task<T> Throws<T>(Func<Task> action) where T : Exception
    {
        try { await action(); }
        catch (T error) { return error; }
        throw new Exception($"Expected {typeof(T).Name}.");
    }

    private sealed class Handler(Func<CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        internal int Requests { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri?.Host != "api.github.com") throw new Exception("Unexpected synthetic request.");
            Requests++;
            return respond(cancellationToken);
        }
    }
}
