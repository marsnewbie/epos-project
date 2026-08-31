using Microsoft.Data.Sqlite;
using RingOrder.Epos.Domain;

namespace RingOrder.Epos.Data;

/// <summary>
/// Appends to the change log, and never does anything else to it.
/// <para>
/// There is no update and no delete, and that is the whole point: a log with a
/// way to rewrite it is a log nobody can rely on. Sync progress is a watermark
/// in <c>settings</c> rather than a column here, so "append-only" has no
/// exceptions to remember.
/// </para>
/// </summary>
public sealed class ChangeLogRepository
{
    private const string SyncedKey = "cloud.change-log-synced-seq";

    private readonly EposDb _db;

    public ChangeLogRepository(EposDb db) => _db = db;

    /// <summary>
    /// Appends inside a transaction the caller already opened.
    /// <para>
    /// <b>This is the one that matters.</b> A log entry written in its own
    /// transaction can commit when the change it describes rolled back, or the
    /// other way round — and a log that disagrees with the data is worse than no
    /// log, because it will be believed.
    /// </para>
    /// <para>
    /// Reading the previous hash inside the same transaction is what makes the
    /// chain safe: SQLite serialises writers, so no second writer can slip an
    /// entry in between the read and the insert.
    /// </para>
    /// </summary>
    public ChangeEntry Append(SqliteConnection conn, SqliteTransaction tx, ChangeDraft draft)
    {
        var prevHash = LastHash(conn, tx);
        var hash = ChangeChain.Hash(prevHash, draft);

        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO change_log(id, terminal_id, entity, entity_id, op, payload, at, staff_id, prev_hash, hash)
            VALUES($id, $terminal, $entity, $entityId, $op, $payload, $at, $staff, $prev, $hash)
            RETURNING seq
            """;
        cmd.Parameters.AddWithValue("$id", draft.Id);
        cmd.Parameters.AddWithValue("$terminal", draft.TerminalId);
        cmd.Parameters.AddWithValue("$entity", draft.Entity);
        cmd.Parameters.AddWithValue("$entityId", draft.EntityId);
        cmd.Parameters.AddWithValue("$op", draft.Op);
        cmd.Parameters.AddWithValue("$payload", draft.Payload);

        // Stored in exactly the spelling that was hashed, so a row can be
        // re-verified from what is on disk without anybody having to know how
        // the timestamp was normalised.
        cmd.Parameters.AddWithValue("$at", ChangeChain.Timestamp(draft.At));

        cmd.Parameters.AddWithValue("$staff", (object?)draft.StaffId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$prev", prevHash);
        cmd.Parameters.AddWithValue("$hash", hash);

        var seq = (long)(cmd.ExecuteScalar() ?? 0L);

        return new ChangeEntry(
            seq, draft.Id, draft.TerminalId, draft.Entity, draft.EntityId,
            draft.Op, draft.Payload, draft.At, draft.StaffId, prevHash, hash);
    }

    /// <summary>
    /// Appends on its own. Only for things that change nothing else — a shift
    /// opening, a cash movement already committed. Anything that writes a row
    /// elsewhere must use the overload that takes the caller's transaction.
    /// </summary>
    public ChangeEntry Append(ChangeDraft draft)
    {
        using var conn = _db.Open();
        using var tx = conn.BeginTransaction();

        var entry = Append(conn, tx, draft);
        tx.Commit();
        return entry;
    }

    /// <summary>
    /// The payload of the most recent entry about one thing, or null if there is
    /// none.
    /// <para>
    /// Used to answer "would this entry say anything new?". One index probe, and
    /// it compares against what was actually written rather than against a guess
    /// reassembled from the tables.
    /// </para>
    /// </summary>
    public string? LastPayloadFor(SqliteConnection conn, SqliteTransaction? tx, string entity, string entityId)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            SELECT payload FROM change_log
             WHERE entity = $entity AND entity_id = $id
             ORDER BY seq DESC LIMIT 1
            """;
        cmd.Parameters.AddWithValue("$entity", entity);
        cmd.Parameters.AddWithValue("$id", entityId);

        return cmd.ExecuteScalar() as string;
    }

