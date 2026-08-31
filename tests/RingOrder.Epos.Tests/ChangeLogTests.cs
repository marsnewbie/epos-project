using Microsoft.Data.Sqlite;
using RingOrder.Epos.Data;
using RingOrder.Epos.Domain;
using Xunit;

namespace RingOrder.Epos.Tests;

/// <summary>
/// The append-only record of what happened, and the chain that makes an
/// alteration to it visible.
/// <para>
/// The chain does not make the log unalterable — anybody with the file can
/// rebuild it. It makes an alteration <em>show</em>, which is what an
/// accountant, an insurer or a fiscal authority actually asks for, and it only
/// works if it has been there since the first transaction.
/// </para>
/// </summary>
public class ChangeLogTests : IDisposable
{
    private readonly string _dbPath =
        Path.Combine(Path.GetTempPath(), $"ringorder-test-{Guid.NewGuid():N}.sqlite");
    private readonly EposDb _db;
    private readonly ChangeLogRepository _log;

    public ChangeLogTests()
    {
        _db = new EposDb(_dbPath);
        _db.Migrate();
        _log = new ChangeLogRepository(_db);
    }

    private static ChangeDraft Draft(
        string entity = ChangeEntity.Order,
        string entityId = "order-1",
        string op = ChangeOp.Placed,
        string payload = """{"total":1250}""",
        string? staffId = "wei") =>
        new(Guid.NewGuid().ToString("n"), "till-a", entity, entityId, op, payload,
            new DateTimeOffset(2026, 8, 31, 19, 30, 0, TimeSpan.Zero), staffId);

    // ---- the chain ---------------------------------------------------------

    [Fact]
    public void The_first_entry_chains_from_the_genesis_hash()
    {
        var entry = _log.Append(Draft());

        Assert.Equal(1, entry.Seq);
        Assert.Equal(ChangeChain.Genesis, entry.PrevHash);
        Assert.Equal(64, entry.Hash.Length);
    }

    [Fact]
    public void Each_entry_carries_the_hash_of_the_one_before_it()
    {
        var first = _log.Append(Draft(entityId: "order-1"));
        var second = _log.Append(Draft(entityId: "order-2"));
        var third = _log.Append(Draft(entityId: "order-3"));

        Assert.Equal(first.Hash, second.PrevHash);
        Assert.Equal(second.Hash, third.PrevHash);
        Assert.True(_log.Verify().Intact);
    }

    /// <summary>
    /// The one that decides whether any of this was worth doing. An amount
    /// changed after the fact must not survive a re-read.
    /// </summary>
    [Fact]
    public void A_payload_changed_after_the_fact_is_reported()
    {
        _log.Append(Draft(entityId: "order-1", payload: """{"total":1250}"""));
        _log.Append(Draft(entityId: "order-2"));
        _log.Append(Draft(entityId: "order-3"));

        Tamper("UPDATE change_log SET payload = '{\"total\":250}' WHERE seq = 1");

        var result = _log.Verify();

        Assert.False(result.Intact);
        Assert.Equal(1, result.BrokenAt);
        Assert.Contains("contents were changed", result.Reason);
    }

    [Fact]
    public void An_entry_removed_from_the_middle_is_reported()
    {
        _log.Append(Draft(entityId: "order-1"));
        _log.Append(Draft(entityId: "order-2"));
        _log.Append(Draft(entityId: "order-3"));

        Tamper("DELETE FROM change_log WHERE seq = 2");

        var result = _log.Verify();

        Assert.False(result.Intact);
        Assert.Equal(3, result.BrokenAt);
        Assert.Contains("removed or reordered", result.Reason);
    }

    /// <summary>
    /// Deleting the newest entry is the one thing the chain cannot see, because
    /// nothing follows it to disagree. Stated as a test so nobody discovers it
    /// later and reports it as a fault: the defence against a truncated tail is
    /// sending entries to the cloud, not the chain.
    /// </summary>
    [Fact]
    public void The_chain_alone_cannot_see_a_truncated_tail()
    {
        _log.Append(Draft(entityId: "order-1"));
        _log.Append(Draft(entityId: "order-2"));

        Tamper("DELETE FROM change_log WHERE seq = 2");

        Assert.True(_log.Verify().Intact);

        // What does notice is the watermark: the cloud was told about an entry
        // that is no longer here.
        _log.RecordSynced(2);
        Assert.True(_log.SyncedThrough() > _log.LastSeq());
    }

