using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace RingOrder.Epos.Domain;

/// <summary>
/// One thing that happened, before it has been written down.
/// <para>
/// The log is an <b>outbox</b>, not an event store: the tables remain the truth
/// and this records what changed them. Full event sourcing — where the log is
/// the truth and every table is a projection — would be a rewrite of every
/// repository for a benefit this product does not need yet.
/// </para>
/// </summary>
/// <param name="Id">A UUID. Unique across terminals, unlike <c>seq</c>, which is local.</param>
/// <param name="TerminalId">Which machine this happened on.</param>
/// <param name="Entity">What kind of thing changed: <c>order</c>, <c>payment</c>, <c>shift</c>.</param>
/// <param name="EntityId">Which one.</param>
/// <param name="Op">The domain verb — <c>placed</c>, <c>paid</c>, <c>voided</c>. Not <c>update</c>.</param>
/// <param name="Payload">
/// JSON, and the caller's job to produce deterministically: it is hashed exactly
/// as given, so a serialiser that reorders keys would break the chain on a
/// re-read rather than on a change.
/// </param>
/// <param name="StaffId">Who, when a person did it. Null for the poller and the scheduler.</param>
public sealed record ChangeDraft(
    string Id,
    string TerminalId,
    string Entity,
    string EntityId,
    string Op,
    string Payload,
    DateTimeOffset At,
    string? StaffId);

/// <summary>One entry as it sits on disk, with its place in the chain.</summary>
/// <param name="Seq">
/// Local, monotonic, never reused. The cursor a sync reads from — and
/// deliberately <b>not</b> part of the hash: order is established by the chain
/// itself, and hashing a value the database assigns at insert time would mean
/// knowing it before the insert.
/// </param>
public sealed record ChangeEntry(
    long Seq,
    string Id,
    string TerminalId,
    string Entity,
    string EntityId,
    string Op,
    string Payload,
    DateTimeOffset At,
    string? StaffId,
    string PrevHash,
    string Hash)
{
    public ChangeDraft ToDraft() => new(Id, TerminalId, Entity, EntityId, Op, Payload, At, StaffId);
}

/// <summary>Where a chain first stops adding up, and how far it got.</summary>
/// <param name="Checked">How many entries were read.</param>
/// <param name="BrokenAt">The <c>seq</c> of the first entry that does not verify, or null.</param>
/// <param name="Reason">What was wrong with it, for the diagnostics export.</param>
public sealed record ChainResult(int Checked, long? BrokenAt, string? Reason)
{
    public bool Intact => BrokenAt is null;
}

/// <summary>
/// The tamper-evident chain over the change log.
/// <para>
/// Each entry carries the hash of the one before it, so altering or removing
/// anything invalidates every entry after it. That does not make the log
/// unalterable — a determined person with the file can rebuild the whole chain —
/// but it makes an alteration <em>visible</em>, which is what a fiscal
/// authority, an insurer or an accountant actually asks for.
/// </para>
/// <para>
/// <b>It has to exist from the first transaction.</b> A chain added later can
/// only attest to what happened after it was added, and the day somebody needs
/// this is a day about the past. Germany's KassenSichV, Italy and France's
/// NF525 all require a tamper-evident journal; none of them can be satisfied
/// retrospectively. Two columns now, impossible later.
/// </para>
/// </summary>
public static class ChangeChain
{
    /// <summary>What the first entry chains from. Sixty-four zeros, by convention.</summary>
    public const string Genesis = "0000000000000000000000000000000000000000000000000000000000000000";

