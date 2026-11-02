using Cove.Core.DTOs;
using Cove.Plugins;
using KariDownloads.Tests.Infrastructure;
using ThisvidScraper;

namespace KariDownloads.Tests;

public sealed class ThisvidScraperTests
{
    private const string VideoUrl = "https://thisvid.com/videos/fixture-thisvid-scene/";
    private const string SearchUrl = "https://thisvid.com/search/?q=amateurs";

    private const string RealisticThisvidHtml = """
        <!doctype html>
        <html lang="en">
          <head>
            <meta charset="utf-8" />
            <meta property="og:title" content="Fixture ThisVid Scene - ThisVid.com" />
            <meta property="og:url" content="https://thisvid.com/videos/fixture-thisvid-scene/" />
            <meta property="og:description" content="Generic og fallback description." />
            <meta property="og:image" content="https://media.thisvid.com/contents/videos_screenshots/1000/1234/preview.jpg" />
            <title>Fixture ThisVid Scene - ThisVid.com</title>
          </head>
          <body>
            <div class="video-holder">
              <div class="video-id" data-video-id="3456789"></div>
              <ul class="description">
                <li>
                  <p>Fixture ThisVid description paragraph.</p>
                </li>
                <li>
                  <span>Added:</span> 22 March 2021 by <a href="/members/111/">uploader</a>
                </li>
                <li>
                  <span>Categories:</span>
                  <a href="/categories/amateurs/">Amateurs</a>
                  <a href="/categories/anal/">Anal</a>
                </li>
                <li>
                  <span>Tags:</span>
                  <a href="/tags/blonde/">Blonde</a>
                </li>
              </ul>
            </div>
          </body>
        </html>
        """;

    private const string RealisticSearchHtml = """
        <!doctype html>
        <html>
        <body>
            <div class="item">
                <a href="/videos/3456789/amateur-fun/" title="Amateur Fun - ThisVid.com">
                    <img src="https://media.thisvid.com/contents/videos_screenshots/3000/3456789/preview.jpg" alt="Amateur Fun" />
                    Amateur Fun - ThisVid.com
                </a>
            </div>
            <div class="item">
                <a href="https://thisvid.com/videos/4567890/blonde-anal/">
                    <img src="https://media.thisvid.com/contents/videos_screenshots/4000/4567890/preview.jpg" />
                    Blonde Anal Scene
                </a>
            </div>
        </body>
        </html>
        """;

    [Fact]
    public void IsSupportedUrl_ValidatesUrlsAccurately()
    {
        Assert.True(ThisvidScraperExtension.IsSupportedUrl("https://thisvid.com/videos/fixture-thisvid-scene/"));
        Assert.True(ThisvidScraperExtension.IsSupportedUrl("https://www.thisvid.com/videos/12345/"));
        Assert.True(ThisvidScraperExtension.IsSupportedUrl("https://thisvid.com/embed/12345/"));
        Assert.False(ThisvidScraperExtension.IsSupportedUrl("https://thisvid.com/members/111/"));
        Assert.False(ThisvidScraperExtension.IsSupportedUrl("https://other.com/videos/12345/"));
        Assert.False(ThisvidScraperExtension.IsSupportedUrl("not-a-url"));
    }

    [Fact]
    public async Task ScrapeVideoAsync_ExtractsAllFieldsAccurately()
    {
        using var harness = new FakeDownloaderHost()
            .MapHtml(VideoUrl, RealisticThisvidHtml);

        var extension = new ThisvidScraperExtension();
        await extension.InitializeAsync(harness.Services);

        var scraped = await extension.ScrapeVideoAsync(
            new ScraperRequest<VideoScrapeInput>(
                ThisvidScraperExtension.PrimaryScraperId,
                new VideoScrapeInput { Url = VideoUrl },
                new ScraperPermissions(["thisvid.com"])),
            CancellationToken.None);

        Assert.NotNull(scraped);
        Assert.Equal("Fixture ThisVid Scene", scraped.Title);
        Assert.Equal("3456789", scraped.Code);
        Assert.Equal("2021-03-22", scraped.Date);
        Assert.Equal("Fixture ThisVid description paragraph.", scraped.Details);
        Assert.Equal("https://media.thisvid.com/contents/videos_screenshots/1000/1234/preview.jpg", scraped.ImageUrl);
        Assert.Equal("ThisVid", scraped.StudioName);
        Assert.Contains("Amateurs", scraped.TagNames);
        Assert.Contains("Anal", scraped.TagNames);
        Assert.Contains("Blonde", scraped.TagNames);
    }

    [Fact]
    public async Task SearchVideosAsync_ParsesSearchResults()
    {
        using var harness = new FakeDownloaderHost()
            .MapHtml(SearchUrl, RealisticSearchHtml);

        var extension = new ThisvidScraperExtension();
        await extension.InitializeAsync(harness.Services);

        var results = await extension.SearchVideosAsync(
            new ScraperRequest<string>(
                ThisvidScraperExtension.PrimaryScraperId,
                "amateurs",
                new ScraperPermissions(["thisvid.com"])),
            CancellationToken.None);

        Assert.NotNull(results);
        Assert.Equal(2, results.Count);

        var first = results[0];
        Assert.Equal("Amateur Fun", first.Title);
        Assert.Equal("3456789", first.Code);
        Assert.Equal("https://thisvid.com/videos/3456789/amateur-fun/", first.Urls[0]);
        Assert.Equal("https://media.thisvid.com/contents/videos_screenshots/3000/3456789/preview.jpg", first.ImageUrl);

        var second = results[1];
        Assert.Equal("Blonde Anal Scene", second.Title);
        Assert.Equal("4567890", second.Code);
        Assert.Equal("https://thisvid.com/videos/4567890/blonde-anal/", second.Urls[0]);
    }
}
