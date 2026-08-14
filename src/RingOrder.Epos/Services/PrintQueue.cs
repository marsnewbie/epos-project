using RingOrder.Epos.Data;
using RingOrder.Epos.Domain;
using RingOrder.Epos.Hardware;

namespace RingOrder.Epos.Services;

/// <summary>
/// Runs the print queue: one worker per device, retrying on its own.
/// <para>
/// The point is that printing is never in the way of a sale. Queueing a ticket
/// is a database write that cannot fail for want of paper; getting it onto
/// paper happens behind the counter. A kitchen printer that is switched off at
/// 6pm must not stop the till taking money at 6:01.
/// </para>
/// </summary>
public sealed class PrintQueue : IAsyncDisposable
{
    private readonly PrintJobRepository _jobs;
    private readonly PrintDeviceRepository _devices;
    private readonly Action<string> _log;
    private readonly Dictionary<string, Task> _workers = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _wake = new(0);

    public PrintQueue(PrintJobRepository jobs, PrintDeviceRepository devices, Action<string>? log = null)
    {
        _jobs = jobs;
        _devices = devices;
        _log = log ?? (_ => { });
    }

    /// <summary>Devices whose last attempt failed, for the status light.</summary>
    public IReadOnlyDictionary<string, string> Faults => _faults;
    private readonly Dictionary<string, string> _faults = new(StringComparer.Ordinal);

    public event EventHandler? Changed;

    public void Start()
    {
        // A job left Claimed belongs to a till that closed or crashed while
        // printing. Nobody saw that paper, so it goes back in the queue.
        var orphans = _jobs.RequeueOrphans();
        if (orphans > 0) _log($"requeued {orphans} job(s) interrupted by a restart");

        foreach (var device in _devices.GetDevices(enabledOnly: true))
            EnsureWorker(device);
    }

    /// <summary>Nudges the workers after something has been queued.</summary>
    public void Wake()
    {
        foreach (var device in _devices.GetDevices(enabledOnly: true))
            EnsureWorker(device);

        if (_wake.CurrentCount == 0) _wake.Release();
    }

    private void EnsureWorker(PrintDevice device)
    {
        if (_workers.ContainsKey(device.Id)) return;
        _workers[device.Id] = Task.Run(() => WorkAsync(device.Id, _cts.Token));
    }

    private async Task WorkAsync(string deviceId, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var device = _devices.GetDevices().FirstOrDefault(d => d.Id == deviceId);
                if (device is { IsEnabled: true })
                {
                    while (!ct.IsCancellationRequested && _jobs.ClaimNext(deviceId) is { } job)
                        await AttemptAsync(device, job, ct);
                }
            }
            catch (Exception ex)
            {
                _log($"print worker {deviceId}: {ex.Message}");
            }

            try
            {
                // Woken by a new job, and otherwise a slow tick so that a
                // printer switched back on recovers without anyone doing
                // anything.
                await _wake.WaitAsync(TimeSpan.FromSeconds(5), ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task AttemptAsync(PrintDevice device, PrintJob job, CancellationToken ct)
    {
        var transport = PrintTransports.For(device.Transport);
        try
        {
            for (var copy = 0; copy < Math.Max(1, job.Copies); copy++)
                await transport.SendAsync(device, job.Payload, ct);

            _jobs.MarkPrinted(job);
            if (_faults.Remove(device.Id)) Changed?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            _jobs.MarkFailed(job, ex.Message);
            _faults[device.Id] = ex.Message;
            Changed?.Invoke(this, EventArgs.Empty);

            _log($"{device.Name}: {ex.Message} (attempt {job.Attempts + 1} of {PrintJob.MaxAttempts})");

            // Out of attempts. The ticket is not lost — it is in the reprint
            // list — but somebody has to be told, because the kitchen has not
            // seen this order.
            if (job.Attempts + 1 >= PrintJob.MaxAttempts)
                _log($"{device.Name}: giving up on order {job.OrderNumber}; reprint from Orders");
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync();
        try { await Task.WhenAll(_workers.Values); } catch { /* shutting down */ }
        _cts.Dispose();
        _wake.Dispose();
    }
}
