using GeckoPeeks.App;
using Xunit;

namespace GeckoPeeks.Tests;

public class AppUpdateTests
{
    private static readonly Uri Feed = new("https://gecko-peeks-api.gecko-apps.workers.dev/");
    private static readonly string Hash = new('a', 64);

    private static string Manifest(
        string version = "2.0.1",
        string? sha256 = "HASH",
        long size = 71651194,
        string? url = "https://gecko-peeks-api.gecko-apps.workers.dev/v1/update/download/stable/2.0.1",
        string? notes = "Line one.\r\nLine two.") =>
        System.Text.Json.JsonSerializer.Serialize(new
        {
            version,
            sha256 = sha256 == "HASH" ? Hash : sha256,
            size,
            notes,
            publishedAt = "2026-11-20T12:00:00.000Z",
            url,
        });

    [Theory]
    [InlineData("v1.0.12", 1, 0, 12)]
    [InlineData("1.0.11", 1, 0, 11)]
    [InlineData("V2.3.4", 2, 3, 4)]
    public void ParseTag_reads_release_tags(string tag, int major, int minor, int build)
    {
        var version = AppUpdate.ParseTag(tag);
        Assert.NotNull(version);
        Assert.Equal(new Version(major, minor, build), version);
    }

    [Theory]
    [InlineData("")]
    [InlineData("latest")]
    [InlineData("v1")]
    [InlineData(null)]
    public void ParseTag_rejects_noise(string? tag) => Assert.Null(AppUpdate.ParseTag(tag));

    [Fact]
    public void IsNewer_compares_three_parts_only()
    {
        Assert.True(AppUpdate.IsNewer(new Version(1, 0, 12), new Version(1, 0, 11)));
        Assert.False(AppUpdate.IsNewer(new Version(1, 0, 11), new Version(1, 0, 11)));
        Assert.False(AppUpdate.IsNewer(new Version(1, 0, 10), new Version(1, 0, 11)));
        Assert.True(AppUpdate.IsNewer(new Version(1, 1, 0), new Version(1, 0, 99)));
    }

    [Fact]
    public void FormatVersion_is_three_parts() =>
        Assert.Equal("1.0.12", AppUpdate.FormatVersion(new Version(1, 0, 12)));

    [Fact]
    public void PickOffer_reads_a_newer_manifest()
    {
        var offer = AppUpdate.PickOffer(Manifest(sha256: new string('A', 64)), new Version(2, 0, 0), Feed);

        Assert.NotNull(offer);
        Assert.Equal(new Version(2, 0, 1), offer!.Version);
        Assert.Equal("https://gecko-peeks-api.gecko-apps.workers.dev/v1/update/download/stable/2.0.1", offer.DownloadUrl);
        Assert.Equal(71651194, offer.SizeBytes);
        Assert.Equal(Hash, offer.Sha256);
        Assert.Equal("Line one.\nLine two.", offer.Notes);
    }

    [Theory]
    [InlineData("2.0.0")]
    [InlineData("1.0.16")]
    public void PickOffer_is_null_for_the_same_or_an_older_version(string version) =>
        Assert.Null(AppUpdate.PickOffer(Manifest(version), new Version(2, 0, 0), Feed));

