using RingOrder.Epos.Domain;
using Xunit;

namespace RingOrder.Epos.Tests;

/// <summary>
/// Money crosses the storage boundary as pence. These cases are the ones that
/// go wrong when someone reaches for a double.
/// </summary>
public class MoneyTests
{
    [Theory]
    [InlineData("0.00", 0)]
    [InlineData("6.20", 620)]
    [InlineData("9.99", 999)]
    [InlineData("-0.50", -50)]
    [InlineData("1237.58", 123758)]
    public void Converts_pounds_to_pence(string amount, int expected)
    {
        Assert.Equal(expected, Money.ToPence(decimal.Parse(amount)));
    }

    [Fact]
    public void Rounds_half_away_from_zero_not_to_even()
    {
        // Banker's rounding would make this 0.12 and a shop's day would not add up.
        Assert.Equal(13, Money.ToPence(0.125m));
        Assert.Equal(-13, Money.ToPence(-0.125m));
    }

    [Fact]
    public void Round_trips_every_penny_in_a_realistic_range()
    {
        for (var pence = 0; pence <= 20_000; pence++)
            Assert.Equal(pence, Money.ToPence(Money.FromPence(pence)));
    }

    [Fact]
    public void Summing_pence_avoids_the_classic_float_drift()
    {
        // 0.1 + 0.2 in binary floating point is not 0.3.
        var total = 0m;
        for (var i = 0; i < 3; i++) total += 0.1m;
        Assert.Equal(30, Money.ToPence(total));
    }
}
