using RingOrder.Epos.Domain;
using Xunit;

namespace RingOrder.Epos.Tests;

/// <summary>
/// What the cloud says a shop may do, and — the part that matters — what the
/// till does when the cloud has never answered, answered a fortnight ago, or
/// answered about somebody else's machine.
/// <para>
/// The rule holding all of this together is that <b>no path locks a till</b>.
/// Every case below ends with a shop that can still take money.
/// </para>
/// </summary>
public class EntitlementTests
{
    private const string Device = "3f2a-this-machine";
    private static readonly DateTimeOffset Now = new(2026, 8, 30, 19, 0, 0, TimeSpan.Zero);

    private static Entitlement Token(
        string edition = ShopEdition.Pos,
        string? deviceId = null,
        IReadOnlyList<string>? features = null,
        int terminals = 1,
        TimeSpan? expiresIn = null) =>
        new(
            ShopId: "magicwok-birmingham",
            DeviceId: deviceId ?? Device,
            Edition: edition,
            Features: features ?? [],
            Terminals: terminals,
            IssuedAt: Now.AddDays(-1),
            ExpiresAt: Now + (expiresIn ?? TimeSpan.FromDays(29)));

    // ---- the ordinary case ------------------------------------------------

    [Fact]
    public void A_current_token_for_this_machine_is_the_answer()
    {
        var state = EntitlementPolicy.Resolve(
            Token(edition: ShopEdition.Print, terminals: 3),
            bundleEdition: ShopEdition.Pos,
            Device,
            Now);

        Assert.Equal(EntitlementSource.Token, state.Source);
        Assert.Equal(ShopEdition.Print, state.Edition);
        Assert.True(state.IsPrintOnly);
        Assert.Equal(3, state.Terminals);
        Assert.False(state.IsStale);
        Assert.Null(state.ExpiredAt);
    }

    /// <summary>The token outranks the bundle — that is the whole point of it.</summary>
    [Fact]
    public void The_token_beats_the_word_the_shop_was_shipped_with()
    {
        var state = EntitlementPolicy.Resolve(
            Token(edition: ShopEdition.Pos),
            bundleEdition: ShopEdition.Print,
            Device,
            Now);

        Assert.Equal(ShopEdition.Pos, state.Edition);
        Assert.False(state.IsPrintOnly);
    }

    // ---- the cloud has gone away ------------------------------------------

    /// <summary>
    /// The one that decides whether this system is safe to ship. An expiry is a
    /// banner, not a stop: a till that shut a shop down at eight on a Saturday
    /// over a billing question would cost the merchant a service and cost us the
    /// merchant.
    /// </summary>
    [Fact]
    public void An_expired_token_still_trades_and_says_so()
    {
        var expired = Token(edition: ShopEdition.Pos, terminals: 4, expiresIn: TimeSpan.FromDays(-3));

        var state = EntitlementPolicy.Resolve(expired, ShopEdition.Print, Device, Now);

        Assert.Equal(EntitlementSource.StaleToken, state.Source);
        Assert.True(state.IsStale);
        Assert.Equal(ShopEdition.Pos, state.Edition);   // keeps what it was told
        Assert.Equal(4, state.Terminals);               // including the seats
        Assert.Equal(expired.ExpiresAt, state.ExpiredAt);
    }

    [Fact]
    public void An_expired_token_keeps_the_features_it_was_last_granted()
    {
        var expired = Token(features: ["drivers"], expiresIn: TimeSpan.FromDays(-40));

        var state = EntitlementPolicy.Resolve(expired, ShopEdition.Pos, Device, Now);

        Assert.True(state.Allows("drivers"));
        Assert.False(state.Allows("tables"));
    }

    // ---- never been online ------------------------------------------------

    /// <summary>
    /// A print-only machine that has never reached the cloud stays print-only.
    /// Falling to <c>pos</c> here would hand the full till to every shop that
    /// unplugged its router, which is the one case this design is meant to tell
    /// apart.
    /// </summary>
    [Fact]
    public void With_no_token_the_bundle_stands_rather_than_the_full_till()
    {
        var state = EntitlementPolicy.Resolve(null, ShopEdition.Print, Device, Now);

        Assert.Equal(EntitlementSource.Bundle, state.Source);
        Assert.Equal(ShopEdition.Print, state.Edition);
        Assert.True(state.IsPrintOnly);
    }

    /// <summary>But a word nobody can read still falls the safe way.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("prnt")]
    public void An_unreadable_bundle_word_is_the_full_till(string? raw)
    {
        var state = EntitlementPolicy.Resolve(null, raw, Device, Now);

        Assert.Equal(ShopEdition.Pos, state.Edition);
        Assert.False(state.IsPrintOnly);
    }

