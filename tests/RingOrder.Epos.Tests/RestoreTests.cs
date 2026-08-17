using RingOrder.Epos.Data;
using Xunit;

namespace RingOrder.Epos.Tests;

/// <summary>
/// Putting a backup back — the most destructive thing the till can be asked to
/// do, and therefore the thing whose safety rails are worth asserting.
/// <para>
/// Every test runs against its own temporary folders. Nothing here may reach
/// <see cref="LocalPaths"/>: a test that wrote into the live shop's backup
/// folder is a defect this project has already had once, and a restore reaching
/// the wrong folder is that bug with the damage already done.
/// </para>
/// </summary>
public class RestoreTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), $"ringorder-restore-{Guid.NewGuid():N}");
    private readonly RestoreRequest.Paths _paths;

    public RestoreTests()
    {
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(Path.Combine(_root, "backups"));
        _paths = new RestoreRequest.Paths(
            _root,
            Path.Combine(_root, "backups"),
            Path.Combine(_root, "data.sqlite"));
    }

    private string Backup(string name, string contents)
    {
        var path = Path.Combine(_paths.BackupDirectory, name);
        File.WriteAllText(path, contents);
        return path;
    }

    private void Live(string contents) => File.WriteAllText(_paths.DatabasePath, contents);

    [Fact]
    public void Nothing_pending_changes_nothing()
    {
        Live("today");
        Assert.Null(RestoreRequest.ApplyPending(paths: _paths));
        Assert.Equal("today", File.ReadAllText(_paths.DatabasePath));
    }

    [Fact]
    public void A_restore_puts_the_backup_in_place()
    {
        Live("today");
        var backup = Backup("daily-2026-08-10.sqlite", "last tuesday");

        RestoreRequest.Write(backup, _paths);
        RestoreRequest.ApplyPending(paths: _paths);

        Assert.Equal("last tuesday", File.ReadAllText(_paths.DatabasePath));
    }

    /// <summary>
    /// A restore must itself be undoable. The person doing one is usually
    /// already having a bad morning, and "I restored the wrong day" must not be
    /// the end of the shop's records.
    /// </summary>
    [Fact]
    public void The_live_database_is_kept_before_it_is_overwritten()
    {
        Live("today's trading");
        RestoreRequest.Write(Backup("daily-2026-08-10.sqlite", "last tuesday"), _paths);
        RestoreRequest.ApplyPending(paths: _paths);

        var kept = RestoreRequest.List(_paths).Single(b => b.IsPreRestore);
        Assert.Equal("today's trading", File.ReadAllText(kept.Path));
    }

    /// <summary>
    /// The write-ahead log belongs to the database that was just replaced.
    /// Left behind, SQLite replays it over the restored file and quietly undoes
    /// part of the restore — which is worse than the restore failing outright.
    /// </summary>
    [Fact]
    public void The_write_ahead_files_of_the_replaced_database_are_removed()
    {
        Live("today");
        File.WriteAllText(_paths.DatabasePath + "-wal", "pending pages");
        File.WriteAllText(_paths.DatabasePath + "-shm", "shared memory");

        RestoreRequest.Write(Backup("daily-2026-08-10.sqlite", "last tuesday"), _paths);
        RestoreRequest.ApplyPending(paths: _paths);

        Assert.False(File.Exists(_paths.DatabasePath + "-wal"));
        Assert.False(File.Exists(_paths.DatabasePath + "-shm"));
    }

    /// <summary>
    /// The marker is cleared even when it cannot be honoured. Otherwise a
    /// backup someone deleted puts the till into failing the same way at every
    /// start, which is a shop that cannot open.
    /// </summary>
    [Fact]
    public void A_marker_naming_a_missing_backup_does_not_survive_to_the_next_start()
    {
        Live("today");
        RestoreRequest.Write(Path.Combine(_paths.BackupDirectory, "gone.sqlite"), _paths);

        var outcome = RestoreRequest.ApplyPending(paths: _paths);

        Assert.Contains("no longer there", outcome);
        Assert.Null(RestoreRequest.Pending(_paths));
        Assert.Equal("today", File.ReadAllText(_paths.DatabasePath));
    }

    [Fact]
    public void A_restore_runs_once_and_not_at_every_start_after()
    {
        Live("today");
        RestoreRequest.Write(Backup("daily-2026-08-10.sqlite", "last tuesday"), _paths);

        RestoreRequest.ApplyPending(paths: _paths);
        Live("trading since the restore");

        // Second start: nothing pending, so the day's work stays.
        Assert.Null(RestoreRequest.ApplyPending(paths: _paths));
        Assert.Equal("trading since the restore", File.ReadAllText(_paths.DatabasePath));
    }

    [Fact]
    public void Backups_are_listed_newest_first_and_named_by_what_they_are()
    {
        var older = Backup("daily-2026-08-10.sqlite", "older");
        File.SetLastWriteTime(older, DateTime.Now.AddDays(-3));
        Backup("pre-migration-v7-20260815-220609.sqlite", "before the upgrade");

        var listed = RestoreRequest.List(_paths);

        Assert.Equal(2, listed.Count);
        Assert.True(listed[0].TakenAt >= listed[1].TakenAt);
        Assert.True(listed.Single(b => b.Name.StartsWith("pre-migration")).IsPreMigration);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* temp */ }
        GC.SuppressFinalize(this);
    }
}
