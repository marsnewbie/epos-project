using RingOrder.Epos.Data;
using RingOrder.Epos.Domain;
using Xunit;

namespace RingOrder.Epos.Tests;

/// <summary>
/// Secrets on the merchant's machine. The exposure being closed is not someone
/// sitting at the till — it is the database leaving the shop in every nightly
/// backup and in every copy sent to support.
/// </summary>
public class LocalSecretTests : IDisposable
{
    private readonly string _dbPath =
        Path.Combine(Path.GetTempPath(), $"ringorder-test-{Guid.NewGuid():N}.sqlite");
    private readonly EposDb _db;
    private readonly SettingsRepository _settings;

    public LocalSecretTests()
    {
        _db = new EposDb(_dbPath);
        _db.Migrate();
        _settings = new SettingsRepository(_db);
    }

    /// <summary>Reads the row as it actually sits on disk, past the repository.</summary>
    private string RawRow()
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT value FROM settings WHERE key='app'";
        return cmd.ExecuteScalar() as string ?? "";
    }

    [Fact]
    public void A_secret_round_trips()
    {
        Assert.Equal("hunter2", LocalSecret.Unprotect(LocalSecret.Protect("hunter2")));
    }

    [Fact]
    public void Empty_stays_empty_because_there_is_nothing_to_hide()
    {
        Assert.Equal("", LocalSecret.Protect(""));
        Assert.Equal("", LocalSecret.Unprotect(""));
    }

    /// <summary>
    /// Settings written before this existed are read exactly as they always
    /// were, which is why none of this needed a migration.
    /// </summary>
    [Fact]
    public void A_value_stored_before_encryption_existed_still_reads()
    {
        Assert.Equal("old-plaintext", LocalSecret.Unprotect("old-plaintext"));
        Assert.False(LocalSecret.IsProtected("old-plaintext"));
    }

    [Fact]
    public void Protecting_twice_does_not_double_wrap()
    {
        var once = LocalSecret.Protect("key");
        Assert.Equal(once, LocalSecret.Protect(once));
        Assert.Equal("key", LocalSecret.Unprotect(LocalSecret.Protect(once)));
    }

    /// <summary>
    /// The assertion the whole change exists for: what lands in the file that
    /// gets copied into `backups/` must not be the password.
    /// </summary>
    [Fact]
    public void The_stored_row_does_not_contain_the_readable_secret()
    {
        var settings = AppSettings.CreateDefaults();
        settings.OnlinePassword = "websitePassword123";
        settings.AddressLookupApiKey = "ak_live_supersecret";
        _settings.Save(settings);

        var raw = RawRow();

        Assert.DoesNotContain("websitePassword123", raw);
        Assert.DoesNotContain("ak_live_supersecret", raw);
        Assert.Contains("dpapi:", raw);
    }

    [Fact]
    public void Loading_gives_the_secrets_back_usable()
    {
        var settings = AppSettings.CreateDefaults();
        settings.OnlinePassword = "websitePassword123";
        settings.AddressLookupApiKey = "ak_live_supersecret";
        _settings.Save(settings);

        var loaded = _settings.Load();

        Assert.Equal("websitePassword123", loaded.OnlinePassword);
        Assert.Equal("ak_live_supersecret", loaded.AddressLookupApiKey);
    }

    /// <summary>
    /// The object the caller passed in is still usable afterwards. Leaving
    /// ciphertext in it would send an encrypted blob to the website as a
    /// password on the very next poll — AppServices caches this object.
    /// </summary>
    [Fact]
    public void Saving_does_not_leave_ciphertext_in_the_callers_object()
    {
        var settings = AppSettings.CreateDefaults();
        settings.OnlinePassword = "websitePassword123";

        _settings.Save(settings);

        Assert.Equal("websitePassword123", settings.OnlinePassword);
    }

    [Fact]
    public void Settings_with_no_secrets_are_not_rewritten()
    {
        _settings.Save(AppSettings.CreateDefaults());
        Assert.False(_settings.ProtectStoredSecrets());
    }

    /// <summary>
    /// The one-time pass over a database written before this existed. It has to
    /// run without the merchant doing anything, because the value is already
    /// travelling in every backup.
    /// </summary>
    [Fact]
    public void A_database_holding_a_plaintext_secret_is_encrypted_in_place()
    {
        // Written the way the old release wrote it: straight JSON, no wrapping.
        var legacy = AppSettings.CreateDefaults();
        legacy.OnlinePassword = "websitePassword123";
        using (var conn = _db.Open())
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText =
                "INSERT INTO settings(key,value) VALUES('app',$v) " +
                "ON CONFLICT(key) DO UPDATE SET value=excluded.value";
            cmd.Parameters.AddWithValue("$v", JsonUtil.Serialize(legacy));
            cmd.ExecuteNonQuery();
        }

        Assert.Contains("websitePassword123", RawRow());

        Assert.True(_settings.ProtectStoredSecrets());

        Assert.DoesNotContain("websitePassword123", RawRow());
        Assert.Equal("websitePassword123", _settings.Load().OnlinePassword);

        // And it is done — a second start does not rewrite the row again.
        Assert.False(_settings.ProtectStoredSecrets());
    }

    /// <summary>
    /// DPAPI is bound to the machine, so a database restored onto different
    /// hardware cannot read these back. Empty is the right answer: the shop
    /// retypes the key. Handing back the ciphertext would send it to the website
    /// as a password.
    /// </summary>
    [Fact]
    public void A_secret_that_cannot_be_decrypted_comes_back_empty_not_as_ciphertext()
    {
        Assert.Equal("", LocalSecret.Unprotect("dpapi:bm90IHJlYWxseSBlbmNyeXB0ZWQ="));
        Assert.Equal("", LocalSecret.Unprotect("dpapi:not-even-base64!!"));
    }

    public void Dispose()
    {
        _db.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            var path = _dbPath + suffix;
            if (File.Exists(path)) File.Delete(path);
        }
        GC.SuppressFinalize(this);
    }
}
