namespace RingOrder.Epos.Domain;

/// <summary>
/// Everything that makes one till belong to one shop: identity, tax, menu,
/// printers, staff. One signed binary is installed everywhere; this file is the
/// entire difference between two merchants.
/// <para>
/// It is a seed, not a runtime dependency. After import the till owns its data
/// and every field is editable in Settings, so a shop is never blocked waiting
/// for us to send a new file.
/// </para>
/// </summary>
public sealed class ShopBundle
{
    public int SchemaVersion { get; set; } = 1;

    /// <summary>Ours, for support: which build of this shop's setup is installed.</summary>
    public string? ProfileVersion { get; set; }

    public ShopIdentity Shop { get; set; } = new();
    public LocaleSettings Locale { get; set; } = new();
    public TaxSettings Tax { get; set; } = new();
    public List<ServiceTypeDef> ServiceTypes { get; set; } = [];
    public MenuBundle Menu { get; set; } = new();
    public List<QuickNoteDef> QuickNotes { get; set; } = [];
    public DeliveryBundle Delivery { get; set; } = new();
    public PrintingBundle Printing { get; set; } = new();
    public ChannelsBundle Channels { get; set; } = new();
    public List<StaffSeed> Staff { get; set; } = [];
    public ReceiptBundle Receipt { get; set; } = new();
}

public sealed class ShopIdentity
{
    public string Slug { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Address { get; set; }
    public string? Postcode { get; set; }
    public string? Phone { get; set; }
    public string? Email { get; set; }
    public string? VatNumber { get; set; }
    public string? AllergyNotice { get; set; }
}

public sealed class LocaleSettings
{
    public string Currency { get; set; } = "GBP";

    /// <summary>Language of the buttons and menus staff read.</summary>
    public string UiLanguage { get; set; } = "en";

    /// <summary>Second language printed on kitchen tickets.</summary>
    public string KitchenLanguage { get; set; } = "zh";
}

public sealed class TaxSettings
{
    /// <summary>UK retail convention: the shelf price already contains the VAT.</summary>
    public bool PricesIncludeTax { get; set; } = true;

    public List<TaxClassDef> Classes { get; set; } = [];
    public string DefaultClassId { get; set; } = "hot-food";
}

public sealed class TaxClassDef
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public int RateBasisPoints { get; set; }
}

/// <summary>Which service types this shop offers, and how each is priced.</summary>
public sealed class ServiceTypeDef
{
    /// <summary>collection | delivery | eat-in</summary>
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public bool IsDefault { get; set; }
}

public sealed class MenuBundle
{
    public List<CategoryDef> Categories { get; set; } = [];
    public List<OptionGroupDef> OptionGroups { get; set; } = [];
    public List<MenuItemDef> Items { get; set; } = [];
}

public sealed class CategoryDef
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Translation { get; set; }
    public string? Description { get; set; }
    public int SortOrder { get; set; }
    public bool IsVisible { get; set; } = true;
    public string PrintClass { get; set; } = Domain.PrintClass.Kitchen;
    public string TaxClassId { get; set; } = "hot-food";
}

public sealed class OptionGroupDef
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Translation { get; set; }

    /// <summary>single | multi</summary>
    public string Type { get; set; } = "single";
    public bool Required { get; set; }
    public int? MinSelections { get; set; }
    public int? MaxSelections { get; set; }
    public List<OptionChoiceDef> Choices { get; set; } = [];
}

public sealed class OptionChoiceDef
{
    public string Id { get; set; } = "";
    public string Label { get; set; } = "";
    public string? Translation { get; set; }
    public int PriceDeltaPence { get; set; }
    public bool IsDefault { get; set; }
    public bool IsAvailable { get; set; } = true;
}

