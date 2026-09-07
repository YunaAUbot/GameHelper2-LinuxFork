namespace NinjaPricer.Tests;

using System.Reflection;
using Xunit;

public sealed class RunecraftCoverageTests
{
    [Fact]
    public void SharedProviderFetchesVerisiumForEverySource()
    {
        Assert.Equal(1, ReadTypes("NinjaExchangeTypes").Count(type => type == "Verisium"));
        Assert.Equal(1, ReadTypes("ScoutCurrencyCategories").Count(type => type == "verisium"));
    }

    private static string[] ReadTypes(string fieldName) =>
        (string[])(typeof(PriceFetcher).GetField(fieldName, BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null)
            ?? throw new InvalidOperationException($"Missing {fieldName}."));
}
