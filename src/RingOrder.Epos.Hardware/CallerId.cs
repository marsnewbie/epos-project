using System.IO.Ports;

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

    /// <summary>The network's name for the caller, when it sends one.</summary>
    public string? CallerName { get; init; }

    public DateTimeOffset ReceivedAt { get; init; } = DateTimeOffset.Now;
}

/// <summary>
/// Why a call arrived without a number. Kept apart from the number itself
/// because "withheld" is a fact about the call, and the alternative — putting
/// the letter the network sent into the number field — is how a shop ends up
/// with a customer called "P".
/// </summary>
public enum CallerIdWithheld
{
    /// <summary>A real number arrived.</summary>
    None,

    /// <summary>The caller withheld it (dialled 141).</summary>
    Private,

    /// <summary>The network could not supply one — international, payphone, trunk.</summary>
    Unavailable,
}

/// <summary>One decoded call.</summary>
public sealed record CallerIdCall(string? Number, string? Name, CallerIdWithheld Withheld)
{
    public bool HasNumber => !string.IsNullOrWhiteSpace(Number);
}

/// <summary>
/// Decodes what a Caller ID modem writes down its serial port.
/// <para>
/// Pure and line-at-a-time, so every format below is a test rather than a shop
/// reporting that the phone popup stopped working. There is no single standard
/// in practice: BT lines here deliver MDMF, cheap USB boxes emit SDMF on one
/// line, and a few firmwares invent their own labels.
/// </para>
/// </summary>
public sealed class CallerIdDecoder
{
    private string? _number;
    private string? _name;
    private CallerIdWithheld _withheld;
    private bool _sawAnything;

    /// <summary>
    /// The two letters that are not phone numbers.
    /// <para>
    /// A withheld call sends <c>NMBR = P</c> and an unavailable one <c>NMBR = O</c>.
    /// Some firmwares spell them out. Storing either as a number would put a
    /// customer called "P" in the phone book and search for them on every
    /// withheld call after.
    /// </para>
    /// </summary>
    private static CallerIdWithheld ClassifyMarker(string value) =>
        value.Trim().ToUpperInvariant() switch
        {
            "P" or "PRIVATE" or "WITHHELD" or "ANONYMOUS" or "BLOCKED" => CallerIdWithheld.Private,
            "O" or "OUT OF AREA" or "OUTOFAREA" or "UNAVAILABLE" or "UNKNOWN" => CallerIdWithheld.Unavailable,
            _ => CallerIdWithheld.None,
        };

    /// <summary>
    /// Feeds one line. Returns a call when the line ended one, otherwise null.
    /// <para>
    /// A call is only emitted at a boundary — a blank line, or the next RING —
    /// never as soon as the number is known. MDMF sends <c>NAME</c> <em>after</em>
    /// <c>NMBR</c>, so emitting on the number throws the caller's name away, and
    /// the screen shows a number for a customer the network just identified.
    /// </para>
    /// <para>
    /// A device that goes quiet without sending either boundary is handled by
    /// the read timeout in <see cref="SerialModemCallerId"/>, which calls
    /// <see cref="Flush"/>.
    /// </para>
    /// </summary>
    public CallerIdCall? Accept(string rawLine)
    {
        var line = rawLine.Trim();
        if (line.Length == 0) return Flush();

        // RING starts a new call. Anything half-collected before it belonged to
        // the previous one and is emitted rather than silently merged.
        if (line.Equals("RING", StringComparison.OrdinalIgnoreCase))
            return Flush();

        // SDMF packs the fields together: DATE=0217TIME=1234NMBR=07700900123.
        // Splitting on the labels rather than on a separator, because there is
        // not reliably one.
        foreach (var (label, value) in Fields(line))
        {
            _sawAnything = true;
            switch (label)
            {
                case "NMBR" or "NUMBER" or "CALLER NUMBER" or "CID" or "PHONE":
                    var marker = ClassifyMarker(value);
                    if (marker != CallerIdWithheld.None) _withheld = marker;
                    else if (Digits(value) is { Length: >= 3 } digits) _number = digits;
                    break;

                case "NAME" or "CALLER NAME" or "NAM":
                    if (ClassifyMarker(value) == CallerIdWithheld.None && value.Trim().Length > 0)
                        _name = value.Trim();
                    break;

                case "MESG" or "MESSAGE":
                    if (ClassifyMarker(value) is var m and not CallerIdWithheld.None) _withheld = m;
                    break;
            }
        }

        return null;
    }

