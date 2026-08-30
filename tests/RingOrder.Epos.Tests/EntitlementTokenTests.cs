using System.Security.Cryptography;
using RingOrder.Epos.Domain;
using RingOrder.Epos.Online;
using Xunit;

namespace RingOrder.Epos.Tests;

/// <summary>
/// The wire format, checked against tokens **signed by Node** rather than by the
/// code doing the checking.
/// <para>
/// That is the whole value of these: the service and the till are different
/// runtimes, and the failure this guards against is the one where each is
/// perfectly self-consistent and they disagree with each other. Node signs DER
/// by default and .NET verifies P1363 by default — two correct implementations
/// that never interoperate until somebody pins the encoding.
/// </para>
/// <para>Regenerate with <c>node fixtures/entitlement/make-fixtures.mjs</c>.</para>
/// </summary>
public class EntitlementTokenTests
{
    private static string FixtureDir =>
        Path.Combine(AppContext.BaseDirectory, "fixtures", "entitlement");

    private static string Token(string name) =>
        File.ReadAllText(Path.Combine(FixtureDir, $"{name}.token")).Trim();

    private static string DeviceId =>
        File.ReadAllText(Path.Combine(FixtureDir, "device-id.txt")).Trim();

    /// <summary>The development public key, as base64 SubjectPublicKeyInfo.</summary>
    private static IReadOnlyList<string> DevKeys
    {
        get
        {
            var pem = File.ReadAllLines(Path.Combine(FixtureDir, "dev-public.pem"));
            return [string.Concat(pem.Where(l => !l.StartsWith("-----")).Select(l => l.Trim()))];
        }
    }

    // ---- what the service signs, the till reads --------------------------

    [Fact]
    public void A_token_signed_by_the_service_verifies_here()
    {
        var result = EntitlementToken.Verify(Token("current"), DevKeys);

        Assert.Equal(TokenProblem.None, result.Problem);
        var entitlement = Assert.IsType<Entitlement>(result.Entitlement);
        Assert.Equal("demo-shop", entitlement.ShopId);
        Assert.Equal(DeviceId, entitlement.DeviceId);
        Assert.Equal(ShopEdition.Pos, entitlement.Edition);
        Assert.Equal(1, entitlement.Terminals);
    }

    [Fact]
    public void A_restricted_shop_arrives_with_its_edition_seats_and_allow_list()
    {
        var result = EntitlementToken.Verify(Token("print-only"), DevKeys);

        var entitlement = Assert.IsType<Entitlement>(result.Entitlement);
        Assert.Equal(ShopEdition.Print, entitlement.Edition);
        Assert.Equal(2, entitlement.Terminals);
        Assert.Equal(["web-orders"], entitlement.Features);

        var state = EntitlementPolicy.Resolve(entitlement, ShopEdition.Pos, DeviceId, DateTimeOffset.Parse("2026-09-01T00:00:00Z"));
        Assert.True(state.IsPrintOnly);
        Assert.True(state.Allows("web-orders"));
        Assert.False(state.Allows("drivers"));
    }

    /// <summary>
    /// The forward-compatibility guarantee, held by a fixture that carries
    /// fields this build has never heard of. Without this the first time the
    /// service added a field, every till that had not updated would stop
    /// reading its entitlement.
    /// </summary>
    [Fact]
    public void A_token_carrying_fields_we_have_never_heard_of_still_loads()
    {
        var result = EntitlementToken.Verify(Token("unknown-fields"), DevKeys);

        Assert.Equal(TokenProblem.None, result.Problem);
        var entitlement = Assert.IsType<Entitlement>(result.Entitlement);
        Assert.Equal(["drivers"], entitlement.Features);
    }

    // ---- what must not be accepted ----------------------------------------

    [Fact]
    public void A_payload_edited_after_signing_does_not_verify()
    {
        var result = EntitlementToken.Verify(Token("tampered"), DevKeys);

        Assert.Equal(TokenProblem.BadSignature, result.Problem);
        Assert.Null(result.Entitlement);
    }

    [Fact]
    public void A_token_signed_by_a_key_we_do_not_hold_does_not_verify()
    {
        using var stranger = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var spki = Convert.ToBase64String(stranger.ExportSubjectPublicKeyInfo());

        Assert.Equal(TokenProblem.BadSignature, EntitlementToken.Verify(Token("current"), [spki]).Problem);
    }

