using System.Reflection;
using RingOrder.Epos.Data;
using RingOrder.Epos.Domain;
using RingOrder.Epos.Online;

namespace RingOrder.Epos.Services;

/// <summary>
/// What this till is allowed to do, and the background habit of asking.
/// <para>
/// <b>Nothing here is on the path to opening the till.</b> The current state is
/// resolved from what is already on disk, synchronously and without a network
/// call; the refresh happens afterwards on a background task whose failure is
/// invisible. A shop with no internet, or with our service down, notices
/// nothing — see docs/CLOUD.md.
/// </para>
/// </summary>
public sealed class EntitlementService : IDisposable
{
    private readonly EntitlementStore _store;
    private readonly ChangeLogRepository? _changes;
    private readonly EntitlementClient _client;
    private readonly Func<AppSettings> _settings;
    private readonly IReadOnlyList<string> _publicKeys;

    /// <summary>
    /// Where this component's lines go, injected rather than reached for. The
    /// default writes to the shop's log directory, and a test that constructed
    /// one without passing its own would be writing into a merchant's live
    /// folder — a defect this project has already had twice.
    /// </summary>
    private readonly Action<string> _log;
    private readonly string _bundlePath;

    /// <summary>Raised when a refresh changed the answer, so a banner can appear or clear.</summary>
    public event EventHandler? Changed;

    /// <summary>
    /// The resolved answer. Never null, never blocks, and always a state a shop
    /// can trade in.
    /// </summary>
    public EntitlementState Current { get; private set; }

    /// <summary>Why the stored token did not verify, when it did not. For Settings → Support.</summary>
    public TokenProblem LastTokenProblem { get; private set; }

    /// <summary>What the last refresh attempt did, or null if none has run in this session.</summary>
    public RefreshOutcome? LastRefresh { get; private set; }

    public EntitlementService(
        EntitlementStore store,
        Func<AppSettings> settings,
        EntitlementClient? client = null,
        IReadOnlyList<string>? publicKeys = null,
        Action<string>? log = null,
        ChangeLogRepository? changeLog = null,
        string? bundlePath = null)
    {
        // Injectable for the same reason the logger is: a test that wrote a
        // bundle into the live profile folder would be replacing a merchant's
        // menu, which is the defect this project has already had twice.
        _bundlePath = bundlePath ?? LocalPaths.CloudBundlePath;
        _store = store;
        _changes = changeLog;
        _settings = settings;
        _client = client ?? new EntitlementClient();
        _publicKeys = publicKeys ?? EntitlementKeys.Production;
        _log = log ?? AppLog.For("cloud");

        Current = Resolve();
    }

    /// <summary>This installation's identity. Shown in diagnostics; support will ask for it.</summary>
    public string DeviceId => _store.DeviceId();

