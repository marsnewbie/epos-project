namespace RingOrder.Epos.Hardware;

/// <summary>
/// How a card transaction ended.
/// <para>
/// <see cref="Unknown"/> is the reason this is an enum rather than a bool, and
/// it is the case the whole design exists for. See <see cref="IPaymentTerminal"/>.
/// </para>
/// </summary>
public enum PaymentOutcome
{
    Approved,
    Declined,

    /// <summary>Stopped at the terminal, by the cashier or the customer.</summary>
    Cancelled,

    /// <summary>The terminal answered, and said it could not do it.</summary>
    Failed,

    /// <summary>
    /// The till does not know. The cable was pulled, the terminal rebooted, or
    /// the answer never came back. **This is not a decline.** A customer whose
    /// card was charged and whose ticket says unpaid will be asked to pay twice.
    /// </summary>
    Unknown,
}

/// <summary>How the cardholder proved it was theirs — printed on the merchant copy.</summary>
public enum CardholderVerification
{
    None,
    Pin,
    Signature,
    OnDevice,
}

/// <summary>
/// One request to the terminal.
/// <para>
/// <see cref="Reference"/> is assigned by the till, not by the terminal, and it
/// is what makes the whole thing recoverable: a till that has lost the answer
/// can ask about a reference it chose, whereas one that waited for the terminal
/// to name the transaction has nothing to ask about.
/// </para>
/// </summary>
public sealed record PaymentRequest
{
    public required string Reference { get; init; }
    public required decimal Amount { get; init; }

    /// <summary>Included in <see cref="Amount"/>; carried so the receipt can name it.</summary>
    public decimal Gratuity { get; init; }

    /// <summary>A refund to the card, which most terminals treat as its own message.</summary>
    public bool IsRefund { get; init; }

    public string? OrderNumber { get; init; }
}

/// <summary>What came back. Everything except the outcome may be absent.</summary>
public sealed record PaymentResult
{
    public required PaymentOutcome Outcome { get; init; }
    public required string Reference { get; init; }

    /// <summary>What was actually authorised. A terminal may approve less than was asked.</summary>
    public decimal AmountAuthorised { get; init; }

    public string? AuthCode { get; init; }

    /// <summary>Visa, Mastercard, Amex — as the terminal names it.</summary>
    public string? Scheme { get; init; }

    /// <summary>Masked by the terminal. The till must never see or store a full PAN.</summary>
    public string? MaskedPan { get; init; }

    public CardholderVerification Verification { get; init; } = CardholderVerification.None;

    /// <summary>Plain wording for the cashier. Never a bare numeric code.</summary>
    public string? Message { get; init; }

    /// <summary>
    /// Receipt text the terminal wants printed. Card scheme rules put the
    /// wording under the acquirer's control, so it is printed as supplied rather
    /// than rebuilt from the fields above.
    /// </summary>
    public string? MerchantReceipt { get; init; }
    public string? CustomerReceipt { get; init; }

    public bool IsApproved => Outcome == PaymentOutcome.Approved;

    /// <summary>
    /// True when the till must not decide anything on its own. Neither taking
    /// the money nor releasing the ticket is safe: someone has to look at the
    /// terminal or the acquirer's portal.
    /// </summary>
    public bool NeedsResolving => Outcome == PaymentOutcome.Unknown;

    public static PaymentResult Unknown(string reference, string? message = null) => new()
    {
        Outcome = PaymentOutcome.Unknown,
        Reference = reference,
        Message = message ?? "No answer from the card terminal",
    };
}

/// <summary>
/// A card terminal the till can drive.
/// <para>
/// Modelled on how integrated terminals actually behave rather than on any one
/// vendor: the till assigns a reference, sends one sale, and either gets an
/// answer or does not. Vendors differ in transport and encoding — Dojo, Zettle,
/// Verifone, Ingenico and PAX all have their own — and none of that belongs
/// above this line.
/// </para>
/// <para>
/// <b>The rule that matters: never retry a sale.</b> A retry is how a customer
/// is charged twice, and a double charge is far worse than a slow one. When an
/// answer is lost the till asks <see cref="QueryAsync"/> about the reference it
/// chose, and the terminal says what happened to it. That is why the reference
/// comes from us.
/// </para>
/// </summary>
public interface IPaymentTerminal
{
    string DisplayName { get; }

    /// <summary>False when the till should not offer integrated card at all.</summary>
    bool IsConfigured { get; }

    /// <summary>
    /// Runs one sale or refund. Must never be called twice for one
    /// <see cref="PaymentRequest.Reference"/> — use <see cref="QueryAsync"/>.
    /// </summary>
    Task<PaymentResult> StartAsync(PaymentRequest request, CancellationToken ct = default);

    /// <summary>
    /// What happened to a reference. The recovery path, and the reason an
    /// <see cref="PaymentOutcome.Unknown"/> is survivable rather than a phone
    /// call to the acquirer.
    /// </summary>
    Task<PaymentResult> QueryAsync(string reference, CancellationToken ct = default);

