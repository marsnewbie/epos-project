using System.Net;
using System.Text.Json;
using RingOrder.Epos.Data;
using RingOrder.Epos.Domain;
using RingOrder.Epos.Online;
using RingOrder.Epos.Services;
using Xunit;

namespace RingOrder.Epos.Tests;

/// <summary>
/// The identity a till keeps, and the habit of asking the cloud about it.
/// <para>
/// Everything here runs against a temporary database and a fake transport. No
/// test in this file may touch <see cref="LocalPaths"/> or open a socket — the
/// service is constructed with its own logger for the same reason.
/// </para>
/// </summary>
public class EntitlementServiceTests : IDisposable
{
    private readonly string _dbPath =
        Path.Combine(Path.GetTempPath(), $"ringorder-test-{Guid.NewGuid():N}.sqlite");
    private readonly EposDb _db;
    private readonly EntitlementStore _store;
    private readonly List<string> _log = [];

    public EntitlementServiceTests()
    {
        _db = new EposDb(_dbPath);
        _db.Migrate();
        _store = new EntitlementStore(_db);
    }

    private static string FixtureDir =>
        Path.Combine(AppContext.BaseDirectory, "fixtures", "entitlement");

    private static string Fixture(string name) =>
        File.ReadAllText(Path.Combine(FixtureDir, $"{name}.token")).Trim();

    private static IReadOnlyList<string> DevKeys =>
        [string.Concat(File.ReadAllLines(Path.Combine(FixtureDir, "dev-public.pem"))
            .Where(l => !l.StartsWith("-----")).Select(l => l.Trim()))];

    private EntitlementService Service(
        AppSettings? settings = null,
        EntitlementClient? client = null) =>
        new(_store,
            () => settings ?? new AppSettings(),
            client,
            DevKeys,
            _log.Add);

    // ---- identity ----------------------------------------------------------

    /// <summary>
    /// The identity has to survive a reinstall, which is why it lives in the
    /// database under %PROGRAMDATA% rather than beside the executable. A shop
    /// that repairs its installation must not have to be reactivated.
    /// </summary>
    [Fact]
    public void The_device_identity_is_created_once_and_then_kept()
    {
        var first = _store.DeviceId();

        Assert.NotEmpty(first);
        Assert.Equal(first, _store.DeviceId());
        Assert.Equal(first, new EntitlementStore(_db).DeviceId());
    }

    [Fact]
    public void Two_installations_do_not_share_an_identity()
    {
        var otherPath = Path.Combine(Path.GetTempPath(), $"ringorder-test-{Guid.NewGuid():N}.sqlite");
        try
        {
            using var otherDb = new EposDb(otherPath);
            otherDb.Migrate();

            Assert.NotEqual(_store.DeviceId(), new EntitlementStore(otherDb).DeviceId());
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            File.Delete(otherPath);
        }
    }

    /// <summary>
    /// The device secret opens the door, unlike the token which only proves what
    /// we already told the shop. It goes to disk the way the website password
    /// does — and the exposure being closed is the nightly backup leaving the
    /// premises, not somebody sitting at the till.
    /// </summary>
    [Fact]
    public void The_device_secret_is_not_written_in_the_clear()
    {
        _store.SaveDeviceSecret("s3cr3t-value");

        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT value FROM settings WHERE key='cloud.device-secret'";
        var stored = cmd.ExecuteScalar() as string ?? "";

        Assert.DoesNotContain("s3cr3t-value", stored);
        Assert.Equal("s3cr3t-value", _store.DeviceSecret());
    }

    // ---- resolving without a network ---------------------------------------

    [Fact]
    public void A_till_that_has_never_synced_resolves_from_its_bundle()
    {
        var service = Service(new AppSettings { Edition = ShopEdition.Print });

        Assert.Equal(EntitlementSource.Bundle, service.Current.Source);
        Assert.True(service.Current.IsPrintOnly);
        Assert.Equal(TokenProblem.Missing, service.LastTokenProblem);
    }

    /// <summary>
    /// A machine restored from another shop's disk image carries a token it can
    /// never use. Dropping it stops support reading a shop id that is not
    /// theirs, and stops the same comparison being made at every start.
    /// </summary>
    [Fact]
    public void A_token_belonging_to_another_machine_is_thrown_away()
    {
        _store.SaveToken(Fixture("other-device"));

        var service = Service(new AppSettings { Edition = ShopEdition.Print });

        Assert.Null(_store.Token());
        Assert.Equal(EntitlementSource.Bundle, service.Current.Source);
        Assert.Contains(_log, l => l.Contains("another device"));
    }

    // ---- asking the cloud ---------------------------------------------------

    [Fact]
    public async Task With_no_cloud_configured_nothing_is_attempted()
    {
        var service = Service();

        Assert.Equal(RefreshOutcome.NotConfigured, await service.RefreshAsync(force: true));
    }

