using System.Text;
using RingOrder.Epos.Hardware;
using Xunit;

namespace RingOrder.Epos.Tests;

/// <summary>
/// Chinese on a ticket, and the one way it goes out.
/// <para>
/// A real kitchen ticket came off the printer with the dish name correct and the
/// note beneath it as rubbish. Dish names went through <c>KitchenLine</c>, which
/// rasterises; notes, order comments and receipt footers went through
/// <c>Line</c>, which emitted code-page bytes the printer could not render.
/// Nothing said so — the ticket printed, it was simply wrong.
/// </para>
/// </summary>
public class TicketCjkTests
{
    /// <summary>`GS v 0` — print raster bit image. Its presence is what proves a bitmap was sent.</summary>
    private static readonly byte[] RasterCommand = [0x1D, 0x76, 0x30, 0x00];

    private static bool Contains(byte[] haystack, byte[] needle)
    {
        for (var i = 0; i + needle.Length <= haystack.Length; i++)
        {
            var match = true;
            for (var j = 0; j < needle.Length; j++)
                if (haystack[i + j] != needle[j]) { match = false; break; }

            if (match) return true;
        }

        return false;
    }

    private static byte[] CodePageBytes(string text)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding("gb2312").GetBytes(text);
    }

    /// <summary>
    /// The defect, as a test. A quick note is the commonest Chinese on a ticket
    /// after the dish itself.
    /// </summary>
    [Fact]
    public void A_note_in_chinese_goes_out_as_a_bitmap_not_as_bytes()
    {
        var built = new EscPosTicketBuilder("gbk", rasterCjk: true)
            .Line("  NOTE: 不要葱")
            .Build();

        Assert.True(Contains(built, RasterCommand), "expected a raster image command");
        Assert.False(Contains(built, CodePageBytes("不要葱")), "the characters must not go out as code-page bytes");
    }

    [Fact]
    public void So_does_a_dish_name_which_always_did()
    {
        var built = new EscPosTicketBuilder("gbk", rasterCjk: true)
            .KitchenLine("宫保鸡丁")
            .Build();

        Assert.True(Contains(built, RasterCommand));
    }

    /// <summary>
    /// The half that must not change. Columns, separators and totals are ASCII
    /// and go out as they always have — a ticket whose layout shifted would be a
    /// worse bug than the one being fixed.
    /// </summary>
    [Fact]
    public void An_ascii_line_is_still_plain_text()
    {
        var built = new EscPosTicketBuilder("gbk", rasterCjk: true)
            .Line("Order 1043")
            .Build();

        Assert.False(Contains(built, RasterCommand));
        Assert.True(Contains(built, Encoding.ASCII.GetBytes("Order 1043")));
    }

    /// <summary>
    /// A shop that has turned rasterising off gets code-page bytes, as it asked
    /// for. Some printers do render GB18030 themselves, and that setting is how
    /// they say so.
    /// </summary>
    [Fact]
    public void A_shop_that_asked_for_text_still_gets_text()
    {
        var built = new EscPosTicketBuilder("gbk", rasterCjk: false)
            .Line("不要葱")
            .Build();

        Assert.False(Contains(built, RasterCommand));
        Assert.True(Contains(built, CodePageBytes("不要葱")));
    }

    /// <summary>
    /// `Line` now routes CJK into the raster writer, and that writer falls back
    /// to text when it cannot draw. If the fallback went back through `Line` the
    /// two would call each other until the stack ran out — on a kitchen ticket,
    /// mid-service.
    /// </summary>
    [Fact]
    public void The_raster_fallback_does_not_call_back_into_the_line_that_called_it()
    {
        var built = new EscPosTicketBuilder("gbk", rasterCjk: true)
            .Line("不要葱")
            .Line("no onion")
            .Build();

        Assert.NotEmpty(built);
    }
}
