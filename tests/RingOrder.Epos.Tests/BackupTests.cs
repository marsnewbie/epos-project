using Microsoft.Data.Sqlite;
using RingOrder.Epos.Data;
using RingOrder.Epos.Domain;
using Xunit;

namespace RingOrder.Epos.Tests;

/// <summary>
/// The copy a shop falls back on when the disk in a cheap PC dies. Untested
/// backups are the ones that turn out to be empty on the morning they matter.
/// </summary>
public class BackupTests : IDisposable
{
    private readonly string _dbPath =
        Path.Combine(Path.GetTempPath(), $"ringorder-backup-{Guid.NewGuid():N}.sqlite");
    private readonly string _backupPath =
        Path.Combine(Path.GetTempPath(), $"ringorder-backup-copy-{Guid.NewGuid():N}.sqlite");
    private readonly EposDb _db;

    public BackupTests()
    {
        _db = new EposDb(_dbPath);
        _db.Migrate();
    }

    [Fact]
    public void A_backup_opens_and_still_holds_the_data()
    {
        var menu = new MenuRepository(_db);
        menu.UpsertCategory(new Category { Id = "cat", Name = "Chicken" });
        menu.UpsertItem(new MenuItem
        {
            Id = "dish", CategoryId = "cat", MenuNumber = "88",
            Name = "Kung po chicken", BasePrice = 6.20m,
        });

        _db.BackupTo(_backupPath);

        Assert.True(File.Exists(_backupPath));

        using var restored = new EposDb(_backupPath);
        var restoredMenu = new MenuRepository(restored);
        Assert.Equal(1, restoredMenu.CountItems());
        Assert.Equal(6.20m, restoredMenu.GetItems(availableOnly: false).Single().BasePrice);
    }

    [Fact]
    public void A_backup_taken_while_writing_is_still_consistent()
    {
        // VACUUM INTO reads through the write-ahead log, so unlike copying the
        // file it cannot capture a half-written page.
        var orders = new OrderRepository(_db);
        for (var i = 0; i < 20; i++)
        {
            orders.Upsert(new PosOrder
            {
                OrderNumber = $"A{i:D3}",
                Lines = [new CartLine { Name = "Dish", BasePrice = 5m, Quantity = 1, IsAdHoc = true }],
            });
        }

        _db.BackupTo(_backupPath);

        using var restored = new EposDb(_backupPath);
        Assert.Equal(20, new OrderRepository(restored).GetToday().Count);
    }

    [Fact]
    public void Backing_up_twice_overwrites_rather_than_failing()
    {
        _db.BackupTo(_backupPath);
        var first = new FileInfo(_backupPath).Length;

        new MenuRepository(_db).UpsertCategory(new Category { Id = "c", Name = "Later" });
        _db.BackupTo(_backupPath);

        Assert.True(File.Exists(_backupPath));
        Assert.True(new FileInfo(_backupPath).Length >= first);
    }

    public void Dispose()
    {
        _db.Dispose();
        SqliteConnection.ClearAllPools();
        foreach (var path in new[] { _dbPath, _backupPath })
        {
            foreach (var suffix in new[] { "", "-wal", "-shm" })
            {
                var file = path + suffix;
                if (File.Exists(file)) File.Delete(file);
            }
        }
        GC.SuppressFinalize(this);
    }
}
