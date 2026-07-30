using MagicWok.Epos.Domain;
using Microsoft.Data.Sqlite;

namespace MagicWok.Epos.Data;

public sealed class SettingsRepository
{
    private readonly EposDb _db;
    private const string Key = "app";

    public SettingsRepository(EposDb db) => _db = db;

    public AppSettings Load()
    {
        var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT value FROM settings WHERE key=$k";
        cmd.Parameters.AddWithValue("$k", Key);
        var raw = cmd.ExecuteScalar() as string;
        if (string.IsNullOrWhiteSpace(raw))
            return AppSettings.CreateDefaults();
        var settings = JsonUtil.Deserialize<AppSettings>(raw);
        MigrateOnlineEndpoints(settings);
        return settings;
    }

    /// <summary>Prefer JSON EPOS adapters when settings still point at Goodcom getorder.</summary>
    private static void MigrateOnlineEndpoints(AppSettings s)
    {
        if (string.IsNullOrWhiteSpace(s.OnlineOrderServerUrl)) return;
        if (!s.OnlineOrderServerUrl.Contains("/api/print/gcanyorder/getorder", StringComparison.OrdinalIgnoreCase))
            return;
        var baseUrl = s.OnlineBaseUrl?.Trim().TrimEnd('/');
        if (string.IsNullOrWhiteSpace(baseUrl) &&
            Uri.TryCreate(s.OnlineOrderServerUrl, UriKind.Absolute, out var u))
            baseUrl = $"{u.Scheme}://{u.Authority}";
        if (string.IsNullOrWhiteSpace(baseUrl)) return;
        s.OnlineBaseUrl = baseUrl;
        s.ApplyOnlineBaseUrl(baseUrl);
    }

