namespace MagicWok.Epos.Domain;

public sealed class AppSettings
{
    public string ShopName { get; set; } = "Magic Wok Chinese";
    public string ShopAddress { get; set; } = "106 Warren Farm Road, Birmingham";
    public string ShopPostcode { get; set; } = "B44 0QN";
    public string ShopPhone { get; set; } = "0121 296 6775";
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
    public string OnlineBaseUrl { get; set; } = "https://magicwoksite.vercel.app";
    /// <summary>JSON next-order API (preferred for EPOS).</summary>
    public string OnlineOrderServerUrl { get; set; } = "https://magicwoksite.vercel.app/api/print/epos/next";
    public string OnlineCallbackUrl { get; set; } = "https://magicwoksite.vercel.app/api/print/epos/ack";
    public string OnlinePrintedUrl { get; set; } = "https://magicwoksite.vercel.app/api/print/epos/ack";
    public string OnlineResId { get; set; } = "";
    public string OnlineUsername { get; set; } = "";
    public string OnlinePassword { get; set; } = "";
    public int OnlinePollIntervalSeconds { get; set; } = 4;
    public bool OnlinePollingEnabled { get; set; }

    public bool CallerIdEnabled { get; set; }
    public string CallerIdMode { get; set; } = "simulate"; // simulate | serial
    public string CallerIdComPort { get; set; } = "COM3";
    public int CallerIdBaud { get; set; } = 9600;

    public decimal DefaultDeliveryFee { get; set; }
    public string? LastMenuImportAt { get; set; }
    public int NextOrderSequence { get; set; } = 1;

    /// <summary>Manager PIN for void / drawer / sensitive settings. Default 1234 — change in shop.</summary>
    public string ManagerPin { get; set; } = "1234";
    public string? CashierPin { get; set; }

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
