using System.IO.Ports;
using System.Net.Sockets;
using RingOrder.Epos.Domain;

namespace RingOrder.Epos.Hardware;

/// <summary>
/// Getting bytes to a printer. The ticket is identical whichever way it goes;
/// only the pipe differs, so this is the only place that has to know about
/// spoolers, sockets and COM ports.
/// </summary>
public interface IPrintTransport
{
    /// <summary>Send a rendered ticket. Throws if it could not be delivered.</summary>
    Task SendAsync(PrintDevice device, byte[] payload, CancellationToken ct = default);

    /// <summary>
    /// Whether the device answers right now. Used for the status light, so it
    /// must be quick and must never throw.
    /// </summary>
    Task<bool> IsReachableAsync(PrintDevice device, CancellationToken ct = default);
}

public static class PrintTransports
{
    public static IPrintTransport For(PrintTransport transport) => transport switch
    {
        Domain.PrintTransport.Tcp => new TcpPrintTransport(),
        Domain.PrintTransport.Serial => new SerialPrintTransport(),
        Domain.PrintTransport.File => new FilePrintTransport(),
        _ => new WindowsQueuePrintTransport(),
    };
}

/// <summary>
/// Through the Windows spooler. The right choice for USB printers and for
/// anything the merchant already installed a driver for.
/// </summary>
public sealed class WindowsQueuePrintTransport : IPrintTransport
{
    public Task SendAsync(PrintDevice device, byte[] payload, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        RawPrinter.SendBytes(device.Address, payload);
        return Task.CompletedTask;
    }

    public Task<bool> IsReachableAsync(PrintDevice device, CancellationToken ct = default) =>
        Task.FromResult(RawPrinter.CanOpen(device.Address));
}

/// <summary>
/// Straight to port 9100. Preferred for kitchen printers: no spooler to jam,
/// and the printer will say whether it has paper.
/// </summary>
public sealed class TcpPrintTransport : IPrintTransport
{
    private const int DefaultPort = 9100;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    public async Task SendAsync(PrintDevice device, byte[] payload, CancellationToken ct = default)
    {
        var (host, port) = Parse(device.Address);
        using var client = new TcpClient();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(Timeout);

        await client.ConnectAsync(host, port, timeout.Token);
        await using var stream = client.GetStream();
        await stream.WriteAsync(payload, timeout.Token);
        await stream.FlushAsync(timeout.Token);
    }

    public async Task<bool> IsReachableAsync(PrintDevice device, CancellationToken ct = default)
    {
        try
        {
            var (host, port) = Parse(device.Address);
            using var client = new TcpClient();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(2));
            await client.ConnectAsync(host, port, timeout.Token);
            return client.Connected;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Real-time status, which the spooler cannot give us: DLE EOT 4 answers
    /// even while the printer is busy. Bit 5 set means out of paper, bit 2 means
    /// the cover is open. Null when the printer does not answer at all.
    /// </summary>
    public async Task<PrinterStatus?> QueryStatusAsync(PrintDevice device, CancellationToken ct = default)
    {
        try
        {
            var (host, port) = Parse(device.Address);
            using var client = new TcpClient();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(2));
            await client.ConnectAsync(host, port, timeout.Token);

            await using var stream = client.GetStream();
            await stream.WriteAsync(new byte[] { 0x10, 0x04, 0x04 }, timeout.Token);

            var buffer = new byte[1];
            var read = await stream.ReadAsync(buffer, timeout.Token);
            if (read != 1) return null;

            var flags = buffer[0];
            return new PrinterStatus(
                OutOfPaper: (flags & 0x20) != 0,
                CoverOpen: (flags & 0x04) != 0);
        }
        catch
        {
            return null;
        }
    }

    private static (string Host, int Port) Parse(string address)
    {
        if (string.IsNullOrWhiteSpace(address))
            throw new InvalidOperationException("Printer address is empty.");

        var parts = address.Split(':', 2);
        var port = parts.Length == 2 && int.TryParse(parts[1], out var parsed) ? parsed : DefaultPort;
        return (parts[0].Trim(), port);
    }
}

public sealed record PrinterStatus(bool OutOfPaper, bool CoverOpen)
{
    public bool IsReady => !OutOfPaper && !CoverOpen;

    public string Describe() =>
        OutOfPaper ? "out of paper" : CoverOpen ? "cover open" : "ready";
}

/// <summary>
/// A serial port, which is also what a paired Bluetooth printer looks like on
/// Windows. Supported because merchants own these, not because they are a good
/// idea in a kitchen: the pairing drops, the adapter sleeps, and re-pairing
/// mid-service is not something anyone should have to do.
/// </summary>
public sealed class SerialPrintTransport : IPrintTransport
{
    public Task SendAsync(PrintDevice device, byte[] payload, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var (portName, baud) = Parse(device.Address);

        using var port = new SerialPort(portName, baud)
        {
            WriteTimeout = 5000,
            Handshake = Handshake.None,
        };
        port.Open();
        port.Write(payload, 0, payload.Length);
        // Serial has no acknowledgement: the write returns as soon as the bytes
        // are in the buffer, so give the printer time before dropping the line.
        Thread.Sleep(Math.Min(2000, 200 + payload.Length / 50));
        return Task.CompletedTask;
    }

    public Task<bool> IsReachableAsync(PrintDevice device, CancellationToken ct = default)
    {
        try
        {
            var (portName, _) = Parse(device.Address);
            return Task.FromResult(SerialPort.GetPortNames()
                .Any(p => string.Equals(p, portName, StringComparison.OrdinalIgnoreCase)));
        }
        catch
        {
            return Task.FromResult(false);
        }
    }

    private static (string Port, int Baud) Parse(string address)
    {
        var parts = address.Split(':', 2);
        var baud = parts.Length == 2 && int.TryParse(parts[1], out var parsed) ? parsed : 9600;
        return (parts[0].Trim(), baud);
    }
}

/// <summary>
/// Writes the ticket to a file. Used in development, and on a merchant's PC
/// when we need to see exactly what a printer was sent.
/// </summary>
public sealed class FilePrintTransport : IPrintTransport
{
    public async Task SendAsync(PrintDevice device, byte[] payload, CancellationToken ct = default)
    {
        var directory = string.IsNullOrWhiteSpace(device.Address)
            ? Path.Combine(Path.GetTempPath(), "ringorder-tickets")
            : device.Address;
        Directory.CreateDirectory(directory);

        var path = Path.Combine(directory, $"{DateTime.Now:yyyyMMdd-HHmmss-fff}.bin");
        await File.WriteAllBytesAsync(path, payload, ct);
    }

    public Task<bool> IsReachableAsync(PrintDevice device, CancellationToken ct = default) =>
        Task.FromResult(true);
}