    /// <summary>
    /// Ask the cloud, unless it was asked recently. Returns immediately; the
    /// work happens on a background task and its failure is swallowed on
    /// purpose.
    /// </summary>
    public void RefreshInBackground(bool force = false)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await RefreshAsync(force);
            }
            catch (Exception ex)
            {
                // Belt and braces. EntitlementClient does not throw, but a fault
                // reaching here must still not be able to take down a till.
                _log($"entitlement refresh faulted: {ex.Message}");
            }
        });
    }

    /// <summary>The refresh itself, awaitable so a test does not have to sleep.</summary>
    public async Task<RefreshOutcome> RefreshAsync(bool force = false, CancellationToken ct = default)
    {
        var now = DateTimeOffset.Now;

        // Two reasons to go: the daily entitlement, or a change log waiting to
        // leave the machine. Nothing pending and nothing due means no request at
        // all — a quiet shop calls once a day, not every five minutes.
        var pending = Pending();
        var due = EntitlementPolicy.ShouldRefresh(_store.LastRefreshAttempt(), now)
                  || EntitlementPolicy.ShouldSendLog(_store.LastRefreshAttempt(), now, pending.Count > 0);

        if (!force && !due) return RefreshOutcome.NotConfigured;

        var settings = _settings();

        Configure(settings, pending);

        var result = await _client.RefreshAsync(ct);
        LastRefresh = result.Outcome;

        // Recorded whatever happened, so an unreachable service is asked once a
        // day rather than at every restart during a long outage.
        _store.RecordRefreshAttempt(now);

        switch (result.Outcome)
        {
            case RefreshOutcome.Fetched:
                if (!string.IsNullOrWhiteSpace(result.DeviceSecret))
                    _store.SaveDeviceSecret(result.DeviceSecret);

                _store.SaveToken(result.Token!);
                Reresolve();
                AdvanceLog(result, pending.Count);
                await StageBundleIfNewAsync(result.BundleVersion, ct);
                break;

            case RefreshOutcome.ClientTooOld:
                _log($"{result.Detail}. Trading continues on the stored entitlement; the updater will deal with it.");
                break;

            case RefreshOutcome.Rejected:
                _log($"entitlement refused: {result.Detail}");
                break;

            case RefreshOutcome.Unreachable:
                _log($"entitlement service unreachable ({result.Detail}) — using what is stored");
                break;
        }

        return result.Outcome;
    }

    /// <summary>
    /// Activate this till with a code somebody typed in Settings.
    /// <para>
    /// Separate from <see cref="RefreshAsync"/> because a person is standing
    /// there waiting for an answer: this one is allowed to be slow, is not
    /// throttled, and reports what happened rather than swallowing it.
    /// </para>
    /// </summary>
    public async Task<RefreshResult> ActivateAsync(string code, CancellationToken ct = default)
    {
        var settings = _settings();
        settings.CloudActivationCode = code;
        Configure(settings);

        var result = await _client.ActivateAsync(ct);
        LastRefresh = result.Outcome;
        _store.RecordRefreshAttempt(DateTimeOffset.Now);

        if (result.Outcome != RefreshOutcome.Fetched)
        {
            _log($"activation failed: {result.Detail}");
            return result;
        }

        if (!string.IsNullOrWhiteSpace(result.DeviceSecret))
            _store.SaveDeviceSecret(result.DeviceSecret);

        _store.SaveToken(result.Token!);
        Reresolve();

        _log($"activated against {result.ShopId ?? "the cloud"}");
        return result;
    }

    private void Configure(AppSettings settings, IReadOnlyList<ChangeEntry>? entries = null) =>
        _client.Configure(new EntitlementClientOptions
        {
            BaseUrl = CloudEndpoint.Resolve(settings.CloudBaseUrl),
            DeviceId = _store.DeviceId(),
            DeviceSecret = _store.DeviceSecret(),
            ActivationCode = settings.CloudActivationCode,
            ClientVersion = ClientVersion,
            Entries = entries ?? [],
        });

    private IReadOnlyList<ChangeEntry> Pending() =>
        _changes is null ? [] : _changes.Since(_changes.SyncedThrough(), EntitlementPolicy.LogBatchSize);

    /// <summary>
    /// Moves the watermark to what the cloud says it stored — never to what was
    /// sent.
    /// <para>
    /// A lost answer must cost a re-send rather than leave a gap, and an entry
    /// the cloud refused has to be offered again: it is evidence, and we would
    /// rather hold it twice than not at all.
    /// </para>
    /// </summary>
    private void AdvanceLog(RefreshResult result, int sent)
    {
        if (_changes is null) return;

        if (result.LogError is { Length: > 0 } problem)
        {
            // Said out loud, once per attempt. This is the alarm the whole chain
            // exists to be able to raise.
            _log($"the cloud will not accept this till's change log: {problem}");
            return;
        }

        if (result.SyncedThrough is { } through && through > _changes.SyncedThrough())
        {
            _changes.RecordSynced(through);
            if (sent > 0) _log($"sent {sent} change-log entries, cloud holds through {through}");
        }
    }

    private void Reresolve()
    {
        var before = Current;
        Current = Resolve();

        if (before != Current)
            Changed?.Invoke(this, EventArgs.Empty);
    }

    private EntitlementState Resolve()
    {
        var settings = _settings();
        var deviceId = _store.DeviceId();

        var verification = EntitlementToken.Verify(_store.Token(), _publicKeys);
        LastTokenProblem = verification.Problem;

        // A token that verifies but names another machine is one this till can
        // never use — a disk image taken from another shop looks exactly like
        // this. Dropping it stops the same pointless comparison happening at
        // every start, and stops support reading a shop id that is not theirs.
        if (verification.Entitlement is { } token && !token.CoversDevice(deviceId))
        {
            _log($"stored entitlement belongs to another device ({token.DeviceId}) — discarded");
            _store.ClearToken();
            verification = new TokenVerification(null, TokenProblem.Missing);
        }

        return EntitlementPolicy.Resolve(
            verification.Entitlement,
            settings.Edition,
            deviceId,
            DateTimeOffset.Now);
    }

    /// <summary>
    /// Downloads the shop's bundle when the cloud is offering a version this
    /// till has not applied, and leaves it in <c>profile/</c>.
    /// <para>
    /// <b>It is not applied here.</b> A bundle replaces the whole catalogue, and
    /// doing that while somebody is ringing a sale would take the dishes out
    /// from under their fingers. It goes in at the next start, the same way a
    /// restore does — and a till gets restarted far more often than a menu
    /// changes.
    /// </para>
    /// </summary>
    private async Task StageBundleIfNewAsync(string? offered, CancellationToken ct)
    {
        if (_store is null || string.IsNullOrWhiteSpace(offered)) return;

        // Compared against what is on disk as well as what has been applied: a
        // download interrupted halfway through must be tried again, not skipped
        // because its version was already written down.
        if (offered == _store.AppliedBundleVersion() && offered == _store.DownloadedBundleVersion()) return;
        if (offered == _store.DownloadedBundleVersion() && File.Exists(_bundlePath)) return;

        var (bundle, version) = await _client.FetchBundleAsync(ct);
        if (string.IsNullOrWhiteSpace(bundle) || string.IsNullOrWhiteSpace(version)) return;

        try
        {
            await File.WriteAllTextAsync(_bundlePath, bundle, ct);
            _store.RecordBundleDownloaded(version);
            _log($"a new shop bundle ({version}) is ready and goes in at the next start");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A profile folder that cannot be written is a setup problem, not a
            // reason to stop: the till carries on with the catalogue it has.
            _log($"could not save the new bundle: {ex.Message}");
        }
    }

    /// <summary>
    /// Asks again on a timer, because nothing else does.
    /// <para>
    /// Until this existed the entitlement was fetched once at startup, so a till
    /// left running for a week never refreshed and never sent a thing. The tick
    /// is short and <see cref="RefreshAsync"/> throttles itself, so most of them
    /// do nothing at all.
    /// </para>
    /// </summary>
    public void StartPeriodicRefresh()
    {
        _timer?.Dispose();
        _timer = new Timer(
            _ => RefreshInBackground(),
            null,
            EntitlementPolicy.LogInterval,
            EntitlementPolicy.LogInterval);
    }

    public void Dispose()
    {
        _timer?.Dispose();
        _timer = null;
    }

    private Timer? _timer;

    private static string ClientVersion =>
        Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0.0.0";
}
