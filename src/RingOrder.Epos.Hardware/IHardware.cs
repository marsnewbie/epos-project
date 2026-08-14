using RingOrder.Epos.Domain;

namespace RingOrder.Epos.Hardware;

public interface ICallerIdProvider : IAsyncDisposable
{
    event EventHandler<CallerIdEventArgs>? CallReceived;
    Task StartAsync(CancellationToken ct = default);
    Task StopAsync(CancellationToken ct = default);
}

public sealed class CallerIdEventArgs : EventArgs
{
    public required string PhoneNumber { get; init; }
    public DateTimeOffset ReceivedAt { get; init; } = DateTimeOffset.Now;
}

public interface IPaymentTerminal
{
    string DisplayName { get; }
    Task<PaymentResult> StartSaleAsync(decimal amount, CancellationToken ct = default);
    Task CancelAsync(CancellationToken ct = default);
}

public sealed record PaymentResult(bool Success, string? Reference, string? Message);

public sealed class ManualCardTerminal : IPaymentTerminal
{
    public string DisplayName => "Card (manual)";

    public Task<PaymentResult> StartSaleAsync(decimal amount, CancellationToken ct = default)
        => Task.FromResult(new PaymentResult(true, null, $"Manual card £{amount:0.00} — confirm on terminal"));

    public Task CancelAsync(CancellationToken ct = default) => Task.CompletedTask;
}

public sealed class SimulatedCallerId : ICallerIdProvider
{
    public event EventHandler<CallerIdEventArgs>? CallReceived;

    public Task StartAsync(CancellationToken ct = default) => Task.CompletedTask;
    public Task StopAsync(CancellationToken ct = default) => Task.CompletedTask;

    public void Simulate(string phone)
        => CallReceived?.Invoke(this, new CallerIdEventArgs { PhoneNumber = phone });

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

/// <summary>Fallback when printer queue missing — logs only.</summary>