    /// <summary>Ends the current call, if there is one worth reporting.</summary>
    public CallerIdCall? Flush()
    {
        if (!_sawAnything) return null;

        var call = new CallerIdCall(_number, _name, _withheld);
        _number = null;
        _name = null;
        _withheld = CallerIdWithheld.None;
        _sawAnything = false;

        return call.HasNumber || call.Withheld != CallerIdWithheld.None ? call : null;
    }

    private static readonly string[] Labels =
    [
        "CALLER NUMBER", "CALLER NAME", "NMBR", "NUMBER", "NAME", "NAM",
        "MESG", "MESSAGE", "DATE", "TIME", "CID", "PHONE",
    ];

    /// <summary>Splits a line into label/value pairs, whether or not it is spaced.</summary>
    private static IEnumerable<(string Label, string Value)> Fields(string line)
    {
        var upper = line.ToUpperInvariant();

        // Find every label occurrence, then take each value up to the next one.
        var hits = new List<(int Index, string Label)>();
        foreach (var label in Labels)
        {
            var from = 0;
            while (true)
            {
                var at = upper.IndexOf(label, from, StringComparison.Ordinal);
                if (at < 0) break;

                // Must be followed by a separator, or "NAME" matches inside a name.
                var after = at + label.Length;
                while (after < line.Length && line[after] == ' ') after++;
                if (after < line.Length && (line[after] == '=' || line[after] == ':'))
                    hits.Add((at, label));

                from = at + label.Length;
            }
        }

        if (hits.Count == 0) yield break;

        // Longer labels win where two overlap ("CALLER NUMBER" over "NUMBER").
        hits.Sort((a, b) => a.Index != b.Index
            ? a.Index.CompareTo(b.Index)
            : b.Label.Length.CompareTo(a.Label.Length));

        var claimed = -1;
        var kept = new List<(int Index, string Label)>();
        foreach (var hit in hits)
        {
            if (hit.Index < claimed) continue;
            kept.Add(hit);
            claimed = hit.Index + hit.Label.Length;
        }

        for (var i = 0; i < kept.Count; i++)
        {
            var (index, label) = kept[i];
            var start = index + label.Length;
            while (start < line.Length && (line[start] == ' ' || line[start] == '=' || line[start] == ':'))
                start++;

            var end = i + 1 < kept.Count ? kept[i + 1].Index : line.Length;
            if (end < start) end = start;

            yield return (label, line[start..end].Trim());
        }
    }

    private static string Digits(string value)
    {
        var chars = value.Where(c => char.IsDigit(c) || c == '+').ToArray();
        return new string(chars);
    }
}

/// <summary>
/// A Caller ID box or modem on a serial port — including the virtual COM port a
/// USB device presents.
/// <para>
/// The device is put into Caller ID mode with <c>AT+VCID=1</c>, which is what
/// almost everything answers to. A device that ignores it and reports anyway
/// still works, because nothing here depends on the command succeeding.
/// </para>
/// <para>
/// Reconnects on its own. A till runs for weeks and someone will unplug the
/// phone box to hoover; a caller display that never comes back until the next
/// restart is one the shop stops trusting.
/// </para>
/// </summary>
public sealed class SerialModemCallerId : ICallerIdProvider
{
    private readonly string _portName;
    private readonly int _baud;
    private readonly Action<string>? _log;
    private readonly CallerIdDecoder _decoder = new();

    private SerialPort? _port;
    private CancellationTokenSource? _cts;
    private Task? _pump;

    /// <summary>
    /// A phone rings six times and some boxes repeat the number on every ring.
    /// The screen should react once.
    /// </summary>
    private static readonly TimeSpan SameCallWindow = TimeSpan.FromSeconds(20);
    private string? _lastNumber;
    private DateTimeOffset _lastAt;