    /// <summary>
    /// A version bump means a field changed meaning, which is a deliberate break
    /// that ships as a new endpoint. Guessing at it would be worse than falling
    /// back to the bundle.
    /// </summary>
    [Fact]
    public void A_payload_version_this_build_does_not_know_is_refused()
    {
        Assert.Equal(TokenProblem.UnknownVersion, EntitlementToken.Verify(Token("future-version"), DevKeys).Problem);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Nothing_stored_is_not_a_fault(string? token)
    {
        Assert.Equal(TokenProblem.Missing, EntitlementToken.Verify(token, DevKeys).Problem);
    }

    [Theory]
    [InlineData("not-a-token")]
    [InlineData("one.two.three")]
    [InlineData("!!!.???")]
    public void Rubbish_is_reported_rather_than_thrown(string token)
    {
        var result = EntitlementToken.Verify(token, DevKeys);

        Assert.Null(result.Entitlement);
        Assert.NotEqual(TokenProblem.None, result.Problem);
    }

    /// <summary>
    /// A build shipped before a production key exists verifies nothing, so every
    /// till falls back to its bundle — the documented behaviour for a shop that
    /// has never reached the cloud, rather than a mystery.
    /// </summary>
    [Fact]
    public void With_no_key_configured_nothing_verifies_and_the_bundle_stands()
    {
        Assert.Equal(TokenProblem.NoKeys, EntitlementToken.Verify(Token("current"), []).Problem);

        var state = EntitlementPolicy.Resolve(null, ShopEdition.Print, DeviceId, DateTimeOffset.Now);
        Assert.Equal(ShopEdition.Print, state.Edition);
    }

    /// <summary>
    /// The private half of the development key is in the repository. A shipped
    /// build that trusted it would accept a token anybody could mint, so this
    /// holds the two apart for good.
    /// </summary>
    [Fact]
    public void The_development_key_is_never_trusted_by_a_shipped_build()
    {
        Assert.DoesNotContain(DevKeys[0], EntitlementKeys.Production);
    }

    /// <summary>
    /// Every shipped key must actually import as a P-256 public key.
    /// <para>
    /// A key mangled by a copy and paste — a stray newline, a truncated tail —
    /// would leave every till falling back to its bundle, silently, because a
    /// malformed key is skipped rather than thrown on. That behaviour is right
    /// (one bad entry must not stop the others being tried) and it is exactly
    /// what would hide this, so it is asserted here instead.
    /// </para>
    /// </summary>
    [Fact]
    public void Every_key_a_shipped_build_trusts_is_a_usable_p256_key()
    {
        Assert.NotEmpty(EntitlementKeys.Production);

        foreach (var spki in EntitlementKeys.Production)
        {
            using var key = ECDsa.Create();
            key.ImportSubjectPublicKeyInfo(Convert.FromBase64String(spki), out var read);

            Assert.Equal(spki.Length, Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()).Length);
            Assert.Equal(256, key.KeySize);
            Assert.True(read > 0);
        }
    }

    // ---- the expired fixture, end to end ----------------------------------

    /// <summary>
    /// The path that matters most, exercised on a real signed token rather than
    /// a constructed one: an entitlement a month past its expiry still opens the
    /// shop.
    /// </summary>
    [Fact]
    public void An_expired_token_verifies_and_the_till_keeps_trading()
    {
        var result = EntitlementToken.Verify(Token("expired"), DevKeys);
        Assert.Equal(TokenProblem.None, result.Problem);

        var state = EntitlementPolicy.Resolve(
            result.Entitlement, ShopEdition.Print, DeviceId, DateTimeOffset.Parse("2026-08-30T00:00:00Z"));

        Assert.Equal(EntitlementSource.StaleToken, state.Source);
        Assert.True(state.IsStale);
        Assert.Equal(ShopEdition.Pos, state.Edition);
        Assert.False(state.IsPrintOnly);
    }

    [Fact]
    public void A_token_for_another_machine_verifies_but_governs_nothing()
    {
        var result = EntitlementToken.Verify(Token("other-device"), DevKeys);

        Assert.Equal(TokenProblem.None, result.Problem);
        Assert.False(result.Entitlement!.CoversDevice(DeviceId));

        var state = EntitlementPolicy.Resolve(result.Entitlement, ShopEdition.Print, DeviceId, DateTimeOffset.Now);
        Assert.Equal(EntitlementSource.Bundle, state.Source);
    }

    // ---- our own signing, for the fixture tooling --------------------------

    [Fact]
    public void What_we_sign_we_can_read_back()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var original = new Entitlement(
            "shop", "device", ShopEdition.Pos, ["tables"], 3,
            DateTimeOffset.Parse("2026-01-01T00:00:00Z"),
            DateTimeOffset.Parse("2026-01-31T00:00:00Z"));

        var token = EntitlementToken.Sign(original, key);
        var read = EntitlementToken.Verify(token, [Convert.ToBase64String(key.ExportSubjectPublicKeyInfo())]);

        Assert.Equal(original, read.Entitlement);
    }
}
