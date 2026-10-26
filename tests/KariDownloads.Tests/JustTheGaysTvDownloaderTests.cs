using System.Text;
using Cove.Plugins;
using JustTheGaysTvDownloader;
using KariDownloads.Tests.Infrastructure;

namespace KariDownloads.Tests;

public sealed class JustTheGaysTvDownloaderTests
{
    [Theory]
    [InlineData("https://justthegays.tv/video/alex-doe-fucks-sam-roe-pxeg5", true)]
    [InlineData("https://www.justthegays.tv/embed/alex-doe-fucks-sam-roe-pxeg5", true)]
    [InlineData("https://justthegays.tv/clip/9mxPTAL3qskf", true)]
    [InlineData("https://justthegays.tv/p6916/clip/9mxPTAL3qskf", true)]
    [InlineData("https://cdn.gff.network/019e29e2/videos/2026/10/sample/index.m3u8", true)]
    [InlineData("https://justthegays.tv/categories", false)]
    [InlineData("https://justthegays.tv/performer/alex-doe-101", false)]
    [InlineData("https://justthegays.com/video/15307-alex-doe-and-sam-roe-fuck-outside/", false)]
    [InlineData("https://example.com/video/12345", false)]
    public async Task IsSupportedUrl_And_MatchAsync_FilterUrlsCorrectly(string url, bool expectedSupported)
    {
        Assert.Equal(expectedSupported, JustTheGaysTvDownloaderExtension.IsSupportedUrl(url));

        if (!expectedSupported)
        {
            var extension = new JustTheGaysTvDownloaderExtension();
            var match = await extension.MatchAsync(url, CancellationToken.None);
            Assert.Null(match);
        }
    }

