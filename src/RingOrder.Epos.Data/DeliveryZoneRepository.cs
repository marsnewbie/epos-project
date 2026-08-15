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
            // Stored canonically, space and all: "B44 0" is a sector and "B40"
            // is a district, and squashing the space out turns one into the other.
            var rule = PostcodeRules.Parse(zone.Prefix);
            if (rule is null) continue;            // a blank or half-typed row
            var prefix = rule.Canonical;

            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO delivery_zones(id,prefix,name,fee_pence,minimum_order_pence,
                                           free_over_pence,is_deliverable,is_active,sort_order)
                VALUES($id,$p,$n,$fee,$min,$free,1,$active,$sort)
                ON CONFLICT(prefix) DO UPDATE SET
                  name=excluded.name, fee_pence=excluded.fee_pence,
                  minimum_order_pence=excluded.minimum_order_pence,
                  free_over_pence=excluded.free_over_pence,
                  is_active=excluded.is_active, sort_order=excluded.sort_order
                """;
            cmd.Parameters.AddWithValue("$id", zone.Id);
            cmd.Parameters.AddWithValue("$p", prefix);
            cmd.Parameters.AddWithValue("$n", zone.Name.Trim());
            cmd.Parameters.AddWithValue("$fee", Money.ToPence(zone.Fee));
            cmd.Parameters.AddWithValue("$min", Money.ToPence(zone.MinimumOrder));
            cmd.Parameters.AddWithValue("$free", Money.ToPence(zone.FreeOverAmount));
            cmd.Parameters.AddWithValue("$active", zone.IsActive ? 1 : 0);
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
                   is_active,sort_order
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
                IsActive = r.GetInt32(6) == 1,
                SortOrder = r.GetInt32(7),
            });

        return zones;
    }
}

/// <summary>
/// Road-distance bands, and the distances themselves.
/// <para>
/// The distance cache exists for the same reason the address cache does: a shop
/// delivers to the same few thousand postcodes for years, and each pair only has
/// to be routed once. It also means the till keeps pricing correctly when the
/// routing service is unreachable, which for a public OSRM instance is a
/// question of when.
/// </para>
/// </summary>
public sealed class MilesBandRepository
{
    private readonly EposDb _db;
    private List<MilesBand>? _cache;
    private readonly object _gate = new();

    public MilesBandRepository(EposDb db) => _db = db;

    public IReadOnlyList<MilesBand> GetBands()
    {
        lock (_gate) return _cache ??= Load();
    }

    public void Invalidate()
    {
        lock (_gate) _cache = null;
    }

    public void Replace(IEnumerable<MilesBand> bands)
    {
        using var conn = _db.Open();
        using var tx = conn.BeginTransaction();

        using (var wipe = conn.CreateCommand())
        {
            wipe.Transaction = tx;
            wipe.CommandText = "DELETE FROM delivery_miles_bands";
            wipe.ExecuteNonQuery();
        }

        var order = 0;
        foreach (var band in bands.OrderBy(b => b.MinMiles))
        {
            if (band.MaxMiles <= band.MinMiles) continue;   // a band with no width prices nothing

            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO delivery_miles_bands(id,min_miles,max_miles,fee_pence,
                                                 minimum_order_pence,free_over_pence,sort_order)
                VALUES($id,$min,$max,$fee,$mo,$free,$sort)
                """;
            cmd.Parameters.AddWithValue("$id", band.Id);
            cmd.Parameters.AddWithValue("$min", (double)band.MinMiles);
            cmd.Parameters.AddWithValue("$max", (double)band.MaxMiles);
            cmd.Parameters.AddWithValue("$fee", Money.ToPence(band.Fee));
            cmd.Parameters.AddWithValue("$mo", Money.ToPence(band.MinimumOrder));
            cmd.Parameters.AddWithValue("$free", Money.ToPence(band.FreeOverAmount));
            cmd.Parameters.AddWithValue("$sort", order++);
            cmd.ExecuteNonQuery();
        }

        tx.Commit();
        Invalidate();
    }

    /// <summary>A routed distance already paid for, or null.</summary>
    public decimal? GetCachedMiles(string fromPostcode, string toPostcode)
    {
        var (from, to) = Key(fromPostcode, toPostcode);
        if (from.Length == 0 || to.Length == 0) return null;

        using var conn = _db.Open();
        using var read = conn.CreateCommand();
        read.CommandText = "SELECT miles FROM distance_cache WHERE from_postcode=$f AND to_postcode=$t";
        read.Parameters.AddWithValue("$f", from);
        read.Parameters.AddWithValue("$t", to);

        var value = read.ExecuteScalar();
        if (value is null || value is DBNull) return null;

        using var bump = conn.CreateCommand();
        bump.CommandText =
            "UPDATE distance_cache SET hits = hits + 1 WHERE from_postcode=$f AND to_postcode=$t";
        bump.Parameters.AddWithValue("$f", from);
        bump.Parameters.AddWithValue("$t", to);
        bump.ExecuteNonQuery();

        return (decimal)Convert.ToDouble(value);
    }

    public void PutCachedMiles(string fromPostcode, string toPostcode, decimal miles)
    {
        var (from, to) = Key(fromPostcode, toPostcode);
        if (from.Length == 0 || to.Length == 0) return;

        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO distance_cache(from_postcode,to_postcode,miles,fetched_at,hits)
            VALUES($f,$t,$m,$at,0)
            ON CONFLICT(from_postcode,to_postcode) DO UPDATE SET
              miles=excluded.miles, fetched_at=excluded.fetched_at
            """;
        cmd.Parameters.AddWithValue("$f", from);
        cmd.Parameters.AddWithValue("$t", to);
        cmd.Parameters.AddWithValue("$m", (double)miles);
        cmd.Parameters.AddWithValue("$at", DateTimeOffset.Now.ToString("o"));
        cmd.ExecuteNonQuery();
    }

    private static (string From, string To) Key(string from, string to) =>
        (UkPostcode.Normalise(from).Value, UkPostcode.Normalise(to).Value);

    private List<MilesBand> Load()
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id,min_miles,max_miles,fee_pence,minimum_order_pence,free_over_pence,sort_order
            FROM delivery_miles_bands ORDER BY min_miles
            """;

        var bands = new List<MilesBand>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
            bands.Add(new MilesBand
            {
                Id = r.GetString(0),
                MinMiles = (decimal)r.GetDouble(1),
                MaxMiles = (decimal)r.GetDouble(2),
                Fee = Money.FromPence(r.GetInt64(3)),
                MinimumOrder = Money.FromPence(r.GetInt64(4)),
                FreeOverAmount = Money.FromPence(r.GetInt64(5)),
                SortOrder = r.GetInt32(6),
            });

        return bands;
    }
}
