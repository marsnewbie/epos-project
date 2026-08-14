using RingOrder.Epos.Domain;
using Microsoft.Data.Sqlite;

namespace RingOrder.Epos.Data;

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
        return string.IsNullOrWhiteSpace(raw)
            ? AppSettings.CreateDefaults()
            : JsonUtil.Deserialize<AppSettings>(raw);
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
        return $"{day}-{seq:D4}";
    }
}

public sealed class MenuRepository
{
    private readonly EposDb _db;

    public MenuRepository(EposDb db) => _db = db;

    // ── Tax classes and price tiers ─────────────────────────────────────────

    public List<TaxClass> GetTaxClasses()
    {
        var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id,name,rate_basis_points FROM tax_classes ORDER BY rate_basis_points DESC,name";
        var list = new List<TaxClass>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Add(new TaxClass { Id = r.GetString(0), Name = r.GetString(1), RateBasisPoints = r.GetInt32(2) });
        return list;
    }

    public List<PriceTier> GetPriceTiers()
    {
        var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id,name,is_default FROM price_tiers ORDER BY is_default DESC,name";
        var list = new List<PriceTier>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Add(new PriceTier { Id = r.GetString(0), Name = r.GetString(1), IsDefault = r.GetInt32(2) == 1 });
        return list;
    }

    public void ReplaceTaxClasses(IEnumerable<TaxClass> classes)
    {
        var conn = _db.Open();
        using var tx = conn.BeginTransaction();
        using (var clear = conn.CreateCommand())
        {
            clear.Transaction = tx;
            clear.CommandText = "DELETE FROM tax_classes";
            clear.ExecuteNonQuery();
        }
        foreach (var c in classes)
        {
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "INSERT INTO tax_classes(id,name,rate_basis_points) VALUES($id,$n,$r)";
            cmd.Parameters.AddWithValue("$id", c.Id);
            cmd.Parameters.AddWithValue("$n", c.Name);
            cmd.Parameters.AddWithValue("$r", c.RateBasisPoints);
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }

    public void ReplacePriceTiers(IEnumerable<PriceTier> tiers)
    {
        var conn = _db.Open();
        using var tx = conn.BeginTransaction();
        using (var clear = conn.CreateCommand())
        {
            clear.Transaction = tx;
            clear.CommandText = "DELETE FROM price_tiers";
            clear.ExecuteNonQuery();
        }
        foreach (var t in tiers)
        {
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "INSERT INTO price_tiers(id,name,is_default) VALUES($id,$n,$d)";
            cmd.Parameters.AddWithValue("$id", t.Id);
            cmd.Parameters.AddWithValue("$n", t.Name);
            cmd.Parameters.AddWithValue("$d", t.IsDefault ? 1 : 0);
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }

    // ── Categories ──────────────────────────────────────────────────────────

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
        cmd.CommandText =
            "SELECT id,name,translation,description,sort_order,is_visible,print_class,tax_class_id FROM categories"
            + (visibleOnly ? " WHERE is_visible=1" : "")
            + " ORDER BY sort_order,name";

        var list = new List<Category>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            list.Add(new Category
            {
                Id = r.GetString(0),
                Name = r.GetString(1),
                Translation = r.IsDBNull(2) ? null : r.GetString(2),
                Description = r.IsDBNull(3) ? null : r.GetString(3),
                SortOrder = r.GetInt32(4),
                IsVisible = r.GetInt32(5) == 1,
                PrintClass = r.GetString(6),
                TaxClassId = r.GetString(7),
            });
        }
        return list;
    }

