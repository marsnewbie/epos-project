using Microsoft.Data.Sqlite;

namespace RingOrder.Epos.Data;

/// <summary>
/// Ordered, numbered schema steps. Every install records which ones it has run,
/// so an upgrade is the same operation on a shop that skipped three releases as
/// on one that is current.
/// </summary>
/// <remarks>
/// Rules for adding a migration: append, never edit a shipped one; keep each one
/// idempotent enough to be safe if a crash lands between the statement and the
/// bookkeeping row; and never write a step that throws away data a shop cannot
/// re-enter. There is no "down" — a bad release is fixed by a new migration or
/// by restoring the backup the runner takes before it starts.
/// </remarks>
public sealed record Migration(int Version, string Name, string Sql);

public static class SchemaMigrations
{
    public static IReadOnlyList<Migration> All { get; } =
    [
        new(1, "initial", InitialSchema),
    ];

    public static int LatestVersion => All.Max(m => m.Version);

    /// <summary>
    /// Money is INTEGER pence everywhere. SQLite REAL is binary floating point,
    /// and a till whose day total lands a penny out cannot be reconciled.
    /// </summary>
    private const string InitialSchema = """
        CREATE TABLE settings (
          key   TEXT PRIMARY KEY,
          value TEXT NOT NULL
        );

        CREATE TABLE staff (
          id             TEXT PRIMARY KEY,
          name           TEXT NOT NULL,
          role           TEXT NOT NULL,
          pin_hash       TEXT NOT NULL,
          pin_salt       TEXT NOT NULL,
          must_change_pin INTEGER NOT NULL DEFAULT 0,
          is_active      INTEGER NOT NULL DEFAULT 1,
          created_at     TEXT NOT NULL
        );

        CREATE TABLE shifts (
          id                 TEXT PRIMARY KEY,
          number             INTEGER NOT NULL,
          status             TEXT NOT NULL,
          terminal_id        TEXT,
          opened_by_staff_id TEXT NOT NULL,
          opened_at          TEXT NOT NULL,
          opening_float_pence INTEGER NOT NULL DEFAULT 0,
          closed_by_staff_id TEXT,
          closed_at          TEXT,
          declared_cash_pence INTEGER,
          expected_cash_pence INTEGER,
          notes              TEXT
        );

        CREATE UNIQUE INDEX idx_shifts_number ON shifts(number);
        CREATE INDEX idx_shifts_status ON shifts(status);

        CREATE TABLE cash_movements (
          id           TEXT PRIMARY KEY,
          shift_id     TEXT NOT NULL,
          staff_id     TEXT NOT NULL,
          amount_pence INTEGER NOT NULL,
          reason       TEXT NOT NULL,
          at           TEXT NOT NULL
        );

        CREATE INDEX idx_cash_movements_shift ON cash_movements(shift_id);

        CREATE TABLE audit_log (
          id         TEXT PRIMARY KEY,
          staff_id   TEXT,
          shift_id   TEXT,
          action     TEXT NOT NULL,
          subject_id TEXT,
          detail     TEXT,
          at         TEXT NOT NULL
        );

        CREATE INDEX idx_audit_at ON audit_log(at);
        CREATE INDEX idx_audit_action ON audit_log(action);

        CREATE TABLE tax_classes (
          id                TEXT PRIMARY KEY,
          name              TEXT NOT NULL,
          rate_basis_points INTEGER NOT NULL
        );

        CREATE TABLE categories (
          id           TEXT PRIMARY KEY,
          name         TEXT NOT NULL,
          translation  TEXT,
          description  TEXT,
          sort_order   INTEGER NOT NULL DEFAULT 0,
          is_visible   INTEGER NOT NULL DEFAULT 1,
          print_class  TEXT NOT NULL DEFAULT 'kitchen',
          tax_class_id TEXT NOT NULL DEFAULT 'hot-food'
        );

        CREATE TABLE menu_items (
          id              TEXT PRIMARY KEY,
          category_id     TEXT NOT NULL,
          menu_number     TEXT,
          name            TEXT NOT NULL,
          item_translation TEXT,
          description     TEXT,
          base_price_pence INTEGER NOT NULL,
          print_class     TEXT,
          tax_class_id    TEXT,
          is_available    INTEGER NOT NULL DEFAULT 1,
          is_bundle       INTEGER NOT NULL DEFAULT 0,
          sort_order      INTEGER NOT NULL DEFAULT 0
        );

        CREATE INDEX idx_menu_items_category ON menu_items(category_id);
        CREATE INDEX idx_menu_items_number ON menu_items(menu_number);

        CREATE TABLE option_groups (
          id             TEXT PRIMARY KEY,
          name           TEXT NOT NULL,
          translation    TEXT,
          type           TEXT NOT NULL,
          required       INTEGER NOT NULL DEFAULT 0,
          min_selections INTEGER,
          max_selections INTEGER
        );

        CREATE TABLE option_choices (
          id                 TEXT PRIMARY KEY,
          group_id           TEXT NOT NULL,
          label              TEXT NOT NULL,
          translation        TEXT,
          price_delta_pence  INTEGER NOT NULL DEFAULT 0,
          is_default         INTEGER NOT NULL DEFAULT 0,
          is_available       INTEGER NOT NULL DEFAULT 1,
          sort_order         INTEGER NOT NULL DEFAULT 0
        );

        CREATE INDEX idx_option_choices_group ON option_choices(group_id);

        -- A dish's use of a shared group. sort_order and show_when live here
        -- because two dishes may place or reveal the same group differently.
        CREATE TABLE menu_item_option_groups (
          item_id       TEXT NOT NULL,
          group_id      TEXT NOT NULL,
          sort_order    INTEGER NOT NULL DEFAULT 0,
          show_when_json TEXT,
          PRIMARY KEY (item_id, group_id)
        );

        CREATE TABLE customers (
          id             TEXT PRIMARY KEY,
          name           TEXT NOT NULL,
          phone          TEXT NOT NULL,
          phone_digits   TEXT NOT NULL,
          notes          TEXT,
          addresses_json TEXT NOT NULL DEFAULT '[]',
          created_at     TEXT NOT NULL,
          updated_at     TEXT NOT NULL
        );

        CREATE INDEX idx_customers_phone ON customers(phone_digits);

        CREATE TABLE orders (
          id                    TEXT PRIMARY KEY,
          order_number          TEXT NOT NULL,
          service_type          TEXT NOT NULL,
          channel               TEXT NOT NULL,
          platform_name         TEXT,
          customer_waiting      INTEGER NOT NULL DEFAULT 0,
          status                TEXT NOT NULL,
          terminal_id           TEXT,
          staff_id              TEXT,
          shift_id              TEXT,
          customer_id           TEXT,
          customer_name         TEXT,
          customer_phone        TEXT,
          delivery_address      TEXT,
          delivery_postcode     TEXT,
          table_number          TEXT,
          hold_label            TEXT,
          void_reason           TEXT,
          subtotal_pence        INTEGER NOT NULL DEFAULT 0,
          delivery_fee_pence    INTEGER NOT NULL DEFAULT 0,
          discount_total_pence  INTEGER NOT NULL DEFAULT 0,
          below_minimum_pence   INTEGER NOT NULL DEFAULT 0,
          total_pence           INTEGER NOT NULL DEFAULT 0,
          notes                 TEXT,
          requested_for         TEXT,
          fulfilment_label      TEXT,
          payment_label         TEXT,
          ticket_footer         TEXT,
          online_external_id    TEXT,
          online_payload        TEXT,
          kitchen_printed       INTEGER NOT NULL DEFAULT 0,
          front_printed         INTEGER NOT NULL DEFAULT 0,
          online_acked          INTEGER NOT NULL DEFAULT 0,
          created_at            TEXT NOT NULL,
          updated_at            TEXT NOT NULL
        );

        CREATE INDEX idx_orders_created ON orders(created_at);
        CREATE INDEX idx_orders_shift ON orders(shift_id);
        CREATE INDEX idx_orders_channel ON orders(channel);
        CREATE UNIQUE INDEX idx_orders_number ON orders(order_number);
        CREATE INDEX idx_orders_online ON orders(online_external_id);

        -- Lines are rows, not a JSON blob: "what sold this month" is the first
        -- question an owner asks, and a blob cannot answer it.
        CREATE TABLE order_lines (
          id                TEXT PRIMARY KEY,
          order_id          TEXT NOT NULL,
          line_number       INTEGER NOT NULL,
          item_id           TEXT,
          name              TEXT NOT NULL,
          item_translation  TEXT,
          quantity          INTEGER NOT NULL DEFAULT 1,
          base_price_pence  INTEGER NOT NULL DEFAULT 0,
          line_total_pence  INTEGER NOT NULL DEFAULT 0,
          tax_class_id      TEXT,
          print_class       TEXT,
          notes             TEXT,
          is_ad_hoc         INTEGER NOT NULL DEFAULT 0,
          kitchen_sent      INTEGER NOT NULL DEFAULT 0,
          kitchen_sent_at   TEXT,
          selections_json   TEXT NOT NULL DEFAULT '[]'
        );

        CREATE INDEX idx_order_lines_order ON order_lines(order_id);
        CREATE INDEX idx_order_lines_item ON order_lines(item_id);

        CREATE TABLE payments (
          id                 TEXT PRIMARY KEY,
          order_id           TEXT NOT NULL,
          shift_id           TEXT,
          staff_id           TEXT,
          tender_type        TEXT NOT NULL,
          amount_pence       INTEGER NOT NULL,
          cash_received_pence INTEGER,
          change_given_pence INTEGER,
          reference          TEXT,
          at                 TEXT NOT NULL
        );

        CREATE INDEX idx_payments_order ON payments(order_id);
        CREATE INDEX idx_payments_shift ON payments(shift_id);

        CREATE TABLE print_jobs (
          id           TEXT PRIMARY KEY,
          order_id     TEXT NOT NULL,
          order_number TEXT NOT NULL,
          channel      TEXT NOT NULL,
          status       TEXT NOT NULL,
          payload_text TEXT,
          error        TEXT,
          attempts     INTEGER NOT NULL DEFAULT 0,
          created_at   TEXT NOT NULL,
          printed_at   TEXT
        );

        CREATE INDEX idx_print_jobs_status ON print_jobs(status);
        CREATE INDEX idx_print_jobs_order ON print_jobs(order_id);
        """;