public sealed class MenuItemDef
{
    public string Id { get; set; } = "";
    public string CategoryId { get; set; } = "";
    public string? MenuNumber { get; set; }
    public string Name { get; set; } = "";
    public string? Translation { get; set; }
    public string? Description { get; set; }
    public int PricePence { get; set; }
    public string? TaxClassId { get; set; }
    public string? PrintClass { get; set; }
    public bool IsAvailable { get; set; } = true;
    public int SortOrder { get; set; }
    public List<MenuItemOptionDef> OptionGroups { get; set; } = [];
}

public sealed class MenuItemOptionDef
{
    public string GroupId { get; set; } = "";
    public int SortOrder { get; set; }
    public ShowWhenDef? ShowWhen { get; set; }
}

public sealed class ShowWhenDef
{
    public string GroupId { get; set; } = "";
    public List<string> ChoiceIds { get; set; } = [];
}

public sealed class DeliveryBundle
{
    public int DefaultFeePence { get; set; }
    public List<DeliveryZoneDef> Zones { get; set; } = [];
}

public sealed class DeliveryZoneDef
{
    /// <summary>Postcode prefix; the longest matching prefix wins.</summary>
    public string Prefix { get; set; } = "";
    public int FeePence { get; set; }
    public int MinimumOrderPence { get; set; }
}

public sealed class PrintingBundle
{
    public List<PrintDeviceDef> Devices { get; set; } = [];
    public List<PrintRouteDef> Routes { get; set; } = [];
}

public sealed class PrintDeviceDef
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";

    /// <summary>windows-queue | tcp | serial</summary>
    public string Transport { get; set; } = "windows-queue";

    /// <summary>Queue name, host:port, or COM port depending on transport.</summary>
    public string? Address { get; set; }

    public int PaperWidthMm { get; set; } = 80;
    public string Encoding { get; set; } = "gbk";
    public bool CjkAsRaster { get; set; } = true;
    public bool HasCashDrawer { get; set; }
}

public sealed class PrintRouteDef
{
    public PrintRouteMatch When { get; set; } = new();
    public string DeviceId { get; set; } = "";
    public int Copies { get; set; } = 1;
    public string Template { get; set; } = "kitchen";

    /// <summary>Where the ticket goes when the target is unreachable.</summary>
    public string? FallbackDeviceId { get; set; }
}

public sealed class PrintRouteMatch
{
    public string? PrintClass { get; set; }
    public string? ServiceType { get; set; }
    public string? Channel { get; set; }

    /// <summary>kitchen | receipt | report</summary>
    public string? Document { get; set; }
}

public sealed class ChannelsBundle
{
    public ChannelToggle Counter { get; set; } = new() { Enabled = true };
    public ChannelToggle Phone { get; set; } = new() { Enabled = true };
    public WebChannelDef Web { get; set; } = new();
    public PlatformChannelDef Platform { get; set; } = new();
}

public sealed class ChannelToggle
{
    public bool Enabled { get; set; }
}

public sealed class WebChannelDef
{
    public bool Enabled { get; set; }

    /// <summary>The shop's own ordering site. Endpoints derive from it.</summary>
    public string? BaseUrl { get; set; }

    public int PollSeconds { get; set; } = 30;
    public bool AutoPrint { get; set; } = true;

    /// <summary>
    /// Where the print credentials come from. "local" means a sibling
    /// secrets.json that never enters version control.
    /// </summary>
    public string? CredentialsRef { get; set; }
}

public sealed class PlatformChannelDef
{
    public bool Enabled { get; set; }

    /// <summary>Marketplace names offered on the ticket, e.g. Uber Eats.</summary>
    public List<string> Providers { get; set; } = [];
}

public sealed class StaffSeed
{
    public string Name { get; set; } = "";

    /// <summary>cashier | supervisor | manager</summary>
    public string Role { get; set; } = "cashier";

    public string Pin { get; set; } = "";
    public bool MustChangePin { get; set; } = true;
}

public sealed class ReceiptBundle
{
    public List<string> HeaderLines { get; set; } = [];
    public List<string> FooterLines { get; set; } = [];
}
