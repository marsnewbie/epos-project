namespace RingOrder.Epos.Domain;

public sealed class AppSettings
{
    // Shop identity arrives from the shop bundle at provisioning time. Blank
    // defaults are deliberate: a till that ships with someone else's name on the
    // receipt is worse than one that visibly needs setting up.
    public string ShopName { get; set; } = "";
    public string ShopAddress { get; set; } = "";
    public string ShopPostcode { get; set; } = "";
    public string ShopPhone { get; set; } = "";

    /// <summary>
    /// Blank means the shop is not VAT registered, which most small takeaways
    /// are not. Nothing about VAT is printed while it is blank: a receipt
    /// claiming VAT from a business that cannot charge it is worse than one
    /// that says nothing.
    /// </summary>
    public string VatNumber { get; set; } = "";

    /// <summary>UK retail convention: the shelf price already contains the VAT.</summary>
    public bool PricesIncludeTax { get; set; } = true;

    /// <summary>Band used for delivery charges and for a line with none of its own.</summary>
    public string DefaultTaxClassId { get; set; } = "hot-food";

    /// <summary>Printed under the totals; the shop's own wording.</summary>
    public List<string> ReceiptFooterLines { get; set; } = [];
    public string UiLanguage { get; set; } = "en"; // en | zh

    public string KitchenPrinterName { get; set; } = "GlPrinter80";
    public string FrontPrinterName { get; set; } = "GlPrinter80";
    public string PrintEncoding { get; set; } = "gbk"; // gbk | gb18030 | utf8
    /// <summary>Render CJK kitchen lines as ESC/POS raster (reliable on Windows → GlPrinter80).</summary>
    public bool PrintChineseAsRaster { get; set; } = true;
    public bool OpenDrawerOnCash { get; set; } = true;
    /// <summary>
    /// When paying: if kitchen not yet printed, print kitchen automatically.
    /// Send button always prints kitchen regardless of this flag.
    /// </summary>
    public bool SendKitchenOnPay { get; set; } = true;
    /// <summary>Legacy alias — maps to SendKitchenOnPay for older settings JSON.</summary>
    public bool SendKitchenOnSend
    {
        get => SendKitchenOnPay;
        set => SendKitchenOnPay = value;
    }
    public bool PrintFrontOnPay { get; set; } = true;
    public bool AutoKitchenPrintOnline { get; set; } = true;
    public bool PrintVoidKitchenTicket { get; set; } = true;

    /// <summary>Editable shop website base; endpoints derive unless overridden.</summary>
    public string OnlineBaseUrl { get; set; } = "";
    /// <summary>JSON next-order API (preferred for EPOS).</summary>
    public string OnlineOrderServerUrl { get; set; } = "";
    public string OnlineCallbackUrl { get; set; } = "";
    public string OnlinePrintedUrl { get; set; } = "";
    public string OnlineResId { get; set; } = "";
    public string OnlineUsername { get; set; } = "";
    public string OnlinePassword { get; set; } = "";
    public int OnlinePollIntervalSeconds { get; set; } = 4;
    public bool OnlinePollingEnabled { get; set; }

    public bool CallerIdEnabled { get; set; }
    public string CallerIdMode { get; set; } = "simulate"; // simulate | serial
    public string CallerIdComPort { get; set; } = "COM3";
    public int CallerIdBaud { get; set; } = 9600;

    /// <summary>
    /// Which postcode lookup to ask, if any. Off by default: the feature costs
    /// money at every provider that can name a house number, and a till that
    /// silently starts spending a merchant's credits is not a till they trust.
    /// </summary>
    public string AddressLookupProvider { get; set; } = AddressProviderNames.None;

    /// <summary>
    /// Kept in the till's own database, never in the shop bundle — the bundle is
    /// a file we email around. Provisioning seeds this from the sibling
    /// secrets.json instead.
    /// </summary>
    public string AddressLookupApiKey { get; set; } = "";

    /// <summary>
    /// Answers are stored and reused. Switchable only because a merchant
    /// debugging a wrong address needs a way to force a fresh call.
    /// </summary>
    public bool AddressLookupCacheEnabled { get; set; } = true;

    /// <summary>
    /// Months of inactivity after which a customer record is no longer needed.
    /// <para>
    /// Zero means "no automatic removal", and that is the shipped default on
    /// purpose. UK GDPR says personal data is not kept longer than the purpose
    /// requires, but the shop is the data controller and that judgement is
    /// theirs — a till that silently deleted a merchant's phone book on first
    /// upgrade would be indefensible. Settings shows them the count and the
    /// obligation, and the decision stays a deliberate click.
    /// </para>
    /// </summary>
    public int CustomerRetentionMonths { get; set; }

    /// <summary>
    /// Whether the dormant sweep runs by itself once a period is set. Off until
    /// the merchant has seen what a sweep would remove.
    /// </summary>
    public bool CustomerRetentionAutomatic { get; set; }

    /// <summary>Charged when no zone matches, and when a shop has set no zones at all.</summary>
    public decimal DefaultDeliveryFee { get; set; }

    /// <summary>Postcode rules, road-distance bands, or rules first then distance.</summary>
    public DeliveryMode DeliveryMode { get; set; } = DeliveryMode.Postcode;

    /// <summary>
    /// Flat amount added when the basket is under the matched minimum — the
    /// shop's price for carrying a small order, not the shortfall. Zero means the
    /// till warns and charges nothing.
    /// <para>
    /// Flat rather than "top up to the minimum" because that is what the
    /// RingOrder website charges, and a shop running both must not quote two
    /// different numbers for the same basket.
    /// </para>
    /// </summary>
    public decimal BelowMinimumSurcharge { get; set; }

    /// <summary>Beyond this, the shop does not deliver at all.</summary>
    public decimal MaxDeliveryMiles { get; set; } = 5m;
    public string? LastMenuImportAt { get; set; }
    public int NextOrderSequence { get; set; } = 1;

    public List<QuickNoteDef> QuickNotes { get; set; } = QuickKitchenNotes.CreateDefaultList();

    public void ApplyOnlineBaseUrl(string baseUrl)
    {
        OnlineBaseUrl = baseUrl.Trim().TrimEnd('/');
        OnlineOrderServerUrl = $"{OnlineBaseUrl}/api/print/epos/next";
        OnlineCallbackUrl = $"{OnlineBaseUrl}/api/print/epos/ack";
        OnlinePrintedUrl = $"{OnlineBaseUrl}/api/print/epos/ack";
    }

    public static AppSettings CreateDefaults() => new();
}