    /// <summary>
    /// The ordinary failure in a takeaway: the router is off, or somebody is
    /// streaming football. It must be invisible — the till carries on with what
    /// it has and says nothing on screen.
    /// </summary>
    [Fact]
    public async Task An_unreachable_service_changes_nothing_and_does_not_throw()
    {
        var settings = Configured();
        _store.SaveDeviceSecret("existing");
        var service = Service(settings, ClientThat(_ => throw new HttpRequestException("no route to host")));

        var before = service.Current;

        Assert.Equal(RefreshOutcome.Unreachable, await service.RefreshAsync(force: true));
        Assert.Equal(before, service.Current);
        Assert.True(service.Current.IsPrintOnly);
    }

    [Fact]
    public async Task A_fetched_token_takes_effect_and_is_kept()
    {
        var settings = Configured();
        _store.SaveDeviceSecret("existing");
        var deviceId = _store.DeviceId();
        var token = SignedFor(deviceId, ShopEdition.Pos, terminals: 3);

        var service = Service(settings, ClientThat(_ => Json($$"""{"token":"{{token}}"}""")));

        var raised = 0;
        service.Changed += (_, _) => raised++;

        Assert.Equal(RefreshOutcome.Fetched, await service.RefreshAsync(force: true));
        Assert.Equal(EntitlementSource.Token, service.Current.Source);
        Assert.False(service.Current.IsPrintOnly);   // the token beat the bundle
        Assert.Equal(3, service.Current.Terminals);
        Assert.Equal(token, _store.Token());
        Assert.Equal(1, raised);
    }

    /// <summary>
    /// The event exists so a banner can appear or clear. Raising it when nothing
    /// moved would have every screen redrawing itself once a day for no reason —
    /// which is what happens if the state's feature list is compared by
    /// reference, so it is pinned here as well as in the domain tests.
    /// </summary>
    [Fact]
    public async Task Fetching_the_same_answer_twice_announces_nothing_the_second_time()
    {
        var settings = Configured();
        _store.SaveDeviceSecret("existing");
        var token = SignedFor(_store.DeviceId(), ShopEdition.Pos, terminals: 1, features: ["drivers"]);
        var service = Service(settings, ClientThat(_ => Json($$"""{"token":"{{token}}"}""")));

        var raised = 0;
        service.Changed += (_, _) => raised++;

        await service.RefreshAsync(force: true);
        await service.RefreshAsync(force: true);

        Assert.Equal(1, raised);
    }

    /// <summary>
    /// A till given a cloud address but no credentials asks nothing and says
    /// nothing. That is a shop provisioned without a cloud key — the ordinary
    /// state today, not a fault, so it must not produce a log line every day for
    /// the rest of its life.
    /// </summary>
    [Fact]
    public async Task A_cloud_address_without_credentials_attempts_nothing()
    {
        var reached = false;
        var service = Service(Configured(), ClientThat(_ =>
        {
            reached = true;
            return Json("{}");
        }));

        Assert.Equal(RefreshOutcome.NotConfigured, await service.RefreshAsync(force: true));
        Assert.False(reached);
        Assert.Empty(_log);
    }

    [Fact]
    public async Task An_activation_keeps_the_secret_it_is_given()
    {
        var settings = Configured();
        settings.CloudActivationCode = "one-time-key";
        var token = SignedFor(_store.DeviceId(), ShopEdition.Pos);

        var service = Service(settings,
            ClientThat(_ => Json($$"""{"token":"{{token}}","deviceSecret":"issued-secret"}""")));

        Assert.Equal(RefreshOutcome.Fetched, await service.RefreshAsync(force: true));
        Assert.Equal("issued-secret", _store.DeviceSecret());
    }

    /// <summary>
    /// Too old to sync is never too old to trade. The till keeps selling on what
    /// it has and the updater deals with it; nobody telephones a merchant.
    /// </summary>
    [Fact]
    public async Task A_build_the_service_will_not_talk_to_keeps_trading()
    {
        var settings = Configured();
        _store.SaveDeviceSecret("existing");

        var service = Service(settings,
            ClientThat(_ => new HttpResponseMessage(HttpStatusCode.UpgradeRequired)));

        Assert.Equal(RefreshOutcome.ClientTooOld, await service.RefreshAsync(force: true));
        Assert.Equal(ShopEdition.Print, service.Current.Edition);   // still open for business
    }

    [Fact]
    public async Task A_device_the_service_does_not_know_is_reported_not_retried()
    {
        var settings = Configured();
        _store.SaveDeviceSecret("stale");

        var service = Service(settings,
            ClientThat(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized)));

