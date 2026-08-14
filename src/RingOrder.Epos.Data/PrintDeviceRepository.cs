using Microsoft.Data.Sqlite;
using RingOrder.Epos.Domain;

namespace RingOrder.Epos.Data;

/// <summary>The shop's printers and the rules that decide what reaches them.</summary>
public sealed class PrintDeviceRepository
{
    private readonly EposDb _db;

    public PrintDeviceRepository(EposDb db) => _db = db;

    public List<PrintDevice> GetDevices(bool enabledOnly = false)
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT id,name,transport,address,paper_width_mm,encoding,cjk_as_raster,has_cash_drawer,is_enabled " +
            "FROM print_devices" + (enabledOnly ? " WHERE is_enabled=1" : "") + " ORDER BY sort_order,name";

        var list = new List<PrintDevice>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            list.Add(new PrintDevice
            {
                Id = r.GetString(0),
                Name = r.GetString(1),
                Transport = Enum.Parse<PrintTransport>(r.GetString(2)),
                Address = r.GetString(3),
                PaperWidthMm = r.GetInt32(4),
                Encoding = r.GetString(5),
                CjkAsRaster = r.GetInt32(6) == 1,
                HasCashDrawer = r.GetInt32(7) == 1,
                IsEnabled = r.GetInt32(8) == 1,
            });
        }
        return list;
    }

    public Dictionary<string, PrintDevice> GetDeviceMap() =>
        GetDevices().ToDictionary(d => d.Id, StringComparer.Ordinal);

    public void UpsertDevice(PrintDevice device, int sortOrder = 0)
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO print_devices(id,name,transport,address,paper_width_mm,encoding,
              cjk_as_raster,has_cash_drawer,is_enabled,sort_order)
            VALUES($id,$n,$t,$a,$w,$e,$r,$d,$en,$s)
            ON CONFLICT(id) DO UPDATE SET
              name=excluded.name, transport=excluded.transport, address=excluded.address,
              paper_width_mm=excluded.paper_width_mm, encoding=excluded.encoding,
              cjk_as_raster=excluded.cjk_as_raster, has_cash_drawer=excluded.has_cash_drawer,
              is_enabled=excluded.is_enabled, sort_order=excluded.sort_order
            """;
        cmd.Parameters.AddWithValue("$id", device.Id);
        cmd.Parameters.AddWithValue("$n", device.Name);
        cmd.Parameters.AddWithValue("$t", device.Transport.ToString());
        cmd.Parameters.AddWithValue("$a", device.Address);
        cmd.Parameters.AddWithValue("$w", device.PaperWidthMm);
        cmd.Parameters.AddWithValue("$e", device.Encoding);
        cmd.Parameters.AddWithValue("$r", device.CjkAsRaster ? 1 : 0);
        cmd.Parameters.AddWithValue("$d", device.HasCashDrawer ? 1 : 0);
        cmd.Parameters.AddWithValue("$en", device.IsEnabled ? 1 : 0);
        cmd.Parameters.AddWithValue("$s", sortOrder);
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Removing a printer takes its routing with it. A rule pointing at a device
    /// that is gone is a ticket that silently goes nowhere.
    /// </summary>
    public void DeleteDevice(string deviceId)
    {
        using var conn = _db.Open();
        using var tx = conn.BeginTransaction();
        foreach (var sql in new[]
                 {
                     "DELETE FROM print_routes WHERE device_id=$id",
                     "UPDATE print_routes SET fallback_device_id=NULL WHERE fallback_device_id=$id",
                     "DELETE FROM print_devices WHERE id=$id",
                 })
        {
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = sql;
            cmd.Parameters.AddWithValue("$id", deviceId);
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }

    public List<PrintRoute> GetRoutes()
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT id,sort_order,is_enabled,document,print_class,service_type,channel," +
            "device_id,copies,fallback_device_id FROM print_routes ORDER BY sort_order";

        var list = new List<PrintRoute>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            list.Add(new PrintRoute
            {
                Id = r.GetString(0),
                SortOrder = r.GetInt32(1),
                IsEnabled = r.GetInt32(2) == 1,
                Document = Enum.Parse<PrintDocument>(r.GetString(3)),
                PrintClass = r.IsDBNull(4) ? null : r.GetString(4),
                ServiceType = r.IsDBNull(5) ? null : Enum.Parse<ServiceType>(r.GetString(5)),
                Channel = r.IsDBNull(6) ? null : Enum.Parse<OrderChannel>(r.GetString(6)),
                DeviceId = r.GetString(7),
                Copies = r.GetInt32(8),
                FallbackDeviceId = r.IsDBNull(9) ? null : r.GetString(9),
            });
        }
        return list;
    }

    public void UpsertRoute(PrintRoute route)
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO print_routes(id,sort_order,is_enabled,document,print_class,service_type,
              channel,device_id,copies,fallback_device_id)
            VALUES($id,$s,$en,$doc,$pc,$st,$ch,$dev,$c,$fb)
            ON CONFLICT(id) DO UPDATE SET
              sort_order=excluded.sort_order, is_enabled=excluded.is_enabled,
              document=excluded.document, print_class=excluded.print_class,
              service_type=excluded.service_type, channel=excluded.channel,
              device_id=excluded.device_id, copies=excluded.copies,
              fallback_device_id=excluded.fallback_device_id
            """;
        cmd.Parameters.AddWithValue("$id", route.Id);
        cmd.Parameters.AddWithValue("$s", route.SortOrder);
        cmd.Parameters.AddWithValue("$en", route.IsEnabled ? 1 : 0);
        cmd.Parameters.AddWithValue("$doc", route.Document.ToString());
        cmd.Parameters.AddWithValue("$pc", (object?)route.PrintClass ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$st", (object?)route.ServiceType?.ToString() ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$ch", (object?)route.Channel?.ToString() ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$dev", route.DeviceId);
        cmd.Parameters.AddWithValue("$c", Math.Clamp(route.Copies, 1, 9));
        cmd.Parameters.AddWithValue("$fb", (object?)route.FallbackDeviceId ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    public void DeleteRoute(string routeId)
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM print_routes WHERE id=$id";
        cmd.Parameters.AddWithValue("$id", routeId);
        cmd.ExecuteNonQuery();
    }

    public void ReplaceAll(IEnumerable<PrintDevice> devices, IEnumerable<PrintRoute> routes)
    {
        using var conn = _db.Open();
        using (var clear = conn.CreateCommand())
        {
            clear.CommandText = "DELETE FROM print_routes; DELETE FROM print_devices;";
            clear.ExecuteNonQuery();
        }

        var order = 0;
        foreach (var device in devices) UpsertDevice(device, order++);
        foreach (var route in routes) UpsertRoute(route);
    }
}

