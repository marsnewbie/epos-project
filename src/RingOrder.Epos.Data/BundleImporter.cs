using System.Text.Json;
using RingOrder.Epos.Domain;

namespace RingOrder.Epos.Data;

/// <summary>What an import did, so it can be shown, logged, and checked.</summary>
public sealed record ImportReport(
    string ShopName,
    string? ProfileVersion,
    int Categories,
    int OptionGroups,
    int Items,
    int QuickNotes,
    int Staff,
    IReadOnlyList<string> Warnings)
{
    public bool HasWarnings => Warnings.Count > 0;

    public string Summary =>
        $"{ShopName}: {Categories} categories, {Items} dishes, {OptionGroups} option groups, "
        + $"{QuickNotes} quick notes, {Staff} staff"
        + (HasWarnings ? $" ({Warnings.Count} warnings)" : "");
}

/// <summary>
/// Turns a shop bundle into a working till. Provisioning is import + physical
/// setup; nothing about a shop is compiled in.
/// </summary>
public sealed class BundleImporter
{
    private readonly MenuRepository _menu;
    private readonly SettingsRepository _settings;
    private readonly StaffRepository _staff;
    private readonly PrintDeviceRepository _printers;
    private readonly DeliveryZoneRepository? _zones;

    public BundleImporter(
        MenuRepository menu,
        SettingsRepository settings,
        StaffRepository staff,
        PrintDeviceRepository printers,
        DeliveryZoneRepository? zones = null)
    {
        _menu = menu;
        _settings = settings;
        _staff = staff;
        _printers = printers;
        _zones = zones;
    }

    public static ShopBundle Read(string path)
    {
        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<ShopBundle>(json, JsonUtil.Options)
               ?? throw new InvalidDataException($"{path} is not a shop bundle");
    }

    /// <summary>
    /// Credentials live beside the bundle in a file that never enters version
    /// control. Missing is normal — a shop with no website has none.
    /// </summary>
    public static ShopSecrets? ReadSecrets(string bundlePath)
    {
        var path = Path.Combine(Path.GetDirectoryName(bundlePath) ?? ".", "secrets.json");
        if (!File.Exists(path)) return null;
        return JsonSerializer.Deserialize<ShopSecrets>(File.ReadAllText(path), JsonUtil.Options);
    }

    public ImportReport ImportFromFile(string path)
        => Import(Read(path), ReadSecrets(path));