        Assert.Equal(RefreshOutcome.Rejected, await service.RefreshAsync(force: true));
        Assert.Contains(_log, l => l.Contains("refused"));
    }

    /// <summary>
    /// A shop offline for a fortnight should make fourteen attempts, not one
    /// every time somebody restarts the till mid-service.
    /// </summary>
    [Fact]
    public async Task An_attempt_just_made_is_not_made_again()
    {
        var settings = Configured();
        _store.SaveDeviceSecret("existing");
        var calls = 0;
        var service = Service(settings, ClientThat(_ =>
        {
            calls++;
            throw new HttpRequestException("down");
        }));

        await service.RefreshAsync(force: true);
        await service.RefreshAsync();      // not forced — should be throttled

        Assert.Equal(1, calls);
    }

    // ---- plumbing -----------------------------------------------------------

    private static AppSettings Configured() => new()
    {
        Edition = ShopEdition.Print,
        CloudBaseUrl = "https://cloud.example.invalid",
        ShopSlug = "demo-shop",
    };

    private string SignedFor(
        string deviceId, string edition, int terminals = 1, IReadOnlyList<string>? features = null)
    {
        var pem = File.ReadAllText(Path.Combine(FixtureDir, "dev-private.pem"));
        using var key = System.Security.Cryptography.ECDsa.Create();
        key.ImportFromPem(pem);

        return EntitlementToken.Sign(
            new Entitlement("demo-shop", deviceId, edition, features ?? [], terminals,
                DateTimeOffset.Now.AddMinutes(-1), DateTimeOffset.Now.AddDays(30)),
            key);
    }

    private static HttpResponseMessage Json(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };

    private static EntitlementClient ClientThat(Func<HttpRequestMessage, HttpResponseMessage> respond) =>
        new(new HttpClient(new StubHandler(respond)));

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(respond(request));
    }

    public void Dispose()
    {
        _db.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { File.Delete(_dbPath); } catch { /* the temp folder can keep it */ }
        GC.SuppressFinalize(this);
    }

    // ---- the wire, as the service reads it ---------------------------------

    /// <summary>
    /// The field names the service actually looks for, asserted rather than
    /// assumed.
    /// <para>
    /// <c>PostAsJsonAsync</c> serialises with web defaults, which happen to be
    /// camelCase — and "happen to be" is not a contract. Somebody passing
    /// explicit options, or a future default changing, would send
    /// <c>ShopId</c> to a service reading <c>shopId</c>, and every till in the
    /// field would start being refused for a reason no log would explain.
    /// </para>
    /// <para>
    /// The names here must match `cloud/src/routes.ts`.
    /// </para>
    /// </summary>
    [Fact]
    public async Task The_body_uses_the_field_names_the_service_reads()
    {
        var settings = Configured();
        _store.SaveDeviceSecret("existing");

        string? path = null;
        string? body = null;

        var service = Service(settings, ClientThat(request =>
        {
            path = request.RequestUri!.AbsolutePath;
            body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return Json("""{"token":""}""");
        }));

        await service.RefreshAsync(force: true);

        Assert.Equal("/v1/sync", path);
        using var sent = JsonDocument.Parse(body!);

        Assert.Equal(_store.DeviceId(), sent.RootElement.GetProperty("deviceId").GetString());
        Assert.Equal("existing", sent.RootElement.GetProperty("deviceSecret").GetString());
        Assert.True(sent.RootElement.TryGetProperty("clientVersion", out _));
    }

    [Fact]
    public async Task An_activation_posts_its_key_to_the_activation_endpoint()
    {
        var settings = Configured();
        settings.CloudActivationCode = "one-time-key";

        string? path = null;
        string? body = null;

        var service = Service(settings, ClientThat(request =>
        {
            path = request.RequestUri!.AbsolutePath;
            body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return Json("""{"token":""}""");
        }));

        await service.RefreshAsync(force: true);

        Assert.Equal("/v1/activate", path);
        using var sent = JsonDocument.Parse(body!);
        Assert.Equal("one-time-key", sent.RootElement.GetProperty("activationCode").GetString());

        // A device secret it does not have must not be sent as null — the
        // service reads a missing field and an explicit null differently.
        Assert.False(sent.RootElement.TryGetProperty("deviceSecret", out _));
    }

    /// <summary>
    /// The two names the till reads out of the answer. Same reasoning as the
    /// request: `cloud/src/routes.ts` writes these.
    /// </summary>
    [Fact]
    public async Task The_answer_is_read_by_the_names_the_service_writes()
    {
        var settings = Configured();
        settings.CloudActivationCode = "one-time-key";
        var token = SignedFor(_store.DeviceId(), ShopEdition.Pos);

        var service = Service(settings,
            ClientThat(_ => Json($$"""{"token":"{{token}}","deviceSecret":"issued"}""")));

        Assert.Equal(RefreshOutcome.Fetched, await service.RefreshAsync(force: true));
        Assert.Equal(token, _store.Token());
        Assert.Equal("issued", _store.DeviceSecret());
    }

    /// <summary>
    /// Asked once. A merchant who skipped is trading, and a prompt every morning
    /// teaches them to dismiss it — the shop showing no tills on our own estate
    /// page reaches the person who can actually act.
    /// </summary>
    [Fact]
    public void The_setup_screen_is_offered_once_and_then_remembered()
    {
        Assert.False(_store.SetupOffered());

        _store.RecordSetupOffered();

        Assert.True(_store.SetupOffered());
        Assert.True(new EntitlementStore(_db).SetupOffered());
    }
}
