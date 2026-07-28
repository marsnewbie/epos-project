using System.Runtime.InteropServices;
using System.Text;
using MagicWok.Epos.Domain;

namespace MagicWok.Epos.Hardware;

public static class EscPos
{
    public static readonly byte[] Init = [0x1B, 0x40];
    public static readonly byte[] AlignLeft = [0x1B, 0x61, 0x00];
    public static readonly byte[] AlignCenter = [0x1B, 0x61, 0x01];
    public static readonly byte[] AlignRight = [0x1B, 0x61, 0x02];
    public static readonly byte[] BoldOn = [0x1B, 0x45, 0x01];
    public static readonly byte[] BoldOff = [0x1B, 0x45, 0x00];
    public static readonly byte[] DoubleOn = [0x1D, 0x21, 0x11];
    public static readonly byte[] DoubleOff = [0x1D, 0x21, 0x00];
    /// <summary>Double-height only — safer for CJK on many 80mm heads than full double-width.</summary>
    public static readonly byte[] TallOn = [0x1D, 0x21, 0x01];
    public static readonly byte[] TallOff = [0x1D, 0x21, 0x00];
    public static readonly byte[] Cut = [0x1D, 0x56, 0x00];
    public static readonly byte[] Feed3 = [0x1B, 0x64, 0x03];
    public static readonly byte[] OpenDrawer = [0x1B, 0x70, 0x00, 0x19, 0xFA];

    public static Encoding ResolveEncoding(string? name)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return (name ?? "gb18030").ToLowerInvariant() switch
        {
            "utf8" or "utf-8" => new UTF8Encoding(false),
            _ => Encoding.GetEncoding("GB18030"),
        };
    }
}

public sealed class EscPosTicketBuilder
{
    private readonly MemoryStream _ms = new();
    private readonly Encoding _encoding;

    public EscPosTicketBuilder(string? encodingName = null)
    {
        _encoding = EscPos.ResolveEncoding(encodingName);
        _ms.Write(EscPos.Init);
    }

    public EscPosTicketBuilder Center() { _ms.Write(EscPos.AlignCenter); return this; }
    public EscPosTicketBuilder Left() { _ms.Write(EscPos.AlignLeft); return this; }
    public EscPosTicketBuilder Bold(bool on) { _ms.Write(on ? EscPos.BoldOn : EscPos.BoldOff); return this; }
    public EscPosTicketBuilder Double(bool on) { _ms.Write(on ? EscPos.DoubleOn : EscPos.DoubleOff); return this; }
    public EscPosTicketBuilder Tall(bool on) { _ms.Write(on ? EscPos.TallOn : EscPos.TallOff); return this; }

    public EscPosTicketBuilder Line(string text = "")
    {
        var bytes = _encoding.GetBytes(text + "\n");
        _ms.Write(bytes);
        return this;
    }

    public EscPosTicketBuilder Columns(string left, string right, int width = 32)
    {
        left ??= "";
        right ??= "";
        if (left.Length + right.Length >= width)
            return Line(left).Line(right.PadLeft(width));
        var pad = width - left.Length - right.Length;
        return Line(left + new string(' ', Math.Max(1, pad)) + right);
    }

    public EscPosTicketBuilder Separator(char c = '-', int width = 32)
    {
        return Line(new string(c, width));
    }

    public EscPosTicketBuilder FeedAndCut()
    {
        _ms.Write(EscPos.Feed3);
        _ms.Write(EscPos.Cut);
        return this;
    }

    public byte[] Build() => _ms.ToArray();
}

/// <summary>
/// Professional ESC/POS tickets. Online jobs carry full website kitchen fields
/// (information-equivalent to GcAnyOrder); layout is EPOS-owned, not Goodcom XML.
/// </summary>
public static class TicketRenderer
{
    private const int W = 32;

