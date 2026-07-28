using System.Text.Json;
using MagicWok.Epos.Domain;
using Microsoft.Data.Sqlite;

namespace MagicWok.Epos.Data;

public sealed class EposDb : IDisposable
{
    private readonly string _connectionString;
    private SqliteConnection? _conn;

    public EposDb(string? path = null)
    {
        path ??= LocalPaths.DatabasePath;
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
        }.ToString();
    }

    public SqliteConnection Open()
    {
        if (_conn is { State: System.Data.ConnectionState.Open })
            return _conn;

        _conn = new SqliteConnection(_connectionString);
        _conn.Open();
        using var pragma = _conn.CreateCommand();
        pragma.CommandText = "PRAGMA foreign_keys=ON; PRAGMA journal_mode=WAL;";
        pragma.ExecuteNonQuery();
        return _conn;
    }

    public void EnsureCreated()
    {
        var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS settings (
              key TEXT PRIMARY KEY,
              value TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS categories (
              id TEXT PRIMARY KEY,
              name TEXT NOT NULL,
              description TEXT,
              sort_order INTEGER NOT NULL DEFAULT 0,
              is_visible INTEGER NOT NULL DEFAULT 1
            );

            CREATE TABLE IF NOT EXISTS menu_items (
              id TEXT PRIMARY KEY,
              category_id TEXT NOT NULL,
              menu_number TEXT,
              name TEXT NOT NULL,
              item_translation TEXT,
              description TEXT,
              base_price REAL NOT NULL,
              is_available INTEGER NOT NULL DEFAULT 1,
              is_bundle INTEGER NOT NULL DEFAULT 0,
              option_groups_json TEXT NOT NULL DEFAULT '[]',
              sort_order INTEGER NOT NULL DEFAULT 0
            );

            CREATE TABLE IF NOT EXISTS customers (
              id TEXT PRIMARY KEY,
              name TEXT NOT NULL,
              phone TEXT NOT NULL,
              notes TEXT,
              addresses_json TEXT NOT NULL DEFAULT '[]',
              created_at TEXT NOT NULL,
              updated_at TEXT NOT NULL
            );

            CREATE INDEX IF NOT EXISTS idx_customers_phone ON customers(phone);

            CREATE TABLE IF NOT EXISTS orders (
              id TEXT PRIMARY KEY,
              order_number TEXT NOT NULL,
              order_type TEXT NOT NULL,
              source TEXT NOT NULL,
              status TEXT NOT NULL,
              customer_id TEXT,
              customer_name TEXT,
              customer_phone TEXT,
              delivery_address TEXT,
              delivery_postcode TEXT,
              lines_json TEXT NOT NULL,
              subtotal REAL NOT NULL,
              delivery_fee REAL NOT NULL,
              discount_total REAL NOT NULL,
              total REAL NOT NULL,
              notes TEXT,
              online_external_id TEXT,
              online_payload TEXT,
              kitchen_printed INTEGER NOT NULL DEFAULT 0,
              front_printed INTEGER NOT NULL DEFAULT 0,
              online_acked INTEGER NOT NULL DEFAULT 0,
              tenders_json TEXT NOT NULL DEFAULT '[]',
              created_at TEXT NOT NULL,
              updated_at TEXT NOT NULL
            );

            CREATE INDEX IF NOT EXISTS idx_orders_created ON orders(created_at);
            CREATE INDEX IF NOT EXISTS idx_orders_online ON orders(online_external_id);
            CREATE INDEX IF NOT EXISTS idx_orders_number ON orders(order_number);

            CREATE TABLE IF NOT EXISTS print_jobs (
              id TEXT PRIMARY KEY,
              order_id TEXT NOT NULL,
              order_number TEXT NOT NULL,
              channel TEXT NOT NULL,
              status TEXT NOT NULL,
              payload_text TEXT,
              error TEXT,
              attempts INTEGER NOT NULL DEFAULT 0,
              created_at TEXT NOT NULL,
              printed_at TEXT
            );

            CREATE INDEX IF NOT EXISTS idx_print_jobs_status ON print_jobs(status);
            """;
        cmd.ExecuteNonQuery();
        EnsureColumn(conn, "orders", "requested_for", "TEXT");
        EnsureColumn(conn, "orders", "fulfilment_label", "TEXT");
        EnsureColumn(conn, "orders", "payment_label", "TEXT");
        EnsureColumn(conn, "orders", "ticket_footer", "TEXT");
        EnsureColumn(conn, "orders", "below_minimum_surcharge", "REAL NOT NULL DEFAULT 0");
    }

    private static void EnsureColumn(SqliteConnection conn, string table, string column, string typeSql)
    {
        using var check = conn.CreateCommand();
        check.CommandText = $"PRAGMA table_info({table})";
        using var r = check.ExecuteReader();
        while (r.Read())
        {
            if (string.Equals(Convert.ToString(r["name"]), column, StringComparison.OrdinalIgnoreCase))
                return;
        }
        r.Close();
        using var alter = conn.CreateCommand();
        alter.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {typeSql}";
        alter.ExecuteNonQuery();
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