    [Fact]
    public void A_shop_we_know_nothing_about_is_held_back_from_nothing()
    {
        var state = EntitlementPolicy.Resolve(null, ShopEdition.Pos, Device, Now);

        Assert.True(state.Allows("drivers"));
        Assert.True(state.Allows("tables"));
        Assert.Equal(EntitlementPolicy.DefaultTerminals, state.Terminals);
    }

    // ---- somebody else's token --------------------------------------------

    /// <summary>
    /// Without this, one shop's token unlocks every install — and nothing
    /// misbehaves without it until the day a token is copied, which is why it
    /// is tested rather than trusted.
    /// </summary>
    [Fact]
    public void A_token_issued_to_another_machine_is_ignored_entirely()
    {
        var stolen = Token(edition: ShopEdition.Pos, deviceId: "some-other-till", terminals: 9);

        var state = EntitlementPolicy.Resolve(stolen, ShopEdition.Print, Device, Now);

        Assert.Equal(EntitlementSource.Bundle, state.Source);
        Assert.Equal(ShopEdition.Print, state.Edition);
        Assert.Equal(EntitlementPolicy.DefaultTerminals, state.Terminals);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void A_machine_with_no_identity_cannot_match_a_token(string? deviceId)
    {
        var state = EntitlementPolicy.Resolve(Token(), ShopEdition.Print, deviceId, Now);

        Assert.Equal(EntitlementSource.Bundle, state.Source);
    }

    [Fact]
    public void Device_identity_is_matched_without_regard_to_case_or_padding()
    {
        var state = EntitlementPolicy.Resolve(Token(), ShopEdition.Print, "  3F2A-THIS-MACHINE  ", Now);

        Assert.Equal(EntitlementSource.Token, state.Source);
    }

    // ---- what an empty list means -----------------------------------------

    /// <summary>
    /// The surprising half of the design, pinned here so it cannot drift. An
    /// empty list restricts nothing; only a populated list is an allow-list.
    /// Read the other way round, the first payload that arrived with a field
    /// missing would have bricked every till on the estate.
    /// </summary>
    [Fact]
    public void An_empty_feature_list_restricts_nothing()
    {
        var state = EntitlementPolicy.Resolve(Token(features: []), ShopEdition.Pos, Device, Now);

        Assert.True(state.Allows("drivers"));
        Assert.True(state.Allows("anything-invented-later"));
    }

    [Fact]
    public void A_populated_list_permits_only_what_it_names()
    {
        var state = EntitlementPolicy.Resolve(
            Token(features: ["drivers", "tables"]), ShopEdition.Pos, Device, Now);

        Assert.True(state.Allows("drivers"));
        Assert.True(state.Allows("TABLES"));
        Assert.False(state.Allows("reports"));
    }

    // ---- clocks -----------------------------------------------------------

    /// <summary>
    /// A till PC with a flat CMOS battery boots in 2009. Refusing to open on
    /// that would take a working shop offline over a fifty-pence part, so only
    /// the expiry is enforced and a token issued "in the future" is honoured.
    /// </summary>
    [Fact]
    public void A_clock_that_has_gone_backwards_does_not_shut_the_shop()
    {
        var state = EntitlementPolicy.Resolve(
            Token(edition: ShopEdition.Pos), ShopEdition.Print, Device, Now.AddYears(-17));

        Assert.Equal(EntitlementSource.Token, state.Source);
        Assert.Equal(ShopEdition.Pos, state.Edition);
    }

    [Fact]
    public void A_token_expiring_this_instant_has_expired()
    {
        var token = Token(expiresIn: TimeSpan.Zero);

        Assert.False(token.IsCurrentAt(Now));
        Assert.True(token.IsCurrentAt(Now.AddTicks(-1)));
    }

    // ---- refreshing -------------------------------------------------------

    [Fact]
    public void The_first_start_asks_the_cloud_straight_away()
    {
        Assert.True(EntitlementPolicy.ShouldRefresh(null, Now));
    }

    [Fact]
    public void A_refresh_is_a_daily_habit_not_a_poll()
    {
        Assert.False(EntitlementPolicy.ShouldRefresh(Now.AddHours(-23), Now));
        Assert.True(EntitlementPolicy.ShouldRefresh(Now.AddHours(-24), Now));
    }

    /// <summary>
    /// The lifetime is how long the cloud may be unreachable, not how often
    /// anybody renews anything: refresh is a sliding window, so in normal
    /// running the token is never more than a day old and the service can be
    /// down for a month before a shop notices.
    /// </summary>
    [Fact]
    public void The_window_is_thirty_days_wide_and_a_day_between_refreshes()
    {
        Assert.Equal(TimeSpan.FromDays(30), EntitlementPolicy.TokenLifetime);
        Assert.Equal(TimeSpan.FromHours(24), EntitlementPolicy.RefreshInterval);
        Assert.True(EntitlementPolicy.TokenLifetime > EntitlementPolicy.RefreshInterval * 7);
    }

    // ---- odd payloads -----------------------------------------------------

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void A_token_claiming_no_terminals_still_runs_one(int terminals)
    {
        var state = EntitlementPolicy.Resolve(
            Token(terminals: terminals), ShopEdition.Pos, Device, Now);

        Assert.Equal(EntitlementPolicy.DefaultTerminals, state.Terminals);
    }

    [Fact]
    public void A_token_naming_an_edition_nobody_recognises_gives_the_full_till()
    {
        var state = EntitlementPolicy.Resolve(
            Token(edition: "enterprise-plus"), ShopEdition.Print, Device, Now);

        Assert.Equal(ShopEdition.Pos, state.Edition);
    }

    // ---- equality, because something depends on it ------------------------

    /// <summary>
    /// A refresh decides whether to raise its "changed" event by comparing the
    /// state before with the state after. A positional record compares a list
    /// member by reference, so without an explicit comparison this would report
    /// a change on every refresh for the rest of the product's life — quietly,
    /// and in the direction nobody checks.
    /// </summary>
    [Fact]
    public void Two_states_naming_the_same_features_are_the_same_state()
    {
        var a = EntitlementPolicy.Resolve(Token(features: ["drivers", "tables"]), ShopEdition.Pos, Device, Now);
        var b = EntitlementPolicy.Resolve(Token(features: ["drivers", "tables"]), ShopEdition.Pos, Device, Now);

        Assert.Equal(a, b);
        Assert.False(a != b);
    }

    [Fact]
    public void A_state_that_gained_a_feature_is_a_different_state()
    {
        var before = EntitlementPolicy.Resolve(Token(features: ["drivers"]), ShopEdition.Pos, Device, Now);
        var after = EntitlementPolicy.Resolve(Token(features: ["drivers", "tables"]), ShopEdition.Pos, Device, Now);

        Assert.NotEqual(before, after);
    }

    [Fact]
    public void Two_tokens_with_the_same_contents_are_the_same_token()
    {
        Assert.Equal(Token(features: ["drivers"]), Token(features: ["drivers"]));
        Assert.NotEqual(Token(features: ["drivers"]), Token(features: ["drivers", "tables"]));
    }

    // ---- what is gated, and what may never be --------------------------------

    /// <summary>
    /// The feature list is an allow-list, so naming one module denies every
    /// other. That is why nothing core may ever be gated: granting a shop
    /// "drivers" would otherwise take away its ability to sell food.
    /// </summary>
    [Fact]
    public void Granting_one_module_denies_the_others_and_nothing_else()
    {
        var state = EntitlementPolicy.Resolve(
            Token(features: [ShopFeatures.Drivers]), ShopEdition.Pos, Device, Now);

        Assert.True(state.Allows(ShopFeatures.Drivers));
        Assert.False(state.Allows(ShopFeatures.CallerId));

        // And the till itself is not a feature, so it cannot be taken away.
        Assert.Equal(ShopEdition.Pos, state.Edition);
        Assert.False(state.IsPrintOnly);
    }

    /// <summary>
    /// Every name this build gates on must be one the service could grant. A
    /// screen checking a feature nobody can spell into a token would be hidden
    /// for every shop, for ever, and the till would look broken rather than
    /// unlicensed.
    /// </summary>
    [Fact]
    public void Every_gated_module_is_a_name_that_can_be_granted()
    {
        var state = EntitlementPolicy.Resolve(
            Token(features: [.. ShopFeatures.All]), ShopEdition.Pos, Device, Now);

        Assert.All(ShopFeatures.All, feature => Assert.True(state.Allows(feature)));
        Assert.Equal(ShopFeatures.All.Count, ShopFeatures.All.Distinct().Count());
    }

    /// <summary>
    /// The reason turning entitlements on changed nothing for anybody: a shop
    /// nobody has configured keeps every module.
    /// </summary>
    [Fact]
    public void A_shop_with_no_list_keeps_every_module()
    {
        var state = EntitlementPolicy.Resolve(Token(features: []), ShopEdition.Pos, Device, Now);

        Assert.All(ShopFeatures.All, feature => Assert.True(state.Allows(feature)));
    }
}
