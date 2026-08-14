using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using RingOrder.Epos.Domain;

namespace RingOrder.Epos.Hardware;

/// <summary>ESC/POS for 80mm GlPrinter80 — full paper width, no £, CJK via raster when needed.</summary>
public static class EscPos
{
    public const int PaperDots = 576; // 80mm @ 203dpi usable width
    public const int ColsFontA = 48;  // Font A 12-dot → 48 chars on 576 dots

    public static readonly byte[] Init = [0x1B, 0x40];
    public static readonly byte[] FontA = [0x1B, 0x4D, 0x00];
    public static readonly byte[] ChineseModeOn = [0x1C, 0x26];
    public static readonly byte[] AlignLeft = [0x1B, 0x61, 0x00];
    public static readonly byte[] AlignCenter = [0x1B, 0x61, 0x01];
    public static readonly byte[] BoldOn = [0x1B, 0x45, 0x01];
    public static readonly byte[] BoldOff = [0x1B, 0x45, 0x00];
    public static readonly byte[] SizeNormal = [0x1D, 0x21, 0x00];
    public static readonly byte[] SizeTall = [0x1D, 0x21, 0x01];
    public static readonly byte[] SizeLarge = [0x1D, 0x21, 0x11];
    public static readonly byte[] Cut = [0x1D, 0x56, 0x00];
    public static readonly byte[] Feed3 = [0x1B, 0x64, 0x03];
    public static readonly byte[] OpenDrawer = [0x1B, 0x70, 0x00, 0x19, 0xFA];

    public static string Money(decimal amount) => amount.ToString("0.00");

    public static Encoding ResolveEncoding(string? name)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return (name ?? "gbk").ToLowerInvariant() switch
        {
            "utf8" or "utf-8" => new UTF8Encoding(false),
            "gb18030" => Encoding.GetEncoding("GB18030"),
            _ => Encoding.GetEncoding(936),
        };
    }

    public static bool ContainsCjk(string? text)
    {
        if (string.IsNullOrEmpty(text)) return false;
        foreach (var ch in text)
        {
            if (ch >= 0x3400 && ch <= 0x9FFF) return true;
            if (ch >= 0xF900 && ch <= 0xFAFF) return true;
        }
        return false;
    }
}

public sealed class EscPosTicketBuilder
{
    private readonly MemoryStream _ms = new();
    private readonly Encoding _encoding;
    private readonly int _cols;
    private readonly bool _rasterCjk;

    public EscPosTicketBuilder(string? encodingName = null, bool rasterCjk = true, int columns = EscPos.ColsFontA)
    {
        _encoding = EscPos.ResolveEncoding(encodingName);
        _cols = columns;
        _rasterCjk = rasterCjk;
        _ms.Write(EscPos.Init);
        _ms.Write(EscPos.FontA);
        _ms.Write(EscPos.ChineseModeOn);
        _ms.Write(EscPos.AlignLeft);
        _ms.Write(EscPos.SizeNormal);
        _ms.Write(EscPos.BoldOff);
    }

    public EscPosTicketBuilder Center() { _ms.Write(EscPos.AlignCenter); return this; }
    public EscPosTicketBuilder Left() { _ms.Write(EscPos.AlignLeft); return this; }
    public EscPosTicketBuilder Bold(bool on) { _ms.Write(on ? EscPos.BoldOn : EscPos.BoldOff); return this; }
    public EscPosTicketBuilder Normal() { _ms.Write(EscPos.SizeNormal); return this; }
    public EscPosTicketBuilder Tall() { _ms.Write(EscPos.SizeTall); return this; }
    public EscPosTicketBuilder Large() { _ms.Write(EscPos.SizeLarge); return this; }

    public EscPosTicketBuilder RawText(string text)
    {
        text = StripCurrency(text);
        if (string.IsNullOrEmpty(text)) return this;
        _ms.Write(_encoding.GetBytes(text));
        return this;
    }

    public EscPosTicketBuilder Nl()
    {
        _ms.WriteByte(0x0A);
        return this;
    }

    /// <summary>ASCII / Latin line (safe for code page).</summary>
    public EscPosTicketBuilder Line(string text = "")
    {
        RawText(text);
        return Nl();
    }

