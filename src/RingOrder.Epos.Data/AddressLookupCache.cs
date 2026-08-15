using Microsoft.Data.Sqlite;
using RingOrder.Epos.Domain;

namespace RingOrder.Epos.Data;

/// <summary>What a cache row holds, so a stored answer replays exactly as it arrived.</summary>
public sealed class CachedAddressPayload
{
    public AddressLookupStatus Status { get; set; }
    public List<AddressCandidate> Candidates { get; set; } = [];
    public string Town { get; set; } = "";
    public string Provider { get; set; } = "";
}

public sealed record AddressCacheStats(int Postcodes, int Hits, DateTimeOffset? Newest);

/// <summary>
/// Stores every answer the lookup provider gave, keyed on the normalised postcode.
/// </summary>
public sealed class AddressCacheRepository
{
    private readonly EposDb _db;

    public AddressCacheRepository(EposDb db) => _db = db;

    /// <summary>Returns the stored answer and counts the hit, or null on a miss.</summary>
    public CachedAddressPayload? Get(UkPostcode postcode)
    {
        if (postcode.IsEmpty) return null;

        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT payload FROM address_cache WHERE postcode=$p";
        cmd.Parameters.AddWithValue("$p", postcode.Value);
        var json = cmd.ExecuteScalar() as string;
        if (string.IsNullOrEmpty(json)) return null;

        using var bump = conn.CreateCommand();
        bump.CommandText = "UPDATE address_cache SET hits = hits + 1 WHERE postcode=$p";
        bump.Parameters.AddWithValue("$p", postcode.Value);
        bump.ExecuteNonQuery();

        try
        {
            return JsonUtil.Deserialize<CachedAddressPayload>(json);
        }
        catch
        {
            // A row written by an older shape is not worth crashing over; treat
            // it as a miss and let the next lookup overwrite it.
            return null;
        }
    }

    public void Put(UkPostcode postcode, AddressLookupResult result)
    {
        if (postcode.IsEmpty) return;

        var payload = new CachedAddressPayload
        {
            Status = result.Status,
            Candidates = [.. result.Candidates],
            Town = result.Town,
            Provider = result.Source,
        };

        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO address_cache(postcode,provider,payload,town,latitude,longitude,fetched_at,hits)
            VALUES($p,$prov,$payload,$town,$lat,$lon,$at,0)
            ON CONFLICT(postcode) DO UPDATE SET
              provider=excluded.provider, payload=excluded.payload, town=excluded.town,
              latitude=excluded.latitude, longitude=excluded.longitude, fetched_at=excluded.fetched_at
            """;
        cmd.Parameters.AddWithValue("$p", postcode.Value);
        cmd.Parameters.AddWithValue("$prov", result.Source);
        cmd.Parameters.AddWithValue("$payload", JsonUtil.Serialize(payload));
        cmd.Parameters.AddWithValue("$town", result.Town);
        cmd.Parameters.AddWithValue("$lat", (object?)result.Latitude ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$lon", (object?)result.Longitude ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$at", DateTimeOffset.Now.ToString("o"));
        cmd.ExecuteNonQuery();
    }

    public AddressCacheStats Stats()
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT COUNT(*), COALESCE(SUM(hits),0), MAX(fetched_at) FROM address_cache";
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return new AddressCacheStats(0, 0, null);

        DateTimeOffset? newest = r[2] is DBNull ? null : DateTimeOffset.Parse(r.GetString(2));
        return new AddressCacheStats(r.GetInt32(0), r.GetInt32(1), newest);
    }

    /// <summary>
    /// Emptied only on request. A merchant who has changed provider may want the
    /// new one's data; everyone else is better off keeping what they paid for.
    /// </summary>
    public int Clear()
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM address_cache";
        return cmd.ExecuteNonQuery();
    }
}

/// <summary>
/// The lookup the till actually calls.
/// <para>
/// Three sources, in the order that costs least and helps most:
/// the cache, which is free and instant; the configured provider, which costs
/// money; and failing both, the shop's own delivery history, which knows the
/// regulars even when nothing else is available.
/// </para>
/// </summary>
public sealed class AddressLookupService
{
    private readonly AddressCacheRepository _cache;
    private readonly CustomerRepository _customers;
    private readonly Func<IAddressLookup> _provider;
    private readonly Func<bool> _cacheEnabled;

    public AddressLookupService(
        AddressCacheRepository cache,
        CustomerRepository customers,
        Func<IAddressLookup> provider,
        Func<bool> cacheEnabled)
    {
        _cache = cache;
        _customers = customers;
        _provider = provider;
        _cacheEnabled = cacheEnabled;
    }

    public async Task<AddressLookupResult> FindAsync(string rawPostcode, CancellationToken ct = default)
    {
        var postcode = UkPostcode.Normalise(rawPostcode);

        if (!postcode.IsValid)
            return FromHistory(postcode) ?? AddressLookupResult.Empty(
                AddressLookupStatus.InvalidPostcode,
                postcode.IsEmpty ? "Enter a postcode first." : $"\"{postcode.Value}\" is not a UK postcode.");

        if (_cacheEnabled() && _cache.Get(postcode) is { } cached)
            return new AddressLookupResult
            {
                Status = cached.Status,
                Candidates = cached.Candidates,
                Town = cached.Town,
                Source = "cache",
                Message = cached.Candidates.Count > 0
                    ? $"{cached.Candidates.Count} address{(cached.Candidates.Count == 1 ? "" : "es")} (saved)."
                    : cached.Town.Length > 0
                        ? $"Postcode confirmed — {cached.Town}."
                        : "No addresses found for that postcode.",
            };

        var result = await _provider().FindAsync(postcode, ct).ConfigureAwait(false);

        // Only a real answer is worth keeping. A timeout or a rejected key says
        // nothing about the postcode, and caching it would make one bad minute
        // permanent.
        if (_cacheEnabled() && result.Status is AddressLookupStatus.Ok or AddressLookupStatus.NotFound)
            _cache.Put(postcode, result);

        if (result.HasCandidates) return result;

        // Nothing usable from outside: fall back to streets this shop has already
        // delivered to. For a regular that is the right answer anyway.
        return FromHistory(postcode) ?? result;
    }

    private AddressLookupResult? FromHistory(UkPostcode postcode)
    {
        if (postcode.IsEmpty) return null;

        var candidates = _customers.FindAddressesByPostcode(postcode);
        if (candidates.Count == 0) return null;

        return new AddressLookupResult
        {
            Status = AddressLookupStatus.Ok,
            Candidates = candidates,
            Town = candidates[0].Town,
            Source = "history",
            Message = $"{candidates.Count} from your own delivery history.",
        };
    }
}