    /// <summary>Asks the terminal to abandon what it is showing.</summary>
    Task CancelAsync(CancellationToken ct = default);
}

/// <summary>
/// No integration: the cashier runs the card on a standalone terminal and tells
/// the till it went through.
/// <para>
/// This is not a placeholder — it is what most small takeaways actually do, and
/// it stays supported after an integration exists. What it cannot do is verify
/// anything, so it never returns <see cref="PaymentOutcome.Unknown"/>: the
/// person pressing the button is the authority.
/// </para>
/// </summary>
public sealed class ManualCardTerminal : IPaymentTerminal
{
    public string DisplayName => "Card (manual)";
    public bool IsConfigured => true;

    public Task<PaymentResult> StartAsync(PaymentRequest request, CancellationToken ct = default) =>
        Task.FromResult(new PaymentResult
        {
            Outcome = PaymentOutcome.Approved,
            Reference = request.Reference,
            AmountAuthorised = request.Amount,
            Message = $"Run £{request.Amount:0.00} on the card machine",
        });

    /// <summary>
    /// Nothing to ask. There is no terminal on the other end, so the honest
    /// answer is that the till cannot find out — not a fabricated approval.
    /// </summary>
    public Task<PaymentResult> QueryAsync(string reference, CancellationToken ct = default) =>
        Task.FromResult(PaymentResult.Unknown(reference,
            "This till is not connected to the card machine — check the machine itself"));

    public Task CancelAsync(CancellationToken ct = default) => Task.CompletedTask;
}

/// <summary>
/// A terminal that behaves like a real one without any hardware: it holds
/// transactions against their references, answers queries about them, and can
/// be told to lose an answer.
/// <para>
/// Here so the recovery path is exercised before any hardware arrives. Losing
/// the answer is the case that is impossible to stage on a real terminal on
/// demand and is exactly the one that must work.
/// </para>
/// </summary>
public sealed class SimulatedPaymentTerminal : IPaymentTerminal
{
    private readonly Dictionary<string, PaymentResult> _completed = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    public string DisplayName => "Card (simulated)";
    public bool IsConfigured => true;

    /// <summary>
    /// How long the terminal appears to take.
    /// <para>
    /// Not decoration. Returning instantly made the simulator lie about the
    /// shape of the flow: the till's "no answer — checking" line was set and
    /// overwritten inside one synchronous continuation, so the recovery was
    /// invisible and a lost answer looked identical to an ordinary sale. Real
    /// hardware takes seconds, and the interface has to be exercised against
    /// something that behaves like it.
    /// </para>
    /// </summary>
    public TimeSpan Latency { get; set; } = TimeSpan.FromMilliseconds(900);

    /// <summary>Next sale declines. For exercising the decline path.</summary>
    public bool NextDeclines { get; set; }

    /// <summary>
    /// Next sale completes on the terminal but the answer never reaches the
    /// till — the case <see cref="QueryAsync"/> exists for.
    /// </summary>
    public bool NextLosesTheAnswer { get; set; }

    public async Task<PaymentResult> StartAsync(PaymentRequest request, CancellationToken ct = default)
    {
        var declines = NextDeclines;
        var loses = NextLosesTheAnswer;
        NextDeclines = false;
        NextLosesTheAnswer = false;

        // A customer tapping a card is not instantaneous, and neither is losing
        // the answer — that one usually means waiting for a timeout.
        await Task.Delay(loses ? Latency + Latency : Latency, ct);

        var result = new PaymentResult
        {
            Outcome = declines ? PaymentOutcome.Declined : PaymentOutcome.Approved,
            Reference = request.Reference,
            AmountAuthorised = declines ? 0m : request.Amount,
            AuthCode = declines ? null : Random.Shared.Next(100000, 999999).ToString(),
            Scheme = "Visa",
            MaskedPan = "**** **** **** 4242",
            Verification = CardholderVerification.OnDevice,
            Message = declines ? "Declined — ask for another card" : null,
        };

        // The terminal always knows, even when the till does not. That asymmetry
        // is the whole point of the recovery path.
        lock (_gate) _completed[request.Reference] = result;

        return loses ? PaymentResult.Unknown(request.Reference) : result;
    }

    public async Task<PaymentResult> QueryAsync(string reference, CancellationToken ct = default)
    {
        await Task.Delay(Latency, ct);

        lock (_gate)
        {
            return _completed.TryGetValue(reference, out var found)
                ? found
                // Never started, so nobody was charged. This is the one case
                // where the till may safely conclude the money did not move.
                : new PaymentResult
                {
                    Outcome = PaymentOutcome.Failed,
                    Reference = reference,
                    Message = "The terminal has no record of this transaction — nothing was taken",
                };
        }
    }

    public Task CancelAsync(CancellationToken ct = default) => Task.CompletedTask;
}