    [Fact]
    public void PickOffer_is_null_for_an_older_version_even_without_a_hash() =>
        Assert.Null(AppUpdate.PickOffer(Manifest("1.0.0", sha256: null), new Version(2, 0, 0), Feed));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("xyz")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    [InlineData("sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    public void PickOffer_requires_a_sha256(string? sha256)
    {
        var error = Assert.Throws<InvalidDataException>(() => AppUpdate.PickOffer(Manifest(sha256: sha256), new Version(2, 0, 0), Feed));
        Assert.Contains("SHA-256", error.Message);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void PickOffer_requires_a_size(long size) =>
        Assert.Throws<InvalidDataException>(() => AppUpdate.PickOffer(Manifest(size: size), new Version(2, 0, 0), Feed));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("/v1/update/download/stable/2.0.1")]
    [InlineData("http://gecko-peeks-api.gecko-apps.workers.dev/v1/update/download/stable/2.0.1")]
    [InlineData("https://evil.example/GeckoPeeks.exe")]
    [InlineData("https://gecko-peeks-api.gecko-apps.workers.dev:8443/x")]
    [InlineData("https://user@gecko-peeks-api.gecko-apps.workers.dev/x")]
    public void PickOffer_requires_a_download_on_the_feed_host(string? url) =>
        Assert.Throws<InvalidDataException>(() => AppUpdate.PickOffer(Manifest(url: url), new Version(2, 0, 0), Feed));

    [Theory]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("{\"version\":\"latest\"}")]
    [InlineData("[]")]
    public void PickOffer_throws_on_a_bad_manifest(string json) =>
        Assert.ThrowsAny<Exception>(() => AppUpdate.PickOffer(json, new Version(2, 0, 0), Feed));

    [Fact]
    public void TrimNotes_caps_the_release_body_at_4000_chars()
    {
        var notes = AppUpdate.TrimNotes(new string('x', 5000));
        Assert.Equal(AppUpdate.MaxNotesLength, notes.Length);
        Assert.EndsWith("…", notes);
        Assert.Equal("", AppUpdate.TrimNotes(null));
        Assert.Equal("short", AppUpdate.TrimNotes("  short \r\n"));
    }

    [Theory]
    [InlineData("{\"error\":\"no_release\"}", true)]
    [InlineData("{\"error\":\"not_found\"}", false)]
    [InlineData("<html>404</html>", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsNoRelease_matches_only_the_no_release_body(string? text, bool expected) =>
        Assert.Equal(expected, UpdateClient.IsNoRelease(text));
}

public class UpdateFeedTests
{
    private static Func<string, string?> Env(string? url = null, string? devUpdates = null) => name => name switch
    {
        UpdateFeed.UrlVariable => url,
        UpdateFeed.DevUpdatesVariable => devUpdates,
        _ => null,
    };

    [Fact]
    public void Production_reads_the_stable_channel_on_the_worker()
    {
        var feed = UpdateFeed.For(RunProfile.Production, Env(devUpdates: "1"));

        Assert.True(feed.Enabled);
        Assert.Equal("stable", feed.Channel);
        Assert.Equal(
            "https://gecko-peeks-api.gecko-apps.workers.dev/v1/update/latest?channel=stable",
            feed.LatestUrl.AbsoluteUri);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("0")]
    [InlineData("true")]
    [InlineData("yes")]
    [InlineData("11")]
    public void Dev_updates_stay_off_unless_the_variable_is_1(string? value)
    {
        var feed = UpdateFeed.For(RunProfile.Dev, Env(devUpdates: value));
        Assert.False(feed.Enabled);
        Assert.Equal("devtest", feed.Channel);
    }

    [Fact]
    public void Dev_updates_on_read_the_devtest_channel()
    {
        var feed = UpdateFeed.For(RunProfile.Dev, Env(devUpdates: " 1 "));

        Assert.True(feed.Enabled);
        Assert.Equal(RunProfile.Dev, feed.Profile);
        Assert.EndsWith("/v1/update/latest?channel=devtest", feed.LatestUrl.AbsoluteUri);
    }

    [Fact]
    public void A_dev_profile_is_off_in_this_production_compilation()
    {
        Assert.True(UpdateFeed.IsEnabled(RunProfile.Production));
        Assert.False(UpdateFeed.IsEnabled(RunProfile.Dev));
        Assert.Equal(RunProfile.Production, UpdateFeed.Current.Profile);
    }

    [Theory]
    [InlineData("https://updates.example/", RunProfile.Production, "https://updates.example/")]
    [InlineData("https://updates.example", RunProfile.Production, "https://updates.example/")]
    [InlineData("https://updates.example/base", RunProfile.Production, "https://updates.example/base/")]
    [InlineData("http://127.0.0.1:8787", RunProfile.Dev, "http://127.0.0.1:8787/")]
    [InlineData("http://localhost:8787/", RunProfile.Dev, "http://localhost:8787/")]
    public void ParseBaseUrl_accepts_https_and_dev_loopback_http(string text, RunProfile profile, string expected) =>
        Assert.Equal(expected, UpdateFeed.ParseBaseUrl(text, profile)!.AbsoluteUri);

    [Theory]
    [InlineData(null, RunProfile.Production)]
    [InlineData("", RunProfile.Production)]
    [InlineData("not a url", RunProfile.Production)]
    [InlineData("http://127.0.0.1:8787", RunProfile.Production)]
    [InlineData("http://updates.example/", RunProfile.Dev)]
    [InlineData("ftp://updates.example/", RunProfile.Production)]
    [InlineData("https://user:pw@updates.example/", RunProfile.Production)]
    [InlineData("https://updates.example/?x=1", RunProfile.Production)]
    [InlineData("file:///C:/x", RunProfile.Dev)]
    public void ParseBaseUrl_rejects_everything_else(string? text, RunProfile profile) =>
        Assert.Null(UpdateFeed.ParseBaseUrl(text, profile));

    [Fact]
    public void A_bad_url_variable_falls_back_to_the_worker()
    {
        var feed = UpdateFeed.For(RunProfile.Production, Env(url: "http://evil.example/"));
        Assert.Equal(UpdateFeed.DefaultBaseUrl, feed.BaseUrl.AbsoluteUri);
    }

    [Theory]
    [InlineData("https://updates.example/")]
    [InlineData("http://127.0.0.1:8787")]
    public void Production_ignores_the_url_variable(string url)
    {
        var feed = UpdateFeed.For(RunProfile.Production, Env(url: url));
        Assert.Equal(UpdateFeed.DefaultBaseUrl, feed.BaseUrl.AbsoluteUri);
    }

    [Fact]
    public void Dev_uses_the_url_variable()
    {
        var feed = UpdateFeed.For(RunProfile.Dev, Env(url: "http://127.0.0.1:8787", devUpdates: "1"));
        Assert.Equal("http://127.0.0.1:8787/", feed.BaseUrl.AbsoluteUri);
    }
}
