namespace RingOrder.Epos.Domain;

/// <summary>
/// What the cloud last said this shop may do.
/// <para>
/// Arrives as a signed token and is cached on disk, so the shape here is the
/// payload's shape. Verification lives outside the domain — by the time an
/// <see cref="Entitlement"/> exists, its signature has already been checked
/// against a key in the binary, and nothing in this file trusts anything it
/// has not been handed.
/// </para>
/// </summary>
public sealed record Entitlement(
    string ShopId,
    string DeviceId,
    string Edition,
    IReadOnlyList<string> Features,
    int Terminals,
    DateTimeOffset IssuedAt,
    DateTimeOffset ExpiresAt)
{
    /// <summary>Payload version. Bumped when a field changes meaning, never for an addition.</summary>
    public const int CurrentVersion = 1;

    /// <summary>
    /// Whether this token was issued to the machine holding it.
    /// <para>
    /// The single field that keeps one shop's token from unlocking every
    /// install, and the easiest one to leave out of a payload — nothing
    /// misbehaves without it until the day a token is copied.
    /// </para>
    /// <para>
    /// The shop is deliberately <em>not</em> re-checked here. A device is
    /// activated for one shop and the server binds them at that point; checking
    /// it again locally adds no security and one more way to lock out a working
    /// till, because a shop renamed in the cloud would stop matching.
    /// </para>
    /// </summary>
    public bool CoversDevice(string? deviceId) =>
        !string.IsNullOrWhiteSpace(deviceId)
        && string.Equals(DeviceId, deviceId.Trim(), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Whether the token is still inside its window.
    /// <para>
    /// Only the expiry is tested. A token whose <see cref="IssuedAt"/> is in the
    /// future is still current — see the note on
    /// <see cref="EntitlementPolicy.Resolve"/> about flat CMOS batteries.
    /// </para>
    /// </summary>
    public bool IsCurrentAt(DateTimeOffset now) => now < ExpiresAt;

    /// <summary>
    /// Compares by value, feature list included.
    /// <para>
    /// A positional record compares a <see cref="IReadOnlyList{T}"/> member by
    /// reference, so two entitlements naming the same features would count as
    /// different. That is quiet and it is wrong in the direction that matters:
    /// callers use equality to decide whether anything actually changed, and
    /// reference comparison answers "yes" every single time.
    /// </para>
    /// </summary>
    public bool Equals(Entitlement? other) =>
        other is not null
        && ShopId == other.ShopId
        && DeviceId == other.DeviceId
        && Edition == other.Edition
        && Terminals == other.Terminals
        && IssuedAt == other.IssuedAt
        && ExpiresAt == other.ExpiresAt
        && Features.SequenceEqual(other.Features);

    public override int GetHashCode() =>
        HashCode.Combine(ShopId, DeviceId, Edition, Terminals, IssuedAt, ExpiresAt, Features.Count);
}

/// <summary>Where the answer in an <see cref="EntitlementState"/> came from.</summary>
public enum EntitlementSource
{
    /// <summary>A signed token issued to this device and still inside its window.</summary>
    Token,

    /// <summary>
    /// A signed token past its expiry, honoured anyway. The shop keeps trading
    /// and something visible says so.
    /// </summary>
    StaleToken,

    /// <summary>
    /// No usable token — never fetched, or issued to another device. The word in
    /// the shop bundle stands.
    /// </summary>
    Bundle,
}

/// <summary>
/// What the till acts on: one resolved answer, whatever its provenance.
/// <para>
/// Screens ask this, never the token, so that "no cloud yet", "cloud answered"
/// and "cloud went away a fortnight ago" are one code path with three values
/// rather than three code paths.
/// </para>
/// </summary>
public sealed record EntitlementState(
    EntitlementSource Source,
    string Edition,
    IReadOnlyList<string> Features,
    int Terminals,
    DateTimeOffset? ExpiredAt)
{
    /// <summary>Running on a token that has run out. Trading normally; worth saying so.</summary>
    public bool IsStale => Source == EntitlementSource.StaleToken;

    /// <summary>The tray-resident web-order printer rather than the full till.</summary>
    public bool IsPrintOnly => ShopEdition.IsPrintOnly(Edition);

    /// <summary>
    /// Whether a named module is permitted.
    /// <para>
    /// <b>An empty list restricts nothing.</b> Only a non-empty list is an
    /// allow-list. This is the surprising half of the design and it is
    /// deliberate: it means switching entitlements on changes nothing for any
    /// shop until somebody deliberately populates a list, and it means an odd
    /// answer from the cloud cannot take a working feature away.
    /// </para>
    /// <para>
    /// The opposite reading — empty means nothing is permitted — would have
    /// bricked every till the first time the server returned a payload with a
    /// field missing.
    /// </para>
    /// </summary>
    public bool Allows(string feature) =>
        Features.Count == 0
        || Features.Contains(feature, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Compares by value, feature list included — see the note on
    /// <see cref="Entitlement.Equals(Entitlement)"/>. This is the one that is
    /// load-bearing: a refresh raises its "changed" event by comparing the state
    /// before with the state after, and reference comparison would announce a
    /// change on every single refresh whether or not anything moved.
    /// </summary>
    public bool Equals(EntitlementState? other) =>
        other is not null
        && Source == other.Source
        && Edition == other.Edition
        && Terminals == other.Terminals
        && ExpiredAt == other.ExpiredAt
        && Features.SequenceEqual(other.Features);

    public override int GetHashCode() =>
        HashCode.Combine(Source, Edition, Terminals, ExpiredAt, Features.Count);
}

/// <summary>
/// Turns a cached token and the shop bundle into the one answer the till acts
/// on. Pure: no clock of its own, no disk, no network.
/// </summary>
public static class EntitlementPolicy
{
    /// <summary>A shop with no stated terminal count runs one till.</summary>
    public const int DefaultTerminals = 1;

    /// <summary>How long a fetched token stays usable without the cloud.</summary>
    public static readonly TimeSpan TokenLifetime = TimeSpan.FromDays(30);

    /// <summary>How often the till tries to refresh. Sliding: each success resets the lifetime.</summary>
    public static readonly TimeSpan RefreshInterval = TimeSpan.FromHours(24);

    /// <summary>
    /// How often the change log goes up when there is something to send.
    /// <para>
    /// Much shorter than the entitlement's day, because this is evidence rather
    /// than configuration: entries sitting on a till are entries somebody could
    /// still delete, and the tail of a chain is the one part the chain cannot
    /// protect on its own.
    /// </para>
    /// <para>
    /// Not shorter still, because a busy shop would then send after every order.
    /// Five minutes bounds the exposure without turning a till into a chatterbox.
    /// </para>
    /// </summary>
    public static readonly TimeSpan LogInterval = TimeSpan.FromMinutes(5);

    /// <summary>How many entries go in one batch. Bounded so a long-offline till does not send a day in one request.</summary>
    public const int LogBatchSize = 200;

    /// <summary>
    /// Resolve what this machine may do.
    /// <list type="number">
    /// <item>signed token, this device, still current → the token</item>
    /// <item>signed token, this device, expired → the token, marked stale</item>
    /// <item>no usable token → the bundle's own edition</item>
    /// <item>bundle word unrecognised → <c>pos</c>, via <see cref="ShopEdition.Normalise"/></item>
    /// </list>
    /// <para>
    /// Step 3 falls to the <em>bundle</em> rather than to <c>pos</c>. A
    /// print-only machine that has never reached the cloud is still print-only,
    /// because that is what was shipped; only a word that cannot be read at all
    /// falls the safe way to the full till.
    /// </para>
    /// <para>
    /// <b>Nothing here can lock a till.</b> Every path returns a working
    /// edition. An expiry is a banner, not a stop — a till that shut a shop down
    /// at eight on a Saturday over a billing question would cost the merchant a
    /// service and cost us the merchant.
    /// </para>
    /// <para>
    /// Only expiry is tested, never <see cref="Entitlement.IssuedAt"/>. Winding
    /// the clock back extends a token, and that abuse is ignored on purpose: a
    /// till PC with a flat CMOS battery boots in 2009, and refusing to open on
    /// that would take a working shop offline over a fifty-pence part.
    /// </para>
    /// </summary>
    /// <param name="cached">The last verified token, or null if there has never been one.</param>
    /// <param name="bundleEdition">The word the shop bundle shipped with.</param>
    /// <param name="deviceId">This installation's identity.</param>
    /// <param name="now">Current time, passed in so the rules are testable.</param>
    public static EntitlementState Resolve(
        Entitlement? cached,
        string? bundleEdition,
        string? deviceId,
        DateTimeOffset now)
    {
        if (cached is null || !cached.CoversDevice(deviceId))
            return FromBundle(bundleEdition);

        var current = cached.IsCurrentAt(now);

        return new EntitlementState(
            current ? EntitlementSource.Token : EntitlementSource.StaleToken,
            ShopEdition.Normalise(cached.Edition),
            cached.Features ?? [],
            cached.Terminals > 0 ? cached.Terminals : DefaultTerminals,
            current ? null : cached.ExpiresAt);
    }

    /// <summary>
    /// The answer before the cloud has ever been reached, and the answer when a
    /// token belongs to another machine. Restricts nothing beyond the edition:
    /// a shop we have not been told about is a shop we do not hold back.
    /// </summary>
    public static EntitlementState FromBundle(string? bundleEdition) =>
        new(EntitlementSource.Bundle,
            ShopEdition.Normalise(bundleEdition),
            [],
            DefaultTerminals,
            null);

    /// <summary>
    /// Whether it is worth asking the cloud again. Refresh is silent and its
    /// failure is invisible, so this is only about not hammering the service.
    /// </summary>
    public static bool ShouldRefresh(DateTimeOffset? lastAttempt, DateTimeOffset now) =>
        lastAttempt is not { } last || now - last >= RefreshInterval;

    /// <summary>
    /// Whether to go now because there is a log to send.
    /// <para>
    /// <b>Nothing pending means no request at all.</b> A quiet shop that took no
    /// orders today makes one call a day, not two hundred and eighty-eight empty
    /// ones.
    /// </para>
    /// </summary>
    public static bool ShouldSendLog(DateTimeOffset? lastAttempt, DateTimeOffset now, bool anyPending) =>
        anyPending && (lastAttempt is not { } last || now - last >= LogInterval);
}
