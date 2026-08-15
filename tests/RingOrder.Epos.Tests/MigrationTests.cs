using Microsoft.Data.Sqlite;
using RingOrder.Epos.Data;
using RingOrder.Epos.Domain;
using Xunit;

namespace RingOrder.Epos.Tests;

/// <summary>
/// Upgrading a till that is already in service. Every one of these is a shop
/// that would otherwise lose its evening: the point of the migration runner is
/// that the shop keeps its data and its trading history across a release.
/// </summary>
public class MigrationTests : IDisposable
{
    private readonly string _dbPath =
        Path.Combine(Path.GetTempPath(), $"ringorder-migrate-{Guid.NewGuid():N}.sqlite");

    [Fact]
    public void Fresh_database_reaches_the_latest_version()
    {
        using var db = new EposDb(_dbPath);
        var applied = db.Migrate();

        Assert.Equal(SchemaMigrations.All.Select(m => m.Version), applied);

        using var conn = db.Open();
        Assert.Equal(SchemaMigrations.LatestVersion, SchemaMigrations.CurrentVersion(conn));
    }

    [Fact]
    public void A_migration_backup_lands_beside_its_own_database()
    {
        // It used to land in the machine-wide shop folder whatever database was
        // being migrated, so a test run — or a copy opened during a support
        // session — dropped a tiny database into the live shop's backups. The
        // restore instruction is "take the newest pre-migration file".
        var scratch = Path.Combine(Path.GetTempPath(), $"ringorder-bk-{Guid.NewGuid():N}");
        Directory.CreateDirectory(scratch);
        var dbPath = Path.Combine(scratch, "data.sqlite");

        try
        {
            SeedVersionOne(dbPath);

            // An upgrade from v1: there is something to lose, so a copy is taken.
            using (var db = new EposDb(dbPath)) db.Migrate();

            var beside = Path.Combine(scratch, "backups");
            Assert.True(Directory.Exists(beside), "backups belong beside the database being migrated");
            Assert.NotEmpty(Directory.GetFiles(beside, "pre-migration-*.sqlite"));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(scratch, recursive: true); } catch { /* the OS will get it */ }
        }
    }

    /// <summary>A database as the first release left it, with nothing newer stamped on.</summary>
    private static void SeedVersionOne(string dbPath)
    {
        using var conn = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
        }.ToString());
        conn.Open();
        SchemaMigrations.CurrentVersion(conn);

        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = SchemaMigrations.All.Single(m => m.Version == 1).Sql;
            cmd.ExecuteNonQuery();
        }

        using (var stamp = conn.CreateCommand())
        {
            stamp.CommandText =
                "INSERT INTO schema_migrations(version,name,applied_at) VALUES(1,'initial',$a)";
            stamp.Parameters.AddWithValue("$a", DateTimeOffset.Now.ToString("o"));
            stamp.ExecuteNonQuery();
        }

        conn.Close();
        SqliteConnection.ClearAllPools();
    }

    [Fact]
    public void Migrating_again_does_nothing()
    {
        using var db = new EposDb(_dbPath);
        db.Migrate();
        Assert.Empty(db.Migrate());
    }

    [Fact]
    public void An_older_install_keeps_its_data_across_an_upgrade()
    {
        // Stand up version 1 exactly as a shop running the first release has it.
        using (var old = new SqliteConnection(new SqliteConnectionStringBuilder
               {
                   DataSource = _dbPath,
                   Mode = SqliteOpenMode.ReadWriteCreate,
               }.ToString()))
        {
            old.Open();
            SchemaMigrations.CurrentVersion(old);

            var first = SchemaMigrations.All.Single(m => m.Version == 1);
            using (var cmd = old.CreateCommand())
            {
                cmd.CommandText = first.Sql;
                cmd.ExecuteNonQuery();
            }
            using (var stamp = old.CreateCommand())
            {
                stamp.CommandText =
                    "INSERT INTO schema_migrations(version,name,applied_at) VALUES(1,'initial',$a)";
                stamp.Parameters.AddWithValue("$a", DateTimeOffset.Now.ToString("o"));
                stamp.ExecuteNonQuery();
            }
        }
        SqliteConnection.ClearAllPools();

        // A day's trading, written the way the old release wrote it — raw v1
        // columns. Using today's repository here would test nothing, because it
        // already knows about columns that version never had.
        using (var old = new SqliteConnection(new SqliteConnectionStringBuilder
               {
                   DataSource = _dbPath,
                   Mode = SqliteOpenMode.ReadWrite,
               }.ToString()))
        {
            old.Open();
            using var cmd = old.CreateCommand();
            cmd.CommandText = """
                INSERT INTO categories(id,name,sort_order,is_visible,print_class,tax_class_id)
                VALUES('cat','Chicken',1,1,'kitchen','hot-food');

                INSERT INTO menu_items(id,category_id,menu_number,name,base_price_pence,is_available,is_bundle,sort_order)
                VALUES('dish','cat','88','Kung po chicken',620,1,0,0);

                INSERT INTO orders(id,order_number,service_type,channel,customer_waiting,status,
                  subtotal_pence,delivery_fee_pence,discount_total_pence,below_minimum_pence,total_pence,
                  kitchen_printed,front_printed,online_acked,created_at,updated_at)
                VALUES('ord','0001','Collection','Counter',0,'Paid',1240,0,0,0,1240,1,1,0,
                  '2026-08-14T19:00:00+00:00','2026-08-14T19:00:00+00:00');

                INSERT INTO order_lines(id,order_id,line_number,item_id,name,quantity,
                  base_price_pence,line_total_pence,is_ad_hoc,kitchen_sent,selections_json)
                VALUES('line','ord',0,'dish','Kung po chicken',2,620,1240,0,1,'[]');

                INSERT INTO payments(id,order_id,tender_type,amount_pence,at)
                VALUES('pay','ord','Cash',1240,'2026-08-14T19:01:00+00:00');
                """;
            cmd.ExecuteNonQuery();
        }
        SqliteConnection.ClearAllPools();

        // The upgrade.
        using (var db = new EposDb(_dbPath))
        {
            var applied = db.Migrate();
            Assert.Contains(2, applied);

            var order = new OrderRepository(db).GetById("ord")!;

            Assert.Equal("0001", order.OrderNumber);
            Assert.Single(order.Lines);
            Assert.Equal(12.40m, order.Total);
            Assert.Single(order.Tenders);
            Assert.Equal(1, new MenuRepository(db).CountItems());
            Assert.Null(order.DiscountReason);   // new column, empty on old rows
        }
    }

    [Fact]
    public void Versions_are_unique_and_contiguous_from_one()
    {
        // An out-of-order or duplicated version means two shops on the same
        // release can end up with different schemas.
        var versions = SchemaMigrations.All.Select(m => m.Version).ToList();
        Assert.Equal(versions.Distinct().Count(), versions.Count);
        Assert.Equal(Enumerable.Range(1, versions.Count), versions.OrderBy(v => v));
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            var path = _dbPath + suffix;
            if (File.Exists(path)) File.Delete(path);
        }
        GC.SuppressFinalize(this);
    }
}