    public void UpsertCategory(Category c)
    {
        var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO categories(id,name,translation,description,sort_order,is_visible,print_class,tax_class_id)
            VALUES($id,$n,$tr,$d,$s,$v,$pc,$tc)
            ON CONFLICT(id) DO UPDATE SET
              name=excluded.name,
              translation=excluded.translation,
              description=excluded.description,
              sort_order=excluded.sort_order,
              is_visible=excluded.is_visible,
              print_class=excluded.print_class,
              tax_class_id=excluded.tax_class_id
            """;
        cmd.Parameters.AddWithValue("$id", c.Id);
        cmd.Parameters.AddWithValue("$n", c.Name);
        cmd.Parameters.AddWithValue("$tr", (object?)c.Translation ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$d", (object?)c.Description ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$s", c.SortOrder);
        cmd.Parameters.AddWithValue("$v", c.IsVisible ? 1 : 0);
        cmd.Parameters.AddWithValue("$pc", c.PrintClass);
        cmd.Parameters.AddWithValue("$tc", c.TaxClassId);
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

    public int CountItemsInCategory(string categoryId)
    {
        var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM menu_items WHERE category_id=$c";
        cmd.Parameters.AddWithValue("$c", categoryId);
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    public void DeleteCategory(string id)
    {
        var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM categories WHERE id=$id";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    }

    // ── Shared option groups ────────────────────────────────────────────────

    /// <summary>The whole catalogue, keyed by id. Small enough to read per query.</summary>
    public Dictionary<string, OptionGroup> GetOptionGroups()
    {
        var conn = _db.Open();
        var groups = new Dictionary<string, OptionGroup>(StringComparer.Ordinal);

        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText =
                "SELECT id,name,translation,type,required,min_selections,max_selections FROM option_groups ORDER BY name";
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                groups[r.GetString(0)] = new OptionGroup
                {
                    Id = r.GetString(0),
                    Name = r.GetString(1),
                    Translation = r.IsDBNull(2) ? null : r.GetString(2),
                    Type = r.GetString(3) == "multi" ? OptionGroupType.Multi : OptionGroupType.Single,
                    Required = r.GetInt32(4) == 1,
                    MinSelections = r.IsDBNull(5) ? null : r.GetInt32(5),
                    MaxSelections = r.IsDBNull(6) ? null : r.GetInt32(6),
                };
            }
        }

        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText =
                "SELECT id,group_id,label,translation,price_delta_pence,is_default,is_available FROM option_choices ORDER BY group_id,sort_order";
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                if (!groups.TryGetValue(r.GetString(1), out var group)) continue;
                group.Choices.Add(new OptionChoice
                {
                    Id = r.GetString(0),
                    Label = r.GetString(2),
                    OptionTranslation = r.IsDBNull(3) ? null : r.GetString(3),
                    PriceDelta = Money.FromPence(r.GetInt64(4)),
                    IsDefault = r.GetInt32(5) == 1,
                    IsAvailable = r.GetInt32(6) == 1,
                });
            }
        }

        return groups;
    }

    public void UpsertOptionGroup(OptionGroup group)
    {
        var conn = _db.Open();
        using var tx = conn.BeginTransaction();
        WriteOptionGroup(conn, tx, group);
        tx.Commit();
    }

    public void DeleteOptionGroup(string groupId)
    {
        var conn = _db.Open();
        using var tx = conn.BeginTransaction();
        foreach (var sql in new[]
                 {
                     "DELETE FROM menu_item_option_groups WHERE group_id=$g",
                     "DELETE FROM option_choices WHERE group_id=$g",
                     "DELETE FROM option_groups WHERE id=$g",
                 })
        {
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = sql;
            cmd.Parameters.AddWithValue("$g", groupId);
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }

    /// <summary>Dishes that reference a group — shown before editing or deleting it.</summary>
    public List<string> GetItemNamesUsingGroup(string groupId)
    {
        var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT i.name FROM menu_item_option_groups l
            JOIN menu_items i ON i.id = l.item_id
            WHERE l.group_id=$g ORDER BY i.name
            """;
        cmd.Parameters.AddWithValue("$g", groupId);
        var list = new List<string>();
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add(r.GetString(0));
        return list;
    }

    // ── Items ───────────────────────────────────────────────────────────────

    public List<MenuItem> GetItems(string? categoryId = null, bool availableOnly = true)
    {
        var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        var sql = ItemSelect + " WHERE 1=1";
        if (availableOnly) sql += " AND is_available=1";
        if (!string.IsNullOrWhiteSpace(categoryId))
        {
            sql += " AND category_id=$c";
            cmd.Parameters.AddWithValue("$c", categoryId);
        }
        cmd.CommandText = sql + " ORDER BY sort_order,menu_number,name";

        var list = new List<MenuItem>();
        using (var r = cmd.ExecuteReader())
        {
            while (r.Read()) list.Add(ReadItem(r));
        }

        AttachOptionGroups(list);
        return list;
    }

