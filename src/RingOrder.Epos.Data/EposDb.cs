using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace RingOrder.Epos.Data;

public sealed class EposDb : IDisposable
{
    private readonly string _connectionString;
    private readonly string _path;
    private SqliteConnection? _conn;

    public EposDb(string? path = null)
    {
        _path = path ?? LocalPaths.DatabasePath;
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = _path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
        }.ToString();
    }

    public string Path => _path;

    public SqliteConnection Open()
    {
        if (_conn is { State: System.Data.ConnectionState.Open })
            return _conn;

        _conn = new SqliteConnection(_connectionString);
        _conn.Open();
        using var pragma = _conn.CreateCommand();
        pragma.CommandText = "PRAGMA foreign_keys=ON; PRAGMA journal_mode=WAL; PRAGMA synchronous=FULL;";
        pragma.ExecuteNonQuery();
        return _conn;
    }

    /// <summary>
    /// Brings the schema up to date, taking a copy of the database first when
    /// there is anything to lose. Returns the versions applied.
    /// </summary>
    public IReadOnlyList<int> Migrate(Action<string>? log = null)
    {
        var conn = Open();
        var from = SchemaMigrations.CurrentVersion(conn);
        if (from >= SchemaMigrations.LatestVersion)
            return [];

        if (from > 0)
        {
            var backup = BackupBeforeMigration(conn, from);
            log?.Invoke($"schema {from} -> {SchemaMigrations.LatestVersion}, backup at {backup}");
        }

        var applied = SchemaMigrations.Apply(conn);
        foreach (var version in applied)
            log?.Invoke($"applied migration {version}");
        return applied;
    }

    /// <summary>
    /// Consistent copy of the live database. <c>VACUUM INTO</c> reads through the
    /// WAL, so unlike copying the file it cannot capture a half-written page.
    /// </summary>
    public string BackupTo(string destinationPath)
    {
        var conn = Open();
        if (File.Exists(destinationPath)) File.Delete(destinationPath);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "VACUUM INTO $dest";
        cmd.Parameters.AddWithValue("$dest", destinationPath);
        cmd.ExecuteNonQuery();
        return destinationPath;
    }

    private string BackupBeforeMigration(SqliteConnection conn, int fromVersion)
    {
        var name = $"pre-migration-v{fromVersion}-{DateTime.Now:yyyyMMdd-HHmmss}.sqlite";
        var dest = System.IO.Path.Combine(LocalPaths.BackupDirectory, name);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "VACUUM INTO $dest";
        cmd.Parameters.AddWithValue("$dest", dest);
        cmd.ExecuteNonQuery();
        return dest;
    }

    public void Dispose() => _conn?.Dispose();
}

public static class JsonUtil
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = false,
    };

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);
    public static T Deserialize<T>(string json) => JsonSerializer.Deserialize<T>(json, Options)!;
}
