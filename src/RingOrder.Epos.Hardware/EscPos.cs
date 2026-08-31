using System.Drawing;
using System.Drawing.Imaging;
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

    /// <summary>
    /// An ordinary line — and Chinese on it goes to raster, like everything else.
    /// <para>
    /// It did not, once, and the result was a kitchen ticket where the dish name
    /// printed correctly and the note beneath it came out as rubbish: dish names
    /// go through <see cref="KitchenLine"/>, which rasterises, while notes,
    /// comments and receipt footers came through here and went out as code-page
    /// bytes the printer could not render.
    /// </para>
    /// <para>
    /// Pure ASCII is untouched, so columns, separators and totals are byte for
    /// byte what they were.
    /// </para>
    /// </summary>
    public EscPosTicketBuilder Line(string text = "")
    {
        if (_rasterCjk && EscPos.ContainsCjk(text))
        {
            _ms.Write(EscPos.AlignLeft);
            _ms.Write(EscPos.SizeNormal);
            WriteRasterLine(text, NormalEm);
            return this;
        }

        RawText(text);
        return Nl();
    }

    /// <summary>
    /// Body-text size for a rasterised line. Smaller than the 28 a dish
    /// translation gets and much smaller than a headline's 40 — a note is meant
    /// to be read, not shouted.
    /// </summary>
    private const float NormalEm = 24f;

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
            // Code-page bytes, and deliberately not through Line: that now routes
            // CJK back to here, and the two would call each other for ever.
            Large().Bold(true);
            RawText(text);
            Nl();
            Bold(false).Normal();
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
            // Same reason: not through Line.
            Large().Bold(true);
            RawText(text);
            Nl();
            Bold(false).Normal();
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