    /// <summary>
    /// Kitchen emphasis line. CJK → raster bitmap (reliable on Windows RAW → GlPrinter80).
    /// Pure ASCII can use ESC/POS large text.
    /// </summary>
    public EscPosTicketBuilder KitchenLine(string text, bool large = true)
    {
        text = StripCurrency(text ?? "");
        if (text.Length == 0) return Nl();

        if (_rasterCjk && EscPos.ContainsCjk(text))
        {
            _ms.Write(EscPos.AlignLeft);
            _ms.Write(EscPos.SizeNormal);
            WriteRasterLine(text, large ? 40 : 28);
            return this;
        }

        if (large) Large();
        else Tall();
        Bold(true);
        Line(text);
        Bold(false);
        Normal();
        return this;
    }

    public EscPosTicketBuilder ItemEnglishAndPrice(string left, string price)
    {
        left = StripCurrency(left);
        price ??= "";
        if (AsciiLen(left) + 1 + price.Length <= _cols)
            return ColumnsAscii(left, price);

        // Full name — never truncate with "Chek."
        foreach (var chunk in WrapAscii(left, _cols))
            Line(chunk);
        return ColumnsAscii("", price);
    }

    public EscPosTicketBuilder ColumnsAscii(string left, string right, int? cols = null)
    {
        left = StripCurrency(left ?? "");
        right = StripCurrency(right ?? "");
        var width = cols ?? _cols;
        if (AsciiLen(left) + AsciiLen(right) >= width)
        {
            if (left.Length > 0) Line(left);
            return Line(right.PadLeft(width));
        }
        var pad = width - AsciiLen(left) - AsciiLen(right);
        if (pad < 1) pad = 1;
        return Line(left + new string(' ', pad) + right);
    }

    public EscPosTicketBuilder Separator(char c = '-')
    {
        return Normal().Line(new string(c, _cols));
    }

    public EscPosTicketBuilder FeedAndCut()
    {
        Normal();
        _ms.Write(EscPos.Feed3);
        _ms.Write(EscPos.Cut);
        return this;
    }

    public byte[] Build() => _ms.ToArray();

    private void WriteRasterLine(string text, float emSize)
    {
        if (!OperatingSystem.IsWindows())
        {
            // Fallback: GBK text
            Large().Bold(true).Line(text).Bold(false).Normal();
            return;
        }

        try
        {
            using var bmp = RenderTextBitmap(text, emSize, EscPos.PaperDots);
            var mono = ToMonoRaster(bmp);
            // GS v 0 — print raster bit image
            var xL = (byte)(mono.WidthBytes & 0xFF);
            var xH = (byte)((mono.WidthBytes >> 8) & 0xFF);
            var yL = (byte)(mono.Height & 0xFF);
            var yH = (byte)((mono.Height >> 8) & 0xFF);
            _ms.Write([0x1D, 0x76, 0x30, 0x00, xL, xH, yL, yH]);
            _ms.Write(mono.Data);
            Nl();
        }
        catch
        {
            Large().Bold(true).Line(text).Bold(false).Normal();
        }
    }

    [SupportedOSPlatform("windows")]
    private static Bitmap RenderTextBitmap(string text, float emSize, int maxWidth)
    {
        using var measureBmp = new Bitmap(1, 1);
        using var mg = Graphics.FromImage(measureBmp);
        using var font = new Font("Microsoft YaHei", emSize, FontStyle.Bold, GraphicsUnit.Pixel);
        var size = mg.MeasureString(text, font, maxWidth);
        var w = Math.Max(8, Math.Min(maxWidth, (int)Math.Ceiling(size.Width) + 4));
        var h = Math.Max(8, (int)Math.Ceiling(size.Height) + 4);
        var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.Clear(Color.White);
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
        g.DrawString(text, font, Brushes.Black, new RectangleF(0, 0, w, h));
        return bmp;
    }

    private sealed record MonoRaster(int WidthBytes, int Height, byte[] Data);

