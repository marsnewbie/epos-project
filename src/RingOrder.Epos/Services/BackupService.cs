using RingOrder.Epos.Data;

namespace RingOrder.Epos.Services;

/// <summary>
/// Nightly copies of the database.
/// <para>
/// The failure this exists for is mundane and eventually certain: a cheap PC's
/// disk dies and the shop loses its order history and its customer phone book.
/// Nobody thinks about it until the morning it happens.
/// </para>
/// <para>
/// <c>VACUUM INTO</c> reads through the write-ahead log, so unlike copying the
/// file it cannot capture a half-written page — the copy is always openable.
/// </para>
/// </summary>
public sealed class BackupService : IAsyncDisposable
{
    private const int KeepDays = 14;

    private readonly EposDb _db;
    private readonly CancellationTokenSource _cts = new();
    private Task? _loop;

    public BackupService(EposDb db) => _db = db;

    public DateTimeOffset? LastBackupAt { get; private set; }
    public string? LastBackupPath { get; private set; }
    public string? LastError { get; private set; }

    public void Start()
    {
        if (_loop is not null) return;
        AppLog.Info("backup", $"scheduler started, keeping {KeepDays} days in {LocalPaths.BackupDirectory}");
        _loop = Task.Run(() => LoopAsync(_cts.Token));
    }

    /// <summary>
    /// Backs up if today has not been backed up yet. Called at startup and
    /// hourly — a shop that closes before midnight would never be backed up by
    /// a scheduler that only fires at 3am, and one that never turns the till off
    /// would never be backed up by one that only fires at startup.
    /// </summary>
    public string? BackupIfDue()
    {
        var today = DateTime.Now.Date;
        var name = $"daily-{today:yyyy-MM-dd}.sqlite";
        var path = Path.Combine(LocalPaths.BackupDirectory, name);

        if (File.Exists(path))
        {
            LastBackupAt ??= File.GetLastWriteTime(path);
            LastBackupPath ??= path;
            return null;
        }

        return BackupNow(path);
    }

    public string? BackupNow(string? path = null)
    {
        path ??= Path.Combine(
            LocalPaths.BackupDirectory, $"manual-{DateTime.Now:yyyyMMdd-HHmmss}.sqlite");

        try
        {
            _db.BackupTo(path);
            LastBackupAt = DateTimeOffset.Now;
            LastBackupPath = path;
            LastError = null;
            AppLog.Info("backup", $"wrote {Path.GetFileName(path)}");
            Prune();
            return path;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            AppLog.Error("backup", "failed", ex);
            return null;
        }
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            // The loop itself must never die. A scheduler that faults silently
            // leaves a shop with no backups and no sign of it.
            try
            {
                BackupIfDue();
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                AppLog.Error("backup", "scheduler tick failed", ex);
            }

            try
            {
                await Task.Delay(TimeSpan.FromHours(1), ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private static void Prune()
    {
        try
        {
            var cutoff = DateTime.Now.AddDays(-KeepDays);
            foreach (var file in Directory.EnumerateFiles(LocalPaths.BackupDirectory, "daily-*.sqlite"))
            {
                if (File.GetLastWriteTime(file) < cutoff) File.Delete(file);
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn("backup", $"could not prune old backups: {ex.Message}");
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync();
        try { if (_loop is not null) await _loop; } catch { /* shutting down */ }
        _cts.Dispose();
    }
}
