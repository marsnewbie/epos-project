using Microsoft.Data.Sqlite;
using RingOrder.Epos.Domain;

namespace RingOrder.Epos.Data;

public sealed class StaffRepository
{
    private readonly EposDb _db;

    public StaffRepository(EposDb db) => _db = db;

    public int CountActive()
    {
        var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM staff WHERE is_active=1";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    public List<StaffMember> ListAll(bool activeOnly = true)
    {
        var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = Select + (activeOnly ? " WHERE is_active=1" : "") + " ORDER BY name";
        var list = new List<StaffMember>();
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add(Read(r));
        return list;
    }

    public StaffMember? GetById(string id)
    {
        var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = Select + " WHERE id=$id";
        cmd.Parameters.AddWithValue("$id", id);
        using var r = cmd.ExecuteReader();
        return r.Read() ? Read(r) : null;
    }

    /// <summary>
    /// Finds whoever owns this PIN. Every active member is checked rather than
    /// looked up, because a PIN is a secret, not a key — two people cannot be
    /// distinguished by it, so a duplicate must resolve to the first match
    /// deterministically instead of failing at a unique index.
    /// </summary>
    public StaffMember? Authenticate(string pin)
    {
        if (string.IsNullOrWhiteSpace(pin)) return null;
        return ListAll().FirstOrDefault(s => PinHasher.Verify(pin, s.PinHash, s.PinSalt));
    }

    public void Upsert(StaffMember staff)
    {
        var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO staff(id,name,role,pin_hash,pin_salt,must_change_pin,is_active,created_at)
            VALUES($id,$n,$r,$h,$s,$m,$a,$c)
            ON CONFLICT(id) DO UPDATE SET
              name=excluded.name,
              role=excluded.role,
              pin_hash=excluded.pin_hash,
              pin_salt=excluded.pin_salt,
              must_change_pin=excluded.must_change_pin,
              is_active=excluded.is_active
            """;
        cmd.Parameters.AddWithValue("$id", staff.Id);
        cmd.Parameters.AddWithValue("$n", staff.Name);
        cmd.Parameters.AddWithValue("$r", staff.Role.ToString());
        cmd.Parameters.AddWithValue("$h", staff.PinHash);
        cmd.Parameters.AddWithValue("$s", staff.PinSalt);
        cmd.Parameters.AddWithValue("$m", staff.MustChangePin ? 1 : 0);
        cmd.Parameters.AddWithValue("$a", staff.IsActive ? 1 : 0);
        cmd.Parameters.AddWithValue("$c", staff.CreatedAt.ToString("o"));
        cmd.ExecuteNonQuery();
    }

    public void SetPin(string staffId, string pin)
    {
        var (hash, salt) = PinHasher.Hash(pin);
        var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "UPDATE staff SET pin_hash=$h, pin_salt=$s, must_change_pin=0 WHERE id=$id";
        cmd.Parameters.AddWithValue("$h", hash);
        cmd.Parameters.AddWithValue("$s", salt);
        cmd.Parameters.AddWithValue("$id", staffId);
        cmd.ExecuteNonQuery();
    }

    private const string Select =
        "SELECT id,name,role,pin_hash,pin_salt,must_change_pin,is_active,created_at FROM staff";

    private static StaffMember Read(SqliteDataReader r) => new()
    {
        Id = r.GetString(0),
        Name = r.GetString(1),
        Role = Enum.Parse<StaffRole>(r.GetString(2)),
        PinHash = r.GetString(3),
        PinSalt = r.GetString(4),
        MustChangePin = r.GetInt32(5) == 1,
        IsActive = r.GetInt32(6) == 1,
        CreatedAt = DateTimeOffset.Parse(r.GetString(7)),
    };
}

public sealed class AuditRepository
{
    private readonly EposDb _db;

    public AuditRepository(EposDb db) => _db = db;

    public void Record(AuditEntry entry)
    {
        var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO audit_log(id,staff_id,shift_id,action,subject_id,detail,at)
            VALUES($id,$st,$sh,$a,$su,$d,$at)
            """;
        cmd.Parameters.AddWithValue("$id", entry.Id);
        cmd.Parameters.AddWithValue("$st", (object?)entry.StaffId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$sh", (object?)entry.ShiftId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$a", entry.Action);
        cmd.Parameters.AddWithValue("$su", (object?)entry.SubjectId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$d", (object?)entry.Detail ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$at", entry.At.ToString("o"));
        cmd.ExecuteNonQuery();
    }

    public List<AuditEntry> Recent(int take = 200)
    {
        var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT id,staff_id,shift_id,action,subject_id,detail,at FROM audit_log ORDER BY at DESC LIMIT $n";
        cmd.Parameters.AddWithValue("$n", take);
        var list = new List<AuditEntry>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            list.Add(new AuditEntry
            {
                Id = r.GetString(0),
                StaffId = r.IsDBNull(1) ? null : r.GetString(1),
                ShiftId = r.IsDBNull(2) ? null : r.GetString(2),
                Action = r.GetString(3),
                SubjectId = r.IsDBNull(4) ? null : r.GetString(4),
                Detail = r.IsDBNull(5) ? null : r.GetString(5),
                At = DateTimeOffset.Parse(r.GetString(6)),
            });
        }
        return list;
    }
}