    [SupportedOSPlatform("windows")]
    private static MonoRaster ToMonoRaster(Bitmap bmp)
    {
        // Width must be multiple of 8 for ESC/POS
        var width = (bmp.Width + 7) / 8 * 8;
        var height = bmp.Height;
        var widthBytes = width / 8;
        var data = new byte[widthBytes * height];

        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < bmp.Width; x++)
            {
                var c = bmp.GetPixel(x, y);
                var lum = (c.R * 30 + c.G * 59 + c.B * 11) / 100;
                if (lum >= 160) continue; // white-ish → no dot
                var byteIndex = y * widthBytes + (x / 8);
                data[byteIndex] |= (byte)(0x80 >> (x % 8));
            }
        }

        return new MonoRaster(widthBytes, height, data);
    }

    private static string StripCurrency(string text) =>
        text.Replace("\u00A3", "")
            .Replace("\u20AC", "")
            .Replace("\u00A5", "")
            .Replace("£", "")
            .Replace("€", "")
            .Replace("¥", "");

    private static int AsciiLen(string s) => s.Length;

    private static IEnumerable<string> WrapAscii(string text, int width)
    {
        if (string.IsNullOrEmpty(text)) yield break;
        var s = text.Trim();
        while (s.Length > width)
        {
            var cut = s.LastIndexOf(' ', width);
            if (cut < width / 3) cut = width;
            yield return s[..cut].TrimEnd();
            s = s[cut..].TrimStart();
        }
        if (s.Length > 0) yield return s;
    }
}

public static class TicketRenderer
{
    public static byte[] RenderKitchen(PosOrder order, AppSettings settings, bool unsentOnly = false, bool isVoid = false)
    {
        var enc = string.IsNullOrWhiteSpace(settings.PrintEncoding) ? "gbk" : settings.PrintEncoding;
        var raster = settings.PrintChineseAsRaster;
        var b = new EscPosTicketBuilder(enc, rasterCjk: raster);
        var shop = string.IsNullOrWhiteSpace(settings.ShopName) ? "KITCHEN" : settings.ShopName;
        var lines = unsentOnly ? order.Lines.Where(l => !l.KitchenSent).ToList() : order.Lines.ToList();
        if (lines.Count == 0) lines = order.Lines.ToList();

        b.Center().Normal().Bold(true).Line(shop.ToUpperInvariant()).Bold(false).Left();
        if (isVoid)
            b.KitchenLine("*** VOID ***", large: true);
        else if (unsentOnly && order.Lines.Any(l => l.KitchenSent))
            b.KitchenLine("*** ADDITIONS ***", large: true);
        b.KitchenLine(TicketHeadline(order), large: true);
        if (ChannelBanner(order) is { } banner)
            b.KitchenLine(banner, large: true);
        b.Normal().Line($"Order No:{order.OrderNumber}");
        b.Line(order.CreatedAt.ToLocalTime().ToString("HH:mm dd-MM-yy"));

        if (!string.IsNullOrWhiteSpace(order.TableNumber))
            b.KitchenLine($"TABLE {order.TableNumber}", large: true);

        var when = FirstNonEmpty(order.RequestedFor, order.FulfilmentLabel);
        if (!string.IsNullOrWhiteSpace(when))
        {
            b.Normal().Line("Requested for:");
            b.KitchenLine(when!, large: true);
        }

        b.Separator('-');

        foreach (var line in lines)
        {
            b.Normal().ItemEnglishAndPrice($"{line.Quantity} x {line.Name}", EscPos.Money(line.LineTotal));

            if (!string.IsNullOrWhiteSpace(line.ItemTranslation))
                b.KitchenLine($"{line.Quantity} {line.ItemTranslation}", large: true);

            foreach (var sel in line.Selections)
            {
                foreach (var c in sel.Choices)
                {
                    b.Normal().Line($"  + {c.Label}");
                    if (!string.IsNullOrWhiteSpace(c.OptionTranslation))
                        b.KitchenLine($"  {c.OptionTranslation}", large: false);
                }
            }

            if (!string.IsNullOrWhiteSpace(line.Notes))
                b.Normal().Line($"  NOTE: {line.Notes}");

            b.Nl();
        }

        b.Separator('-');
        if (!unsentOnly)
        {
            if (order.DiscountTotal > 0)
                b.Normal().ColumnsAscii("Discount", "-" + EscPos.Money(order.DiscountTotal));
            if (order.DeliveryFee > 0)
                b.Normal().ColumnsAscii("Delivery", EscPos.Money(order.DeliveryFee));

            b.Large().Bold(true).ColumnsAscii("Total", EscPos.Money(order.Total), cols: 24).Bold(false).Normal();
            if (order.AmountPaid > 0)
            {
                b.Normal().ColumnsAscii("Paid", EscPos.Money(order.AmountPaid));
                if (!order.IsFullyPaid)
                    b.Normal().ColumnsAscii("DUE", EscPos.Money(order.BalanceDue));
            }
            b.Separator('-');
        }

        var pay = ResolvePayment(order);
        if (!string.IsNullOrWhiteSpace(pay))
            b.KitchenLine($"Payment:{pay}", large: true);

        b.Normal().Line("Cus Info:");
        if (!string.IsNullOrWhiteSpace(order.CustomerName))
            b.Line(order.CustomerName!);
        if (!string.IsNullOrWhiteSpace(order.CustomerPhone))
            b.KitchenLine(order.CustomerPhone!, large: true);

        if (order.ServiceType == ServiceType.Delivery)
        {
            var addr = NormalizeAddress(order.DeliveryAddress);
            foreach (var addrLine in WrapLines(addr, EscPos.ColsFontA))
                b.Normal().Line(addrLine);
            if (!string.IsNullOrWhiteSpace(order.DeliveryPostcode))
                b.Line(order.DeliveryPostcode!);
        }
        else
        {
            b.KitchenLine(TicketHeadline(order), large: true);
        }

        if (!string.IsNullOrWhiteSpace(order.Notes))
        {
            b.Separator('-');
            b.Bold(true).Line("Comments:").Bold(false);
            foreach (var n in WrapLines(order.Notes, EscPos.ColsFontA))
                b.Line(n);
        }

        if (!string.IsNullOrWhiteSpace(order.TicketFooter))
        {
            b.Separator('-');
            b.Center();
            foreach (var f in WrapLines(order.TicketFooter, EscPos.ColsFontA))
                b.Normal().Line(f!);
            b.Left();
        }

        b.FeedAndCut();
        return b.Build();
    }

