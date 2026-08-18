using RingOrder.Epos.Domain;
using Xunit;

namespace RingOrder.Epos.Tests;

/// <summary>
/// Which product a shop bought. One signed binary is installed everywhere and a
/// word in the bundle is the entire difference.
/// </summary>
public class EditionTests
{
    [Theory]
    [InlineData("print")]
    [InlineData("PRINT")]
    [InlineData("  Print  ")]
    public void The_print_edition_is_recognised_however_it_is_written(string raw)
    {
        Assert.True(ShopEdition.IsPrintOnly(raw));
        Assert.Equal(ShopEdition.Print, ShopEdition.Normalise(raw));
    }

    /// <summary>
    /// Falling the safe way, deliberately. A typo that silently downgraded a
    /// paying shop to a printer would take their till away mid-service; a typo
    /// that leaves a print-only machine with a Till tab it never opens costs
    /// nobody a service.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("pos")]
    [InlineData("prnt")]
    [InlineData("full")]
    public void Anything_unrecognised_is_the_full_till(string? raw)
    {
        Assert.False(ShopEdition.IsPrintOnly(raw));
        Assert.Equal(ShopEdition.Pos, ShopEdition.Normalise(raw));
    }

    [Fact]
    public void A_bundle_that_says_nothing_is_the_full_till()
    {
        Assert.Equal(ShopEdition.Pos, new ShopBundle().Edition);
        Assert.Equal(ShopEdition.Pos, AppSettings.CreateDefaults().Edition);
    }
}
