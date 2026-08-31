using RingOrder.Epos.Domain;
using Microsoft.Data.Sqlite;

namespace RingOrder.Epos.Data;

/// <summary>
/// Orders, their lines and their payments. Lines and tenders are rows rather
/// than JSON columns: "what sold this week" and "what did each till take" are
/// the first two questions any owner asks, and neither can be answered from a
/// blob.
/// </summary>
public sealed class OrderRepository
{
    private readonly EposDb _db;
    private readonly ChangeLogRepository? _changes;

    /// <param name="changes">
    /// Optional so a test can build a repository without one. In the running
    /// till it is always supplied — see <c>AppServices</c>.
    /// </param>
    public OrderRepository(EposDb db, ChangeLogRepository? changes = null)
    {
        _db = db;
        _changes = changes;
    }

    public void Upsert(PosOrder order)
    {
        LinePricing.RecalculateOrder(order);
        using var conn = _db.Open();
        using var tx = conn.BeginTransaction();

        // Read before writing, inside the same transaction, so the verb is
        // derived from what actually changed rather than declared by a caller.
        // A log the callers have to remember to write is a log with holes, and
        // the hole is always the path somebody added in a hurry.
        var (previousStatus, wasFullyPaid, alreadyRecorded) = StateBefore(conn, tx, order.Id);

        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO orders(
                  id,order_number,service_type,channel,platform_name,customer_waiting,status,
                  terminal_id,staff_id,shift_id,
                  customer_id,customer_name,customer_phone,delivery_address,delivery_postcode,
                  table_number,hold_label,void_reason,
                  subtotal_pence,delivery_fee_pence,discount_total_pence,discount_reason,below_minimum_pence,total_pence,
                  notes,requested_for,fulfilment_label,payment_label,ticket_footer,
                  online_external_id,online_payload,kitchen_printed,front_printed,online_acked,
                  driver_staff_id,dispatched_at,delivered_at,
                  created_at,updated_at)
                VALUES(
                  $id,$on,$svc,$ch,$pn,$cw,$st,$term,$staff,$shift,
                  $cid,$cn,$cp,$da,$dp,$tn,$hl,$vr,
                  $sub,$df,$disc,$dreason,$bms,$tot,
                  $notes,$rf,$fl,$pl,$tf,$oe,$op,$kp,$fp,$oa,
                  $drv,$disp,$deliv,$ca,$ua)
                ON CONFLICT(id) DO UPDATE SET
                  order_number=excluded.order_number,
                  service_type=excluded.service_type,
                  channel=excluded.channel,
                  platform_name=excluded.platform_name,
                  customer_waiting=excluded.customer_waiting,
                  status=excluded.status,
                  terminal_id=excluded.terminal_id,
                  staff_id=excluded.staff_id,
                  shift_id=excluded.shift_id,
                  customer_id=excluded.customer_id,
                  customer_name=excluded.customer_name,
                  customer_phone=excluded.customer_phone,
                  delivery_address=excluded.delivery_address,
                  delivery_postcode=excluded.delivery_postcode,
                  table_number=excluded.table_number,
                  hold_label=excluded.hold_label,
                  void_reason=excluded.void_reason,
                  subtotal_pence=excluded.subtotal_pence,
                  delivery_fee_pence=excluded.delivery_fee_pence,
                  discount_total_pence=excluded.discount_total_pence,
                  discount_reason=excluded.discount_reason,
                  below_minimum_pence=excluded.below_minimum_pence,
                  total_pence=excluded.total_pence,
                  notes=excluded.notes,
                  requested_for=excluded.requested_for,
                  fulfilment_label=excluded.fulfilment_label,
                  payment_label=excluded.payment_label,
                  ticket_footer=excluded.ticket_footer,
                  online_external_id=excluded.online_external_id,
                  online_payload=excluded.online_payload,
                  kitchen_printed=excluded.kitchen_printed,
                  front_printed=excluded.front_printed,
                  online_acked=excluded.online_acked,
                  driver_staff_id=excluded.driver_staff_id,
                  dispatched_at=excluded.dispatched_at,
                  delivered_at=excluded.delivered_at,
                  updated_at=excluded.updated_at
                """;
            Bind(cmd, order);
            cmd.ExecuteNonQuery();
        }

        WriteLines(conn, tx, order);
        WritePayments(conn, tx, order);

        RecordChanges(conn, tx, order, previousStatus, wasFullyPaid, alreadyRecorded);

        tx.Commit();
    }

    /// <summary>
    /// The order's status and settled tenders as they stand on disk, before this
    /// save replaces them.
    /// </summary>
    private static (PosOrderStatus? Status, bool FullyPaid, HashSet<string> Tenders) StateBefore(
        SqliteConnection conn, SqliteTransaction tx, string orderId)
    {
        PosOrderStatus? status = null;
        var paid = false;

        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = """
                SELECT o.status,
                       COALESCE(SUM(p.amount_pence), 0) >= o.total_pence
                  FROM orders o
                  LEFT JOIN payments p ON p.order_id = o.id
                 WHERE o.id = $id
                 GROUP BY o.id
                """;
            cmd.Parameters.AddWithValue("$id", orderId);

            using var reader = cmd.ExecuteReader();
            if (reader.Read())
            {
                status = Enum.TryParse<PosOrderStatus>(reader.GetString(0), out var parsed)
                    ? parsed
                    : PosOrderStatus.Open;
                paid = reader.GetBoolean(1);
            }
        }

        var tenders = new HashSet<string>(StringComparer.Ordinal);
        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "SELECT id FROM payments WHERE order_id = $id";
            cmd.Parameters.AddWithValue("$id", orderId);

            using var reader = cmd.ExecuteReader();
            while (reader.Read()) tenders.Add(reader.GetString(0));
        }

        return (status, paid, tenders);
    }

    /// <summary>
    /// Appends what happened, in the same transaction as the change itself.
    /// <para>
    /// A draft is not recorded. An order being typed is saved on nearly every
    /// keystroke, and a log of four hundred amendments per ticket would bury the
    /// events anybody cares about — it starts existing when it is sent, held or
    /// paid, which is when it has become a thing that happened rather than a
    /// thing being typed.
    /// </para>
    /// </summary>
    private void RecordChanges(
        SqliteConnection conn,
        SqliteTransaction tx,
        PosOrder order,
        PosOrderStatus? previousStatus,
        bool wasFullyPaid,
        HashSet<string> alreadyRecorded)
    {
        if (_changes is null) return;
        if (order.Status == PosOrderStatus.Draft && previousStatus is null or PosOrderStatus.Draft) return;

        var terminal = order.TerminalId ?? "";
        var at = DateTimeOffset.Now;

        _changes.Append(conn, tx, new ChangeDraft(
            Guid.NewGuid().ToString("n"),
            terminal,
            ChangeEntity.Order,
            order.Id,
            OrderChangeVerb.For(previousStatus, wasFullyPaid, order),
            JsonUtil.Serialize(OrderSnapshot.Of(order)),
            at,
            order.StaffId));

        // Each tender gets its own entry. Money is the thing this log exists to
        // be able to account for, and a split payment where only the total was
        // recorded cannot be reconciled against a card terminal's own report.
        foreach (var tender in order.Tenders.Where(t => !alreadyRecorded.Contains(t.Id)))
        {
            _changes.Append(conn, tx, new ChangeDraft(
                Guid.NewGuid().ToString("n"),
                terminal,
                ChangeEntity.Payment,
                tender.Id,
                ChangeOp.Paid,
                JsonUtil.Serialize(PaymentSnapshot.Of(order, tender)),
                at,
                tender.StaffId ?? order.StaffId));
        }
    }

    public PosOrder? GetById(string id) => QueryOne("SELECT * FROM orders WHERE id=$p", id);

    public PosOrder? GetByOnlineExternalId(string externalId) =>
        QueryOne("SELECT * FROM orders WHERE online_external_id=$p LIMIT 1", externalId);

    public List<PosOrder> GetToday(OrderChannel? channel = null)
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        var sql = "SELECT * FROM orders WHERE created_at>=$start";
        if (channel is not null)
        {
            sql += " AND channel=$ch";
            cmd.Parameters.AddWithValue("$ch", channel.Value.ToString());
        }
        cmd.CommandText = sql + " ORDER BY created_at DESC";
        cmd.Parameters.AddWithValue("$start", DateTime.Today.ToString("o"));
        return ReadMany(cmd);
    }

    public List<PosOrder> GetForShift(string shiftId)
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM orders WHERE shift_id=$s ORDER BY created_at DESC";
        cmd.Parameters.AddWithValue("$s", shiftId);
        return ReadMany(cmd);
    }

    public List<PosOrder> GetRecentByChannel(OrderChannel channel, int take = 50)
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM orders WHERE channel=$ch ORDER BY created_at DESC LIMIT $n";
        cmd.Parameters.AddWithValue("$ch", channel.ToString());
        cmd.Parameters.AddWithValue("$n", take);
        return ReadMany(cmd);
    }

    public List<PosOrder> GetTodayFiltered(string filter)
    {
        var all = GetToday();
        return filter.ToLowerInvariant() switch
        {
            "unpaid" => all.Where(o => o.IsUnpaid && o.Status != PosOrderStatus.Held).ToList(),
            "held" => all.Where(o => o.Status == PosOrderStatus.Held).ToList(),
            "paid" => all.Where(o => o.Status is PosOrderStatus.Paid or PosOrderStatus.Completed).ToList(),
            "voided" => all.Where(o => o.Status == PosOrderStatus.Voided).ToList(),
            _ => all,
        };
    }

    // ── Internals ───────────────────────────────────────────────────────────

    private PosOrder? QueryOne(string sql, string parameter)
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("$p", parameter);
        return ReadMany(cmd).FirstOrDefault();
    }

    private List<PosOrder> ReadMany(SqliteCommand cmd)
    {
        var orders = new List<PosOrder>();
        using (var r = cmd.ExecuteReader())
        {
            while (r.Read()) orders.Add(ReadOrder(r));
        }

        if (orders.Count > 0) AttachChildren(orders);
        return orders;
    }

    private void AttachChildren(List<PosOrder> orders)
    {
        var byId = orders.ToDictionary(o => o.Id, StringComparer.Ordinal);
        using var conn = _db.Open();

        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                SELECT order_id,id,item_id,name,item_translation,quantity,base_price_pence,
                       line_total_pence,tax_class_id,print_class,notes,is_ad_hoc,
                       kitchen_sent,kitchen_sent_at,selections_json
                FROM order_lines ORDER BY order_id,line_number
                """;
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                if (!byId.TryGetValue(r.GetString(0), out var order)) continue;
                order.Lines.Add(new CartLine
                {
                    Id = r.GetString(1),
                    ItemId = r.IsDBNull(2) ? null : r.GetString(2),
                    Name = r.GetString(3),
                    ItemTranslation = r.IsDBNull(4) ? null : r.GetString(4),
                    Quantity = r.GetInt32(5),
                    BasePrice = Money.FromPence(r.GetInt64(6)),
                    LineTotal = Money.FromPence(r.GetInt64(7)),
                    TaxClassId = r.IsDBNull(8) ? null : r.GetString(8),
                    PrintClass = r.IsDBNull(9) ? null : r.GetString(9),
                    Notes = r.IsDBNull(10) ? null : r.GetString(10),
                    IsAdHoc = r.GetInt32(11) == 1,
                    KitchenSent = r.GetInt32(12) == 1,
                    KitchenSentAt = r.IsDBNull(13) ? null : DateTimeOffset.Parse(r.GetString(13)),
                    Selections = JsonUtil.Deserialize<List<CartLineSelection>>(r.GetString(14)),
                });
            }
        }

        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                SELECT order_id,id,tender_type,amount_pence,cash_received_pence,change_given_pence,
                       reference,staff_id,at
                FROM payments WHERE is_refund=0 ORDER BY order_id,at
                """;
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                if (!byId.TryGetValue(r.GetString(0), out var order)) continue;
                order.Tenders.Add(new OrderTender
                {
                    Id = r.GetString(1),
                    Type = Enum.Parse<TenderType>(r.GetString(2)),
                    Amount = Money.FromPence(r.GetInt64(3)),
                    CashReceived = r.IsDBNull(4) ? null : Money.FromPence(r.GetInt64(4)),
                    ChangeGiven = r.IsDBNull(5) ? null : Money.FromPence(r.GetInt64(5)),
                    Reference = r.IsDBNull(6) ? null : r.GetString(6),
                    StaffId = r.IsDBNull(7) ? null : r.GetString(7),
                    At = DateTimeOffset.Parse(r.GetString(8)),
                });
            }
        }

        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                SELECT order_id,id,shift_id,staff_id,amount_pence,tender_type,reason,lines_json,is_full,at
                FROM refunds ORDER BY order_id,at
                """;
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                if (!byId.TryGetValue(r.GetString(0), out var order)) continue;
                order.Refunds.Add(new Refund
                {
                    Id = r.GetString(1),
                    OrderId = r.GetString(0),
                    ShiftId = r.IsDBNull(2) ? null : r.GetString(2),
                    StaffId = r.IsDBNull(3) ? null : r.GetString(3),
                    Amount = Money.FromPence(r.GetInt64(4)),
                    Tender = Enum.Parse<TenderType>(r.GetString(5)),
                    Reason = r.GetString(6),
                    Lines = JsonUtil.Deserialize<List<RefundLine>>(r.GetString(7)),
                    IsFull = r.GetInt32(8) == 1,
                    At = DateTimeOffset.Parse(r.GetString(9)),
                });
            }
        }
    }

    private static void WriteLines(SqliteConnection conn, SqliteTransaction tx, PosOrder order)
    {
        using (var wipe = conn.CreateCommand())
        {
            wipe.Transaction = tx;
            wipe.CommandText = "DELETE FROM order_lines WHERE order_id=$o";
            wipe.Parameters.AddWithValue("$o", order.Id);
            wipe.ExecuteNonQuery();
        }

        for (var i = 0; i < order.Lines.Count; i++)
        {
            var line = order.Lines[i];
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO order_lines(id,order_id,line_number,item_id,name,item_translation,quantity,
                  base_price_pence,line_total_pence,tax_class_id,print_class,notes,is_ad_hoc,
                  kitchen_sent,kitchen_sent_at,selections_json)
                VALUES($id,$o,$ln,$it,$n,$tr,$q,$bp,$lt,$tc,$pc,$notes,$ah,$ks,$ksa,$sel)
                """;
            cmd.Parameters.AddWithValue("$id", line.Id);
            cmd.Parameters.AddWithValue("$o", order.Id);
            cmd.Parameters.AddWithValue("$ln", i);
            cmd.Parameters.AddWithValue("$it", (object?)line.ItemId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$n", line.Name);
            cmd.Parameters.AddWithValue("$tr", (object?)line.ItemTranslation ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$q", line.Quantity);
            cmd.Parameters.AddWithValue("$bp", Money.ToPence(line.BasePrice));
            cmd.Parameters.AddWithValue("$lt", Money.ToPence(line.LineTotal));
            cmd.Parameters.AddWithValue("$tc", (object?)line.TaxClassId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$pc", (object?)line.PrintClass ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$notes", (object?)line.Notes ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$ah", line.IsAdHoc ? 1 : 0);
            cmd.Parameters.AddWithValue("$ks", line.KitchenSent ? 1 : 0);
            cmd.Parameters.AddWithValue("$ksa",
                (object?)line.KitchenSentAt?.ToString("o") ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$sel", JsonUtil.Serialize(line.Selections));
            cmd.ExecuteNonQuery();
        }
    }

    private static void WritePayments(SqliteConnection conn, SqliteTransaction tx, PosOrder order)
    {
        using (var wipe = conn.CreateCommand())
        {
            wipe.Transaction = tx;
            // Refund rows live in this table too and are not the caller's to
            // rewrite: re-saving an order must never erase money already handed
            // back. Only the sale's own tenders are replaced.
            wipe.CommandText = "DELETE FROM payments WHERE order_id=$o AND is_refund=0";
            wipe.Parameters.AddWithValue("$o", order.Id);
            wipe.ExecuteNonQuery();
        }

        foreach (var tender in order.Tenders)
        {
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO payments(id,order_id,shift_id,staff_id,tender_type,amount_pence,
                  cash_received_pence,change_given_pence,reference,at)
                VALUES($id,$o,$sh,$st,$t,$a,$cr,$cg,$ref,$at)
                """;
            cmd.Parameters.AddWithValue("$id", tender.Id);
            cmd.Parameters.AddWithValue("$o", order.Id);
            cmd.Parameters.AddWithValue("$sh", (object?)order.ShiftId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$st", (object?)(tender.StaffId ?? order.StaffId) ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$t", tender.Type.ToString());
            cmd.Parameters.AddWithValue("$a", Money.ToPence(tender.Amount));
            cmd.Parameters.AddWithValue("$cr",
                tender.CashReceived is { } cr ? Money.ToPence(cr) : DBNull.Value);
            cmd.Parameters.AddWithValue("$cg",
                tender.ChangeGiven is { } cg ? Money.ToPence(cg) : DBNull.Value);
            cmd.Parameters.AddWithValue("$ref", (object?)tender.Reference ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$at", tender.At.ToString("o"));
            cmd.ExecuteNonQuery();
        }
    }

    private static void Bind(SqliteCommand cmd, PosOrder o)
    {
        cmd.Parameters.AddWithValue("$id", o.Id);
        cmd.Parameters.AddWithValue("$on", o.OrderNumber);
        cmd.Parameters.AddWithValue("$svc", o.ServiceType.ToString());
        cmd.Parameters.AddWithValue("$ch", o.Channel.ToString());
        cmd.Parameters.AddWithValue("$pn", (object?)o.PlatformName ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$cw", o.CustomerWaiting ? 1 : 0);
        cmd.Parameters.AddWithValue("$st", o.Status.ToString());
        cmd.Parameters.AddWithValue("$term", (object?)o.TerminalId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$staff", (object?)o.StaffId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$shift", (object?)o.ShiftId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$cid", (object?)o.CustomerId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$cn", (object?)o.CustomerName ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$cp", (object?)o.CustomerPhone ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$da", (object?)o.DeliveryAddress ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$dp", (object?)o.DeliveryPostcode ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$tn", (object?)o.TableNumber ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$hl", (object?)o.HoldLabel ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$vr", (object?)o.VoidReason ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$sub", Money.ToPence(o.Subtotal));
        cmd.Parameters.AddWithValue("$df", Money.ToPence(o.DeliveryFee));
        cmd.Parameters.AddWithValue("$disc", Money.ToPence(o.DiscountTotal));
        cmd.Parameters.AddWithValue("$dreason", (object?)o.DiscountReason ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$bms", Money.ToPence(o.BelowMinimumSurcharge));
        cmd.Parameters.AddWithValue("$tot", Money.ToPence(o.Total));
        cmd.Parameters.AddWithValue("$notes", (object?)o.Notes ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$rf", (object?)o.RequestedFor ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$fl", (object?)o.FulfilmentLabel ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$pl", (object?)o.PaymentLabel ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$tf", (object?)o.TicketFooter ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$oe", (object?)o.OnlineExternalId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$op", (object?)o.OnlinePayload ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$kp", o.KitchenPrinted ? 1 : 0);
        cmd.Parameters.AddWithValue("$fp", o.FrontPrinted ? 1 : 0);
        cmd.Parameters.AddWithValue("$oa", o.OnlineAcked ? 1 : 0);
        cmd.Parameters.AddWithValue("$drv", (object?)o.DriverStaffId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$disp", (object?)o.DispatchedAt?.ToString("o") ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$deliv", (object?)o.DeliveredAt?.ToString("o") ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$ca", o.CreatedAt.ToString("o"));
        cmd.Parameters.AddWithValue("$ua", o.UpdatedAt.ToString("o"));
    }

    private static PosOrder ReadOrder(SqliteDataReader r)
    {
        string S(string col) => r[col] is DBNull ? "" : Convert.ToString(r[col])!;
        string? SN(string col) => r[col] is DBNull ? null : Convert.ToString(r[col]);
        decimal P(string col) => Money.FromPence(Convert.ToInt64(r[col]));
        bool B(string col) => Convert.ToInt32(r[col]) == 1;

        return new PosOrder
        {
            Id = S("id"),
            OrderNumber = S("order_number"),
            ServiceType = Enum.Parse<ServiceType>(S("service_type")),
            Channel = Enum.Parse<OrderChannel>(S("channel")),
            PlatformName = SN("platform_name"),
            CustomerWaiting = B("customer_waiting"),
            Status = Enum.Parse<PosOrderStatus>(S("status")),
            TerminalId = SN("terminal_id"),
            StaffId = SN("staff_id"),
            ShiftId = SN("shift_id"),
            CustomerId = SN("customer_id"),
            CustomerName = SN("customer_name"),
            CustomerPhone = SN("customer_phone"),
            DeliveryAddress = SN("delivery_address"),
            DeliveryPostcode = SN("delivery_postcode"),
            TableNumber = SN("table_number"),
            HoldLabel = SN("hold_label"),
            VoidReason = SN("void_reason"),
            Subtotal = P("subtotal_pence"),
            DeliveryFee = P("delivery_fee_pence"),
            DiscountTotal = P("discount_total_pence"),
            DiscountReason = SN("discount_reason"),
            BelowMinimumSurcharge = P("below_minimum_pence"),
            Total = P("total_pence"),
            Notes = SN("notes"),
            RequestedFor = SN("requested_for"),
            FulfilmentLabel = SN("fulfilment_label"),
            PaymentLabel = SN("payment_label"),
            TicketFooter = SN("ticket_footer"),
            DriverStaffId = SN("driver_staff_id"),
            DispatchedAt = SN("dispatched_at") is { } sent ? DateTimeOffset.Parse(sent) : null,
            DeliveredAt = SN("delivered_at") is { } done ? DateTimeOffset.Parse(done) : null,
            OnlineExternalId = SN("online_external_id"),
            OnlinePayload = SN("online_payload"),
            KitchenPrinted = B("kitchen_printed"),
            FrontPrinted = B("front_printed"),
            OnlineAcked = B("online_acked"),
            CreatedAt = DateTimeOffset.Parse(S("created_at")),
            UpdatedAt = DateTimeOffset.Parse(S("updated_at")),
        };
    }
}
