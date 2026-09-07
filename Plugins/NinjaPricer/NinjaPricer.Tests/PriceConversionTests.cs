using NinjaPricer;

namespace NinjaPricer.Tests;

public sealed class PriceConversionTests
{
    [Fact]
    public void ConvertsChaosIntoAllQuoteCurrencies()
    {
        var values = PriceConversion.FromChaos(120m, 12m, 0.1m);

        Assert.Equal(120m, values.Chaos);
        Assert.Equal(10m, values.Divine);
        Assert.Equal(1200m, values.Exalted);
    }

    [Theory]
    [InlineData(0, 0.1, 0, 1200)]
    [InlineData(-1, 0.1, 0, 1200)]
    [InlineData(12, 0, 10, 0)]
    [InlineData(12, -1, 10, 0)]
    public void InvalidRatesYieldZeroForOnlyThatCurrency(
        decimal chaosPerDivine,
        decimal chaosPerExalted,
        decimal expectedDivine,
        decimal expectedExalted)
    {
        var values = PriceConversion.FromChaos(120m, chaosPerDivine, chaosPerExalted);

        Assert.Equal(expectedDivine, values.Divine);
        Assert.Equal(expectedExalted, values.Exalted);
    }
}
