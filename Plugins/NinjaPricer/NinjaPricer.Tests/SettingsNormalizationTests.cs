using NinjaPricer;

namespace NinjaPricer.Tests;

public sealed class SettingsNormalizationTests
{
    [Fact]
    public void InvalidValuesNormalizeToLiveDefaultsWithoutNetwork()
    {
        var settings = new NinjaPricerSettings
        {
            PriceSource = 99,
            League = "   ",
            RefreshIntervalMin = 0,
        };

        NinjaPricerSettingsNormalizer.Normalize(settings);

        Assert.Equal(PriceFetcher.SourcePoe2Scout, settings.PriceSource);
        Assert.Equal("Runes of Aldur", settings.League);
        Assert.Equal(1, settings.RefreshIntervalMin);
    }

    [Fact]
    public void ValidSourceAndTrimmedLeagueArePreserved()
    {
        var settings = new NinjaPricerSettings
        {
            PriceSource = PriceFetcher.SourcePoeNinja,
            League = "  Standard  ",
            RefreshIntervalMin = 120,
        };

        NinjaPricerSettingsNormalizer.Normalize(settings);

        Assert.Equal(PriceFetcher.SourcePoeNinja, settings.PriceSource);
        Assert.Equal("Standard", settings.League);
        Assert.Equal(120, settings.RefreshIntervalMin);
    }
}
