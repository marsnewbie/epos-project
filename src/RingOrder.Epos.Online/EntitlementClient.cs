using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace RingOrder.Epos.Online;

/// <summary>Where the cloud is, and what this till calls itself to it.</summary>
public sealed class EntitlementClientOptions
{
    /// <summary>
    /// Where the service is. Defaults to the address compiled into
    /// <see cref="CloudEndpoint"/>; a shop only overrides it to reach a staging
    /// service.
    /// </summary>
    public string BaseUrl { get; set; } = CloudEndpoint.Default;

    public string DeviceId { get; set; } = "";
    public string? DeviceSecret { get; set; }

    /// <summary>
    /// The short code somebody typed in Settings.
    /// <para>
    /// It is the whole credential: it says which shop this is <em>and</em>
    /// authorises the enrolment. That is what lets a person activate a till by
    /// typing eight characters rather than by editing a file.
    /// </para>
    /// </summary>
    public string? ActivationCode { get; set; }

    /// <summary>
    /// Sent on every request so the service can see what is actually installed
    /// out there. This is what eventually makes it safe to delete old server
    /// code, and it costs one header.
    /// </summary>
    public string ClientVersion { get; set; } = "";

    /// <summary>Whether there is somewhere to ask. False only if an override is set to rubbish.</summary>
    public bool IsConfigured => !string.IsNullOrWhiteSpace(BaseUrl);
}

/// <summary>What a refresh attempt did. Never an exception — startup does not get to fail here.</summary>
public enum RefreshOutcome
{
    /// <summary>A new token was fetched and stored.</summary>
    Fetched,

    /// <summary>The service is not configured, so nothing was attempted.</summary>
    NotConfigured,

    /// <summary>Unreachable, timed out, or answered with a server error. Ordinary and invisible.</summary>
    Unreachable,

    /// <summary>The service does not recognise this device. Needs a person, not a retry.</summary>
    Rejected,

    /// <summary>
    /// This build is too old for the service to talk to. The till carries on
    /// trading on its cached entitlement and updates itself.
    /// </summary>
    ClientTooOld,
}

/// <param name="Outcome">What happened.</param>
/// <param name="Token">The new token, when one was fetched.</param>
/// <param name="DeviceSecret">Set only by an activation, which issues one.</param>
/// <param name="Detail">One line for the log.</param>
/// <param name="ShopId">Which shop an activation turned out to be for.</param>
public sealed record RefreshResult(
    RefreshOutcome Outcome,
    string? Token = null,
    string? DeviceSecret = null,
    string Detail = "",
    string? ShopId = null);

/// <summary>
/// Talks to the entitlement service.
/// <para>
/// Every method returns a <see cref="RefreshResult"/> and none of them throw.
/// This runs on a startup path that must not be able to stop a till opening, so
/// an exception escaping here would be the defect, not the network fault that
/// caused it.
/// </para>
/// </summary>
public sealed class EntitlementClient
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private readonly HttpClient _http;
    private EntitlementClientOptions _options = new();

    public EntitlementClient(HttpClient? http = null)
    {
        _http = http ?? new HttpClient();
        _http.Timeout = Timeout;
    }

    public void Configure(EntitlementClientOptions options) => _options = options;

    /// <summary>
    /// Fetch a fresh token, activating first if this device has never been seen.
    /// <para>
    /// The endpoint is <c>v1/sync</c> rather than <c>v1/entitlement</c> because
    /// it is the one call a till makes on a schedule, and order ingest and the
    /// change log arrive in this same answer as additional fields — see
    /// docs/CLOUD.md. Unknown fields are ignored, so they can be added without
    /// breaking anything already installed.
    /// </para>
    /// </summary>
    public async Task<RefreshResult> RefreshAsync(CancellationToken ct = default)
    {
        if (!_options.IsConfigured)
            return new RefreshResult(RefreshOutcome.NotConfigured, Detail: "no cloud URL configured");

        if (string.IsNullOrWhiteSpace(_options.DeviceSecret))
            return await ActivateAsync(ct);

        return await PostAsync(
            "v1/sync",
            new EntitlementRequest
            {
                DeviceId = _options.DeviceId,
                DeviceSecret = _options.DeviceSecret,
                ClientVersion = _options.ClientVersion,
            },
            ct);
    }

    /// <summary>
    /// Exchange the typed code for a device secret and a first token. Runs once
    /// per installation, and again if the answer was lost on the way back.
    /// </summary>
    public async Task<RefreshResult> ActivateAsync(CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_options.ActivationCode))
            return new RefreshResult(RefreshOutcome.NotConfigured, Detail: "no activation code");

        return await PostAsync(
            "v1/activate",
            new EntitlementRequest
            {
                DeviceId = _options.DeviceId,
                ActivationCode = _options.ActivationCode,
                ClientVersion = _options.ClientVersion,
            },
            ct);
    }

    private async Task<RefreshResult> PostAsync(string path, EntitlementRequest body, CancellationToken ct)
    {
        try
        {
            var url = $"{_options.BaseUrl.TrimEnd('/')}/{path}";
            using var response = await _http.PostAsJsonAsync(url, body, ct);

            // 426 is "upgrade required": the service will not speak this
            // version. The till keeps trading on what it has and the updater
            // deals with it — nobody telephones a merchant.
            if (response.StatusCode == HttpStatusCode.UpgradeRequired)
                return new RefreshResult(RefreshOutcome.ClientTooOld, Detail: $"service requires a newer till than {body.ClientVersion}");

            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.NotFound)
                return new RefreshResult(RefreshOutcome.Rejected, Detail: $"{(int)response.StatusCode} from {path}");

            if (!response.IsSuccessStatusCode)
                return new RefreshResult(RefreshOutcome.Unreachable, Detail: $"{(int)response.StatusCode} from {path}");

            var payload = await response.Content.ReadFromJsonAsync<EntitlementResponse>(ct);

            if (payload is null || string.IsNullOrWhiteSpace(payload.Token))
                return new RefreshResult(RefreshOutcome.Unreachable, Detail: "no token in the answer");

            return new RefreshResult(
                RefreshOutcome.Fetched,
                payload.Token,
                payload.DeviceSecret,
                $"token fetched from {path}",
                payload.ShopId);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException)
        {
            // The ordinary case in a takeaway: the router is off, the line is
            // down, or somebody is streaming football. Nothing to report to
            // anyone and nothing that should be visible on screen.
            return new RefreshResult(RefreshOutcome.Unreachable, Detail: ex.Message);
        }
    }

    private sealed class EntitlementRequest
    {
        public string DeviceId { get; set; } = "";

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? DeviceSecret { get; set; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? ActivationCode { get; set; }

        public string ClientVersion { get; set; } = "";
    }

    private sealed class EntitlementResponse
    {
        public string? Token { get; set; }
        public string? DeviceSecret { get; set; }

        /// <summary>Which shop the code turned out to belong to, so the screen can name it.</summary>
        public string? ShopId { get; set; }
    }
}