    [Fact]
    public async Task JustTheGaysTv_HlsVideoScene_DownloadsSegmentsAndExtractsMetadata()
    {
        const string embedUrl = "https://justthegays.tv/embed/alex-doe-and-sam-roe-flip-fuck-pxeg5";
        const string canonicalPageUrl = "https://justthegays.tv/video/alex-doe-and-sam-roe-flip-fuck-pxeg5";
        const string masterM3u8Url = "https://cdn.gff.network/site/videos/2026/10/alex-doe-and-sam-roe-flip-fuck-pxeg5/index.m3u8";
        const string variant720Url = "https://cdn.gff.network/site/videos/2026/10/alex-doe-and-sam-roe-flip-fuck-pxeg5/720p.m3u8";
        const string variant1080Url = "https://cdn.gff.network/site/videos/2026/10/alex-doe-and-sam-roe-flip-fuck-pxeg5/1080p.m3u8";
        const string seg0Url = "https://cache-slc-fbs-01.gff.network/site/videos/2026/10/alex-doe-and-sam-roe-flip-fuck-pxeg5/seg0.ts?expires=1792903500";
        const string seg1Url = "https://cache-slc-fbs-01.gff.network/site/videos/2026/10/alex-doe-and-sam-roe-flip-fuck-pxeg5/seg1.ts?expires=1792903500";

        const string tvHtml = """
            <!doctype html>
            <html lang="en">
            <head>
                <script type="application/ld+json">
                {
                  "@context": "https://schema.org",
                  "@type": "VideoObject",
                  "name": "Alex Doe and Sam Roe flip fuck",
                  "description": "Get off while streaming Alex Doe and Sam Roe in the erotic new video \"Alex Doe and Sam Roe flip fuck\". Watch these two studs go at it raw. This video has plenty of mind-blowing Anal, Bareback and Muscle action that will make you hard and horny. Find it here on JustTheGays - justthegays.tv, the best free amateur gay porn and OnlyFans streaming site of 2026",
                  "thumbnailUrl": "https://cdn.gff.network/site/thumbnails/2026/10/alex-doe-and-sam-roe-flip-fuck-pxeg5.jpg",
                  "uploadDate": "2026-10-24T03:48:48.416Z",
                  "duration": "PT25M10S",
                  "contentUrl": "https://cdn.gff.network/site/videos/2026/10/alex-doe-and-sam-roe-flip-fuck-pxeg5/index.m3u8",
                  "actor": [
                    {"@type": "Person", "name": "Alex Doe"},
                    {"@type": "Person", "name": "Sam Roe"}
                  ]
                }
                </script>
                <meta property="og:title" content="Alex Doe and Sam Roe flip fuck - gay porn, OnlyFans &amp; nude videos | JustTheGays" />
            </head>
            <body>
                <script>
                    __sveltekit_1967x15 = {
                        data: [{type:"data",data:{}},{type:"data",data:{video:{id:"1792806-cp2l",slug:"alex-doe-and-sam-roe-flip-fuck-pxeg5",title:"Alex Doe and Sam Roe flip fuck",jtg_title:"Alex Doe and Sam Roe flip fuck",upload_date:"2026-10-24T03:48:48.416Z",has720P:true,has1080P:true,categories:[{id:"anal",name:"Anal"},{id:"bareback",name:"Bareback"},{id:"muscle",name:"Muscle"}],performers:[{id:"alex-doe",name:"Alex Doe",slug:"alex-doe-101"},{id:"sam-roe",name:"Sam Roe",slug:"sam-roe-202"}]},inQuickies:false,related:[],videoSrc:"https://cdn.gff.network/site/videos/2026/10/alex-doe-and-sam-roe-flip-fuck-pxeg5/index.m3u8"}}]
                    };
                </script>
            </body>
            </html>
            """;

        const string masterM3u8 = """
            #EXTM3U
            #EXT-X-VERSION:3
            #EXT-X-STREAM-INF:BANDWIDTH=2500000,RESOLUTION=1280x720
            720p.m3u8
            #EXT-X-STREAM-INF:BANDWIDTH=5000000,RESOLUTION=1920x1080
            1080p.m3u8
            """;

        const string media720M3u8 = """
            #EXTM3U
            #EXT-X-VERSION:3
            #EXT-X-TARGETDURATION:10
            #EXTINF:10.0,
            https://cache-slc-fbs-01.gff.network/site/videos/2026/10/alex-doe-and-sam-roe-flip-fuck-pxeg5/seg0.ts?expires=1792903500
            #EXTINF:10.0,
            https://cache-slc-fbs-01.gff.network/site/videos/2026/10/alex-doe-and-sam-roe-flip-fuck-pxeg5/seg1.ts?expires=1792903500
            #EXT-X-ENDLIST
            """;

        var seg0Bytes = Encoding.ASCII.GetBytes("SEGMENT_ZERO_DATA_");
        var seg1Bytes = Encoding.ASCII.GetBytes("SEGMENT_ONE_DATA");

        using var harness = new FakeDownloaderHost()
            .MapHtml(canonicalPageUrl, tvHtml)
            .MapHtml(masterM3u8Url, masterM3u8)
            .MapHtml(variant720Url, media720M3u8)
            .MapHtml(variant1080Url, media720M3u8)
            .MapBytes(seg0Url, seg0Bytes, "video/mp2t")
            .MapBytes(seg1Url, seg1Bytes, "video/mp2t");

        var extension = new JustTheGaysTvDownloaderExtension();
        await extension.InitializeAsync(harness.Services);

        var match = await extension.MatchAsync(embedUrl, CancellationToken.None);
        Assert.NotNull(match);
        Assert.Equal(JustTheGaysTvDownloaderExtension.PrimaryDownloaderId, match.DownloaderId);
        Assert.Equal(canonicalPageUrl, match.NormalizedUrl);
        Assert.Equal(embedUrl, match.SourceUrl);
        Assert.Equal("Alex Doe and Sam Roe flip fuck", match.Label);
        Assert.NotNull(match.QualityOptions);
        Assert.Contains(match.QualityOptions, q => q.Id == "height:1080" && q.Height == 1080);
        Assert.Contains(match.QualityOptions, q => q.Id == "height:720" && q.Height == 720);

        var result = await extension.DownloadAsync(
            new DownloaderRequest(
                JustTheGaysTvDownloaderExtension.PrimaryDownloaderId,
                canonicalPageUrl,
                DownloaderEntity.Video,
                new DownloaderPermissions(["justthegays.tv", "cdn.gff.network", "cache-slc-fbs-01.gff.network"]),
                QualityId: "height:720"),
            harness,
            CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("Alex Doe and Sam Roe flip fuck.mp4", result.OriginalFilename);

        var savedPath = Path.Combine(harness.TempDirectory, result.LocalPath);
        Assert.True(File.Exists(savedPath));
        Assert.True(new FileInfo(savedPath).Length > 0);
        Assert.Contains(harness.ProgressUpdates, p => p.Progress >= 100.0);

        var meta = result.InlineVideoMetadata;
        Assert.NotNull(meta);
        Assert.Equal("Alex Doe and Sam Roe flip fuck", meta.Title);
        Assert.Equal("pxeg5", meta.Code);
        Assert.Equal("2026-10-24", meta.Date);
        Assert.Equal("Watch these two studs go at it raw.", meta.Details);
        Assert.Equal("https://cdn.gff.network/site/thumbnails/2026/10/alex-doe-and-sam-roe-flip-fuck-pxeg5.jpg", meta.ImageUrl);
        Assert.Equal("JustTheGays", meta.StudioName);
        Assert.Equal(["Alex Doe", "Sam Roe"], meta.PerformerNames);
        Assert.Equal(["Anal", "Bareback", "Muscle"], meta.TagNames);
        Assert.Contains(canonicalPageUrl, meta.Urls);
    }

    [Fact]
    public async Task JustTheGaysTv_ClipUrl_DownloadsDirectMp4()
    {
        const string clipUrl = "https://justthegays.tv/clip/9mxPTAL3qskf";
        const string clipMp4Url = "https://cdn.gff.network/site/clips/2026-08/9mxPTAL3qskf.mp4";
        const string clipHtml = """
            <!doctype html>
            <html>
            <head>
                <script type="application/ld+json">
                {
                  "@context": "https://schema.org",
                  "@type": "VideoObject",
                  "name": "Mattstandingjerk",
                  "description": "A 12-second clip from MattOffTheTrail jerks off on JustTheGays.",
                  "thumbnailUrl": "https://cdn.gff.network/site/clips/2026-08/9mxPTAL3qskf.jpg",
                  "uploadDate": "2026-08-24T03:01:49.934Z",
                  "duration": "PT12S",
                  "contentUrl": "https://cdn.gff.network/site/clips/2026-08/9mxPTAL3qskf.mp4"
                }
                </script>
            </head>
            <body></body>
            </html>
            """;

        using var harness = new FakeDownloaderHost()
            .MapHtml(clipUrl, clipHtml)
            .MapBytes(clipMp4Url, [0x00, 0x00, 0x00, 0x18, 0x66, 0x74, 0x79, 0x70], "video/mp4");

        var extension = new JustTheGaysTvDownloaderExtension();
        await extension.InitializeAsync(harness.Services);

        var result = await extension.DownloadAsync(
            new DownloaderRequest(
                JustTheGaysTvDownloaderExtension.PrimaryDownloaderId,
                clipUrl,
                DownloaderEntity.Video,
                new DownloaderPermissions(["justthegays.tv", "cdn.gff.network"])),
            harness,
            CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("Mattstandingjerk.mp4", result.OriginalFilename);
        Assert.True(File.Exists(Path.Combine(harness.TempDirectory, result.LocalPath)));
        Assert.NotNull(result.InlineVideoMetadata);
        Assert.Equal("Mattstandingjerk", result.InlineVideoMetadata.Title);
        Assert.Equal("9mxPTAL3qskf", result.InlineVideoMetadata.Code);
        Assert.Equal("2026-08-24", result.InlineVideoMetadata.Date);
    }
}
