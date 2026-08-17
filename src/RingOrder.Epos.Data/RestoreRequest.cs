namespace RingOrder.Epos.Data;

/// <summary>One backup file, as the restore screen lists it.</summary>
public sealed record BackupFile(string Path, string Name, DateTimeOffset TakenAt, long Bytes)
{
    /// <summary>Taken automatically before a schema upgrade rather than nightly.</summary>
    public bool IsPreMigration => Name.StartsWith("pre-migration-", StringComparison.OrdinalIgnoreCase);

    /// <summary>The copy taken of the live database immediately before a restore.</summary>
    public bool IsPreRestore => Name.StartsWith("pre-restore-", StringComparison.OrdinalIgnoreCase);

    public string SizeLabel => Bytes >= 1024 * 1024
        ? $"{Bytes / 1024d / 1024d:0.0} MB"
        : $"{Bytes / 1024d:0} KB";
}

/// <summary>
/// Putting a backup back.
/// <para>
/// The swap happens at the <em>next startup</em>, before anything opens the
/// database, and never while the till is running. SQLite in WAL mode has a
/// second and third file beside the main one and a pool of live connections;
/// copying over the top of that from inside the running process is how a
/// restore produces a database that is neither the backup nor the original.
/// </para>
/// <para>
/// So a restore is a request: a marker file naming the backup. The next start
/// honours it and deletes it. A crash between the two changes nothing — the
/// request is simply still there and still valid.
/// </para>
/// </summary>
public static class RestoreRequest
{
    /// <summary>
    /// Where a restore reads and writes. Passed in rather than read from
    /// <see cref="LocalPaths"/> so a test can never reach the live shop folder.
    /// <para>
    /// Not caution for its own sake: <c>BackupBeforeMigration</c> once wrote to
    /// a fixed machine-wide path, and every test run for months dropped a
    /// five-row database into the trading shop's backups. The restore
    /// instruction is "take the newest file", and the newest was usually a test.
    /// A restore reaching the wrong folder is the same bug with the destruction
    /// already done.
    /// </para>
    /// </summary>
    public sealed record Paths(string Root, string BackupDirectory, string DatabasePath)
    {
        public static Paths Live => new(
            LocalPaths.RootDirectory, LocalPaths.BackupDirectory, LocalPaths.DatabasePath);

        public string MarkerPath => System.IO.Path.Combine(Root, "restore-pending.txt");
    }

    public static void Write(string backupPath, Paths? paths = null) =>
        File.WriteAllText((paths ?? Paths.Live).MarkerPath, backupPath);

    public static string? Pending(Paths? paths = null)
    {
        var marker = (paths ?? Paths.Live).MarkerPath;
        return File.Exists(marker) ? File.ReadAllText(marker).Trim() : null;
    }

    public static void Clear(Paths? paths = null)
    {
        try
        {
            var marker = (paths ?? Paths.Live).MarkerPath;
            if (File.Exists(marker)) File.Delete(marker);
        }
        catch { /* a stale marker is handled by the validity check on the next run */ }
    }

    /// <summary>Newest first. Pre-restore copies are included: undoing a restore is a restore.</summary>
    public static List<BackupFile> List(Paths? paths = null)
    {
        var dir = (paths ?? Paths.Live).BackupDirectory;
        if (!Directory.Exists(dir)) return [];

        return Directory
            .EnumerateFiles(dir, "*.sqlite")
            .Select(p => new FileInfo(p))
            .Select(f => new BackupFile(f.FullName, f.Name, f.LastWriteTime, f.Length))
            .OrderByDescending(b => b.TakenAt)
            .ToList();
    }

    /// <summary>
    /// Carries out a pending restore. Called once at startup before the database
    /// is opened; a no-op when there is nothing pending.
    /// </summary>
    /// <returns>What happened, for the log, or null when there was nothing to do.</returns>
    public static string? ApplyPending(Action<string>? log = null, Paths? paths = null)
    {
        var where = paths ?? Paths.Live;
        if (Pending(where) is not { Length: > 0 } source) return null;

        // Clear first. A marker naming a file that cannot be restored must not
        // put the till into a loop of failing to start the same way every time.
        Clear(where);

        if (!File.Exists(source))
        {
            var missing = $"restore skipped — {Path.GetFileName(source)} is no longer there";
            log?.Invoke(missing);
            return missing;
        }

        var live = where.DatabasePath;

        try
        {
            // The live database is kept before it is overwritten. A restore is
            // the most destructive thing this till can be asked to do, and the
            // person doing it is usually already having a bad morning.
            if (File.Exists(live))
            {
                var safety = Path.Combine(
                    where.BackupDirectory,
                    $"pre-restore-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}"[..40] + ".sqlite");
                File.Copy(live, safety, overwrite: true);
                log?.Invoke($"live database kept at {Path.GetFileName(safety)}");
            }

            File.Copy(source, live, overwrite: true);

            // The write-ahead log and shared-memory file belong to the database
            // that was just replaced. Left behind, SQLite would replay them over
            // the restored file and undo part of the restore.
            foreach (var stray in new[] { live + "-wal", live + "-shm" })
                if (File.Exists(stray)) File.Delete(stray);

            var done = $"restored from {Path.GetFileName(source)}";
            log?.Invoke(done);
            return done;
        }
        catch (Exception ex)
        {
            var failed = $"restore from {Path.GetFileName(source)} failed: {ex.Message}";
            log?.Invoke(failed);
            return failed;
        }
    }
}
