using Microsoft.Data.Sqlite;
using RingOrder.Epos.Domain;

namespace RingOrder.Epos.Data;

/// <summary>
/// The phone book. Personal data throughout — see <c>docs/PRIVACY.md</c> — so
/// the addresses themselves live in <see cref="AddressRepository"/> and only the
/// link between a person and a place is stored here.
/// </summary>
public sealed class CustomerRepository
{
    private readonly EposDb _db;
    private readonly AddressRepository _addresses;

    public CustomerRepository(EposDb db, AddressRepository addresses)
    {
        _db = db;
        _addresses = addresses;
    }

    public void Upsert(Customer c)
    {
        c.UpdatedAt = DateTimeOffset.Now;
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO customers(id,name,phone,phone_digits,notes,addresses_json,created_at,updated_at,last_order_at)
            VALUES($id,$n,$p,$pd,$notes,'[]',$ca,$ua,$lo)
            ON CONFLICT(id) DO UPDATE SET
              name=excluded.name, phone=excluded.phone, phone_digits=excluded.phone_digits,
              notes=excluded.notes, updated_at=excluded.updated_at,
              last_order_at=COALESCE(excluded.last_order_at, customers.last_order_at)
            """;
        cmd.Parameters.AddWithValue("$id", c.Id);
        cmd.Parameters.AddWithValue("$n", c.Name);
        cmd.Parameters.AddWithValue("$p", c.Phone);
        cmd.Parameters.AddWithValue("$pd", NormalizePhone(c.Phone));
        cmd.Parameters.AddWithValue("$notes", (object?)c.Notes ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$ca", c.CreatedAt.ToString("o"));
        cmd.Parameters.AddWithValue("$ua", c.UpdatedAt.ToString("o"));
        cmd.Parameters.AddWithValue("$lo", (object?)c.LastOrderAt?.ToString("o") ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Attaches a place to a customer, creating the place if the shop has never
    /// delivered there. Returns the link, or null when there was no address worth
    /// storing.
    /// </summary>
    public CustomerAddress? SaveAddress(
        Customer customer,
        string? line1, string? line2, string? town, string? postcode,
        AddressSource source = AddressSource.Manual,
        string label = "Home",
        string? notes = null,
        bool makeDefault = false,
        double? latitude = null, double? longitude = null)
    {
        var address = _addresses.GetOrCreate(line1, line2, town, postcode, source, latitude, longitude);
        if (address is null) return null;

        using var conn = _db.Open();

        // A customer who moves back to an old address should not get a second
        // link to it, so the pair is unique and an existing one is updated.
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO customer_addresses(id,customer_id,address_id,label,notes,is_default,created_at,last_used_at)
            VALUES($id,$cid,$aid,$label,$notes,$def,$at,$at)
            ON CONFLICT(customer_id,address_id) DO UPDATE SET
              label=excluded.label,
              notes=COALESCE(excluded.notes, customer_addresses.notes),
              is_default=MAX(excluded.is_default, customer_addresses.is_default),
              last_used_at=excluded.last_used_at
            """;
        var link = new CustomerAddress
        {
            CustomerId = customer.Id,
            AddressId = address.Id,
            Label = label,
            Notes = notes,
            IsDefault = makeDefault || customer.Addresses.Count == 0,
            LastUsedAt = DateTimeOffset.Now,
            Address = address,
        };
        cmd.Parameters.AddWithValue("$id", link.Id);
        cmd.Parameters.AddWithValue("$cid", link.CustomerId);
        cmd.Parameters.AddWithValue("$aid", link.AddressId);
        cmd.Parameters.AddWithValue("$label", link.Label);
        cmd.Parameters.AddWithValue("$notes", (object?)link.Notes ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$def", link.IsDefault ? 1 : 0);
        cmd.Parameters.AddWithValue("$at", DateTimeOffset.Now.ToString("o"));
        cmd.ExecuteNonQuery();

        if (link.IsDefault) ClearOtherDefaults(conn, customer.Id, link.AddressId);

        return link;
    }

    /// <summary>Records that an order went to this address, which is what retention counts from.</summary>
    public void TouchAddress(string customerId, string addressId)
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "UPDATE customer_addresses SET last_used_at=$at WHERE customer_id=$cid AND address_id=$aid";
        cmd.Parameters.AddWithValue("$at", DateTimeOffset.Now.ToString("o"));
        cmd.Parameters.AddWithValue("$cid", customerId);
        cmd.Parameters.AddWithValue("$aid", addressId);
        cmd.ExecuteNonQuery();
    }