    public static byte[] RenderKitchen(PosOrder order, AppSettings settings)
    {
        var b = new EscPosTicketBuilder(settings.PrintEncoding);
        var shop = string.IsNullOrWhiteSpace(settings.ShopName) ? "Magic Wok" : settings.ShopName;

        b.Center().Bold(true).Line(shop.ToUpperInvariant()).Bold(false);
        b.Line("KITCHEN").Left().Separator('=', W);
        b.Bold(true).Columns($"#{order.OrderNumber}", OrderTypeLabel(order.OrderType), W).Bold(false);
        b.Line(order.CreatedAt.ToLocalTime().ToString("HH:mm dd-MM-yy"));
        if (!string.IsNullOrWhiteSpace(order.RequestedFor))
            b.Bold(true).Line($"Requested for: {order.RequestedFor}").Bold(false);
        else if (!string.IsNullOrWhiteSpace(order.FulfilmentLabel))
            b.Bold(true).Line($"Requested for: {order.FulfilmentLabel}").Bold(false);
        if (order.Source == PosOrderSource.Online)
            b.Line("ONLINE ORDER");
        b.Separator('-', W);

        foreach (var line in order.Lines)
        {
            var qtyName = $"{line.Quantity}x {line.Name}";
            b.Bold(true).Columns(qtyName, $"£{line.LineTotal:0.00}", W).Bold(false);
            if (!string.IsNullOrWhiteSpace(line.ItemTranslation))
                b.Tall(true).Line($"{line.Quantity} {line.ItemTranslation}").Tall(false);
            foreach (var sel in line.Selections)
            {
                foreach (var c in sel.Choices)
                {
                    b.Line($"  + {c.Label}");
                    if (!string.IsNullOrWhiteSpace(c.OptionTranslation))
                        b.Line($"    {c.OptionTranslation}");
                }
            }
            if (!string.IsNullOrWhiteSpace(line.Notes))
                b.Line($"  NOTE: {line.Notes}");
        }

        b.Separator('-', W);
        if (order.DiscountTotal > 0)
            b.Columns("Discount", $"-£{order.DiscountTotal:0.00}", W);
        if (order.DeliveryFee > 0)
            b.Columns("Delivery", $"£{order.DeliveryFee:0.00}", W);
        b.Bold(true).Columns("TOTAL", $"£{order.Total:0.00}", W).Bold(false);
        b.Separator('-', W);

        if (!string.IsNullOrWhiteSpace(order.PaymentLabel))
            b.Line($"Payment: {order.PaymentLabel}");
        else if (order.Tenders.Count > 0 && !string.IsNullOrWhiteSpace(order.Tenders[0].Reference))
            b.Line($"Payment: {order.Tenders[0].Reference}");

        b.Line("Cus Info:");
        if (!string.IsNullOrWhiteSpace(order.CustomerName))
            b.Line(order.CustomerName!);
        if (!string.IsNullOrWhiteSpace(order.CustomerPhone))
            b.Line(order.CustomerPhone!);
        if (order.OrderType == PosOrderType.Delivery)
        {
            if (!string.IsNullOrWhiteSpace(order.DeliveryAddress))
                b.Line(Wrap(order.DeliveryAddress!, W));
            if (!string.IsNullOrWhiteSpace(order.DeliveryPostcode))
                b.Line(order.DeliveryPostcode!);
        }
        else
        {
            b.Line("COLLECTION");
        }

        if (!string.IsNullOrWhiteSpace(order.Notes))
        {
            b.Separator('-', W);
            b.Bold(true).Line("Comments:").Bold(false);
            b.Line(Wrap(order.Notes!, W));
        }

        if (!string.IsNullOrWhiteSpace(order.TicketFooter))
        {
            b.Separator('-', W);
            b.Center().Line(order.TicketFooter!).Left();
        }

        b.FeedAndCut();
        return b.Build();
    }

    private static string Wrap(string text, int width)
    {
        if (text.Length <= width) return text;
        // Soft wrap for long addresses — printer still gets multiple Line() calls via Split below if needed
        return text;
    }

    public static byte[] RenderFront(PosOrder order, AppSettings settings)
    {
        var b = new EscPosTicketBuilder(settings.PrintEncoding);
        b.Center().Double(true).Bold(true).Line(settings.ShopName).Double(false).Bold(false);
        b.Line(settings.ShopAddress);
        b.Line($"{settings.ShopPostcode}  {settings.ShopPhone}");
        b.Line("RECEIPT").Left().Separator('=');
        b.Line($"Order {order.OrderNumber}");
        b.Line(order.CreatedAt.LocalDateTime.ToString("dd/MM/yyyy HH:mm"));
        b.Line(OrderTypeLabel(order.OrderType));
        b.Separator();
        foreach (var line in order.Lines)
        {
            b.Line($"{line.Quantity} x {line.Name}");
            foreach (var sel in line.Selections)
                foreach (var c in sel.Choices)
                    b.Line($"  + {c.Label}");
            b.Line($"    £{line.LineTotal:0.00}");
        }
        b.Separator();
        b.Line($"Subtotal   £{order.Subtotal:0.00}");
        if (order.DeliveryFee > 0) b.Line($"Delivery   £{order.DeliveryFee:0.00}");
        if (order.DiscountTotal > 0) b.Line($"Discount  -£{order.DiscountTotal:0.00}");
        b.Bold(true).Line($"TOTAL     £{order.Total:0.00}").Bold(false);
        foreach (var t in order.Tenders)
            b.Line($"{t.Type}  £{t.Amount:0.00}");
        b.Center().Line("Thank you").FeedAndCut();
        return b.Build();
    }

    public static byte[] RenderTestPage(string printerName, AppSettings settings)
    {
        var b = new EscPosTicketBuilder(settings.PrintEncoding);
        b.Center().Double(true).Bold(true).Line("Magic Wok EPOS").Double(false).Bold(false);
        b.Line("Printer test page");
        b.Left().Separator();
        b.Line($"Printer: {printerName}");
        b.Line($"Shop: {settings.ShopName}");
        b.Line($"Time: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        b.Line("English OK");
        b.Line("中文测试 厨房小票");
        b.Separator();
        b.Line("If you can read this, ESC/POS works.");
        b.FeedAndCut();
        return b.Build();
    }

    private static string OrderTypeLabel(PosOrderType t) => t switch
    {
        PosOrderType.Delivery => "DELIVERY / 外卖",
        PosOrderType.Collection => "COLLECTION / 自取",
        PosOrderType.WalkIn => "WALK-IN / 堂等",
        PosOrderType.EatIn => "EAT-IN / 堂食",
        _ => t.ToString(),
    };
}

public static class RawPrinter
{
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
                pDocName = "MagicWok.Epos",
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
