using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace RingOrder.Epos.Data;

/// <summary>
/// The till's database.
/// <para>
/// <see cref="Open"/> hands out a fresh pooled connection every time and the
/// caller disposes it. The alternative — one cached connection shared by
/// everything — worked only while a single thread touched the database, and
/// broke the moment print workers ran in the background: two transactions on
/// one connection is an error, and it surfaces as a kitchen ticket that never
/// prints.
/// </para>
/// <para>
/// WAL lets readers and one writer work at once; <c>busy_timeout</c> makes a
/// second writer wait its turn instead of failing instantly. Connection
/// pooling makes this cheap.
/// </para>
/// </summary>
public sealed class EposDb : IDisposable
{
    private readonly string _connectionString;
    private readonly string _path;

    public EposDb(string? path = null)
    {
        _path = path ?? LocalPaths.DatabasePath;
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = _path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = true,
        }.ToString();
    }

    public string Path => _path;

    /// <summary>An open connection. The caller owns it and must dispose it.</summary>
    public SqliteConnection Open()
    {
        var conn = new SqliteConnection(_connectionString);
        conn.Open();
        using var pragma = conn.CreateCommand();
        pragma.CommandText =
            "PRAGMA foreign_keys=ON; PRAGMA journal_mode=WAL; PRAGMA synchronous=FULL; PRAGMA busy_timeout=5000;";
        pragma.ExecuteNonQuery();
        return conn;
    }

    /// <summary>
    /// Brings the schema up to date, taking a copy of the database first when
    /// there is anything to lose. Returns the versions applied.
    /// </summary>
    public IReadOnlyList<int> Migrate(Action<string>? log = null)
    {
        using var conn = Open();
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
        using var conn = Open();
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
        var dest = System.IO.Path.Combine(BackupDirectory, name);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "VACUUM INTO $dest";
        cmd.Parameters.AddWithValue("$dest", dest);
        cmd.ExecuteNonQuery();
        return dest;
    }

    /// <summary>
    /// Where this database's backups go — beside the database, not at a fixed
    /// machine-wide path.
    /// <para>
    /// It used to be fixed, which meant any database migrated on the machine
    /// wrote its pre-migration copy into the live shop's backup folder: every
    /// test run, and any copy a support session opened to investigate. The
    /// restore instruction is "take the newest pre-migration file", and the
    /// newest could easily have been a five-row test database — restoring it
    /// over a trading shop would have been the worst possible outcome of a
    /// safety feature.
    /// </para>
    /// </summary>
    private string BackupDirectory
    {
        get
        {
            if (string.Equals(_path, LocalPaths.DatabasePath, StringComparison.OrdinalIgnoreCase))
                return LocalPaths.BackupDirectory;

            var beside = System.IO.Path.Combine(
                System.IO.Path.GetDirectoryName(_path) ?? ".", "backups");
            Directory.CreateDirectory(beside);
            return beside;
        }
    }

    /// <summary>
    /// Nothing is held open, so this only clears the pool — which matters in
    /// tests, where the file is deleted straight afterwards and Windows will
    /// not let go of a handle a pooled connection still owns.
    /// </summary>
    public void Dispose() => SqliteConnection.ClearPool(new SqliteConnection(_connectionString));
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
