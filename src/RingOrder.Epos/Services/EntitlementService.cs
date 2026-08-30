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
public sealed class EntitlementService
{
    private readonly EntitlementStore _store;
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
        Action<string>? log = null)
    {
        _store = store;
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

        if (!force && !EntitlementPolicy.ShouldRefresh(_store.LastRefreshAttempt(), now))
            return RefreshOutcome.NotConfigured;

        var settings = _settings();

        _client.Configure(new EntitlementClientOptions
        {
            BaseUrl = settings.CloudBaseUrl,
            ShopId = settings.ShopSlug,
            DeviceId = _store.DeviceId(),
            DeviceSecret = _store.DeviceSecret(),
            ActivationKey = settings.CloudActivationKey,
            ClientVersion = ClientVersion,
        });

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

    private static string ClientVersion =>
        Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0.0.0";
}
