using RingOrder.Epos.Domain;
using Xunit;

namespace RingOrder.Epos.Tests;

/// <summary>
/// VAT on a UK takeaway receipt. Getting the direction wrong overstates a
/// shop's takings by a fifth, which is the kind of error an accountant finds a
/// year later.
/// </summary>
public class TaxTests
{
    private static readonly TaxClass Standard = new()
        { Id = "hot-food", Name = "Hot food", RateBasisPoints = 2000 };

    private static readonly TaxClass Zero = new()
        { Id = "cold-food", Name = "Cold food", RateBasisPoints = 0 };

    private static readonly TaxClass[] Classes = [Standard, Zero];

    private static CartLine Line(decimal total, string? taxClassId) => new()
    {
        Name = "Dish",
        Quantity = 1,
        BasePrice = total,
        LineTotal = total,
        TaxClassId = taxClassId,
        IsAdHoc = true,
    };

    [Fact]
    public void Prices_include_vat_so_the_maths_runs_backwards()
    {
        // £6.00 at 20% is £5.00 net plus £1.00 VAT — not £6.00 plus £1.20.
        var order = new PosOrder { Lines = [Line(6.00m, "hot-food")], Total = 6.00m };

        var band = TaxCalculator.Summarise(order, Classes).Single();

        Assert.Equal(5.00m, band.Net);
        Assert.Equal(1.00m, band.Vat);
        Assert.Equal(6.00m, band.Gross);
        Assert.Equal("20%", band.RateLabel);
    }

    [Fact]
    public void A_zero_rated_band_carries_no_vat()
    {
        var order = new PosOrder { Lines = [Line(4.00m, "cold-food")], Total = 4.00m };

        var band = TaxCalculator.Summarise(order, Classes).Single();

        Assert.Equal(0m, band.Vat);
        Assert.Equal(4.00m, band.Net);
    }

    [Fact]
    public void An_order_across_two_bands_splits_and_the_totals_still_add_up()
    {
        var order = new PosOrder
        {
            Lines = [Line(6.00m, "hot-food"), Line(4.00m, "cold-food")],
            Total = 10.00m,
        };

        var bands = TaxCalculator.Summarise(order, Classes);

        Assert.Equal(2, bands.Count);
        Assert.Equal(10.00m, bands.Sum(b => b.Gross));
        Assert.Equal(1.00m, TaxCalculator.TotalVat(bands));
    }

    [Fact]
    public void A_discount_reduces_every_band_it_touches()
    {
        // £2 off a ticket that is half hot and half cold takes £1 off each,
        // rather than all of it off the standard-rated half.
        var order = new PosOrder
        {
            Lines = [Line(10.00m, "hot-food"), Line(10.00m, "cold-food")],
            DiscountTotal = 2.00m,
            Total = 18.00m,
        };

        var bands = TaxCalculator.Summarise(order, Classes);

        Assert.Equal(18.00m, bands.Sum(b => b.Gross));
        Assert.Equal(9.00m, bands.Single(b => b.Class.Id == "cold-food").Gross);
        Assert.Equal(9.00m, bands.Single(b => b.Class.Id == "hot-food").Gross);
    }

    [Fact]
    public void Delivery_follows_the_shops_default_band()
    {
        var order = new PosOrder
        {
            Lines = [Line(10.00m, "cold-food")],
            DeliveryFee = 2.40m,
            Total = 12.40m,
        };

        var bands = TaxCalculator.Summarise(order, Classes);

        Assert.Equal(2.40m, bands.Single(b => b.Class.Id == "hot-food").Gross);
        Assert.Equal(10.00m, bands.Single(b => b.Class.Id == "cold-food").Gross);
    }

    [Fact]
    public void A_line_with_no_tax_class_falls_back_rather_than_vanishing()
    {
        var order = new PosOrder { Lines = [Line(6.00m, null)], Total = 6.00m };

        var band = TaxCalculator.Summarise(order, Classes).Single();

        Assert.Equal("hot-food", band.Class.Id);
        Assert.Equal(6.00m, band.Gross);
    }

    [Fact]
    public void A_shop_with_no_tax_classes_reports_nothing_rather_than_guessing()
    {
        var order = new PosOrder { Lines = [Line(6.00m, "hot-food")], Total = 6.00m };
        Assert.Empty(TaxCalculator.Summarise(order, []));
    }

    [Fact]
    public void A_free_ticket_does_not_divide_by_zero()
    {
        var order = new PosOrder { Lines = [Line(0m, "hot-food")], DiscountTotal = 0m, Total = 0m };
        Assert.Empty(TaxCalculator.Summarise(order, Classes));
    }

    [Theory]
    [InlineData("0.99", "0.17")]
    [InlineData("5.99", "1.00")]
    [InlineData("12.40", "2.07")]
    [InlineData("100.00", "16.67")]
    public void Vat_rounds_to_the_penny(string gross, string expectedVat)
    {
        var amount = decimal.Parse(gross);
        var order = new PosOrder { Lines = [Line(amount, "hot-food")], Total = amount };

        Assert.Equal(decimal.Parse(expectedVat), TaxCalculator.Summarise(order, Classes).Single().Vat);
    }

    [Fact]
    public void Net_and_vat_always_reconstruct_the_gross()
    {
        // The property that matters on a receipt: the parts must add back up.
        for (var pence = 1; pence <= 5000; pence++)
        {
            var amount = Money.FromPence(pence);
            var order = new PosOrder { Lines = [Line(amount, "hot-food")], Total = amount };
            var band = TaxCalculator.Summarise(order, Classes).Single();

            Assert.Equal(band.Gross, Money.Round(band.Net + band.Vat));
        }
    }
}