    public static byte[] RenderFront(PosOrder order, AppSettings settings)
    {
        var enc = string.IsNullOrWhiteSpace(settings.PrintEncoding) ? "gbk" : settings.PrintEncoding;
        var b = new EscPosTicketBuilder(enc, rasterCjk: settings.PrintChineseAsRaster);
        var interim = !order.IsFullyPaid;
        b.Center().KitchenLine(settings.ShopName, large: true);
        b.Normal().Line(settings.ShopAddress);
        b.Line($"{settings.ShopPostcode}  {settings.ShopPhone}");
        b.Line(interim ? "*** INTERIM / NOT PAID IN FULL ***" : "RECEIPT").Left().Separator('=');
        b.Line($"Order {order.OrderNumber}");
        b.Line(order.CreatedAt.ToLocalTime().ToString("dd/MM/yyyy HH:mm"));
        b.Line(TicketHeadline(order));
        b.Separator();
        foreach (var line in order.Lines)
        {
            b.ItemEnglishAndPrice($"{line.Quantity} x {line.Name}", EscPos.Money(line.LineTotal));
            foreach (var sel in line.Selections)
                foreach (var c in sel.Choices)
                    b.Line($"  + {c.Label}");
        }
        b.Separator();
        b.ColumnsAscii("Subtotal", EscPos.Money(order.Subtotal));
        if (order.DeliveryFee > 0) b.ColumnsAscii("Delivery", EscPos.Money(order.DeliveryFee));
        if (order.DiscountTotal > 0) b.ColumnsAscii("Discount", "-" + EscPos.Money(order.DiscountTotal));
        b.Large().Bold(true).ColumnsAscii("TOTAL", EscPos.Money(order.Total), cols: 24).Bold(false).Normal();
        b.Separator('-');
        if (order.Tenders.Count == 0)
        {
            b.ColumnsAscii("Paid", EscPos.Money(0));
            b.Large().Bold(true).ColumnsAscii("BALANCE DUE", EscPos.Money(order.BalanceDue), cols: 24).Bold(false).Normal();
        }
        else
        {
            foreach (var t in order.Tenders)
            {
                var label = t.Type switch
                {
                    TenderType.Cash => "Cash",
                    TenderType.CardManual => "Card",
                    TenderType.CardIntegrated => "Card",
                    TenderType.PrepaidOnline => "Paid online",
                    TenderType.Voucher => "Voucher",
                    _ => t.Type.ToString(),
                };
                b.ColumnsAscii(label, EscPos.Money(t.Amount));
                if (t.CashReceived is > 0)
                    b.ColumnsAscii("  Tendered", EscPos.Money(t.CashReceived.Value));
                if (t.ChangeGiven is > 0)
                    b.ColumnsAscii("  Change", EscPos.Money(t.ChangeGiven.Value));
            }
            b.ColumnsAscii("Total paid", EscPos.Money(order.AmountPaid));
            if (interim)
                b.Large().Bold(true).ColumnsAscii("BALANCE DUE", EscPos.Money(order.BalanceDue), cols: 24).Bold(false).Normal();
            else
                b.Large().Bold(true).ColumnsAscii("PAID IN FULL", EscPos.Money(order.AmountPaid), cols: 24).Bold(false).Normal();
        }
        b.Center().Line(interim ? "Not a final receipt" : "Thank you").FeedAndCut();
        return b.Build();
    }