/// <summary>
/// The print queue. Jobs are rows, so a ticket queued while the kitchen printer
/// was off still prints when it comes back — including after the till restarts.
/// </summary>
public sealed class PrintJobRepository
{
    private readonly EposDb _db;

    public PrintJobRepository(EposDb db) => _db = db;

    public void Enqueue(PrintJob job)
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO print_jobs(id,order_id,order_number,device_id,document,template,copies,
              status,payload,attempts,error,next_attempt_at,created_at,printed_at)
            VALUES($id,$oid,$on,$dev,$doc,$tpl,$c,$st,$p,$a,$err,$next,$ca,$pa)
            """;
        Bind(cmd, job);
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Takes one job for a device and marks it Claimed in the same statement, so
    /// two passes over the queue cannot both print it.
    /// </summary>
    public PrintJob? ClaimNext(string deviceId)
    {
        using var conn = _db.Open();
        using var tx = conn.BeginTransaction();

        PrintJob? job;
        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = """
                SELECT id,order_id,order_number,device_id,document,template,copies,status,
                       payload,attempts,error,next_attempt_at,created_at,printed_at
                FROM print_jobs
                WHERE device_id=$dev
                  AND status IN ('Pending','Failed')
                  AND attempts < $max
                  AND (next_attempt_at IS NULL OR next_attempt_at <= $now)
                ORDER BY created_at
                LIMIT 1
                """;
            cmd.Parameters.AddWithValue("$dev", deviceId);
            cmd.Parameters.AddWithValue("$max", PrintJob.MaxAttempts);
            cmd.Parameters.AddWithValue("$now", DateTimeOffset.Now.ToString("o"));
            using var r = cmd.ExecuteReader();
            job = r.Read() ? Read(r) : null;
        }

        if (job is null) return null;

        using (var claim = conn.CreateCommand())
        {
            claim.Transaction = tx;
            claim.CommandText = "UPDATE print_jobs SET status='Claimed' WHERE id=$id";
            claim.Parameters.AddWithValue("$id", job.Id);
            claim.ExecuteNonQuery();
        }

        tx.Commit();
        job.Status = PrintJobStatus.Claimed;
        return job;
    }

    /// <summary>
    /// Paper came out. The payload is cleared — it can be tens of kilobytes of
    /// raster, and once printed it is history rather than work.
    /// </summary>
    public void MarkPrinted(PrintJob job)
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE print_jobs
            SET status='Printed', printed_at=$at, attempts=$a, error=NULL, payload=zeroblob(0)
            WHERE id=$id
            """;
        cmd.Parameters.AddWithValue("$id", job.Id);
        cmd.Parameters.AddWithValue("$at", DateTimeOffset.Now.ToString("o"));
        cmd.Parameters.AddWithValue("$a", job.Attempts + 1);
        cmd.ExecuteNonQuery();
    }

    /// <summary>Failed. Backs off, and stops trying after MaxAttempts.</summary>
    public void MarkFailed(PrintJob job, string error)
    {
        var attempts = job.Attempts + 1;
        var backoff = TimeSpan.FromSeconds(Math.Min(60, Math.Pow(2, attempts)));

        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE print_jobs SET status='Failed', attempts=$a, error=$e, next_attempt_at=$next
            WHERE id=$id
            """;
        cmd.Parameters.AddWithValue("$id", job.Id);
        cmd.Parameters.AddWithValue("$a", attempts);
        cmd.Parameters.AddWithValue("$e", error);
        cmd.Parameters.AddWithValue("$next", DateTimeOffset.Now.Add(backoff).ToString("o"));
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Anything left Claimed by a till that was closed or crashed mid-print.
    /// Put back to Pending at startup: a ticket nobody saw print is a ticket the
    /// kitchen never got.
    /// </summary>
    public int RequeueOrphans()
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE print_jobs SET status='Pending' WHERE status='Claimed'";
        return cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Puts an abandoned job back in the queue with its attempts reset. Only
    /// ever called deliberately: an automatic retry that never stops is how a
    /// kitchen ends up with forty copies of one order.
    /// </summary>
    public void Requeue(PrintJob job)
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "UPDATE print_jobs SET status='Pending', attempts=0, error=NULL, next_attempt_at=NULL WHERE id=$id";
        cmd.Parameters.AddWithValue("$id", job.Id);
        cmd.ExecuteNonQuery();
    }

    /// <summary>Jobs that ran out of attempts, for the reprint list.</summary>
    public List<PrintJob> GetAbandoned()
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id,order_id,order_number,device_id,document,template,copies,status,
                   payload,attempts,error,next_attempt_at,created_at,printed_at
            FROM print_jobs WHERE status='Failed' AND attempts >= $max ORDER BY created_at DESC
            """;
        cmd.Parameters.AddWithValue("$max", PrintJob.MaxAttempts);
        var list = new List<PrintJob>();
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add(Read(r));
        return list;
    }

    public int CountWaiting()
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT COUNT(*) FROM print_jobs WHERE status IN ('Pending','Claimed') " +
            "OR (status='Failed' AND attempts < $max)";
        cmd.Parameters.AddWithValue("$max", PrintJob.MaxAttempts);
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    public List<PrintJob> GetForOrder(string orderId)
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id,order_id,order_number,device_id,document,template,copies,status,
                   payload,attempts,error,next_attempt_at,created_at,printed_at
            FROM print_jobs WHERE order_id=$oid ORDER BY created_at DESC
            """;
        cmd.Parameters.AddWithValue("$oid", orderId);
        var list = new List<PrintJob>();
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add(Read(r));
        return list;
    }

    /// <summary>Printed jobs older than a week. The queue is work, not an archive.</summary>
    public int PurgePrintedBefore(DateTimeOffset cutoff)
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM print_jobs WHERE status='Printed' AND printed_at < $c";
        cmd.Parameters.AddWithValue("$c", cutoff.ToString("o"));
        return cmd.ExecuteNonQuery();
    }

    private static void Bind(SqliteCommand cmd, PrintJob job)
    {
        cmd.Parameters.AddWithValue("$id", job.Id);
        cmd.Parameters.AddWithValue("$oid", job.OrderId);
        cmd.Parameters.AddWithValue("$on", job.OrderNumber);
        cmd.Parameters.AddWithValue("$dev", job.DeviceId);
        cmd.Parameters.AddWithValue("$doc", job.Document.ToString());
        cmd.Parameters.AddWithValue("$tpl", job.Template);
        cmd.Parameters.AddWithValue("$c", job.Copies);
        cmd.Parameters.AddWithValue("$st", job.Status.ToString());
        cmd.Parameters.AddWithValue("$p", job.Payload);
        cmd.Parameters.AddWithValue("$a", job.Attempts);
        cmd.Parameters.AddWithValue("$err", (object?)job.Error ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$next", (object?)job.NextAttemptAt?.ToString("o") ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$ca", job.CreatedAt.ToString("o"));
        cmd.Parameters.AddWithValue("$pa", (object?)job.PrintedAt?.ToString("o") ?? DBNull.Value);
    }

    private static PrintJob Read(SqliteDataReader r) => new()
    {
        Id = r.GetString(0),
        OrderId = r.GetString(1),
        OrderNumber = r.GetString(2),
        DeviceId = r.GetString(3),
        Document = Enum.Parse<PrintDocument>(r.GetString(4)),
        Template = r.GetString(5),
        Copies = r.GetInt32(6),
        Status = Enum.Parse<PrintJobStatus>(r.GetString(7)),
        Payload = r.IsDBNull(8) ? [] : (byte[])r[8],
        Attempts = r.GetInt32(9),
        Error = r.IsDBNull(10) ? null : r.GetString(10),
        NextAttemptAt = r.IsDBNull(11) ? null : DateTimeOffset.Parse(r.GetString(11)),
        CreatedAt = DateTimeOffset.Parse(r.GetString(12)),
        PrintedAt = r.IsDBNull(13) ? null : DateTimeOffset.Parse(r.GetString(13)),
    };
}
