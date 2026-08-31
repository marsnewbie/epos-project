using RingOrder.Epos.Services;
using Xunit;

namespace RingOrder.Epos.Tests;

/// <summary>
/// Keeping a till up to date without ever interrupting it.
/// <para>
/// The rule that shapes all of this: <b>a till is never restarted while it is
/// running.</b> An update is downloaded quietly and applied at the next start,
/// before a window exists — the only moment at which restarting one costs
/// nobody anything.
/// </para>
/// </summary>
public class UpdateTests
{
    private readonly List<string> _log = [];

    /// <summary>
    /// The shipped state today. With no feed the service does nothing at all,
    /// which is exactly what a till installed by hand should do — and it is the
    /// state a build made before the feed exists would ship in.
    /// </summary>
    [Fact]
    public void With_no_feed_nothing_is_checked_and_nothing_is_applied()
    {
        var updates = new UpdateService(_log.Add, feedUrl: "");

        Assert.False(updates.IsInstalled);
        Assert.False(updates.ApplyIfReady());
        Assert.Null(updates.Downloaded);
        Assert.Contains("no update feed", updates.Status);
        Assert.Empty(_log);
    }

    /// <summary>
    /// A private repository would need a token, and a token inside every till is
    /// one anybody can extract — and it would read the source as well as the
    /// releases. If the source ever goes private the releases move to a separate
    /// public repository; the token never moves into the binary.
    /// </summary>
    [Fact]
    public void The_feed_is_a_public_repository_and_carries_no_credential()
    {
        Assert.True(UpdateFeed.IsConfigured);
        Assert.StartsWith("https://github.com/", UpdateFeed.Repository);
        Assert.DoesNotContain("@", UpdateFeed.Repository);
        Assert.DoesNotContain("token", UpdateFeed.Repository, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>A shop is not a test channel. Trying a build happens on a machine that is not a merchant's.</summary>
    [Fact]
    public void A_pre_release_is_never_sent_to_a_shop()
    {
        Assert.False(UpdateFeed.AllowPrerelease);
    }

    /// <summary>
    /// A developer running from <c>dotnet run</c> is not a managed installation.
    /// Telling them about an update they could never apply would be noise, and
    /// trying to apply one would be worse.
    /// </summary>
    [Fact]
    public async Task An_installation_velopack_does_not_manage_is_left_alone()
    {
        var updates = new UpdateService(_log.Add, feedUrl: "https://example.invalid/releases");

        Assert.False(updates.IsInstalled);
        Assert.False(updates.ApplyIfReady());
        Assert.False(await updates.CheckAsync());
    }

    /// <summary>
    /// A malformed feed is a setup mistake of ours, and it must not be able to
    /// stop a shop opening its till.
    /// </summary>
    [Fact]
    public void A_feed_that_makes_no_sense_does_not_stop_the_till()
    {
        var updates = new UpdateService(_log.Add, feedUrl: "   not a url   ");

        Assert.False(updates.ApplyIfReady());
        Assert.NotNull(updates.Status);
    }

    /// <summary>
    /// Hourly rather than daily: a fix that matters is usually one that matters
    /// today, and a check that finds nothing costs a few hundred bytes.
    /// </summary>
    [Fact]
    public void It_looks_once_an_hour()
    {
        Assert.Equal(TimeSpan.FromHours(1), UpdateService.CheckInterval);
    }
}