    private static string LastHash(SqliteConnection conn, SqliteTransaction? tx)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT hash FROM change_log ORDER BY seq DESC LIMIT 1";

        return cmd.ExecuteScalar() as string ?? ChangeChain.Genesis;
    }

    /// <summary>Everything after <paramref name="afterSeq"/>. The shape a sync reads.</summary>
    public List<ChangeEntry> Since(long afterSeq, int take = 500)
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT seq, id, terminal_id, entity, entity_id, op, payload, at, staff_id, prev_hash, hash
              FROM change_log
             WHERE seq > $after
             ORDER BY seq
             LIMIT $take
            """;
        cmd.Parameters.AddWithValue("$after", afterSeq);
        cmd.Parameters.AddWithValue("$take", take);

        return Read(cmd);
    }

    /// <summary>What happened to one thing, oldest first. The question support actually asks.</summary>
    public List<ChangeEntry> For(string entity, string entityId)
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT seq, id, terminal_id, entity, entity_id, op, payload, at, staff_id, prev_hash, hash
              FROM change_log
             WHERE entity = $entity AND entity_id = $id
             ORDER BY seq
            """;
        cmd.Parameters.AddWithValue("$entity", entity);
        cmd.Parameters.AddWithValue("$id", entityId);

        return Read(cmd);
    }

    public long LastSeq()
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COALESCE(MAX(seq), 0) FROM change_log";
        return (long)(cmd.ExecuteScalar() ?? 0L);
    }

    /// <summary>
    /// Re-hashes the whole chain from the beginning and reports the first entry
    /// that does not add up.
    /// <para>
    /// Read in pages rather than all at once: a busy shop's log outlives the
    /// memory of the machine it is on, and a verification that needs a gigabyte
    /// is one nobody ever runs.
    /// </para>
    /// </summary>
    public ChainResult Verify(int pageSize = 2000)
    {
        var expectedPrev = ChangeChain.Genesis;
        var after = 0L;
        var checkedSoFar = 0;

        while (true)
        {
            var page = Since(after, pageSize);
            if (page.Count == 0) return new ChainResult(checkedSoFar, null, null);

            var result = ChangeChain.Verify(page, expectedPrev);
            if (!result.Intact)
                return result with { Checked = checkedSoFar + result.Checked };

            checkedSoFar += page.Count;
            expectedPrev = page[^1].Hash;
            after = page[^1].Seq;
        }
    }

    /// <summary>How far the cloud has been told. A watermark, because the log is strictly ordered.</summary>
    public long SyncedThrough()
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT value FROM settings WHERE key=$k";
        cmd.Parameters.AddWithValue("$k", SyncedKey);

        return long.TryParse(cmd.ExecuteScalar() as string, out var seq) ? seq : 0L;
    }

    public void RecordSynced(long seq)
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO settings(key, value) VALUES($k, $v)
            ON CONFLICT(key) DO UPDATE SET value=excluded.value
            """;
        cmd.Parameters.AddWithValue("$k", SyncedKey);
        cmd.Parameters.AddWithValue("$v", seq.ToString());
        cmd.ExecuteNonQuery();
    }

    private static List<ChangeEntry> Read(SqliteCommand cmd)
    {
        var entries = new List<ChangeEntry>();
        using var reader = cmd.ExecuteReader();

        while (reader.Read())
        {
            entries.Add(new ChangeEntry(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetString(5),
                reader.GetString(6),
                DateTimeOffset.Parse(reader.GetString(7), null, System.Globalization.DateTimeStyles.RoundtripKind),
                reader.IsDBNull(8) ? null : reader.GetString(8),
                reader.GetString(9),
                reader.GetString(10)));
        }

        return entries;
    }
}