    public static byte[] RenderTestPage(string printerName, AppSettings settings)
    {
        var enc = string.IsNullOrWhiteSpace(settings.PrintEncoding) ? "gbk" : settings.PrintEncoding;
        var b = new EscPosTicketBuilder(enc, rasterCjk: settings.PrintChineseAsRaster);
        b.Center().KitchenLine(string.IsNullOrWhiteSpace(settings.ShopName) ? "RingOrder EPOS" : settings.ShopName, large: true);
        b.Normal().Line("Printer test").Left().Separator();
        b.Line($"Printer: {printerName}");
        b.Line($"Encoding: {enc}  cols={EscPos.ColsFontA}");
        b.Line($"CJK raster: {settings.PrintChineseAsRaster}");
        b.Line(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
        b.Separator();
        b.ItemEnglishAndPrice("1 x Sample: Optional Checkbox Extras", "2.00");
        b.KitchenLine("1 测试中文菜名", large: true);
        b.Normal().Line("  + Extra Rice");
        b.KitchenLine("  白饭", large: false);
        b.Separator();
        b.Large().ColumnsAscii("Total", "9.50", cols: 24).Normal();
        b.KitchenLine("Payment:CASH", large: true);
        b.Line("Cus Info:");
        b.KitchenLine("07700 900000", large: true);
        b.Line("12 Sample Street");
        b.Line("AB1 2CD");
        b.Separator();
        b.Center().Line("Printer test complete");
        b.FeedAndCut();
        return b.Build();
    }

    public static string NormalizeAddress(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";
        var parts = raw.Split(',').Select(p => p.Trim()).Where(p => p.Length > 0);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var outParts = new List<string>();
        foreach (var p in parts)
        {
            if (seen.Add(p)) outParts.Add(p);
        }
        return string.Join(", ", outParts);
    }

    /// <summary>
    /// The one line the kitchen reads first: what has to happen to this food.
    /// A collection order for someone standing at the counter says so, because
    /// that is the difference between cooking it now and cooking it next.
    /// </summary>
    public static string TicketHeadline(PosOrder order) => order.ServiceType switch
    {
        ServiceType.Delivery => "DELIVERY",
        ServiceType.EatIn => string.IsNullOrWhiteSpace(order.TableNumber)
            ? "EAT-IN"
            : $"TABLE {order.TableNumber}",
        _ => order.CustomerWaiting ? "COLLECTION - WAITING" : "COLLECTION",
    };

    /// <summary>
    /// Where it came from, when that is not the counter. Marketplace orders in
    /// particular have to be obvious: they are already paid and already timed.
    /// </summary>
    public static string? ChannelBanner(PosOrder order) => order.Channel switch
    {
        OrderChannel.Phone => "PHONE ORDER",
        OrderChannel.Web => "WEB ORDER",
        OrderChannel.Platform => string.IsNullOrWhiteSpace(order.PlatformName)
            ? "PLATFORM ORDER"
            : order.PlatformName!.ToUpperInvariant(),
        _ => null,
    };

    private static string? ResolvePayment(PosOrder order)
    {
        // Prefer live balance over stale PaymentLabel when partially paid
        if (order.HasPayments && !order.IsFullyPaid)
            return $"PART PAID DUE {EscPos.Money(order.BalanceDue)}";

        if (order.IsFullyPaid)
        {
            if (!string.IsNullOrWhiteSpace(order.PaymentLabel) &&
                !order.PaymentLabel.Contains("PART", StringComparison.OrdinalIgnoreCase) &&
                !order.PaymentLabel.Contains("DUE", StringComparison.OrdinalIgnoreCase))
                return order.PaymentLabel;

            if (order.Tenders.Count > 1)
                return "SPLIT";
            if (order.Tenders.Count > 0)
            {
                return order.Tenders[0].Type switch
                {
                    TenderType.Cash => "CASH",
                    TenderType.CardManual => "CARD",
                    TenderType.CardIntegrated => "CARD",
                    TenderType.PrepaidOnline => order.Tenders[0].Reference ?? "PAID ONLINE",
                    _ => order.Tenders[0].Type.ToString(),
                };
            }
            return order.PaymentLabel;
        }

        // Unpaid kitchen ticket — do not imply paid
        if (!string.IsNullOrWhiteSpace(order.PaymentLabel) &&
            order.PaymentLabel.Contains("PART", StringComparison.OrdinalIgnoreCase))
            return order.PaymentLabel;

        return order.Channel is OrderChannel.Web or OrderChannel.Platform ? "Order Not Paid" : "UNPAID";
    }

    private static string? FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));

    private static IEnumerable<string> WrapLines(string? text, int width)
    {
        if (string.IsNullOrWhiteSpace(text)) yield break;
        var s = text.Trim();
        while (s.Length > width)
        {
            var cut = s.LastIndexOf(' ', width);
            if (cut < width / 2) cut = width;
            yield return s[..cut].TrimEnd();
            s = s[cut..].TrimStart();
        }
        if (s.Length > 0) yield return s;
    }
}

