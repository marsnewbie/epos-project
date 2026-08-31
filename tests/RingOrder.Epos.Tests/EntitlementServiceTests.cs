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

    private readonly string _bundlePath =
        Path.Combine(Path.GetTempPath(), $"ringorder-bundle-{Guid.NewGuid():N}.json");

    private EntitlementService Service(
        AppSettings? settings = null,
        EntitlementClient? client = null,
        ChangeLogRepository? changeLog = null) =>
        new(_store,
            () => settings ?? new AppSettings(),
            client,
            DevKeys,
            _log.Add,
            changeLog,
            _bundlePath);

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
        try { File.Delete(_bundlePath); } catch { /* same */ }
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

    // ---- the change log riding on the same call ------------------------------

    private ChangeLogRepository Changes() => new(_db);

    private static ChangeDraft Entry(string entityId) =>
        new(Guid.NewGuid().ToString("n"), "till-a", ChangeEntity.Order, entityId,
            ChangeOp.Placed, """{"totalPence":1250}""", DateTimeOffset.Now, "wei");

    /// <summary>
    /// They go on the entitlement's call rather than one of their own — the pipe
    /// docs/CLOUD.md described: one question a till asks on a schedule, with
    /// everything else joining that answer.
    /// </summary>
    [Fact]
    public async Task Pending_entries_ride_on_the_same_call()
    {
        var settings = Configured();
        _store.SaveDeviceSecret("existing");
        var changes = Changes();
        changes.Append(Entry("order-1"));
        changes.Append(Entry("order-2"));

        string? body = null;
        var token = SignedFor(_store.DeviceId(), ShopEdition.Pos);
        var service = Service(settings, ClientThat(request =>
        {
            body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return Json($$"""{"token":"{{token}}","syncedThrough":2}""");
        }), changes);

        Assert.Equal(RefreshOutcome.Fetched, await service.RefreshAsync(force: true));

        using var sent = JsonDocument.Parse(body!);
        var entries = sent.RootElement.GetProperty("entries");
        Assert.Equal(2, entries.GetArrayLength());
        Assert.Equal(1, entries[0].GetProperty("seq").GetInt64());
        Assert.Equal(ChangeChain.Genesis, entries[0].GetProperty("prevHash").GetString());
    }

    /// <summary>
    /// The watermark follows what the cloud says it stored, never what was sent.
    /// A lost answer must cost a re-send rather than leave a gap.
    /// </summary>
    [Fact]
    public async Task The_watermark_follows_the_cloud_not_the_send()
    {
        var settings = Configured();
        _store.SaveDeviceSecret("existing");
        var changes = Changes();
        for (var i = 0; i < 5; i++) changes.Append(Entry($"order-{i}"));

        var token = SignedFor(_store.DeviceId(), ShopEdition.Pos);
        var service = Service(settings,
            ClientThat(_ => Json($$"""{"token":"{{token}}","syncedThrough":3}""")), changes);

        await service.RefreshAsync(force: true);

        Assert.Equal(3, changes.SyncedThrough());
        Assert.Equal(2, changes.Since(changes.SyncedThrough()).Count);
    }

    /// <summary>
    /// The alarm this whole chain exists to be able to raise. The entitlement
    /// still arrived — a broken log is ours to look at, not a reason to stop a
    /// shop trading.
    /// </summary>
    [Fact]
    public async Task A_refused_log_is_said_out_loud_and_keeps_its_entries()
    {
        var settings = Configured();
        _store.SaveDeviceSecret("existing");
        var changes = Changes();
        changes.Append(Entry("order-1"));

        var token = SignedFor(_store.DeviceId(), ShopEdition.Pos);
        var service = Service(settings, ClientThat(_ =>
            Json($$"""{"token":"{{token}}","syncedThrough":0,"logError":"entries are missing"}""")), changes);

        Assert.Equal(RefreshOutcome.Fetched, await service.RefreshAsync(force: true));

        Assert.Equal(0, changes.SyncedThrough());
        Assert.Single(changes.Since(0));
        Assert.Contains(_log, l => l.Contains("will not accept"));
        Assert.Equal(EntitlementSource.Token, service.Current.Source);
    }

    /// <summary>
    /// A quiet shop that took no orders makes one call a day, not two hundred
    /// and eighty-eight empty ones.
    /// </summary>
    [Fact]
    public async Task Nothing_pending_sends_no_entries_field_at_all()
    {
        var settings = Configured();
        _store.SaveDeviceSecret("existing");

        string? body = null;
        var token = SignedFor(_store.DeviceId(), ShopEdition.Pos);
        var service = Service(settings, ClientThat(request =>
        {
            body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return Json($$"""{"token":"{{token}}"}""");
        }), Changes());

        await service.RefreshAsync(force: true);

        using var sent = JsonDocument.Parse(body!);
        Assert.False(sent.RootElement.TryGetProperty("entries", out _));
    }

    /// <summary>
    /// Two reasons to go: the daily entitlement, or a log waiting to leave. With
    /// neither, nothing happens — which is what keeps the five-minute tick from
    /// becoming five-minute traffic.
    /// </summary>
    [Fact]
    public async Task A_recent_attempt_with_nothing_pending_makes_no_request()
    {
        var settings = Configured();
        _store.SaveDeviceSecret("existing");
        _store.RecordRefreshAttempt(DateTimeOffset.Now);

        var called = false;
        var service = Service(settings, ClientThat(_ =>
        {
            called = true;
            return Json("{}");
        }), Changes());

        await service.RefreshAsync();

        Assert.False(called);
    }

    [Fact]
    public async Task A_recent_attempt_with_a_backlog_goes_anyway()
    {
        var settings = Configured();
        _store.SaveDeviceSecret("existing");
        _store.RecordRefreshAttempt(DateTimeOffset.Now - EntitlementPolicy.LogInterval);

        var changes = Changes();
        changes.Append(Entry("order-1"));

        var called = false;
        var token = SignedFor(_store.DeviceId(), ShopEdition.Pos);
        var service = Service(settings, ClientThat(_ =>
        {
            called = true;
            return Json($$"""{"token":"{{token}}","syncedThrough":1}""");
        }), changes);

        await service.RefreshAsync();

        Assert.True(called);
        Assert.Equal(1, changes.SyncedThrough());
    }

    // ---- the shop bundle arriving on its own ---------------------------------

    private const string Menu = """{"shop":{"slug":"demo"},"menu":{"items":[]}}""";

    /// <summary>
    /// The step this replaces: somebody copying a JSON file onto the machine.
    /// It lands on disk here and goes in at the next start — never mid-service,
    /// because a bundle replaces the whole catalogue.
    /// </summary>
    [Fact]
    public async Task A_new_menu_is_downloaded_and_left_for_the_next_start()
    {
        var settings = Configured();
        _store.SaveDeviceSecret("existing");
        var token = SignedFor(_store.DeviceId(), ShopEdition.Pos);

        var service = Service(settings, ClientThat(request =>
            request.RequestUri!.AbsolutePath == "/v1/bundle"
                ? Json($$"""{"bundle":{{JsonSerializer.Serialize(Menu)}},"bundleVersion":"abc123"}""")
                : Json($$"""{"token":"{{token}}","bundleVersion":"abc123"}""")));

        await service.RefreshAsync(force: true);

        Assert.Equal(Menu, await File.ReadAllTextAsync(_bundlePath));
        Assert.Equal("abc123", _store.DownloadedBundleVersion());

        // Not applied. That happens at startup, where nobody is mid-sale.
        Assert.Null(_store.AppliedBundleVersion());
        Assert.Contains(_log, l => l.Contains("next start"));
    }

    /// <summary>
    /// Most syncs carry a version the till already has. Fetching the menu each
    /// time would send a shop's whole catalogue down the wire every five minutes.
    /// </summary>
    [Fact]
    public async Task A_version_already_applied_downloads_nothing()
    {
        var settings = Configured();
        _store.SaveDeviceSecret("existing");
        _store.RecordBundleApplied("abc123");
        _store.RecordBundleDownloaded("abc123");
        var token = SignedFor(_store.DeviceId(), ShopEdition.Pos);

        var asked = false;
        var service = Service(settings, ClientThat(request =>
        {
            if (request.RequestUri!.AbsolutePath == "/v1/bundle") asked = true;
            return Json($$"""{"token":"{{token}}","bundleVersion":"abc123"}""");
        }));

        await service.RefreshAsync(force: true);

        Assert.False(asked);
    }

    /// <summary>
    /// A download interrupted halfway through must be tried again, not skipped
    /// because its version was already written down.
    /// </summary>
    [Fact]
    public async Task A_version_noted_but_missing_from_disk_is_fetched_again()
    {
        var settings = Configured();
        _store.SaveDeviceSecret("existing");
        _store.RecordBundleDownloaded("abc123");
        var token = SignedFor(_store.DeviceId(), ShopEdition.Pos);

        var service = Service(settings, ClientThat(request =>
            request.RequestUri!.AbsolutePath == "/v1/bundle"
                ? Json($$"""{"bundle":{{JsonSerializer.Serialize(Menu)}},"bundleVersion":"abc123"}""")
                : Json($$"""{"token":"{{token}}","bundleVersion":"abc123"}""")));

        await service.RefreshAsync(force: true);

        Assert.True(File.Exists(_bundlePath));
    }

    /// <summary>
    /// Most shops were set up before this existed and the cloud holds no bundle
    /// for them. That is the ordinary state, not a fault.
    /// </summary>
    [Fact]
    public async Task A_shop_whose_menu_the_cloud_does_not_hold_is_left_alone()
    {
        var settings = Configured();
        _store.SaveDeviceSecret("existing");
        var token = SignedFor(_store.DeviceId(), ShopEdition.Pos);

        var service = Service(settings, ClientThat(_ => Json($$"""{"token":"{{token}}"}""")));

        await service.RefreshAsync(force: true);

        Assert.False(File.Exists(_bundlePath));
        Assert.Null(_store.DownloadedBundleVersion());
    }
}
