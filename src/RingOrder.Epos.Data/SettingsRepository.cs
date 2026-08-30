using RingOrder.Epos.Domain;

namespace RingOrder.Epos.Data;

public sealed class SettingsRepository
{
    private readonly EposDb _db;
    private const string Key = "app";

    public SettingsRepository(EposDb db) => _db = db;

    public AppSettings Load()
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT value FROM settings WHERE key=$k";
        cmd.Parameters.AddWithValue("$k", Key);
        var raw = cmd.ExecuteScalar() as string;
        if (string.IsNullOrWhiteSpace(raw)) return AppSettings.CreateDefaults();

        var settings = JsonUtil.Deserialize<AppSettings>(raw);
        settings.OnlinePassword = LocalSecret.Unprotect(settings.OnlinePassword);
        settings.AddressLookupApiKey = LocalSecret.Unprotect(settings.AddressLookupApiKey);
        settings.CloudActivationCode = LocalSecret.Unprotect(settings.CloudActivationCode);
        return settings;
    }

    /// <summary>
    /// Encrypts any secret still sitting in the clear, and reports whether it
    /// had to.
    /// <para>
    /// Run once at startup rather than as a schema migration: this rewrites one
    /// row's contents, not its shape, and a shop that downgrades must still be
    /// able to read its own settings — which it can, because
    /// <see cref="LocalSecret"/>'s stored form is self-describing.
    /// </para>
    /// </summary>
    public bool ProtectStoredSecrets()
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT value FROM settings WHERE key=$k";
        cmd.Parameters.AddWithValue("$k", Key);
        if (cmd.ExecuteScalar() is not string raw || string.IsNullOrWhiteSpace(raw)) return false;

        var stored = JsonUtil.Deserialize<AppSettings>(raw);

        var exposed =
            (!string.IsNullOrEmpty(stored.OnlinePassword) && !LocalSecret.IsProtected(stored.OnlinePassword)) ||
            (!string.IsNullOrEmpty(stored.AddressLookupApiKey) && !LocalSecret.IsProtected(stored.AddressLookupApiKey)) ||
            (!string.IsNullOrEmpty(stored.CloudActivationCode) && !LocalSecret.IsProtected(stored.CloudActivationCode));

        if (!exposed) return false;

        Save(stored);
        return true;
    }

    public void Save(AppSettings settings)
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO settings(key, value) VALUES($k, $v)
            ON CONFLICT(key) DO UPDATE SET value=excluded.value
            """;
        cmd.Parameters.AddWithValue("$k", Key);
        cmd.Parameters.AddWithValue("$v", SerialiseWithSecretsProtected(settings));
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Serialises with the two secret fields encrypted, and hands the caller
    /// back the object it passed in.
    /// <para>
    /// The swap-and-restore is deliberate. <see cref="AppSettings"/> is a live
    /// object the caller keeps using — <c>AppServices</c> caches it — so leaving
    /// ciphertext in it would send an encrypted blob to the website as a
    /// password on the very next poll.
    /// </para>
    /// </summary>
    private static string SerialiseWithSecretsProtected(AppSettings settings)
    {
        var password = settings.OnlinePassword;
        var apiKey = settings.AddressLookupApiKey;
        var activation = settings.CloudActivationCode;
        try
        {
            settings.OnlinePassword = LocalSecret.Protect(password);
            settings.AddressLookupApiKey = LocalSecret.Protect(apiKey);
            settings.CloudActivationCode = LocalSecret.Protect(activation);
            return JsonUtil.Serialize(settings);
        }
        finally
        {
            settings.OnlinePassword = password;
            settings.AddressLookupApiKey = apiKey;
            settings.CloudActivationCode = activation;
        }
    }

    public string AllocateOrderNumber()
    {
        var settings = Load();
        var day = DateTime.Now.ToString("yyMMdd");
        var seq = settings.NextOrderSequence++;
        Save(settings);
        return $"{day}-{seq:D4}";
    }
}