    [Fact]
    public void Two_entries_swapped_over_are_reported()
    {
        _log.Append(Draft(entityId: "order-1"));
        _log.Append(Draft(entityId: "order-2"));

        Tamper("UPDATE change_log SET entity_id = 'order-2' WHERE seq = 1");
        Tamper("UPDATE change_log SET entity_id = 'order-1' WHERE seq = 2");

        Assert.False(_log.Verify().Intact);
    }

    // ---- the canonical form ------------------------------------------------

    /// <summary>
    /// Why the fields are length-prefixed rather than joined with a separator.
    /// <para>
    /// A payload is arbitrary JSON, so any character chosen as a delimiter is a
    /// character somebody can put inside a field. Joined naively, these two
    /// different entries produce the same string and therefore the same hash —
    /// which is a forged entry that verifies.
    /// </para>
    /// </summary>
    [Fact]
    public void A_field_containing_the_separator_cannot_impersonate_two_fields()
    {
        var a = Draft(entity: "order|payment", entityId: "42");
        var b = Draft(entity: "order", entityId: "payment|42") with { Id = a.Id };

        Assert.NotEqual(
            ChangeChain.Hash(ChangeChain.Genesis, a),
            ChangeChain.Hash(ChangeChain.Genesis, b));
    }

    /// <summary>
    /// The same instant written two ways must hash the same, or an entry stops
    /// verifying when it is read back in a different time zone — which is what a
    /// support copy of a database is.
    /// </summary>
    [Fact]
    public void The_same_instant_in_two_time_zones_is_one_hash()
    {
        var london = Draft() with { At = new DateTimeOffset(2026, 8, 31, 20, 30, 0, TimeSpan.FromHours(1)) };
        var utc = london with { At = new DateTimeOffset(2026, 8, 31, 19, 30, 0, TimeSpan.Zero) };

        Assert.Equal(
            ChangeChain.Hash(ChangeChain.Genesis, london),
            ChangeChain.Hash(ChangeChain.Genesis, utc));
    }

    [Fact]
    public void An_entry_survives_the_round_trip_through_the_database()
    {
        var written = _log.Append(Draft(payload: """{"total":1250,"note":"no chilli 不要辣"}"""));

        var read = _log.Since(0).Single();

        Assert.Equal(written, read);
        Assert.Equal(read.Hash, ChangeChain.Hash(read.PrevHash, read.ToDraft()));
    }

    // ---- writing it alongside the change it describes -----------------------

    /// <summary>
    /// The reason <c>Append</c> takes the caller's transaction. A log entry that
    /// commits when the change it describes rolled back is worse than no log,
    /// because it will be believed.
    /// </summary>
    [Fact]
    public void A_rolled_back_change_leaves_no_entry_behind()
    {
        using (var conn = _db.Open())
        using (var tx = conn.BeginTransaction())
        {
            _log.Append(conn, tx, Draft());
            // No commit: the change this described did not happen.
        }

        Assert.Empty(_log.Since(0));
        Assert.Equal(0, _log.LastSeq());
    }

    [Fact]
    public void A_committed_change_keeps_its_entry_and_the_chain_holds()
    {
        using (var conn = _db.Open())
        using (var tx = conn.BeginTransaction())
        {
            _log.Append(conn, tx, Draft(entityId: "order-1"));
            _log.Append(conn, tx, Draft(entityId: "order-2"));
            tx.Commit();
        }

        Assert.Equal(2, _log.Since(0).Count);
        Assert.True(_log.Verify().Intact);
    }

    // ---- reading it --------------------------------------------------------

    [Fact]
    public void What_happened_to_one_order_reads_back_in_order()
    {
        _log.Append(Draft(entityId: "order-1", op: ChangeOp.Placed));
        _log.Append(Draft(entityId: "order-2", op: ChangeOp.Placed));
        _log.Append(Draft(entityId: "order-1", op: ChangeOp.Paid));
        _log.Append(Draft(entityId: "order-1", op: ChangeOp.Refunded));

        var story = _log.For(ChangeEntity.Order, "order-1");

        Assert.Equal([ChangeOp.Placed, ChangeOp.Paid, ChangeOp.Refunded], story.Select(e => e.Op));
    }

