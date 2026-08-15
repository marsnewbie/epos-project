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
        new(2, "order_discount_reason", OrderDiscountReason),
        new(3, "print_devices_and_routing", PrintDevicesAndRouting),
        new(4, "address_cache", AddressCache),
        new(5, "addresses_and_customer_links", AddressesAndCustomerLinks),
        new(6, "refunds", Refunds),
        new(7, "delivery_zones", DeliveryZones),
        new(8, "delivery_miles_and_levels", DeliveryMilesAndLevels),
    ];

    /// <summary>
    /// Brings delivery into line with the RingOrder website.
    /// <para>
    /// Zones gain <c>is_active</c>, and the prefix is stored canonically with its
    /// space — "B44 0" is a sector and "B40" is a district, and squashing the
    /// space out turns one into the other. Version 7 shipped for a day and
    /// matched prefixes as strings, which would have let a B4 rule price a B47
    /// delivery; the unique index is rebuilt because the stored form changes.
    /// </para>
    /// <para>
    /// Miles bands arrive alongside, so a shop pricing by road distance on its
    /// website prices the same way on the till.
    /// </para>
    /// </summary>
    private const string DeliveryMilesAndLevels = """
        ALTER TABLE delivery_zones ADD COLUMN is_active INTEGER NOT NULL DEFAULT 1;

        DROP INDEX IF EXISTS idx_delivery_zones_prefix;
        CREATE UNIQUE INDEX idx_delivery_zones_prefix ON delivery_zones(prefix);

        CREATE TABLE delivery_miles_bands (
          id                  TEXT PRIMARY KEY,
          min_miles           REAL NOT NULL DEFAULT 0,
          max_miles           REAL NOT NULL DEFAULT 0,
          fee_pence           INTEGER NOT NULL DEFAULT 0,
          minimum_order_pence INTEGER NOT NULL DEFAULT 0,
          free_over_pence     INTEGER NOT NULL DEFAULT 0,
          sort_order          INTEGER NOT NULL DEFAULT 0
        );

        CREATE TABLE distance_cache (
          from_postcode TEXT NOT NULL,
          to_postcode   TEXT NOT NULL,
          miles         REAL NOT NULL,
          fetched_at    TEXT NOT NULL,
          hits          INTEGER NOT NULL DEFAULT 0,
          PRIMARY KEY (from_postcode, to_postcode)
        );
        """;

    /// <summary>
    /// Delivery priced by postcode prefix.
    /// <para>
    /// The shop bundle has carried a <c>zones</c> list since the schema rebuild
    /// and nothing ever read it — every delivery was charged the one flat default.
    /// This is the table behind it.
    /// </para>
    /// </summary>
    private const string DeliveryZones = """
        CREATE TABLE delivery_zones (
          id                  TEXT PRIMARY KEY,
          prefix              TEXT NOT NULL,
          name                TEXT NOT NULL DEFAULT '',
          fee_pence           INTEGER NOT NULL DEFAULT 0,
          minimum_order_pence INTEGER NOT NULL DEFAULT 0,
          free_over_pence     INTEGER NOT NULL DEFAULT 0,
          is_deliverable      INTEGER NOT NULL DEFAULT 1,
          sort_order          INTEGER NOT NULL DEFAULT 0
        );

        CREATE UNIQUE INDEX idx_delivery_zones_prefix ON delivery_zones(prefix);
        """;

    /// <summary>
    /// Money given back, recorded rather than subtracted.
    /// <para>
    /// The refund row holds why it happened and who did it; the matching
    /// <c>payments</c> row holds the money, stored negative and flagged. Both
    /// exist because they answer different questions — the payment row keeps the
    /// drawer and the shift totals right without any query having to know what a
    /// refund is, and the refund row is what a manager reads when the takings
    /// look short.
    /// </para>
    /// <para>
    /// Storing the amount negative means every existing sum over <c>payments</c>
    /// keeps working and quietly becomes a net figure — which is the safe
    /// direction for a total to be wrong in.
    /// </para>
    /// </summary>
    private const string Refunds = """
        ALTER TABLE payments ADD COLUMN is_refund INTEGER NOT NULL DEFAULT 0;

        CREATE TABLE refunds (
          id           TEXT PRIMARY KEY,
          order_id     TEXT NOT NULL,
          shift_id     TEXT,
          staff_id     TEXT,
          amount_pence INTEGER NOT NULL,
          tender_type  TEXT NOT NULL,
          reason       TEXT NOT NULL,
          lines_json   TEXT NOT NULL DEFAULT '[]',
          is_full      INTEGER NOT NULL DEFAULT 0,
          at           TEXT NOT NULL
        );

        CREATE INDEX idx_refunds_order ON refunds(order_id);
        CREATE INDEX idx_refunds_shift ON refunds(shift_id);
        """;

    /// <summary>
    /// Splits a place from a person's relationship to it.
    /// <para>
    /// Addresses used to live as a JSON blob on the customer row, which meant the
    /// same door was stored once per customer who lived at it, could not be
    /// indexed, and could not be told apart from the personal data wrapped around
    /// it. Now <c>addresses</c> holds the building — public geography, shared,
    /// deduplicated on a fingerprint — and <c>customer_addresses</c> holds the
    /// link plus the parts that belong to the household, like the note telling a
    /// driver which bell to ring.
    /// </para>
    /// <para>
    /// This is what makes erasure possible without gutting the business: deleting
    /// a customer takes their links and their notes, and leaves a delivery map
    /// that never named anybody.
    /// </para>
    /// <para>
    /// The tables arrive empty. Existing <c>customers.addresses_json</c> is moved
    /// across by <c>AddressBackfill</c>, in C#, so the fingerprint that decides
    /// whether two rows are the same door is computed by exactly one piece of
    /// code rather than by an SQL approximation of it.
    /// </para>
    /// </summary>
    private const string AddressesAndCustomerLinks = """
        CREATE TABLE addresses (
          id          TEXT PRIMARY KEY,
          fingerprint TEXT NOT NULL UNIQUE,
          line1       TEXT NOT NULL,
          line2       TEXT,
          town        TEXT NOT NULL DEFAULT '',
          postcode    TEXT NOT NULL DEFAULT '',
          outward     TEXT NOT NULL DEFAULT '',
          latitude    REAL,
          longitude   REAL,
          source      TEXT NOT NULL DEFAULT 'Manual',
          created_at  TEXT NOT NULL
        );

        CREATE INDEX idx_addresses_postcode ON addresses(postcode);
        CREATE INDEX idx_addresses_outward ON addresses(outward);

        CREATE TABLE customer_addresses (
          id           TEXT PRIMARY KEY,
          customer_id  TEXT NOT NULL,
          address_id   TEXT NOT NULL,
          label        TEXT NOT NULL DEFAULT 'Home',
          notes        TEXT,
          is_default   INTEGER NOT NULL DEFAULT 0,
          created_at   TEXT NOT NULL,
          last_used_at TEXT,
          UNIQUE(customer_id, address_id)
        );

        CREATE INDEX idx_customer_addresses_customer ON customer_addresses(customer_id);
        CREATE INDEX idx_customer_addresses_address ON customer_addresses(address_id);

        ALTER TABLE customers ADD COLUMN last_order_at TEXT;
        """;

    /// <summary>
    /// Answers from the postcode lookup, kept forever.
    /// <para>
    /// A takeaway delivers inside a few miles and serves the same streets for
    /// years, so its universe is a couple of thousand postcodes that never
    /// change. Cached, a paid lookup is charged once per postcode for the life of
    /// the shop rather than once per phone call — which is what makes a
    /// pay-per-lookup provider affordable here at all.
    /// </para>
    /// <para>
    /// <c>hits</c> is not statistics for its own sake: it is the evidence in
    /// Settings that the cache is doing the work, so a merchant looking at a bill
    /// can see how many lookups they did not pay for.
    /// </para>
    /// </summary>
    private const string AddressCache = """
        CREATE TABLE address_cache (
          postcode   TEXT PRIMARY KEY,
          provider   TEXT NOT NULL,
          payload    TEXT NOT NULL,
          town       TEXT NOT NULL DEFAULT '',
          latitude   REAL,
          longitude  REAL,
          fetched_at TEXT NOT NULL,
          hits       INTEGER NOT NULL DEFAULT 0
        );
        """;

    /// <summary>
    /// A discount without a reason is an unexplained hole in the takings. The
    /// amount already existed; this is the half that makes it auditable.
    /// </summary>
    private const string OrderDiscountReason = """
        ALTER TABLE orders ADD COLUMN discount_reason TEXT;
        """;

    /// <summary>
    /// Printers become a registry with routing rules, and the job queue gains
    /// what it needs to survive a restart: the rendered bytes, the device it is
    /// for, and when to try again.
    /// <para>
    /// The old print_jobs table is dropped rather than migrated. It held a
    /// one-line description of a job that had already printed — history of an
    /// event, not work outstanding — and no shop has one worth keeping.
    /// </para>
    /// </summary>
    private const string PrintDevicesAndRouting = """
        CREATE TABLE print_devices (
          id             TEXT PRIMARY KEY,
          name           TEXT NOT NULL,
          transport      TEXT NOT NULL,
          address        TEXT NOT NULL,
          paper_width_mm INTEGER NOT NULL DEFAULT 80,
          encoding       TEXT NOT NULL DEFAULT 'gbk',
          cjk_as_raster  INTEGER NOT NULL DEFAULT 1,
          has_cash_drawer INTEGER NOT NULL DEFAULT 0,
          is_enabled     INTEGER NOT NULL DEFAULT 1,
          sort_order     INTEGER NOT NULL DEFAULT 0
        );

        CREATE TABLE print_routes (
          id                  TEXT PRIMARY KEY,
          sort_order          INTEGER NOT NULL DEFAULT 0,
          is_enabled          INTEGER NOT NULL DEFAULT 1,
          document            TEXT NOT NULL,
          print_class         TEXT,
          service_type        TEXT,
          channel             TEXT,
          device_id           TEXT NOT NULL,
          copies              INTEGER NOT NULL DEFAULT 1,
          fallback_device_id  TEXT
        );

        CREATE INDEX idx_print_routes_device ON print_routes(device_id);

        DROP TABLE print_jobs;

        CREATE TABLE print_jobs (
          id              TEXT PRIMARY KEY,
          order_id        TEXT NOT NULL,
          order_number    TEXT NOT NULL,
          device_id       TEXT NOT NULL,
          document        TEXT NOT NULL,
          template        TEXT NOT NULL,
          copies          INTEGER NOT NULL DEFAULT 1,
          status          TEXT NOT NULL,
          payload         BLOB NOT NULL,
          attempts        INTEGER NOT NULL DEFAULT 0,
          error           TEXT,
          next_attempt_at TEXT,
          created_at      TEXT NOT NULL,
          printed_at      TEXT
        );

        CREATE INDEX idx_print_jobs_pending ON print_jobs(status, device_id, next_attempt_at);
        CREATE INDEX idx_print_jobs_order ON print_jobs(order_id);
        """;

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
