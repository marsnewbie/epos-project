using RingOrder.Epos.Domain;
using Microsoft.Data.Sqlite;

namespace RingOrder.Epos.Data;

public sealed class OrderRepository
{
    private readonly EposDb _db;

    public OrderRepository(EposDb db) => _db = db;

    public void Upsert(PosOrder order)
    {
        LinePricing.RecalculateOrder(order);
        var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO orders(
              id,order_number,order_type,source,status,customer_id,customer_name,customer_phone,
              delivery_address,delivery_postcode,table_number,hold_label,void_reason,
              lines_json,subtotal,delivery_fee,discount_total,total,
              notes,requested_for,fulfilment_label,payment_label,ticket_footer,below_minimum_surcharge,
              online_external_id,online_payload,kitchen_printed,front_printed,online_acked,
              tenders_json,created_at,updated_at)
            VALUES(
              $id,$on,$ot,$src,$st,$cid,$cn,$cp,$da,$dp,$tn,$hl,$vr,$lj,$sub,$df,$disc,$tot,
              $notes,$rf,$fl,$pl,$tf,$bms,$oe,$op,$kp,$fp,$oa,$tj,$ca,$ua)
            ON CONFLICT(id) DO UPDATE SET
              order_number=excluded.order_number,
              order_type=excluded.order_type,
              source=excluded.source,
              status=excluded.status,
              customer_id=excluded.customer_id,
              customer_name=excluded.customer_name,
              customer_phone=excluded.customer_phone,
              delivery_address=excluded.delivery_address,
              delivery_postcode=excluded.delivery_postcode,
              table_number=excluded.table_number,
              hold_label=excluded.hold_label,
              void_reason=excluded.void_reason,
              lines_json=excluded.lines_json,
              subtotal=excluded.subtotal,
              delivery_fee=excluded.delivery_fee,
              discount_total=excluded.discount_total,
              total=excluded.total,
              notes=excluded.notes,
              requested_for=excluded.requested_for,
              fulfilment_label=excluded.fulfilment_label,
              payment_label=excluded.payment_label,
              ticket_footer=excluded.ticket_footer,
              below_minimum_surcharge=excluded.below_minimum_surcharge,
              online_external_id=excluded.online_external_id,
              online_payload=excluded.online_payload,
              kitchen_printed=excluded.kitchen_printed,
              front_printed=excluded.front_printed,
              online_acked=excluded.online_acked,
              tenders_json=excluded.tenders_json,
              updated_at=excluded.updated_at
            """;
        Bind(cmd, order);
        cmd.ExecuteNonQuery();
    }

    public PosOrder? GetById(string id)
    {
        var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM orders WHERE id=$id";
        cmd.Parameters.AddWithValue("$id", id);
        using var r = cmd.ExecuteReader();
        return r.Read() ? Read(r) : null;
    }

    public PosOrder? GetByOnlineExternalId(string externalId)
    {
        var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM orders WHERE online_external_id=$e LIMIT 1";
        cmd.Parameters.AddWithValue("$e", externalId);
        using var r = cmd.ExecuteReader();
        return r.Read() ? Read(r) : null;
    }

    public List<PosOrder> GetToday(PosOrderSource? source = null)
    {
        var start = DateTime.Today.ToString("o");
        var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        var sql = "SELECT * FROM orders WHERE created_at>=$start";
        if (source is not null)
        {
            sql += " AND source=$src";
            cmd.Parameters.AddWithValue("$src", source.Value.ToString());
        }
        sql += " ORDER BY created_at DESC";
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("$start", start);

        var list = new List<PosOrder>();
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add(Read(r));
        return list;
    }

    public List<PosOrder> GetOnlineRecent(int take = 50)
    {
        var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM orders WHERE source='Online' ORDER BY created_at DESC LIMIT $n";
        cmd.Parameters.AddWithValue("$n", take);
        var list = new List<PosOrder>();
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add(Read(r));
        return list;
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

    private static void Bind(SqliteCommand cmd, PosOrder o)
    {
        cmd.Parameters.AddWithValue("$id", o.Id);
        cmd.Parameters.AddWithValue("$on", o.OrderNumber);
        cmd.Parameters.AddWithValue("$ot", o.OrderType.ToString());
        cmd.Parameters.AddWithValue("$src", o.Source.ToString());
        cmd.Parameters.AddWithValue("$st", o.Status.ToString());
        cmd.Parameters.AddWithValue("$cid", (object?)o.CustomerId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$cn", (object?)o.CustomerName ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$cp", (object?)o.CustomerPhone ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$da", (object?)o.DeliveryAddress ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$dp", (object?)o.DeliveryPostcode ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$tn", (object?)o.TableNumber ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$hl", (object?)o.HoldLabel ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$vr", (object?)o.VoidReason ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$lj", JsonUtil.Serialize(o.Lines));
        cmd.Parameters.AddWithValue("$sub", (double)o.Subtotal);
        cmd.Parameters.AddWithValue("$df", (double)o.DeliveryFee);
        cmd.Parameters.AddWithValue("$disc", (double)o.DiscountTotal);
        cmd.Parameters.AddWithValue("$tot", (double)o.Total);
        cmd.Parameters.AddWithValue("$notes", (object?)o.Notes ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$rf", (object?)o.RequestedFor ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$fl", (object?)o.FulfilmentLabel ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$pl", (object?)o.PaymentLabel ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$tf", (object?)o.TicketFooter ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$bms", (double)o.BelowMinimumSurcharge);
        cmd.Parameters.AddWithValue("$oe", (object?)o.OnlineExternalId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$op", (object?)o.OnlinePayload ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$kp", o.KitchenPrinted ? 1 : 0);
        cmd.Parameters.AddWithValue("$fp", o.FrontPrinted ? 1 : 0);
        cmd.Parameters.AddWithValue("$oa", o.OnlineAcked ? 1 : 0);
        cmd.Parameters.AddWithValue("$tj", JsonUtil.Serialize(o.Tenders));
        cmd.Parameters.AddWithValue("$ca", o.CreatedAt.ToString("o"));
        cmd.Parameters.AddWithValue("$ua", o.UpdatedAt.ToString("o"));
    }

    private static PosOrder Read(SqliteDataReader r)
    {
        string S(string col) => r[col] is DBNull ? "" : Convert.ToString(r[col])!;
        string? SN(string col) => r[col] is DBNull ? null : Convert.ToString(r[col]);
        decimal D(string col) => Convert.ToDecimal(r[col]);
        bool B(string col) => Convert.ToInt32(r[col]) == 1;
        bool Has(string col)
        {
            for (var i = 0; i < r.FieldCount; i++)
                if (string.Equals(r.GetName(i), col, StringComparison.OrdinalIgnoreCase))
                    return true;
            return false;
        }

        return new PosOrder
        {
            Id = S("id"),
            OrderNumber = S("order_number"),
            OrderType = Enum.Parse<PosOrderType>(S("order_type")),
            Source = Enum.Parse<PosOrderSource>(S("source")),
            Status = Enum.Parse<PosOrderStatus>(S("status")),
            CustomerId = SN("customer_id"),
            CustomerName = SN("customer_name"),
            CustomerPhone = SN("customer_phone"),
            DeliveryAddress = SN("delivery_address"),
            DeliveryPostcode = SN("delivery_postcode"),
            TableNumber = Has("table_number") ? SN("table_number") : null,
            HoldLabel = Has("hold_label") ? SN("hold_label") : null,
            VoidReason = Has("void_reason") ? SN("void_reason") : null,
            Lines = JsonUtil.Deserialize<List<CartLine>>(S("lines_json")),
            Subtotal = D("subtotal"),
            DeliveryFee = D("delivery_fee"),
            DiscountTotal = D("discount_total"),
            Total = D("total"),
            Notes = Has("notes") ? SN("notes") : null,
            RequestedFor = Has("requested_for") ? SN("requested_for") : null,
            FulfilmentLabel = Has("fulfilment_label") ? SN("fulfilment_label") : null,
            PaymentLabel = Has("payment_label") ? SN("payment_label") : null,
            TicketFooter = Has("ticket_footer") ? SN("ticket_footer") : null,
            BelowMinimumSurcharge = Has("below_minimum_surcharge") ? D("below_minimum_surcharge") : 0,
            OnlineExternalId = SN("online_external_id"),
            OnlinePayload = SN("online_payload"),
            KitchenPrinted = B("kitchen_printed"),
            FrontPrinted = B("front_printed"),
            OnlineAcked = B("online_acked"),
            Tenders = JsonUtil.Deserialize<List<OrderTender>>(S("tenders_json")),
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
            INSERT INTO customers(id,name,phone,notes,addresses_json,created_at,updated_at)
            VALUES($id,$n,$p,$notes,$aj,$ca,$ua)
            ON CONFLICT(id) DO UPDATE SET
              name=excluded.name, phone=excluded.phone, notes=excluded.notes,
              addresses_json=excluded.addresses_json, updated_at=excluded.updated_at
            """;
        cmd.Parameters.AddWithValue("$id", c.Id);
        cmd.Parameters.AddWithValue("$n", c.Name);
        cmd.Parameters.AddWithValue("$p", c.Phone);
        cmd.Parameters.AddWithValue("$notes", (object?)c.Notes ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$aj", JsonUtil.Serialize(c.Addresses));
        cmd.Parameters.AddWithValue("$ca", c.CreatedAt.ToString("o"));
        cmd.Parameters.AddWithValue("$ua", c.UpdatedAt.ToString("o"));
        cmd.ExecuteNonQuery();
    }

    public Customer? FindByPhone(string phone)
    {
        var normalized = NormalizePhone(phone);
        var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM customers";
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var c = Read(r);
            if (NormalizePhone(c.Phone) == normalized)
                return c;
        }
        return null;
    }

    public List<Customer> Search(string query)
    {
        var q = query.Trim();
        var all = ListAll();
        if (q.Length == 0) return all;
        return all.Where(c =>
                c.Name.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                c.Phone.Contains(q, StringComparison.OrdinalIgnoreCase))
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
        new string(phone.Where(char.IsDigit).ToArray());

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
