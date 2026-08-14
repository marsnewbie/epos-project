using Microsoft.Data.Sqlite;
using RingOrder.Epos.Domain;

namespace RingOrder.Epos.Data;

/// <summary>
/// Totals for one trading session, computed from the rows that carry its id.
/// This is what an X or Z report prints.
/// </summary>
public sealed record ShiftTotals(
    decimal OpeningFloat,
    decimal CashSales,
    decimal CardSales,
    decimal PrepaidSales,
    decimal OtherSales,
    decimal CashMovements,
    decimal OutstandingDue,
    int OrdersPaid,
    int OrdersOpen,
    int OrdersVoided,
    decimal GrossPaid)
{
    /// <summary>What should be in the drawer: float, plus cash taken, plus pay-ins.</summary>
    public decimal ExpectedCash => Money.Round(OpeningFloat + CashSales + CashMovements);

    public decimal TotalTaken => Money.Round(CashSales + CardSales + PrepaidSales + OtherSales);
}

public sealed class ShiftRepository
{
    private readonly EposDb _db;

    public ShiftRepository(EposDb db) => _db = db;

    public Shift? GetOpen(string? terminalId = null)
    {
        var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = Select + " WHERE status='Open'"
            + (terminalId is null ? "" : " AND terminal_id=$t")
            + " ORDER BY opened_at DESC LIMIT 1";
        if (terminalId is not null) cmd.Parameters.AddWithValue("$t", terminalId);
        using var r = cmd.ExecuteReader();
        return r.Read() ? Read(r) : null;
    }

    public Shift? GetById(string id)
    {
        var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = Select + " WHERE id=$id";
        cmd.Parameters.AddWithValue("$id", id);
        using var r = cmd.ExecuteReader();
        return r.Read() ? Read(r) : null;
    }

    public List<Shift> Recent(int take = 30)
    {
        var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = Select + " ORDER BY number DESC LIMIT $n";
        cmd.Parameters.AddWithValue("$n", take);
        var list = new List<Shift>();
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add(Read(r));
        return list;
    }

    public Shift Open(string staffId, decimal openingFloat, string? terminalId = null)
    {
        var conn = _db.Open();
        using var next = conn.CreateCommand();
        next.CommandText = "SELECT COALESCE(MAX(number), 0) + 1 FROM shifts";
        var number = Convert.ToInt32(next.ExecuteScalar());

        var shift = new Shift
        {
            Number = number,
            Status = ShiftStatus.Open,
            TerminalId = terminalId,
            OpenedByStaffId = staffId,
            OpeningFloat = openingFloat,
        };
        Upsert(shift);
        return shift;
    }

    public void Close(Shift shift, string staffId, decimal declaredCash, decimal expectedCash, string? notes)
    {
        shift.Status = ShiftStatus.Closed;
        shift.ClosedByStaffId = staffId;
        shift.ClosedAt = DateTimeOffset.Now;
        shift.DeclaredCash = declaredCash;
        shift.ExpectedCash = expectedCash;
        shift.Notes = notes;
        Upsert(shift);
    }