    /// <summary>
    /// Replaces the catalogue and shop configuration. Trading data — orders,
    /// shifts, customers — is never touched: a menu update mid-week must not
    /// erase the week.
    /// </summary>
    public ImportReport Import(ShopBundle bundle, ShopSecrets? secrets = null)
    {
        var warnings = new List<string>();

        var taxClasses = bundle.Tax.Classes
            .Select(t => new TaxClass { Id = t.Id, Name = t.Name, RateBasisPoints = t.RateBasisPoints })
            .ToList();
        if (taxClasses.Count == 0)
            warnings.Add("bundle defines no tax classes; VAT will read as zero on every line");
        _menu.ReplaceTaxClasses(taxClasses);

        var taxClassIds = taxClasses.Select(t => t.Id).ToHashSet(StringComparer.Ordinal);

        var categories = bundle.Menu.Categories.Select(c => new Category
        {
            Id = c.Id,
            Name = c.Name,
            Translation = c.Translation,
            Description = c.Description,
            SortOrder = c.SortOrder,
            IsVisible = c.IsVisible,
            PrintClass = c.PrintClass,
            TaxClassId = c.TaxClassId,
        }).ToList();

        var groups = bundle.Menu.OptionGroups.Select(g => new OptionGroup
        {
            Id = g.Id,
            Name = g.Name,
            Translation = g.Translation,
            Type = string.Equals(g.Type, "multi", StringComparison.OrdinalIgnoreCase)
                ? OptionGroupType.Multi
                : OptionGroupType.Single,
            Required = g.Required,
            MinSelections = g.MinSelections,
            MaxSelections = g.MaxSelections,
            Choices = g.Choices.Select(c => new OptionChoice
            {
                Id = c.Id,
                Label = c.Label,
                OptionTranslation = c.Translation,
                PriceDelta = Money.FromPence(c.PriceDeltaPence),
                IsDefault = c.IsDefault,
                IsAvailable = c.IsAvailable,
            }).ToList(),
        }).ToList();

        var groupIds = groups.Select(g => g.Id).ToHashSet(StringComparer.Ordinal);
        var categoryIds = categories.Select(c => c.Id).ToHashSet(StringComparer.Ordinal);

        foreach (var category in categories.Where(c => !taxClassIds.Contains(c.TaxClassId)))
            warnings.Add($"category {category.Name}: tax class '{category.TaxClassId}' is not in the bundle");

        var items = new List<MenuItem>();
        foreach (var def in bundle.Menu.Items)
        {
            if (!categoryIds.Contains(def.CategoryId))
                warnings.Add($"{def.Name}: category '{def.CategoryId}' is not in the bundle");

            if (def.TaxClassId is { } taxClassId && !taxClassIds.Contains(taxClassId))
                warnings.Add($"{def.Name}: tax class '{taxClassId}' is not in the bundle");

            var links = new List<MenuItemOptionLink>();
            foreach (var link in def.OptionGroups)
            {
                if (!groupIds.Contains(link.GroupId))
                {
                    warnings.Add($"{def.Name}: option group '{link.GroupId}' is not in the bundle");
                    continue;
                }

                OptionShowWhen? showWhen = null;
                if (link.ShowWhen is { } sw)
                {
                    var source = groups.FirstOrDefault(g => g.Id == sw.GroupId);
                    var missing = sw.ChoiceIds
                        .Where(id => source?.Choices.All(c => c.Id != id) ?? true)
                        .ToList();

                    if (source is null || missing.Count > 0)
                        warnings.Add(
                            $"{def.Name}: '{link.GroupId}' shows when {sw.GroupId} is chosen, but that "
                            + (source is null ? "group is missing" : $"choice is missing ({string.Join(", ", missing)})"));
                    else
                        showWhen = new OptionShowWhen { GroupId = sw.GroupId, ChoiceIds = sw.ChoiceIds };
                }

                links.Add(new MenuItemOptionLink
                {
                    GroupId = link.GroupId,
                    SortOrder = link.SortOrder,
                    ShowWhen = showWhen,
                });
            }

            items.Add(new MenuItem
            {
                Id = def.Id,
                CategoryId = def.CategoryId,
                MenuNumber = def.MenuNumber,
                Name = def.Name,
                ItemTranslation = def.Translation,
                Description = def.Description,
                BasePrice = Money.FromPence(def.PricePence),
                PrintClass = def.PrintClass,
                TaxClassId = def.TaxClassId,
                IsAvailable = def.IsAvailable,
                SortOrder = def.SortOrder,
                OptionLinks = links,
            });
        }

        var duplicateNumbers = items
            .Where(i => !string.IsNullOrWhiteSpace(i.MenuNumber))
            .GroupBy(i => i.MenuNumber!, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .ToList();
        foreach (var duplicate in duplicateNumbers)
            warnings.Add($"menu number {duplicate.Key} is used by {duplicate.Count()} dishes");

        _menu.ReplaceAll(categories, groups, items);

        var settings = _settings.Load();
        ApplyToSettings(settings, bundle, secrets);
        _settings.Save(settings);

        ImportPrinters(bundle, warnings);
        ImportDeliveryZones(bundle, warnings);
        var staffCount = SeedStaff(bundle, warnings);

        return new ImportReport(
            bundle.Shop.Name,
            bundle.ProfileVersion,
            categories.Count,
            groups.Count,
            items.Count,
            bundle.QuickNotes.Count,
            staffCount,
            warnings);
    }

    /// <summary>
    /// Printers and routing. Replaced wholesale on import, like the menu: a
    /// bundle describes a shop's hardware as we set it up, and a shop that has
    /// since moved a printer re-runs setup rather than merging by hand.
    /// </summary>
    private void ImportPrinters(ShopBundle bundle, List<string> warnings)
    {
        if (bundle.Printing.Devices.Count == 0)
        {
            warnings.Add("bundle defines no printers; add them in Settings before the shop opens");
            return;
        }

        var devices = bundle.Printing.Devices.Select(d => new PrintDevice
        {
            Id = string.IsNullOrWhiteSpace(d.Id) ? Guid.NewGuid().ToString("N") : d.Id,
            Name = d.Name,
            Transport = Enum.TryParse<PrintTransport>(d.Transport.Replace("-", ""), ignoreCase: true, out var t)
                ? t
                : PrintTransport.WindowsQueue,
            Address = d.Address ?? "",
            PaperWidthMm = d.PaperWidthMm,
            Encoding = d.Encoding,
            CjkAsRaster = d.CjkAsRaster,
            HasCashDrawer = d.HasCashDrawer,
        }).ToList();

        var deviceIds = devices.Select(d => d.Id).ToHashSet(StringComparer.Ordinal);

        var routes = new List<PrintRoute>();
        var order = 0;
        foreach (var def in bundle.Printing.Routes)
        {
            if (!deviceIds.Contains(def.DeviceId))
            {
                warnings.Add($"print rule targets '{def.DeviceId}', which is not a printer in this bundle");
                continue;
            }

            if (def.FallbackDeviceId is { } fallback && !deviceIds.Contains(fallback))
            {
                warnings.Add($"print rule falls back to '{fallback}', which is not a printer in this bundle");
                def.FallbackDeviceId = null;
            }

            routes.Add(new PrintRoute
            {
                SortOrder = order++,
                Document = ResolveDocument(def.When.Document, def.Template),
                PrintClass = def.When.PrintClass,
                ServiceType = Enum.TryParse<ServiceType>(def.When.ServiceType, ignoreCase: true, out var st)
                    ? st
                    : null,
                Channel = Enum.TryParse<OrderChannel>(def.When.Channel, ignoreCase: true, out var ch)
                    ? ch
                    : null,
                DeviceId = def.DeviceId,
                Copies = Math.Clamp(def.Copies, 1, 9),
                FallbackDeviceId = def.FallbackDeviceId,
            });
        }

        if (routes.Count == 0)
        {
            routes = PrintRouting.DefaultRoutes(devices).ToList();
            warnings.Add("bundle defines no print rules; kitchen and receipt defaults were applied");
        }

        _printers.ReplaceAll(devices, routes);
    }

    /// <summary>
    /// Delivery areas from the bundle. The list has been in the format since the
    /// schema rebuild and was read by nothing — every shop was charged the single
    /// flat default however many zones its bundle declared.
    /// </summary>
    private void ImportDeliveryZones(ShopBundle bundle, List<string> warnings)
    {
        if (_zones is null) return;

        var zones = new List<DeliveryZone>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var def in bundle.Delivery.Zones)
        {
            var rule = PostcodeRules.Parse(def.Prefix);
            if (rule is null)
            {
                warnings.Add($"delivery zone \"{def.Prefix}\" is not a postcode prefix and was skipped");
                continue;
            }

            var prefix = rule.Canonical;
            if (!seen.Add(prefix))
            {
                warnings.Add($"delivery zone {prefix} appears more than once; the first was kept");
                continue;
            }

            zones.Add(new DeliveryZone
            {
                Prefix = prefix,
                Fee = Money.FromPence(def.FeePence),
                MinimumOrder = Money.FromPence(def.MinimumOrderPence),
                SortOrder = zones.Count,
            });
        }

        if (zones.Count > 0) _zones.Replace(zones);
    }

