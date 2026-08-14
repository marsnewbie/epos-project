using RingOrder.Epos.Domain;

namespace RingOrder.Epos.Hardware;

public interface IReceiptPrinter
{
    string Name { get; }
    Task PrintRawAsync(byte[] escPosPayload, CancellationToken ct = default);
    Task PrintTestPageAsync(CancellationToken ct = default);
}

public interface IKitchenPrinter : IReceiptPrinter;

public interface ICashDrawer
{
    Task OpenAsync(CancellationToken ct = default);
}

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

public sealed class WindowsEscPosPrinter : IReceiptPrinter, IKitchenPrinter
{
    private readonly Func<AppSettings> _settings;

    public WindowsEscPosPrinter(string name, Func<AppSettings> settings)
    {
        Name = name;
        _settings = settings;
    }

    public string Name { get; private set; }

    public void SetName(string name) => Name = name;

    public Task PrintRawAsync(byte[] escPosPayload, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            RawPrinter.SendBytes(Name, escPosPayload);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[HAL] Print failed → {Name}: {ex.Message}");
            throw;
        }
        return Task.CompletedTask;
    }

    public Task PrintTestPageAsync(CancellationToken ct = default)
    {
        var payload = TicketRenderer.RenderTestPage(Name, _settings());
        return PrintRawAsync(payload, ct);
    }
}

public sealed class EscPosCashDrawer : ICashDrawer
{
    private readonly Func<string> _printerName;

    public EscPosCashDrawer(Func<string> printerName) => _printerName = printerName;

    public Task OpenAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        RawPrinter.SendBytes(_printerName(), EscPos.OpenDrawer);
        return Task.CompletedTask;
    }
}

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
public sealed class LoggingReceiptPrinter : IReceiptPrinter, IKitchenPrinter
{
    public LoggingReceiptPrinter(string name) => Name = name;
    public string Name { get; }

    public Task PrintRawAsync(byte[] escPosPayload, CancellationToken ct = default)
    {
        Console.WriteLine($"[HAL] Print raw {escPosPayload.Length} bytes → {Name} (log stub)");
        return Task.CompletedTask;
    }

    public Task PrintTestPageAsync(CancellationToken ct = default)
    {
        Console.WriteLine($"[HAL] Test page → {Name} (log stub)");
        return Task.CompletedTask;
    }
}

public sealed class NullCashDrawer : ICashDrawer
{
    public Task OpenAsync(CancellationToken ct = default)
    {
        Console.WriteLine("[HAL] Open cash drawer (stub)");
        return Task.CompletedTask;
    }
}