    public void Upsert(Shift shift)
    {
        var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO shifts(id,number,status,terminal_id,opened_by_staff_id,opened_at,
              opening_float_pence,closed_by_staff_id,closed_at,declared_cash_pence,expected_cash_pence,notes)
            VALUES($id,$num,$st,$t,$ob,$oa,$of,$cb,$ca,$dc,$ec,$n)
            ON CONFLICT(id) DO UPDATE SET
              status=excluded.status,
              closed_by_staff_id=excluded.closed_by_staff_id,
              closed_at=excluded.closed_at,
              declared_cash_pence=excluded.declared_cash_pence,
              expected_cash_pence=excluded.expected_cash_pence,
              notes=excluded.notes
            """;
        cmd.Parameters.AddWithValue("$id", shift.Id);
        cmd.Parameters.AddWithValue("$num", shift.Number);
        cmd.Parameters.AddWithValue("$st", shift.Status.ToString());
        cmd.Parameters.AddWithValue("$t", (object?)shift.TerminalId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$ob", shift.OpenedByStaffId);
        cmd.Parameters.AddWithValue("$oa", shift.OpenedAt.ToString("o"));
        cmd.Parameters.AddWithValue("$of", Money.ToPence(shift.OpeningFloat));
        cmd.Parameters.AddWithValue("$cb", (object?)shift.ClosedByStaffId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$ca", (object?)shift.ClosedAt?.ToString("o") ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$dc",
            shift.DeclaredCash is { } d ? Money.ToPence(d) : DBNull.Value);
        cmd.Parameters.AddWithValue("$ec",
            shift.ExpectedCash is { } e ? Money.ToPence(e) : DBNull.Value);
        cmd.Parameters.AddWithValue("$n", (object?)shift.Notes ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    public void RecordCashMovement(CashMovement movement)
    {
        var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO cash_movements(id,shift_id,staff_id,amount_pence,reason,at)
            VALUES($id,$sh,$st,$a,$r,$at)
            """;
        cmd.Parameters.AddWithValue("$id", movement.Id);
        cmd.Parameters.AddWithValue("$sh", movement.ShiftId);
        cmd.Parameters.AddWithValue("$st", movement.StaffId);
        cmd.Parameters.AddWithValue("$a", Money.ToPence(movement.Amount));
        cmd.Parameters.AddWithValue("$r", movement.Reason);
        cmd.Parameters.AddWithValue("$at", movement.At.ToString("o"));
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Sums the shift straight from payments and orders. Nothing is accumulated
    /// into a running column, so a crash mid-service cannot leave a total that
    /// disagrees with the rows behind it.
    /// </summary>
    public ShiftTotals GetTotals(Shift shift)
    {
        var conn = _db.Open();

        decimal SumPayments(string tenderTypes)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText =
                $"SELECT COALESCE(SUM(amount_pence), 0) FROM payments WHERE shift_id=$s AND tender_type IN ({tenderTypes})";
            cmd.Parameters.AddWithValue("$s", shift.Id);
            return Money.FromPence(Convert.ToInt64(cmd.ExecuteScalar()));
        }

        var cash = SumPayments("'Cash'");
        var card = SumPayments("'CardManual','CardIntegrated'");
        var prepaid = SumPayments("'PrepaidOnline'");
        var other = SumPayments("'Voucher','Other'");

        decimal movements;
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText =
                "SELECT COALESCE(SUM(amount_pence), 0) FROM cash_movements WHERE shift_id=$s";
            cmd.Parameters.AddWithValue("$s", shift.Id);
            movements = Money.FromPence(Convert.ToInt64(cmd.ExecuteScalar()));
        }

        int paid = 0, open = 0, voided = 0;
        decimal grossPaid = 0, outstanding = 0;
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                SELECT o.status,
                       o.total_pence,
                       COALESCE((SELECT SUM(p.amount_pence) FROM payments p WHERE p.order_id = o.id), 0)
                FROM orders o WHERE o.shift_id=$s
                """;
            cmd.Parameters.AddWithValue("$s", shift.Id);
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var status = Enum.Parse<PosOrderStatus>(r.GetString(0));
                var total = Money.FromPence(r.GetInt64(1));
                var settled = Money.FromPence(r.GetInt64(2));

                if (status is PosOrderStatus.Voided or PosOrderStatus.Cancelled)
                {
                    voided++;
                    continue;
                }

                if (settled >= total && total > 0)
                {
                    paid++;
                    grossPaid += total;
                }
                else
                {
                    open++;
                    outstanding += Math.Max(0, total - settled);
                }
            }
        }

        return new ShiftTotals(
            shift.OpeningFloat, cash, card, prepaid, other, movements,
            Money.Round(outstanding), paid, open, voided, Money.Round(grossPaid));
    }

    private const string Select =
        "SELECT id,number,status,terminal_id,opened_by_staff_id,opened_at,opening_float_pence," +
        "closed_by_staff_id,closed_at,declared_cash_pence,expected_cash_pence,notes FROM shifts";

    private static Shift Read(SqliteDataReader r) => new()
    {
        Id = r.GetString(0),
        Number = r.GetInt32(1),
        Status = Enum.Parse<ShiftStatus>(r.GetString(2)),
        TerminalId = r.IsDBNull(3) ? null : r.GetString(3),
        OpenedByStaffId = r.GetString(4),
        OpenedAt = DateTimeOffset.Parse(r.GetString(5)),
        OpeningFloat = Money.FromPence(r.GetInt64(6)),
        ClosedByStaffId = r.IsDBNull(7) ? null : r.GetString(7),
        ClosedAt = r.IsDBNull(8) ? null : DateTimeOffset.Parse(r.GetString(8)),
        DeclaredCash = r.IsDBNull(9) ? null : Money.FromPence(r.GetInt64(9)),
        ExpectedCash = r.IsDBNull(10) ? null : Money.FromPence(r.GetInt64(10)),
        Notes = r.IsDBNull(11) ? null : r.GetString(11),
    };
}