    public SerialModemCallerId(string portName, int baud = 9600, Action<string>? log = null)
    {
        _portName = portName;
        _baud = baud;
        _log = log;
    }

    public event EventHandler<CallerIdEventArgs>? CallReceived;

    /// <summary>Raised when a call came in with no number to show.</summary>
    public event EventHandler<CallerIdWithheld>? WithheldCallReceived;

    public string? LastError { get; private set; }
    public bool IsConnected => _port?.IsOpen == true;

    public Task StartAsync(CancellationToken ct = default)
    {
        if (_pump is not null) return Task.CompletedTask;

        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _pump = Task.Run(() => PumpAsync(_cts.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken ct = default)
    {
        if (_cts is null) return;
        await _cts.CancelAsync();

        try
        {
            if (_pump is not null) await _pump.WaitAsync(TimeSpan.FromSeconds(3), ct);
        }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
        {
            // A serial read that will not come back must not stop the till closing.
        }

        ClosePort();
        _pump = null;
        _cts.Dispose();
        _cts = null;
    }

    private async Task PumpAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                OpenPort();
                LastError = null;

                while (!ct.IsCancellationRequested && _port?.IsOpen == true)
                {
                    string line;
                    try
                    {
                        line = _port.ReadLine();
                    }
                    catch (TimeoutException)
                    {
                        // Silence between calls is the normal state, and it is
                        // also the end of a call from a device that sends no
                        // blank line and no second RING.
                        if (_decoder.Flush() is { } ended) Raise(ended);
                        continue;
                    }

                    if (_decoder.Accept(line) is { } call) Raise(call);
                }
            }
            catch (Exception ex)
            {
                // Unplugged, wrong port, or taken by something else. Recorded so
                // the Support screen can say so, then retried.
                LastError = ex.Message;
                _log?.Invoke($"caller id on {_portName}: {ex.Message}");
                ClosePort();
            }

            if (!ct.IsCancellationRequested)
            {
                try { await Task.Delay(TimeSpan.FromSeconds(5), ct); }
                catch (OperationCanceledException) { break; }
            }
        }

        ClosePort();
    }

    private void OpenPort()
    {
        if (_port?.IsOpen == true) return;

        _port = new SerialPort(_portName, _baud, Parity.None, 8, StopBits.One)
        {
            ReadTimeout = 1000,
            NewLine = "\r\n",
            DtrEnable = true,
            RtsEnable = true,
        };
        _port.Open();

        // Ask for Caller ID. A box that already reports ignores this harmlessly.
        try
        {
            _port.WriteLine("AT+VCID=1");
        }
        catch (Exception ex)
        {
            _log?.Invoke($"caller id: {_portName} did not accept AT+VCID=1 ({ex.Message})");
        }
    }

    private void ClosePort()
    {
        try { _port?.Dispose(); } catch { /* closing a broken port is not news */ }
        _port = null;
    }

    private void Raise(CallerIdCall call)
    {
        if (!call.HasNumber)
        {
            WithheldCallReceived?.Invoke(this, call.Withheld);
            return;
        }

        var now = DateTimeOffset.Now;
        if (call.Number == _lastNumber && now - _lastAt < SameCallWindow) return;

        _lastNumber = call.Number;
        _lastAt = now;

        CallReceived?.Invoke(this, new CallerIdEventArgs
        {
            PhoneNumber = call.Number!,
            CallerName = call.Name,
            ReceivedAt = now,
        });
    }

    public async ValueTask DisposeAsync() => await StopAsync();
}

/// <summary>
/// Stands in for the hardware. Also how the caller-display flow is demonstrated
/// to a merchant who has not bought a box yet.
/// </summary>
public sealed class SimulatedCallerId : ICallerIdProvider
{
    public event EventHandler<CallerIdEventArgs>? CallReceived;

    public Task StartAsync(CancellationToken ct = default) => Task.CompletedTask;
    public Task StopAsync(CancellationToken ct = default) => Task.CompletedTask;

    public void Simulate(string phone)
        => CallReceived?.Invoke(this, new CallerIdEventArgs { PhoneNumber = phone });

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
