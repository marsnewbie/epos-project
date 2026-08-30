namespace RingOrder.Epos.Data;

/// <summary>
/// This installation's identity, and the last entitlement the cloud sent it.
/// <para>
/// Rows in the existing <c>settings</c> key-value table rather than a table of
/// their own: three values that are operational state, not merchant
/// configuration, and not worth a migration.
/// </para>
/// <para>
/// It lives under <c>%PROGRAMDATA%</c> with the rest of the till's data, so an
/// uninstall and reinstall keeps the same identity and the same entitlement —
/// nobody has to reactivate a machine because a merchant repaired their
/// installation.
/// </para>
/// </summary>
public sealed class EntitlementStore
{
    private const string DeviceIdKey = "cloud.device-id";
    private const string TokenKey = "cloud.entitlement-token";
    private const string SecretKey = "cloud.device-secret";
    private const string LastAttemptKey = "cloud.last-refresh-attempt";

    private readonly EposDb _db;

    public EntitlementStore(EposDb db) => _db = db;

    /// <summary>
    /// This machine's identity, created on first use and stable thereafter.
    /// <para>
    /// A random value, deliberately <b>not</b> derived from the hardware. A
    /// fingerprint revokes itself when a merchant replaces a dying PC or plugs
    /// in a USB dock that moves the MAC address, and it defends against copying
    /// that is not a real risk in this trade. A new machine simply activates
    /// again; a copied token is useless on the machine it was copied to, which
    /// is the whole of what this has to achieve.
    /// </para>
    /// </summary>
    public string DeviceId()
    {
        if (Read(DeviceIdKey) is { Length: > 0 } existing) return existing;

        var created = Guid.NewGuid().ToString("n");
        Write(DeviceIdKey, created);
        return created;
    }

    /// <summary>The last token the cloud sent, verified or not — verification happens on read.</summary>
    public string? Token() => Read(TokenKey);

    public void SaveToken(string token) => Write(TokenKey, token);

    /// <summary>
    /// Forget the stored token. Used when a token turns out to belong to
    /// another device — which is what a machine restored from another shop's
    /// disk image looks like — so the till stops carrying something it can
    /// never use.
    /// </summary>
    public void ClearToken() => Delete(TokenKey);

    /// <summary>
    /// The credential this device authenticates with, encrypted at rest.
    /// <para>
    /// Unlike the token, this one is a secret: the token is signed and proves
    /// only what we already told the shop, while this opens the door. It goes
    /// through <see cref="LocalSecret"/> for the same reason the website
    /// password does.
    /// </para>
    /// </summary>
    public string? DeviceSecret() => LocalSecret.Unprotect(Read(SecretKey)) is { Length: > 0 } s ? s : null;

    public void SaveDeviceSecret(string secret) => Write(SecretKey, LocalSecret.Protect(secret));

    /// <summary>
    /// When the till last <em>tried</em> to refresh, successfully or not.
    /// <para>
    /// The attempt is recorded rather than the success, so a shop that is
    /// offline for a fortnight makes fourteen attempts instead of one every
    /// time a member of staff restarts the till.
    /// </para>
    /// </summary>
    public DateTimeOffset? LastRefreshAttempt() =>
        DateTimeOffset.TryParse(Read(LastAttemptKey), out var at) ? at : null;

    public void RecordRefreshAttempt(DateTimeOffset at) =>
        Write(LastAttemptKey, at.ToString("O"));

    private string? Read(string key)
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT value FROM settings WHERE key=$k";
        cmd.Parameters.AddWithValue("$k", key);
        return cmd.ExecuteScalar() as string;
    }

    private void Write(string key, string value)
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO settings(key, value) VALUES($k, $v)
            ON CONFLICT(key) DO UPDATE SET value=excluded.value
            """;
        cmd.Parameters.AddWithValue("$k", key);
        cmd.Parameters.AddWithValue("$v", value);
        cmd.ExecuteNonQuery();
    }

    private void Delete(string key)
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM settings WHERE key=$k";
        cmd.Parameters.AddWithValue("$k", key);
        cmd.ExecuteNonQuery();
    }
}