public static class RawPrinter
{
    /// <summary>
    /// Whether Windows can open this queue right now. Cheap enough for a status
    /// light, and it catches the everyday failure: a printer renamed, unplugged,
    /// or never installed on this machine. It does not promise paper.
    /// </summary>
    public static bool CanOpen(string? printerName)
    {
        if (string.IsNullOrWhiteSpace(printerName) || !OperatingSystem.IsWindows())
            return false;

        if (!OpenPrinter(printerName.Normalize(), out var handle, IntPtr.Zero))
            return false;

        ClosePrinter(handle);
        return true;
    }

    public static void SendBytes(string printerName, byte[] data)
    {
        if (string.IsNullOrWhiteSpace(printerName))
            throw new InvalidOperationException("Printer name is empty.");
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Raw printing requires Windows.");

        if (!OpenPrinter(printerName.Normalize(), out var hPrinter, IntPtr.Zero))
            throw new InvalidOperationException($"Cannot open printer '{printerName}'. Check Windows queue name.");

        try
        {
            var di = new DOCINFOA
            {
                pDocName = "RingOrder.Epos",
                pDataType = "RAW",
            };

            if (!StartDocPrinter(hPrinter, 1, di))
                throw new InvalidOperationException($"StartDocPrinter failed for '{printerName}'.");

            try
            {
                if (!StartPagePrinter(hPrinter))
                    throw new InvalidOperationException("StartPagePrinter failed.");

                try
                {
                    var pinned = GCHandle.Alloc(data, GCHandleType.Pinned);
                    try
                    {
                        if (!WritePrinter(hPrinter, pinned.AddrOfPinnedObject(), data.Length, out _))
                            throw new InvalidOperationException("WritePrinter failed.");
                    }
                    finally
                    {
                        pinned.Free();
                    }
                }
                finally
                {
                    EndPagePrinter(hPrinter);
                }
            }
            finally
            {
                EndDocPrinter(hPrinter);
            }
        }
        finally
        {
            ClosePrinter(hPrinter);
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
    private class DOCINFOA
    {
        [MarshalAs(UnmanagedType.LPStr)] public string? pDocName;
        [MarshalAs(UnmanagedType.LPStr)] public string? pOutputFile;
        [MarshalAs(UnmanagedType.LPStr)] public string? pDataType;
    }

    [DllImport("winspool.drv", EntryPoint = "OpenPrinterA", SetLastError = true, CharSet = CharSet.Ansi)]
    private static extern bool OpenPrinter(string szPrinter, out IntPtr hPrinter, IntPtr pd);

    [DllImport("winspool.drv", SetLastError = true)]
    private static extern bool ClosePrinter(IntPtr hPrinter);

    [DllImport("winspool.drv", EntryPoint = "StartDocPrinterA", SetLastError = true, CharSet = CharSet.Ansi)]
    private static extern bool StartDocPrinter(IntPtr hPrinter, int level, [In] DOCINFOA di);

    [DllImport("winspool.drv", SetLastError = true)]
    private static extern bool EndDocPrinter(IntPtr hPrinter);

    [DllImport("winspool.drv", SetLastError = true)]
    private static extern bool StartPagePrinter(IntPtr hPrinter);

    [DllImport("winspool.drv", SetLastError = true)]
    private static extern bool EndPagePrinter(IntPtr hPrinter);

    [DllImport("winspool.drv", SetLastError = true)]
    private static extern bool WritePrinter(IntPtr hPrinter, IntPtr pBytes, int dwCount, out int dwWritten);
}
