using Microsoft.Data.Sqlite;
using RingOrder.Epos.Domain;

namespace RingOrder.Epos.Data;

public sealed class CustomerRepository
{
    private readonly EposDb _db;

    public CustomerRepository(EposDb db) => _db = db;

    public void Upsert(Customer c)
    {
        c.UpdatedAt = DateTimeOffset.Now;
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO customers(id,name,phone,phone_digits,notes,addresses_json,created_at,updated_at)
            VALUES($id,$n,$p,$pd,$notes,$aj,$ca,$ua)
            ON CONFLICT(id) DO UPDATE SET
              name=excluded.name, phone=excluded.phone, phone_digits=excluded.phone_digits,
              notes=excluded.notes, addresses_json=excluded.addresses_json,
              updated_at=excluded.updated_at
            """;
        cmd.Parameters.AddWithValue("$id", c.Id);
        cmd.Parameters.AddWithValue("$n", c.Name);
        cmd.Parameters.AddWithValue("$p", c.Phone);
        cmd.Parameters.AddWithValue("$pd", NormalizePhone(c.Phone));
        cmd.Parameters.AddWithValue("$notes", (object?)c.Notes ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$aj", JsonUtil.Serialize(c.Addresses));
        cmd.Parameters.AddWithValue("$ca", c.CreatedAt.ToString("o"));
        cmd.Parameters.AddWithValue("$ua", c.UpdatedAt.ToString("o"));
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
        using var r = cmd.ExecuteReader();
        return r.Read() ? Read(r) : null;
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
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add(Read(r));
        return list;
    }

    public static string NormalizePhone(string phone) =>
        new(phone.Where(char.IsDigit).ToArray());

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
            Addresses = JsonUtil.Deserialize<List<CustomerAddress>>(S("addresses_json")),
            CreatedAt = DateTimeOffset.Parse(S("created_at")),
            UpdatedAt = DateTimeOffset.Parse(S("updated_at")),
        };
    }
}