    /// <summary>Stamps the last order, so a ten-year regular is never treated as dormant.</summary>
    public void RecordOrder(string customerId, DateTimeOffset when)
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE customers SET last_order_at=$at, updated_at=$at WHERE id=$id";
        cmd.Parameters.AddWithValue("$at", when.ToString("o"));
        cmd.Parameters.AddWithValue("$id", customerId);
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Matches on digits only, so a number saved as "0121 296 6775" is still
    /// found when caller ID delivers "01212966775".
    /// </summary>
    public Customer? FindByPhone(string phone)
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM customers WHERE phone_digits=$p LIMIT 1";
        cmd.Parameters.AddWithValue("$p", NormalizePhone(phone));

        var found = new List<Customer>();
        using (var r = cmd.ExecuteReader())
            if (r.Read()) found.Add(Read(r));

        AttachAddresses(conn, found);
        return found.FirstOrDefault();
    }

    public Customer? ById(string id)
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM customers WHERE id=$id";
        cmd.Parameters.AddWithValue("$id", id);

        var found = new List<Customer>();
        using (var r = cmd.ExecuteReader())
            if (r.Read()) found.Add(Read(r));

        AttachAddresses(conn, found);
        return found.FirstOrDefault();
    }

    public List<Customer> Search(string query)
    {
        var q = query.Trim();
        var all = ListAll();
        if (q.Length == 0) return all;
        var digits = NormalizePhone(q);
        return all.Where(c =>
                c.Name.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                (digits.Length > 0 && NormalizePhone(c.Phone).Contains(digits, StringComparison.Ordinal)))
            .ToList();
    }

    public List<Customer> ListAll()
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM customers ORDER BY updated_at DESC";

        var list = new List<Customer>();
        using (var r = cmd.ExecuteReader())
            while (r.Read()) list.Add(Read(r));

        AttachAddresses(conn, list);
        return list;
    }

    public int Count()
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM customers";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    public static string NormalizePhone(string phone) =>
        new(phone.Where(char.IsDigit).ToArray());

    private static void ClearOtherDefaults(SqliteConnection conn, string customerId, string keepAddressId)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "UPDATE customer_addresses SET is_default=0 WHERE customer_id=$cid AND address_id<>$aid";
        cmd.Parameters.AddWithValue("$cid", customerId);
        cmd.Parameters.AddWithValue("$aid", keepAddressId);
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Loads every customer's links in one query rather than one per customer —
    /// the phone book of a busy shop is thousands of rows, and a query per row is
    /// how a screen that used to open instantly starts taking a second.
    /// </summary>
    private static void AttachAddresses(SqliteConnection conn, List<Customer> customers)
    {
        if (customers.Count == 0) return;

        var byId = customers.ToDictionary(c => c.Id);

        foreach (var chunk in byId.Keys.Chunk(400))
        {
            var names = chunk.Select((_, i) => $"$c{i}").ToArray();

            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"""
                SELECT ca.id AS link_id, ca.customer_id, ca.address_id, ca.label, ca.notes,
                       ca.is_default, ca.created_at AS link_created_at, ca.last_used_at, a.*
                FROM customer_addresses ca
                JOIN addresses a ON a.id = ca.address_id
                WHERE ca.customer_id IN ({string.Join(",", names)})
                ORDER BY ca.is_default DESC, ca.last_used_at DESC
                """;

            for (var i = 0; i < chunk.Length; i++)
                cmd.Parameters.AddWithValue(names[i], chunk[i]);

            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var customerId = Convert.ToString(r["customer_id"])!;
                if (!byId.TryGetValue(customerId, out var customer)) continue;

                customer.Addresses.Add(new CustomerAddress
                {
                    Id = Convert.ToString(r["link_id"])!,
                    CustomerId = customerId,
                    AddressId = Convert.ToString(r["address_id"])!,
                    Label = Convert.ToString(r["label"]) ?? "Home",
                    Notes = r["notes"] is DBNull ? null : Convert.ToString(r["notes"]),
                    IsDefault = Convert.ToInt32(r["is_default"]) == 1,
                    CreatedAt = DateTimeOffset.TryParse(
                        Convert.ToString(r["link_created_at"]), out var created) ? created : DateTimeOffset.Now,
                    LastUsedAt = r["last_used_at"] is DBNull
                        ? null
                        : DateTimeOffset.Parse(Convert.ToString(r["last_used_at"])!),
                    Address = AddressRepository.Read(r),
                });
            }
        }
    }

    private static Customer Read(SqliteDataReader r)
    {
        string S(string col) => r[col] is DBNull ? "" : Convert.ToString(r[col])!;
        string? SN(string col) => r[col] is DBNull ? null : Convert.ToString(r[col]);

        return new Customer
        {
            Id = S("id"),
            Name = S("name"),
            Phone = S("phone"),
            Notes = SN("notes"),
            CreatedAt = DateTimeOffset.Parse(S("created_at")),
            UpdatedAt = DateTimeOffset.Parse(S("updated_at")),
            LastOrderAt = SN("last_order_at") is { Length: > 0 } lo ? DateTimeOffset.Parse(lo) : null,
        };
    }
}