    public static int CurrentVersion(SqliteConnection conn)
    {
        using var create = conn.CreateCommand();
        create.CommandText = """
            CREATE TABLE IF NOT EXISTS schema_migrations (
              version    INTEGER PRIMARY KEY,
              name       TEXT NOT NULL,
              applied_at TEXT NOT NULL
            )
            """;
        create.ExecuteNonQuery();

        using var read = conn.CreateCommand();
        read.CommandText = "SELECT COALESCE(MAX(version), 0) FROM schema_migrations";
        return Convert.ToInt32(read.ExecuteScalar());
    }

    /// <summary>Applies every pending step in order. Returns the versions run.</summary>
    public static IReadOnlyList<int> Apply(SqliteConnection conn)
    {
        var from = CurrentVersion(conn);
        var applied = new List<int>();

        foreach (var migration in All.Where(m => m.Version > from).OrderBy(m => m.Version))
        {
            using var tx = conn.BeginTransaction();
            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = migration.Sql;
                cmd.ExecuteNonQuery();
            }

            using (var stamp = conn.CreateCommand())
            {
                stamp.Transaction = tx;
                stamp.CommandText =
                    "INSERT INTO schema_migrations(version, name, applied_at) VALUES($v, $n, $a)";
                stamp.Parameters.AddWithValue("$v", migration.Version);
                stamp.Parameters.AddWithValue("$n", migration.Name);
                stamp.Parameters.AddWithValue("$a", DateTimeOffset.Now.ToString("o"));
                stamp.ExecuteNonQuery();
            }

            tx.Commit();
            applied.Add(migration.Version);
        }

        return applied;
    }
}