    private static PrintDocument ResolveDocument(string? document, string? template)
    {
        var value = document ?? template ?? "kitchen";
        return value.ToLowerInvariant() switch
        {
            "receipt" or "front" => PrintDocument.Receipt,
            "report" => PrintDocument.Report,
            _ => PrintDocument.Kitchen,
        };
    }

    private static void ApplyToSettings(AppSettings settings, ShopBundle bundle, ShopSecrets? secrets)
    {
        settings.Edition = ShopEdition.Normalise(bundle.Edition);
        settings.ShopName = bundle.Shop.Name;
        settings.ShopAddress = bundle.Shop.Address ?? "";
        settings.ShopPostcode = bundle.Shop.Postcode ?? "";
        settings.ShopPhone = bundle.Shop.Phone ?? "";
        settings.UiLanguage = bundle.Locale.UiLanguage;
        settings.VatNumber = bundle.Shop.VatNumber ?? "";
        settings.PricesIncludeTax = bundle.Tax.PricesIncludeTax;
        settings.DefaultTaxClassId = bundle.Tax.DefaultClassId;
        settings.ReceiptFooterLines = bundle.Receipt.FooterLines;

        if (bundle.QuickNotes.Count > 0)
            settings.QuickNotes = bundle.QuickNotes;

        settings.DefaultDeliveryFee = Money.FromPence(bundle.Delivery.DefaultFeePence);

        var front = bundle.Printing.Devices.FirstOrDefault(d => d.HasCashDrawer)
                    ?? bundle.Printing.Devices.FirstOrDefault();
        var kitchen = bundle.Printing.Devices.FirstOrDefault(d => !d.HasCashDrawer) ?? front;
        if (front is not null)
        {
            settings.FrontPrinterName = front.Address ?? settings.FrontPrinterName;
            settings.PrintEncoding = front.Encoding;
            settings.PrintChineseAsRaster = front.CjkAsRaster;
        }
        if (kitchen is not null)
            settings.KitchenPrinterName = kitchen.Address ?? settings.KitchenPrinterName;

        var web = bundle.Channels.Web;
        settings.OnlinePollIntervalSeconds = Math.Clamp(web.PollSeconds, 5, 300);
        settings.AutoKitchenPrintOnline = web.AutoPrint;

        var baseUrl = secrets?.Web?.BaseUrl ?? web.BaseUrl;
        if (!string.IsNullOrWhiteSpace(baseUrl))
            settings.ApplyOnlineBaseUrl(baseUrl);

        if (secrets?.Web is { } credentials)
        {
            settings.OnlineResId = credentials.ResId ?? "";
            settings.OnlineUsername = credentials.Username ?? "";
            settings.OnlinePassword = credentials.Password ?? "";
        }

        if (secrets?.AddressLookup is { } lookup)
        {
            if (!string.IsNullOrWhiteSpace(lookup.Provider))
                settings.AddressLookupProvider = lookup.Provider.Trim().ToLowerInvariant();
            if (!string.IsNullOrWhiteSpace(lookup.ApiKey))
                settings.AddressLookupApiKey = lookup.ApiKey.Trim();
        }

        // The slug is how this shop names itself to our own systems, so it comes
        // from the bundle's identity and not from anything a merchant types.
        if (!string.IsNullOrWhiteSpace(bundle.Shop.Slug))
            settings.ShopSlug = bundle.Shop.Slug.Trim();

        if (secrets?.Cloud is { } cloud)
        {
            if (!string.IsNullOrWhiteSpace(cloud.BaseUrl))
                settings.CloudBaseUrl = cloud.BaseUrl.Trim().TrimEnd('/');

            // Only set when supplied. A re-import to update a menu must not wipe
            // an activation that has already happened.
            if (!string.IsNullOrWhiteSpace(cloud.ActivationCode))
                settings.CloudActivationCode = cloud.ActivationCode.Trim();
        }
    }