    [Fact]
    public void A_cursor_reads_forward_and_never_sees_a_number_twice()
    {
        for (var i = 0; i < 5; i++) _log.Append(Draft(entityId: $"order-{i}"));

        var first = _log.Since(0, take: 2);
        var next = _log.Since(first[^1].Seq, take: 2);

        Assert.Equal([1L, 2L], first.Select(e => e.Seq));
        Assert.Equal([3L, 4L], next.Select(e => e.Seq));
    }

    /// <summary>
    /// A watermark rather than a column on every row, so the log has no mutable
    /// field at all and "append-only" needs no exceptions remembering.
    /// </summary>
    [Fact]
    public void Sync_progress_is_a_watermark_and_starts_at_nothing()
    {
        Assert.Equal(0, _log.SyncedThrough());

        _log.Append(Draft());
        _log.Append(Draft());
        _log.RecordSynced(_log.LastSeq());

        Assert.Equal(2, _log.SyncedThrough());
        Assert.Empty(_log.Since(_log.SyncedThrough()));
    }

    [Fact]
    public void Verification_pages_through_a_log_longer_than_one_read()
    {
        for (var i = 0; i < 25; i++) _log.Append(Draft(entityId: $"order-{i}"));

        var result = _log.Verify(pageSize: 4);

        Assert.True(result.Intact);
        Assert.Equal(25, result.Checked);
    }

    [Fact]
    public void An_empty_log_is_an_intact_one()
    {
        var result = _log.Verify();

        Assert.True(result.Intact);
        Assert.Equal(0, result.Checked);
    }

    /// <summary>Raw SQL, because no repository offers a way to do this — which is the point.</summary>
    private void Tamper(string sql)
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    public void Dispose()
    {
        _db.Dispose();
        SqliteConnection.ClearAllPools();
        try { File.Delete(_dbPath); } catch { /* the temp folder can keep it */ }
        GC.SuppressFinalize(this);
    }

    // ---- what the till actually writes --------------------------------------

    private OrderRepository Orders() => new(_db, _log);
    private ShiftRepository Shifts() => new(_db, _log);

    /// <summary>Order numbers are unique, so a test with two tickets needs two.</summary>
    private static PosOrder Ticket(string id = "order-1", string number = "1043") => new()
    {
        Id = id,
        OrderNumber = number,
        Status = PosOrderStatus.Open,
        TerminalId = "till-a",
        StaffId = "wei",
        ServiceType = ServiceType.Collection,
        Channel = OrderChannel.Counter,
        Lines = [new CartLine { Id = $"{id}-line-1", Name = "Kung Po", Quantity = 1, BasePrice = 8.50m }],
    };

    /// <summary>
    /// The verb is worked out from what changed, not passed in. A log the
    /// callers have to remember to write is a log with holes, and the hole is
    /// always the path somebody added in a hurry.
    /// </summary>
    [Fact]
    public void An_order_writes_placed_then_amended_then_paid()
    {
        var orders = Orders();
        var ticket = Ticket();

        orders.Upsert(ticket);

        ticket.Lines.Add(new CartLine { Id = "line-2", Name = "Rice", Quantity = 1, BasePrice = 3.00m });
        orders.Upsert(ticket);

        ticket.Tenders.Add(new OrderTender { Id = "t-1", Type = TenderType.Cash, Amount = 11.50m });
        ticket.Status = PosOrderStatus.Paid;
        orders.Upsert(ticket);

        var story = _log.For(ChangeEntity.Order, ticket.Id).Select(e => e.Op);

        Assert.Equal([ChangeOp.Placed, ChangeOp.Amended, ChangeOp.Paid], story);
        Assert.True(_log.Verify().Intact);
    }

    /// <summary>
    /// A ticket being typed is saved on nearly every keystroke. Four hundred
    /// amendments per order would bury the events anybody cares about.
    /// </summary>
    [Fact]
    public void A_draft_nobody_has_committed_to_is_not_recorded()
    {
        var ticket = Ticket();
        ticket.Status = PosOrderStatus.Draft;

        Orders().Upsert(ticket);
        Orders().Upsert(ticket);

        Assert.Empty(_log.Since(0));
    }

