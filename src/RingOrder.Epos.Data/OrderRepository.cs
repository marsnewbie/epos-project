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

    public OrderRepository(EposDb db) => _db = db;

    public void Upsert(PosOrder order)
    {
        LinePricing.RecalculateOrder(order);
        var conn = _db.Open();
        using var tx = conn.BeginTransaction();

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
                  created_at,updated_at)
                VALUES(
                  $id,$on,$svc,$ch,$pn,$cw,$st,$term,$staff,$shift,
                  $cid,$cn,$cp,$da,$dp,$tn,$hl,$vr,
                  $sub,$df,$disc,$dreason,$bms,$tot,
                  $notes,$rf,$fl,$pl,$tf,$oe,$op,$kp,$fp,$oa,$ca,$ua)
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
                  updated_at=excluded.updated_at
                """;
            Bind(cmd, order);
            cmd.ExecuteNonQuery();
        }

        WriteLines(conn, tx, order);
        WritePayments(conn, tx, order);
        tx.Commit();
    }

    public PosOrder? GetById(string id) => QueryOne("SELECT * FROM orders WHERE id=$p", id);

    public PosOrder? GetByOnlineExternalId(string externalId) =>
        QueryOne("SELECT * FROM orders WHERE online_external_id=$p LIMIT 1", externalId);

    public List<PosOrder> GetToday(OrderChannel? channel = null)
    {
        var conn = _db.Open();
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
        var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM orders WHERE shift_id=$s ORDER BY created_at DESC";
        cmd.Parameters.AddWithValue("$s", shiftId);
        return ReadMany(cmd);
    }

    public List<PosOrder> GetRecentByChannel(OrderChannel channel, int take = 50)
    {
        var conn = _db.Open();
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
        var conn = _db.Open();
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
        var conn = _db.Open();

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
                FROM payments ORDER BY order_id,at
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
            wipe.CommandText = "DELETE FROM payments WHERE order_id=$o";
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

public sealed class PrintJobRepository
{
    private readonly EposDb _db;

    public PrintJobRepository(EposDb db) => _db = db;

    public void Insert(PrintJob job)
    {
        var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO print_jobs(id,order_id,order_number,channel,status,payload_text,error,attempts,created_at,printed_at)
            VALUES($id,$oid,$on,$ch,$st,$pt,$err,$at,$ca,$pa)
            """;
        cmd.Parameters.AddWithValue("$id", job.Id);
        cmd.Parameters.AddWithValue("$oid", job.OrderId);
        cmd.Parameters.AddWithValue("$on", job.OrderNumber);
        cmd.Parameters.AddWithValue("$ch", job.Channel.ToString());
        cmd.Parameters.AddWithValue("$st", job.Status.ToString());
        cmd.Parameters.AddWithValue("$pt", (object?)job.PayloadText ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$err", (object?)job.Error ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$at", job.Attempts);
        cmd.Parameters.AddWithValue("$ca", job.CreatedAt.ToString("o"));
        cmd.Parameters.AddWithValue("$pa", (object?)job.PrintedAt?.ToString("o") ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    public void Update(PrintJob job)
    {
        var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE print_jobs SET status=$st, payload_text=$pt, error=$err, attempts=$at, printed_at=$pa
            WHERE id=$id
            """;
        cmd.Parameters.AddWithValue("$id", job.Id);
        cmd.Parameters.AddWithValue("$st", job.Status.ToString());
        cmd.Parameters.AddWithValue("$pt", (object?)job.PayloadText ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$err", (object?)job.Error ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$at", job.Attempts);
        cmd.Parameters.AddWithValue("$pa", (object?)job.PrintedAt?.ToString("o") ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    public List<PrintJob> GetForOrder(string orderId)
    {
        var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM print_jobs WHERE order_id=$oid ORDER BY created_at DESC";
        cmd.Parameters.AddWithValue("$oid", orderId);
        var list = new List<PrintJob>();
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add(Read(r));
        return list;
    }

    private static PrintJob Read(SqliteDataReader r)
    {
        string S(string col) => r[col] is DBNull ? "" : Convert.ToString(r[col])!;
        string? SN(string col) => r[col] is DBNull ? null : Convert.ToString(r[col]);
        return new PrintJob
        {
            Id = S("id"),
            OrderId = S("order_id"),
            OrderNumber = S("order_number"),
            Channel = Enum.Parse<PrintJobChannel>(S("channel")),
            Status = Enum.Parse<PrintJobStatus>(S("status")),
            PayloadText = SN("payload_text"),
            Error = SN("error"),
            Attempts = Convert.ToInt32(r["attempts"]),
            CreatedAt = DateTimeOffset.Parse(S("created_at")),
            PrintedAt = SN("printed_at") is { } p ? DateTimeOffset.Parse(p) : null,
        };
    }
}

public sealed class CustomerRepository
{
    private readonly EposDb _db;

    public CustomerRepository(EposDb db) => _db = db;

    public void Upsert(Customer c)
    {
        c.UpdatedAt = DateTimeOffset.Now;
        var conn = _db.Open();
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
        var conn = _db.Open();
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
        var conn = _db.Open();
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
