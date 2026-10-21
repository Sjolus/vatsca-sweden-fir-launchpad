using System.Net;
using Velopack;
using Velopack.Sources;

sealed class MetadataHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.RequestUri?.AbsoluteUri != "https://api.github.com/repos/Sjolus/vatsca-sweden-fir-launchpad/releases?per_page=10&page=1" ||
            request.Headers.Authorization is not null)
            throw new Exception("Unexpected self-update metadata request.");
        var response = respond(request);
        response.RequestMessage = request;
        return Task.FromResult(response);
    }
}

sealed class SyntheticGithubDownloader(string feedDirectory, string packageName) : IFileDownloader
{
    public int Catalogs, Packages;
    public Task<string> DownloadString(string url, IDictionary<string, string>? headers = null, double timeout = 30) =>
        throw new Exception("The catalog must use the metadata reader.");
    public Task<byte[]> DownloadBytes(string url, IDictionary<string, string>? headers = null, double timeout = 30)
    {
        if (url != "https://example.invalid/stable/releases.win-x64.json") throw new Exception("Unexpected catalog asset.");
        Catalogs++;
        return File.ReadAllBytesAsync(Path.Combine(feedDirectory, "releases.win-x64.json"));
    }
    public Task DownloadFile(string url, string targetFile, Action<int> progress,
        IDictionary<string, string>? headers = null, double timeout = 30, CancellationToken cancelToken = default)
    {
        if (url != "https://example.invalid/stable/package.nupkg") throw new Exception("Unexpected package asset.");
        Packages++;
        File.Copy(Path.Combine(feedDirectory, packageName), targetFile, true);
        progress(100);
        return Task.CompletedTask;
    }
}