    public void Save(AppSettings settings)
    {
        var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO settings(key, value) VALUES($k, $v)
            ON CONFLICT(key) DO UPDATE SET value=excluded.value
            """;
        cmd.Parameters.AddWithValue("$k", Key);
        cmd.Parameters.AddWithValue("$v", JsonUtil.Serialize(settings));
        cmd.ExecuteNonQuery();
    }

    public string AllocateOrderNumber()
    {
        var settings = Load();
        var day = DateTime.Now.ToString("yyMMdd");
        var seq = settings.NextOrderSequence++;
        Save(settings);
        return $"L{day}-{seq:D4}";
    }
}

public sealed class MenuRepository
{
    private readonly EposDb _db;

    public MenuRepository(EposDb db) => _db = db;

    public int CountItems()
    {
        var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM menu_items";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    public List<Category> GetCategories(bool visibleOnly = true)
    {
        var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = visibleOnly
            ? "SELECT id,name,description,sort_order,is_visible FROM categories WHERE is_visible=1 ORDER BY sort_order,name"
            : "SELECT id,name,description,sort_order,is_visible FROM categories ORDER BY sort_order,name";
        var list = new List<Category>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            list.Add(new Category
            {
                Id = r.GetString(0),
                Name = r.GetString(1),
                Description = r.IsDBNull(2) ? null : r.GetString(2),
                SortOrder = r.GetInt32(3),
                IsVisible = r.GetInt32(4) == 1,
            });
        }
        return list;
    }

    public void UpsertCategory(Category c)
    {
        var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO categories(id,name,description,sort_order,is_visible)
            VALUES($id,$n,$d,$s,$v)
            ON CONFLICT(id) DO UPDATE SET
              name=excluded.name,
              description=excluded.description,
              sort_order=excluded.sort_order,
              is_visible=excluded.is_visible
            """;
        cmd.Parameters.AddWithValue("$id", c.Id);
        cmd.Parameters.AddWithValue("$n", c.Name);
        cmd.Parameters.AddWithValue("$d", (object?)c.Description ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$s", c.SortOrder);
        cmd.Parameters.AddWithValue("$v", c.IsVisible ? 1 : 0);
        cmd.ExecuteNonQuery();
    }

    public void SetCategoryVisible(string id, bool visible)
    {
        var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE categories SET is_visible=$v WHERE id=$id";
        cmd.Parameters.AddWithValue("$v", visible ? 1 : 0);
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    }

    public List<MenuItem> GetItems(string? categoryId = null, bool availableOnly = true)
    {
        var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        var sql = "SELECT id,category_id,menu_number,name,item_translation,description,base_price,is_available,is_bundle,option_groups_json,sort_order FROM menu_items WHERE 1=1";
        if (availableOnly) sql += " AND is_available=1";
        if (!string.IsNullOrWhiteSpace(categoryId))
        {
            sql += " AND category_id=$c";
            cmd.Parameters.AddWithValue("$c", categoryId);
        }
        sql += " ORDER BY sort_order,menu_number,name";
        cmd.CommandText = sql;

        var list = new List<MenuItem>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Add(ReadItem(r));
        return list;
    }

    public MenuItem? GetItem(string id)
    {
        var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id,category_id,menu_number,name,item_translation,description,base_price,is_available,is_bundle,option_groups_json,sort_order FROM menu_items WHERE id=$id";
        cmd.Parameters.AddWithValue("$id", id);
        using var r = cmd.ExecuteReader();
        return r.Read() ? ReadItem(r) : null;
    }

    public List<MenuItem> Search(string query)
    {
        var q = query.Trim();
        if (q.Length == 0) return GetItems();
        var all = GetItems();
        return all.Where(i =>
                (i.MenuNumber?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false) ||
                i.Name.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                (i.ItemTranslation?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false))
            .ToList();
    }

    public void ReplaceAll(IEnumerable<Category> categories, IEnumerable<MenuItem> items)
    {
        var conn = _db.Open();
        using var tx = conn.BeginTransaction();
        using (var clear = conn.CreateCommand())
        {
            clear.Transaction = tx;
            clear.CommandText = "DELETE FROM menu_items; DELETE FROM categories;";
            clear.ExecuteNonQuery();
        }

        foreach (var c in categories)
        {
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO categories(id,name,description,sort_order,is_visible)
                VALUES($id,$n,$d,$s,$v)
                """;
            cmd.Parameters.AddWithValue("$id", c.Id);
            cmd.Parameters.AddWithValue("$n", c.Name);
            cmd.Parameters.AddWithValue("$d", (object?)c.Description ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$s", c.SortOrder);
            cmd.Parameters.AddWithValue("$v", c.IsVisible ? 1 : 0);
            cmd.ExecuteNonQuery();
        }

        foreach (var i in items)
        {
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO menu_items(id,category_id,menu_number,name,item_translation,description,base_price,is_available,is_bundle,option_groups_json,sort_order)
                VALUES($id,$c,$mn,$n,$tr,$d,$p,$a,$b,$og,$s)
                """;
            cmd.Parameters.AddWithValue("$id", i.Id);
            cmd.Parameters.AddWithValue("$c", i.CategoryId);
            cmd.Parameters.AddWithValue("$mn", (object?)i.MenuNumber ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$n", i.Name);
            cmd.Parameters.AddWithValue("$tr", (object?)i.ItemTranslation ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$d", (object?)i.Description ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$p", (double)i.BasePrice);
            cmd.Parameters.AddWithValue("$a", i.IsAvailable ? 1 : 0);
            cmd.Parameters.AddWithValue("$b", i.IsBundle ? 1 : 0);
            cmd.Parameters.AddWithValue("$og", JsonUtil.Serialize(i.OptionGroups));
            cmd.Parameters.AddWithValue("$s", i.SortOrder);
            cmd.ExecuteNonQuery();
        }

        tx.Commit();
    }

    public void UpsertItem(MenuItem i)
    {
        var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO menu_items(id,category_id,menu_number,name,item_translation,description,base_price,is_available,is_bundle,option_groups_json,sort_order)
            VALUES($id,$c,$mn,$n,$tr,$d,$p,$a,$b,$og,$s)
            ON CONFLICT(id) DO UPDATE SET
              category_id=excluded.category_id,
              menu_number=excluded.menu_number,
              name=excluded.name,
              item_translation=excluded.item_translation,
              description=excluded.description,
              base_price=excluded.base_price,
              is_available=excluded.is_available,
              is_bundle=excluded.is_bundle,
              option_groups_json=excluded.option_groups_json,
              sort_order=excluded.sort_order
            """;
        cmd.Parameters.AddWithValue("$id", i.Id);
        cmd.Parameters.AddWithValue("$c", i.CategoryId);
        cmd.Parameters.AddWithValue("$mn", (object?)i.MenuNumber ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$n", i.Name);
        cmd.Parameters.AddWithValue("$tr", (object?)i.ItemTranslation ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$d", (object?)i.Description ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$p", (double)i.BasePrice);
        cmd.Parameters.AddWithValue("$a", i.IsAvailable ? 1 : 0);
        cmd.Parameters.AddWithValue("$b", i.IsBundle ? 1 : 0);
        cmd.Parameters.AddWithValue("$og", JsonUtil.Serialize(i.OptionGroups));
        cmd.Parameters.AddWithValue("$s", i.SortOrder);
        cmd.ExecuteNonQuery();
    }

    public void SetItemAvailable(string id, bool available)
    {
        var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE menu_items SET is_available=$a WHERE id=$id";
        cmd.Parameters.AddWithValue("$a", available ? 1 : 0);
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    }

    public void UpdateItemPrice(string id, decimal price)
    {
        var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE menu_items SET base_price=$p WHERE id=$id";
        cmd.Parameters.AddWithValue("$p", (double)price);
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    }

    public void DeleteItem(string id)
    {
        var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM menu_items WHERE id=$id";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    }

    public void DeleteCategory(string id)
    {
        var conn = _db.Open();
        using var tx = conn.BeginTransaction();
        using (var move = conn.CreateCommand())
        {
            move.Transaction = tx;
            // Block delete if items remain — caller should check CountItemsInCategory
            move.CommandText = "DELETE FROM categories WHERE id=$id";
            move.Parameters.AddWithValue("$id", id);
            move.ExecuteNonQuery();
        }
        tx.Commit();
    }

    public int CountItemsInCategory(string categoryId)
    {
        var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM menu_items WHERE category_id=$c";
        cmd.Parameters.AddWithValue("$c", categoryId);
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    public MenuItem? FindByMenuNumber(string menuNumber)
    {
        var q = menuNumber.Trim();
        if (q.Length == 0) return null;
        return GetItems(availableOnly: false)
            .FirstOrDefault(i => string.Equals(i.MenuNumber, q, StringComparison.OrdinalIgnoreCase));
    }

    private static MenuItem ReadItem(SqliteDataReader r) => new()
    {
        Id = r.GetString(0),
        CategoryId = r.GetString(1),
        MenuNumber = r.IsDBNull(2) ? null : r.GetString(2),
        Name = r.GetString(3),
        ItemTranslation = r.IsDBNull(4) ? null : r.GetString(4),
        Description = r.IsDBNull(5) ? null : r.GetString(5),
        BasePrice = Convert.ToDecimal(r.GetDouble(6)),
        IsAvailable = r.GetInt32(7) == 1,
        IsBundle = r.GetInt32(8) == 1,
        OptionGroups = JsonUtil.Deserialize<List<OptionGroup>>(r.GetString(9)) ?? [],
        SortOrder = r.GetInt32(10),
    };
}
