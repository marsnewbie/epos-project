using RingOrder.Epos.Domain;

namespace RingOrder.Epos.Data;

/// <summary>
/// The shop's delivery areas.
/// <para>
/// Small and read on every delivery order, so it is cached in memory and only
/// re-read when something writes. A postcode typed on the phone must price
/// itself instantly; a database round trip per keystroke would be felt.
/// </para>
/// </summary>
public sealed class DeliveryZoneRepository
{
    private readonly EposDb _db;
    private List<DeliveryZone>? _cache;
    private readonly object _gate = new();

    public DeliveryZoneRepository(EposDb db) => _db = db;

    public IReadOnlyList<DeliveryZone> GetZones()
    {
        lock (_gate)
        {
            return _cache ??= Load();
        }
    }

    public void Invalidate()
    {
        lock (_gate) _cache = null;
    }

    /// <summary>
    /// Replaces the whole set in one transaction. Zones are edited as a list on
    /// one screen, so saving one at a time would leave a half-applied price list
    /// if anything went wrong in the middle.
    /// </summary>
    public void Replace(IEnumerable<DeliveryZone> zones)
    {
        using var conn = _db.Open();
        using var tx = conn.BeginTransaction();

        using (var wipe = conn.CreateCommand())
        {
            wipe.Transaction = tx;
            wipe.CommandText = "DELETE FROM delivery_zones";
            wipe.ExecuteNonQuery();
        }

        var order = 0;
        foreach (var zone in zones)
        {
            var prefix = DeliveryZone.Normalise(zone.Prefix);
            if (prefix.Length == 0) continue;      // a blank row is a row being typed

            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO delivery_zones(id,prefix,name,fee_pence,minimum_order_pence,
                                           free_over_pence,is_deliverable,sort_order)
                VALUES($id,$p,$n,$fee,$min,$free,$del,$sort)
                ON CONFLICT(prefix) DO UPDATE SET
                  name=excluded.name, fee_pence=excluded.fee_pence,
                  minimum_order_pence=excluded.minimum_order_pence,
                  free_over_pence=excluded.free_over_pence,
                  is_deliverable=excluded.is_deliverable, sort_order=excluded.sort_order
                """;
            cmd.Parameters.AddWithValue("$id", zone.Id);
            cmd.Parameters.AddWithValue("$p", prefix);
            cmd.Parameters.AddWithValue("$n", zone.Name.Trim());
            cmd.Parameters.AddWithValue("$fee", Money.ToPence(zone.Fee));
            cmd.Parameters.AddWithValue("$min", Money.ToPence(zone.MinimumOrder));
            cmd.Parameters.AddWithValue("$free", Money.ToPence(zone.FreeOverAmount));
            cmd.Parameters.AddWithValue("$del", zone.IsDeliverable ? 1 : 0);
            cmd.Parameters.AddWithValue("$sort", order++);
            cmd.ExecuteNonQuery();
        }

        tx.Commit();
        Invalidate();
    }

    public int Count() => GetZones().Count;

    private List<DeliveryZone> Load()
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id,prefix,name,fee_pence,minimum_order_pence,free_over_pence,
                   is_deliverable,sort_order
            FROM delivery_zones ORDER BY sort_order, prefix
            """;

        var zones = new List<DeliveryZone>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
            zones.Add(new DeliveryZone
            {
                Id = r.GetString(0),
                Prefix = r.GetString(1),
                Name = r.GetString(2),
                Fee = Money.FromPence(r.GetInt64(3)),
                MinimumOrder = Money.FromPence(r.GetInt64(4)),
                FreeOverAmount = Money.FromPence(r.GetInt64(5)),
                IsDeliverable = r.GetInt32(6) == 1,
                SortOrder = r.GetInt32(7),
            });

        return zones;
    }
}
