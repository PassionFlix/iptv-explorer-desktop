using IPTVExplorer.Desktop;

namespace IPTVExplorer.Tests;

public sealed class MediaDownloadNamingTests
{
    [Fact]
    public void StreamQueryExtensionWinsOverPhpRoute()
    {
        var uri = new Uri("https://provider.invalid/play/movie.php?mac=00%3A1A%3A79%3AAA%3ABB%3ACC&stream=1655467.mkv&play_token=fixture&type=series");

        Assert.Equal("mkv", MediaDownloadNaming.ResolveExtension(null, uri));
    }

    [Fact]
    public void ExplicitMediaExtensionStillWins()
    {
        var uri = new Uri("https://provider.invalid/play/movie.php?stream=1655467.mkv&type=series");

        Assert.Equal("ts", MediaDownloadNaming.ResolveExtension(".TS", uri));
    }

    [Fact]
    public void EncodedStreamUrlIsDecodedBeforeReadingExtension()
    {
        var uri = new Uri("https://provider.invalid/play/movie.php?stream=https%3A%2F%2Fcdn.invalid%2Fvideo.m4v%3Ftoken%3Dfixture&type=series");

        Assert.Equal("m4v", MediaDownloadNaming.ResolveExtension(null, uri));
    }

    [Fact]
    public void PhpRouteWithoutMediaHintFallsBackToMp4()
    {
        var uri = new Uri("https://provider.invalid/play/movie.php?token=fixture");

        Assert.Equal("mp4", MediaDownloadNaming.ResolveExtension(null, uri));
        Assert.Null(MediaDownloadNaming.NormalizeExtension("php"));
    }

    [Fact]
    public void DirectMediaPathKeepsItsExtension()
    {
        var uri = new Uri("https://provider.invalid/media/video.webm?token=fixture");

        Assert.Equal("webm", MediaDownloadNaming.ResolveExtension(null, uri));
    }
}