    /// <summary>
    /// The exact bytes that get hashed.
    /// <para>
    /// Each field is written as its UTF-8 <em>byte</em> length, a colon, then the
    /// field. Length-prefixing rather than a separator because a payload is
    /// arbitrary JSON and any character chosen as a delimiter is a character an
    /// attacker can put inside a field to make two different entries hash the
    /// same. Byte length rather than character count so that a reimplementation
    /// in another language agrees — this is a contract with the cloud service,
    /// not an internal detail.
    /// </para>
    /// <para>
    /// <b>Never change this.</b> Every chain ever written is only verifiable by
    /// the exact function that wrote it; a "tidier" version would declare every
    /// shop's history broken.
    /// </para>
    /// </summary>
    public static string Canonical(string prevHash, ChangeDraft draft)
    {
        var sb = new StringBuilder();
        sb.Append(prevHash);

        Field(sb, draft.Id);
        Field(sb, draft.TerminalId);
        Field(sb, draft.Entity);
        Field(sb, draft.EntityId);
        Field(sb, draft.Op);
        Field(sb, Timestamp(draft.At));
        Field(sb, draft.StaffId ?? "");
        Field(sb, draft.Payload);

        return sb.ToString();

        static void Field(StringBuilder sb, string value) =>
            sb.Append('|').Append(Encoding.UTF8.GetByteCount(value)).Append(':').Append(value);
    }

    /// <summary>
    /// The one spelling of an instant this chain understands: UTC, round-trip
    /// format. Normalised rather than taken as written, so an entry hashes the
    /// same whether it was created in London in July or read back off a disk in
    /// another time zone.
    /// </summary>
    public static string Timestamp(DateTimeOffset at) =>
        at.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture);

    /// <summary>
    /// Lower-case hex, spelled out rather than using <c>ToHexStringLower</c>,
    /// which arrived in .NET 9. The casing is part of the stored value and so
    /// part of the contract.
    /// </summary>
    public static string Hash(string prevHash, ChangeDraft draft) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Canonical(prevHash, draft))))
               .ToLowerInvariant();

    /// <summary>
    /// Walks a chain and reports the first entry that does not add up.
    /// <para>
    /// Stops at the first break on purpose: everything after it is unverifiable
    /// anyway, and a list of five hundred consequential failures hides the one
    /// that matters.
    /// </para>
    /// </summary>
    /// <param name="entries">In <c>seq</c> order, starting from the first ever written.</param>
    public static ChainResult Verify(IReadOnlyList<ChangeEntry> entries) =>
        Verify(entries, Genesis);

    /// <summary>
    /// Verifies a slice that begins part-way along, given what the entry before
    /// it hashed to. Lets a long log be checked a page at a time rather than
    /// held in memory all at once — a verification that needs a gigabyte is one
    /// nobody ever runs.
    /// </summary>
    public static ChainResult Verify(IReadOnlyList<ChangeEntry> entries, string expectedFirstPrev)
    {
        var expectedPrev = expectedFirstPrev;

        for (var i = 0; i < entries.Count; i++)
        {
            var entry = entries[i];

            if (entry.PrevHash != expectedPrev)
                return new ChainResult(i, entry.Seq,
                    $"entry {entry.Seq} follows {Short(entry.PrevHash)}, but the entry before it hashes to {Short(expectedPrev)} — something was removed or reordered");

            var recomputed = Hash(entry.PrevHash, entry.ToDraft());
            if (recomputed != entry.Hash)
                return new ChainResult(i, entry.Seq,
                    $"entry {entry.Seq} hashes to {Short(recomputed)} but stores {Short(entry.Hash)} — its contents were changed after it was written");

            expectedPrev = entry.Hash;
        }

        return new ChainResult(entries.Count, null, null);
    }

    private static string Short(string hash) => hash.Length <= 12 ? hash : hash[..12];
}

/// <summary>
/// The verbs the log uses. Constants rather than an enum: they are written to
/// disk and read by another language, so an accidental renumbering must not be
/// possible.
/// </summary>
public static class ChangeEntity
{
    public const string Order = "order";
    public const string Payment = "payment";
    public const string Refund = "refund";
    public const string Shift = "shift";
    public const string CashMovement = "cash-movement";
}

/// <summary>
/// What happened, in the trade's words rather than the database's.
/// <para>
/// <c>placed</c> and <c>voided</c> rather than <c>insert</c> and <c>update</c>,
/// because the whole value of this log is that it can be read — by a person
/// asking what went on, and later by something reasoning about it. A log of
/// <c>update</c> tells neither anything.
/// </para>
/// </summary>
public static class ChangeOp
{
    public const string Placed = "placed";
    public const string Amended = "amended";
    public const string Voided = "voided";
    public const string Paid = "paid";
    public const string Refunded = "refunded";
    public const string Opened = "opened";
    public const string Closed = "closed";
    public const string Moved = "moved";
}
