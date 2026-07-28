using MagicWok.Epos.Data;

var path = Path.Combine(Path.GetTempPath(), "magicwok-epos-smoke.sqlite");
if (File.Exists(path)) File.Delete(path);
using var db = new EposDb(path);
db.EnsureCreated();
var settings = new SettingsRepository(db);
var menu = new MenuRepository(db);
var seeder = new MenuSeeder(menu, settings);
var (cats, items) = seeder.ImportEmbedded();
var s = settings.Load();
Console.WriteLine($"OK cats={cats} items={items} shop={s.ShopName} encoding={s.PrintEncoding}");