    public MenuItem? GetItem(string id)
    {
        var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = ItemSelect + " WHERE id=$id";
        cmd.Parameters.AddWithValue("$id", id);

        MenuItem? item;
        using (var r = cmd.ExecuteReader())
        {
            item = r.Read() ? ReadItem(r) : null;
        }

        if (item is not null) AttachOptionGroups([item]);
        return item;
    }

    public List<MenuItem> Search(string query)
    {
        var q = query.Trim();
        if (q.Length == 0) return GetItems();
        return GetItems().Where(i =>
                (i.MenuNumber?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false) ||
                i.Name.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                (i.ItemTranslation?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false))
            .ToList();
    }

    public MenuItem? FindByMenuNumber(string menuNumber)
    {
        var q = menuNumber.Trim();
        if (q.Length == 0) return null;
        return GetItems(availableOnly: false)
            .FirstOrDefault(i => string.Equals(i.MenuNumber, q, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Saves a dish. Any resolved groups it carries are written back to the
    /// shared catalogue, which is how the dish editor edits a group — and why
    /// the editor has to tell the user which other dishes that touches.
    /// A group arriving with no choices is left alone rather than emptied.
    /// </summary>
    public void UpsertItem(MenuItem item)
    {
        var conn = _db.Open();
        using var tx = conn.BeginTransaction();
        foreach (var group in item.OptionGroups.Where(g => g.Choices.Count > 0))
            WriteOptionGroup(conn, tx, group);
        WriteItem(conn, tx, item);
        tx.Commit();
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
        cmd.CommandText = "UPDATE menu_items SET base_price_pence=$p WHERE id=$id";
        cmd.Parameters.AddWithValue("$p", Money.ToPence(price));
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    }

    public void DeleteItem(string id)
    {
        var conn = _db.Open();
        using var tx = conn.BeginTransaction();
        foreach (var sql in new[]
                 {
                     "DELETE FROM menu_item_option_groups WHERE item_id=$id",
                     "DELETE FROM menu_item_tier_prices WHERE item_id=$id",
                     "DELETE FROM menu_items WHERE id=$id",
                 })
        {
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = sql;
            cmd.Parameters.AddWithValue("$id", id);
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }

    /// <summary>Wholesale catalogue replacement — used by bundle import.</summary>
    public void ReplaceAll(
        IEnumerable<Category> categories,
        IEnumerable<OptionGroup> optionGroups,
        IEnumerable<MenuItem> items)
    {
        var conn = _db.Open();
        using var tx = conn.BeginTransaction();

        using (var clear = conn.CreateCommand())
        {
            clear.Transaction = tx;
            clear.CommandText = """
                DELETE FROM menu_item_option_groups;
                DELETE FROM menu_item_tier_prices;
                DELETE FROM option_choices;
                DELETE FROM option_groups;
                DELETE FROM menu_items;
                DELETE FROM categories;
                """;
            clear.ExecuteNonQuery();
        }

        foreach (var c in categories)
        {
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO categories(id,name,translation,description,sort_order,is_visible,print_class,tax_class_id)
                VALUES($id,$n,$tr,$d,$s,$v,$pc,$tc)
                """;
            cmd.Parameters.AddWithValue("$id", c.Id);
            cmd.Parameters.AddWithValue("$n", c.Name);
            cmd.Parameters.AddWithValue("$tr", (object?)c.Translation ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$d", (object?)c.Description ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$s", c.SortOrder);
            cmd.Parameters.AddWithValue("$v", c.IsVisible ? 1 : 0);
            cmd.Parameters.AddWithValue("$pc", c.PrintClass);
            cmd.Parameters.AddWithValue("$tc", c.TaxClassId);
            cmd.ExecuteNonQuery();
        }

        foreach (var g in optionGroups) WriteOptionGroup(conn, tx, g);
        foreach (var i in items) WriteItem(conn, tx, i);

        tx.Commit();
    }

    // ── Internals ───────────────────────────────────────────────────────────

    private const string ItemSelect =
        "SELECT id,category_id,menu_number,name,item_translation,description,base_price_pence," +
        "print_class,tax_class_id,is_available,is_bundle,sort_order FROM menu_items";

    private static MenuItem ReadItem(SqliteDataReader r) => new()
    {
        Id = r.GetString(0),
        CategoryId = r.GetString(1),
        MenuNumber = r.IsDBNull(2) ? null : r.GetString(2),
        Name = r.GetString(3),
        ItemTranslation = r.IsDBNull(4) ? null : r.GetString(4),
        Description = r.IsDBNull(5) ? null : r.GetString(5),
        BasePrice = Money.FromPence(r.GetInt64(6)),
        PrintClass = r.IsDBNull(7) ? null : r.GetString(7),
        TaxClassId = r.IsDBNull(8) ? null : r.GetString(8),
        IsAvailable = r.GetInt32(9) == 1,
        IsBundle = r.GetInt32(10) == 1,
        SortOrder = r.GetInt32(11),
    };

    /// <summary>
    /// Resolves each dish's links against the shared catalogue. The group object
    /// is copied per dish so one dish's placement never leaks into another's.
    /// </summary>
    private void AttachOptionGroups(List<MenuItem> items)
    {
        if (items.Count == 0) return;
        var catalogue = GetOptionGroups();
        var byId = items.ToDictionary(i => i.Id, StringComparer.Ordinal);

        var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT item_id,group_id,sort_order,show_when_json FROM menu_item_option_groups ORDER BY item_id,sort_order";
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            if (!byId.TryGetValue(r.GetString(0), out var item)) continue;
            var showWhen = r.IsDBNull(3)
                ? null
                : JsonUtil.Deserialize<OptionShowWhen>(r.GetString(3));

            item.OptionLinks.Add(new MenuItemOptionLink
            {
                GroupId = r.GetString(1),
                SortOrder = r.GetInt32(2),
                ShowWhen = showWhen,
            });

            if (catalogue.TryGetValue(r.GetString(1), out var group))
                item.OptionGroups.Add(group.ForItem(r.GetInt32(2), showWhen));
        }

        foreach (var tierPrices in ReadTierPrices(items.Select(i => i.Id)))
            byId[tierPrices.Key].TierPrices = tierPrices.Value;
    }

    private Dictionary<string, Dictionary<string, decimal>> ReadTierPrices(IEnumerable<string> itemIds)
    {
        var wanted = itemIds.ToHashSet(StringComparer.Ordinal);
        var result = new Dictionary<string, Dictionary<string, decimal>>(StringComparer.Ordinal);
        var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT item_id,tier_id,price_pence FROM menu_item_tier_prices";
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var itemId = r.GetString(0);
            if (!wanted.Contains(itemId)) continue;
            if (!result.TryGetValue(itemId, out var map))
                result[itemId] = map = new Dictionary<string, decimal>(StringComparer.Ordinal);
            map[r.GetString(1)] = Money.FromPence(r.GetInt64(2));
        }
        return result;
    }

    private static void WriteOptionGroup(SqliteConnection conn, SqliteTransaction tx, OptionGroup group)
    {
        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO option_groups(id,name,translation,type,required,min_selections,max_selections)
                VALUES($id,$n,$tr,$t,$req,$min,$max)
                ON CONFLICT(id) DO UPDATE SET
                  name=excluded.name,
                  translation=excluded.translation,
                  type=excluded.type,
                  required=excluded.required,
                  min_selections=excluded.min_selections,
                  max_selections=excluded.max_selections
                """;
            cmd.Parameters.AddWithValue("$id", group.Id);
            cmd.Parameters.AddWithValue("$n", group.Name);
            cmd.Parameters.AddWithValue("$tr", (object?)group.Translation ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$t", group.Type == OptionGroupType.Multi ? "multi" : "single");
            cmd.Parameters.AddWithValue("$req", group.Required ? 1 : 0);
            cmd.Parameters.AddWithValue("$min", (object?)group.MinSelections ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$max", (object?)group.MaxSelections ?? DBNull.Value);
            cmd.ExecuteNonQuery();
        }

        using (var wipe = conn.CreateCommand())
        {
            wipe.Transaction = tx;
            wipe.CommandText = "DELETE FROM option_choices WHERE group_id=$g";
            wipe.Parameters.AddWithValue("$g", group.Id);
            wipe.ExecuteNonQuery();
        }

        for (var i = 0; i < group.Choices.Count; i++)
        {
            var choice = group.Choices[i];
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO option_choices(id,group_id,label,translation,price_delta_pence,is_default,is_available,sort_order)
                VALUES($id,$g,$l,$tr,$pd,$def,$av,$s)
                """;
            cmd.Parameters.AddWithValue("$id", choice.Id);
            cmd.Parameters.AddWithValue("$g", group.Id);
            cmd.Parameters.AddWithValue("$l", choice.Label);
            cmd.Parameters.AddWithValue("$tr", (object?)choice.OptionTranslation ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$pd", Money.ToPence(choice.PriceDelta));
            cmd.Parameters.AddWithValue("$def", choice.IsDefault ? 1 : 0);
            cmd.Parameters.AddWithValue("$av", choice.IsAvailable ? 1 : 0);
            cmd.Parameters.AddWithValue("$s", i);
            cmd.ExecuteNonQuery();
        }
    }

    private static void WriteItem(SqliteConnection conn, SqliteTransaction tx, MenuItem item)
    {
        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO menu_items(id,category_id,menu_number,name,item_translation,description,
                  base_price_pence,print_class,tax_class_id,is_available,is_bundle,sort_order)
                VALUES($id,$c,$mn,$n,$tr,$d,$p,$pc,$tc,$a,$b,$s)
                ON CONFLICT(id) DO UPDATE SET
                  category_id=excluded.category_id,
                  menu_number=excluded.menu_number,
                  name=excluded.name,
                  item_translation=excluded.item_translation,
                  description=excluded.description,
                  base_price_pence=excluded.base_price_pence,
                  print_class=excluded.print_class,
                  tax_class_id=excluded.tax_class_id,
                  is_available=excluded.is_available,
                  is_bundle=excluded.is_bundle,
                  sort_order=excluded.sort_order
                """;
            cmd.Parameters.AddWithValue("$id", item.Id);
            cmd.Parameters.AddWithValue("$c", item.CategoryId);
            cmd.Parameters.AddWithValue("$mn", (object?)item.MenuNumber ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$n", item.Name);
            cmd.Parameters.AddWithValue("$tr", (object?)item.ItemTranslation ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$d", (object?)item.Description ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$p", Money.ToPence(item.BasePrice));
            cmd.Parameters.AddWithValue("$pc", (object?)item.PrintClass ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$tc", (object?)item.TaxClassId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$a", item.IsAvailable ? 1 : 0);
            cmd.Parameters.AddWithValue("$b", item.IsBundle ? 1 : 0);
            cmd.Parameters.AddWithValue("$s", item.SortOrder);
            cmd.ExecuteNonQuery();
        }

        foreach (var sql in new[]
                 {
                     "DELETE FROM menu_item_option_groups WHERE item_id=$id",
                     "DELETE FROM menu_item_tier_prices WHERE item_id=$id",
                 })
        {
            using var wipe = conn.CreateCommand();
            wipe.Transaction = tx;
            wipe.CommandText = sql;
            wipe.Parameters.AddWithValue("$id", item.Id);
            wipe.ExecuteNonQuery();
        }

        // Links come from OptionLinks when set, otherwise from the resolved
        // groups — the menu editor hands back whichever it was working with.
        var links = item.OptionLinks.Count > 0
            ? item.OptionLinks
            : item.OptionGroups.Select(g => new MenuItemOptionLink
            {
                GroupId = g.Id,
                SortOrder = g.SortOrder,
                ShowWhen = g.ShowWhen,
            }).ToList();

        foreach (var link in links)
        {
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO menu_item_option_groups(item_id,group_id,sort_order,show_when_json)
                VALUES($i,$g,$s,$sw)
                ON CONFLICT(item_id,group_id) DO UPDATE SET
                  sort_order=excluded.sort_order,
                  show_when_json=excluded.show_when_json
                """;
            cmd.Parameters.AddWithValue("$i", item.Id);
            cmd.Parameters.AddWithValue("$g", link.GroupId);
            cmd.Parameters.AddWithValue("$s", link.SortOrder);
            cmd.Parameters.AddWithValue("$sw",
                link.ShowWhen is null ? DBNull.Value : JsonUtil.Serialize(link.ShowWhen));
            cmd.ExecuteNonQuery();
        }

        foreach (var (tierId, price) in item.TierPrices)
        {
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO menu_item_tier_prices(item_id,tier_id,price_pence) VALUES($i,$t,$p)
                ON CONFLICT(item_id,tier_id) DO UPDATE SET price_pence=excluded.price_pence
                """;
            cmd.Parameters.AddWithValue("$i", item.Id);
            cmd.Parameters.AddWithValue("$t", tierId);
            cmd.Parameters.AddWithValue("$p", Money.ToPence(price));
            cmd.ExecuteNonQuery();
        }
    }
}
