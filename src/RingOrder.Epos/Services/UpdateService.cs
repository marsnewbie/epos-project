using RingOrder.Epos.Data;
using Velopack;
using Velopack.Sources;

namespace RingOrder.Epos.Services;

/// <summary>
/// Where a till looks for a new version of itself: the repository whose GitHub
/// releases hold the packages.
/// <para>
/// Compiled in, because it is the same for every shop and it is not a secret —
/// the same reasoning as <c>CloudEndpoint</c>.
/// </para>
/// <para>
/// <b>The repository must be public.</b> A private one would need a token, and a
/// token shipped inside every till is a token anybody can extract — one that
/// would then read the source as well as the releases. If the source ever has to
/// go private, the releases move to a separate public repository rather than the
/// token moving into the binary.
/// </para>
/// <para>
/// Empty disables everything, and that is the safe default: with no feed the
/// till runs the build it was installed with, which is exactly what it does
/// today.
/// </para>
/// </summary>
public static class UpdateFeed
{
    public const string Repository = "https://github.com/marsnewbie/epos-project";

    /// <summary>
    /// Whether a pre-release counts. False: a shop is not a test channel, and
    /// the way to try a build is to install it on a machine that is not a
    /// merchant's.
    /// </summary>
    public const bool AllowPrerelease = false;

    public static bool IsConfigured => !string.IsNullOrWhiteSpace(Repository);
}

/// <summary>
/// Keeps the till up to date without ever interrupting it.
/// <para>
/// The rule that shapes all of this: <b>a till is never restarted while it is
/// running.</b> An update is downloaded quietly in the background and applied at
/// the next start, before the window opens — at which point nobody is mid-sale
/// by definition. A restart at seven on a Saturday, however well intentioned,
/// costs a merchant a service and costs us the merchant.
/// </para>
/// <para>
/// Tills get restarted. Shops turn them off at night, staff reboot them, Windows
/// updates them. Waiting for that is slower than forcing it and is the only
/// version of this that is safe.
/// </para>
/// </summary>
public sealed class UpdateService
{
    /// <summary>
    /// How often to look. Hourly rather than daily because a fix that matters is
    /// usually one that matters today, and an HTTP call that finds nothing costs
    /// a few hundred bytes.
    /// </summary>
    public static readonly TimeSpan CheckInterval = TimeSpan.FromHours(1);

    private readonly Action<string> _log;
    private readonly UpdateManager? _manager;

    private Timer? _timer;

    /// <summary>What the last check found, for Settings → Support.</summary>
    public string Status { get; private set; } = "";

    /// <summary>A version sitting on disk, waiting for the next start.</summary>
    public string? Downloaded { get; private set; }

    public UpdateService(Action<string>? log = null, string? feedUrl = null)
    {
        _log = log ?? AppLog.For("update");

        var url = feedUrl ?? UpdateFeed.Repository;

        if (string.IsNullOrWhiteSpace(url))
        {
            Status = "no update feed configured";
            return;
        }

        try
        {
            // GithubSource rather than SimpleWebSource: a GitHub release is not a
            // static directory of files, and pointing a plain web source at one
            // finds nothing while looking like it is working.
            //
            // No access token, deliberately — see UpdateFeed.
            _manager = new UpdateManager(new GithubSource(url, null, UpdateFeed.AllowPrerelease));
        }
        catch (Exception ex)
        {
            // A malformed feed must not stop a till opening.
            Status = $"update feed unusable: {ex.Message}";
            _log(Status);
        }
    }

    /// <summary>
    /// True when this build is running from an installation Velopack manages.
    /// A developer running from <c>dotnet run</c> is not, and must not be told
    /// about updates it could never apply.
    /// </summary>
    public bool IsInstalled => _manager?.IsInstalled ?? false;

    /// <summary>
    /// Applies anything already downloaded, restarting into it.
    /// <para>
    /// Called at startup, before a window exists. This is the only place an
    /// update is ever applied, and it is the only moment at which restarting a
    /// till costs nobody anything.
    /// </para>
    /// </summary>
    public bool ApplyIfReady()
    {
        if (_manager is null || !_manager.IsInstalled) return false;

        try
        {
            if (_manager.UpdatePendingRestart is not { } update) return false;

            _log($"applying update {update.Version} before opening");
            _manager.ApplyUpdatesAndRestart(update);
            return true;
        }
        catch (Exception ex)
        {
            // A failed update leaves the shop on the build it has, which works.
            _log($"could not apply the downloaded update: {ex.Message}");
            return false;
        }
    }

    /// <summary>Starts looking, quietly and on a timer. Failure is invisible.</summary>
    public void StartChecking()
    {
        if (_manager is null) return;

        _timer?.Dispose();
        _timer = new Timer(_ => _ = CheckAsync(), null, TimeSpan.FromMinutes(2), CheckInterval);
    }

    /// <summary>
    /// Looks for a new version and downloads it. Never applies it — see the note
    /// on this class.
    /// </summary>
    public async Task<bool> CheckAsync(CancellationToken ct = default)
    {
        if (_manager is null || !_manager.IsInstalled) return false;

        try
        {
            var update = await _manager.CheckForUpdatesAsync().ConfigureAwait(false);

            if (update is null)
            {
                Status = $"up to date ({_manager.CurrentVersion})";
                return false;
            }

            await _manager.DownloadUpdatesAsync(update, cancelToken: ct).ConfigureAwait(false);

            Downloaded = update.TargetFullRelease.Version.ToString();
            Status = $"{Downloaded} downloaded — it goes in at the next start";
            _log(Status);
            return true;
        }
        catch (Exception ex)
        {
            // The ordinary case in a takeaway: the router is off. Nothing to
            // report to anyone and nothing that should reach the screen.
            Status = $"could not check for updates: {ex.Message}";
            return false;
        }
    }

    public void Dispose()
    {
        _timer?.Dispose();
        _timer = null;
    }
}
