using Microsoft.Data.Sqlite;
using RingOrder.Epos.Domain;

namespace RingOrder.Epos.Data;

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