    /// <summary>
    /// Money is the thing this log exists to account for. A split payment
    /// recorded only as a total cannot be reconciled against a card terminal's
    /// own report.
    /// </summary>
    [Fact]
    public void Every_tender_gets_its_own_entry_exactly_once()
    {
        var orders = Orders();
        var ticket = Ticket();
        orders.Upsert(ticket);

        ticket.Tenders.Add(new OrderTender { Id = "t-1", Type = TenderType.Cash, Amount = 5.00m });
        orders.Upsert(ticket);

        ticket.Tenders.Add(new OrderTender { Id = "t-2", Type = TenderType.CardIntegrated, Amount = 3.50m, Reference = "auth-99" });
        ticket.Status = PosOrderStatus.Paid;
        orders.Upsert(ticket);

        // Saved again, changing nothing about the money.
        orders.Upsert(ticket);

        var payments = _log.Since(0).Where(e => e.Entity == ChangeEntity.Payment).ToList();

        Assert.Equal(["t-1", "t-2"], payments.Select(p => p.EntityId));
        Assert.Contains("auth-99", payments[1].Payload);
        Assert.DoesNotContain("5.00", payments[0].Payload);   // pence, never a decimal string
        Assert.Contains("500", payments[0].Payload);
    }

    [Fact]
    public void A_void_and_a_refund_are_different_events()
    {
        var orders = Orders();

        var voided = Ticket("order-void", "2001");
        orders.Upsert(voided);
        voided.Status = PosOrderStatus.Voided;
        orders.Upsert(voided);

        var refunded = Ticket("order-refund", "2002");
        refunded.Tenders.Add(new OrderTender { Id = "t-9", Type = TenderType.Cash, Amount = 8.50m });
        refunded.Status = PosOrderStatus.Paid;
        orders.Upsert(refunded);
        refunded.Status = PosOrderStatus.Refunded;
        orders.Upsert(refunded);

        Assert.Equal(ChangeOp.Voided, _log.For(ChangeEntity.Order, "order-void")[^1].Op);
        Assert.Equal(ChangeOp.Refunded, _log.For(ChangeEntity.Order, "order-refund")[^1].Op);
    }

    [Fact]
    public void Opening_and_closing_a_shift_are_recorded_with_both_counts()
    {
        var shifts = Shifts();

        var shift = shifts.Open("wei", openingFloat: 100m, terminalId: "till-a");
        shifts.Close(shift, "wei", declaredCash: 341.50m, expectedCash: 340m, notes: null);

        var story = _log.For(ChangeEntity.Shift, shift.Id);

        Assert.Equal([ChangeOp.Opened, ChangeOp.Closed], story.Select(e => e.Op));
        Assert.Contains("10000", story[0].Payload);   // the float, in pence
        Assert.Contains("34150", story[1].Payload);   // counted
        Assert.Contains("34000", story[1].Payload);   // expected
        Assert.True(_log.Verify().Intact);
    }

    /// <summary>
    /// The order and its payments go in one transaction, so a chain built from
    /// them holds without anybody having to sequence the writes by hand.
    /// </summary>
    [Fact]
    public void An_order_and_its_money_land_in_one_unbroken_chain()
    {
        var orders = Orders();
        var ticket = Ticket();
        ticket.Tenders.Add(new OrderTender { Id = "t-1", Type = TenderType.Cash, Amount = 8.50m });
        ticket.Status = PosOrderStatus.Paid;

        orders.Upsert(ticket);

        var entries = _log.Since(0);
        Assert.Equal(2, entries.Count);
        Assert.Equal(ChangeChain.Genesis, entries[0].PrevHash);
        Assert.Equal(entries[0].Hash, entries[1].PrevHash);
        Assert.True(_log.Verify().Intact);
    }

    /// <summary>A till built without a change log still works — every test above this line proves the opposite case.</summary>
    [Fact]
    public void A_repository_with_no_change_log_writes_orders_as_before()
    {
        new OrderRepository(_db).Upsert(Ticket());

        Assert.Empty(_log.Since(0));
    }
}
