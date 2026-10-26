using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Cove.Plugins;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace KariDownloads.Tests.Infrastructure;

/// <summary>
/// Deterministic offline test harness implementing <see cref="IDownloaderHost"/> and providing an
/// <see cref="IServiceProvider"/> with a fixture-backed <see cref="IHttpClientFactory"/> for testing
/// both <see cref="IDownloaderProvider"/> and <see cref="IScraperProvider"/> extensions without live network calls.
/// </summary>
public sealed class FakeDownloaderHost : IDownloaderHost, IDisposable
{
    private readonly Dictionary<string, Func<HttpRequestMessage, HttpResponseMessage>> _routes =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly List<(double Progress, string? Message)> _progressUpdates = [];
    private readonly ServiceProvider _serviceProvider;

    public FakeDownloaderHost()
    {
        TempDirectory = Path.Combine(Path.GetTempPath(), $"cove-ext-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(TempDirectory);

        HttpClients = new FakeHttpClientFactory(_routes);

        var services = new ServiceCollection();
        services.AddSingleton<IHttpClientFactory>(HttpClients);
        services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        _serviceProvider = services.BuildServiceProvider();
    }

    public string TempDirectory { get; }

    public IHttpClientFactory HttpClients { get; }

    public IServiceProvider Services => _serviceProvider;

    public IReadOnlyList<(double Progress, string? Message)> ProgressUpdates => _progressUpdates;

    public FakeDownloaderHost MapHtml(string url, string html, HttpStatusCode status = HttpStatusCode.OK)
    {
        _routes[url] = _ => new HttpResponseMessage(status)
        {
            Content = new StringContent(html, Encoding.UTF8, "text/html")
        };
        return this;
    }

    public FakeDownloaderHost MapJson(string url, string json, HttpStatusCode status = HttpStatusCode.OK)
    {
        _routes[url] = _ => new HttpResponseMessage(status)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
        return this;
    }

    public FakeDownloaderHost MapBytes(string url, byte[] payload, string contentType = "application/octet-stream", HttpStatusCode status = HttpStatusCode.OK)
    {
        _routes[url] = _ =>
        {
            var content = new ByteArrayContent(payload);
            content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
            content.Headers.ContentLength = payload.Length;
            return new HttpResponseMessage(status) { Content = content };
        };
        return this;
    }

    public ILogger CreateLogger(string categoryName) => NullLogger.Instance;

    public void ReportProgress(double progress, string? message = null)
    {
        _progressUpdates.Add((progress, message));
    }

    public void Dispose()
    {
        _serviceProvider.Dispose();
        if (Directory.Exists(TempDirectory))
        {
            try
            {
                Directory.Delete(TempDirectory, recursive: true);
            }
            catch
            {
                // Ignore best-effort cleanup errors
            }
        }
    }

    private sealed class FakeHttpClientFactory(
        IReadOnlyDictionary<string, Func<HttpRequestMessage, HttpResponseMessage>> routes) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new FixtureMessageHandler(routes), disposeHandler: true);
    }

    private sealed class FixtureMessageHandler(
        IReadOnlyDictionary<string, Func<HttpRequestMessage, HttpResponseMessage>> routes) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri?.ToString() ?? "";
            var absUrl = request.RequestUri?.AbsoluteUri ?? "";
            if (routes.TryGetValue(url, out var factory) || (!string.IsNullOrEmpty(absUrl) && routes.TryGetValue(absUrl, out factory)))
                return Task.FromResult(factory(request));

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)
            {
                RequestMessage = request,
                Content = new StringContent($"Unmapped fixture URL: {url}")
            });
        }
    }
}
