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
        return string.IsNullOrWhiteSpace(raw)
            ? AppSettings.CreateDefaults()
            : JsonUtil.Deserialize<AppSettings>(raw);
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
        cmd.Parameters.AddWithValue("$v", JsonUtil.Serialize(settings));
        cmd.ExecuteNonQuery();
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
