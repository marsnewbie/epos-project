using RingOrder.Epos.Domain;

namespace RingOrder.Epos.Data;

/// <summary>
/// Writes a refund: the reason, and the money, together or not at all.
/// <para>
/// Two rows in two tables, in one transaction. A refund row without its payment
/// row would be a reason for money that never left; a payment row without its
/// refund row would be money gone with nothing to explain it. Either on its own
/// is worse than neither.
/// </para>
/// </summary>
public sealed class RefundRepository
{
    private readonly EposDb _db;

    public RefundRepository(EposDb db) => _db = db;

    public void Record(Refund refund)
    {
        using var conn = _db.Open();
        using var tx = conn.BeginTransaction();

        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO refunds(id,order_id,shift_id,staff_id,amount_pence,tender_type,
                                    reason,lines_json,is_full,at)
                VALUES($id,$o,$sh,$st,$amt,$t,$reason,$lines,$full,$at)
                """;
            cmd.Parameters.AddWithValue("$id", refund.Id);
            cmd.Parameters.AddWithValue("$o", refund.OrderId);
            cmd.Parameters.AddWithValue("$sh", (object?)refund.ShiftId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$st", (object?)refund.StaffId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$amt", Money.ToPence(refund.Amount));
            cmd.Parameters.AddWithValue("$t", refund.Tender.ToString());
            cmd.Parameters.AddWithValue("$reason", refund.Reason);
            cmd.Parameters.AddWithValue("$lines", JsonUtil.Serialize(refund.Lines));
            cmd.Parameters.AddWithValue("$full", refund.IsFull ? 1 : 0);
            cmd.Parameters.AddWithValue("$at", refund.At.ToString("o"));
            cmd.ExecuteNonQuery();
        }

        // Negative, so every existing sum over payments keeps working and
        // becomes a net figure without knowing refunds exist. The drawer and the
        // shift's expected cash come out right for free.
        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO payments(id,order_id,shift_id,staff_id,tender_type,amount_pence,
                                     reference,at,is_refund)
                VALUES($id,$o,$sh,$st,$t,$amt,$ref,$at,1)
                """;
            cmd.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("N"));
            cmd.Parameters.AddWithValue("$o", refund.OrderId);
            cmd.Parameters.AddWithValue("$sh", (object?)refund.ShiftId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$st", (object?)refund.StaffId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$t", refund.Tender.ToString());
            cmd.Parameters.AddWithValue("$amt", -Money.ToPence(refund.Amount));
            cmd.Parameters.AddWithValue("$ref", $"refund:{refund.Id}");
            cmd.Parameters.AddWithValue("$at", refund.At.ToString("o"));
            cmd.ExecuteNonQuery();
        }

        tx.Commit();
    }

    public List<Refund> ForOrder(string orderId)
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id,order_id,shift_id,staff_id,amount_pence,tender_type,reason,lines_json,is_full,at
            FROM refunds WHERE order_id=$o ORDER BY at
            """;
        cmd.Parameters.AddWithValue("$o", orderId);

        var found = new List<Refund>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
            found.Add(new Refund
            {
                Id = r.GetString(0),
                OrderId = r.GetString(1),
                ShiftId = r.IsDBNull(2) ? null : r.GetString(2),
                StaffId = r.IsDBNull(3) ? null : r.GetString(3),
                Amount = Money.FromPence(r.GetInt64(4)),
                Tender = Enum.Parse<TenderType>(r.GetString(5)),
                Reason = r.GetString(6),
                Lines = JsonUtil.Deserialize<List<RefundLine>>(r.GetString(7)),
                IsFull = r.GetInt32(8) == 1,
                At = DateTimeOffset.Parse(r.GetString(9)),
            });

        return found;
    }

    /// <summary>Every refund in a shift, for the X and Z reports.</summary>
    public List<Refund> ForShift(string shiftId)
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id,order_id,shift_id,staff_id,amount_pence,tender_type,reason,lines_json,is_full,at
            FROM refunds WHERE shift_id=$s ORDER BY at
            """;
        cmd.Parameters.AddWithValue("$s", shiftId);

        var found = new List<Refund>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
            found.Add(new Refund
            {
                Id = r.GetString(0),
                OrderId = r.GetString(1),
                ShiftId = r.IsDBNull(2) ? null : r.GetString(2),
                StaffId = r.IsDBNull(3) ? null : r.GetString(3),
                Amount = Money.FromPence(r.GetInt64(4)),
                Tender = Enum.Parse<TenderType>(r.GetString(5)),
                Reason = r.GetString(6),
                Lines = JsonUtil.Deserialize<List<RefundLine>>(r.GetString(7)),
                IsFull = r.GetInt32(8) == 1,
                At = DateTimeOffset.Parse(r.GetString(9)),
            });

        return found;
    }
}