    private int SeedStaff(ShopBundle bundle, List<string> warnings)
    {
        if (_staff.CountActive() > 0)
            return 0;   // a working till's staff list is theirs, not the bundle's

        var seeded = 0;
        foreach (var seed in bundle.Staff)
        {
            if (string.IsNullOrWhiteSpace(seed.Pin))
            {
                warnings.Add($"staff '{seed.Name}' has no PIN and was skipped");
                continue;
            }

            var (hash, salt) = PinHasher.Hash(seed.Pin);
            _staff.Upsert(new StaffMember
            {
                Name = seed.Name,
                Role = Enum.TryParse<StaffRole>(seed.Role, ignoreCase: true, out var role)
                    ? role
                    : StaffRole.Cashier,
                PinHash = hash,
                PinSalt = salt,
                MustChangePin = seed.MustChangePin,
            });
            seeded++;
        }

        return seeded;
    }
}

public sealed class ShopSecrets
{
    public string? ShopSlug { get; set; }
    public WebSecrets? Web { get; set; }

    /// <summary>
    /// Optional, and normally absent.
    /// <para>
    /// A till is activated by typing a short code in Settings → Cloud, which is
    /// what a person actually does on an install. This block exists only so that
    /// an automated provisioning run can pre-fill the same two fields, and a
    /// merchant is never asked to edit it.
    /// </para>
    /// </summary>
    public CloudSecrets? Cloud { get; set; }

    /// <summary>
    /// The postcode-lookup account. Here rather than in the bundle because the
    /// key is billable: a bundle gets forwarded, attached to emails and copied
    /// onto USB sticks, and a leaked key spends someone else's money.
    /// </summary>
    public AddressLookupSecrets? AddressLookup { get; set; }
}

public sealed class CloudSecrets
{
    /// <summary>A service other than the shipped one. Blank in every real shop.</summary>
    public string? BaseUrl { get; set; }

    /// <summary>
    /// Pre-fills the box in Settings → Cloud. Spent once for a device secret and
    /// then cleared, so a merchant who loses the file loses nothing.
    /// </summary>
    public string? ActivationCode { get; set; }
}

public sealed class AddressLookupSecrets
{
    /// <summary>none | postcodesio | getaddress | idealpostcodes</summary>
    public string? Provider { get; set; }

    public string? ApiKey { get; set; }
}

public sealed class WebSecrets
{
    public string? BaseUrl { get; set; }
    public string? ResId { get; set; }
    public string? Username { get; set; }
    public string? Password { get; set; }
}
