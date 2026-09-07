namespace LootValue.Tests;

using GameHelper.Plugin.Price;
using Newtonsoft.Json;
using Xunit;

public sealed class LootValuePricingTests
{
    [Fact]
    public void CaptureSnapshotsProviderExactlyOnceForTheWholePass()
    {
        var first = new StubProvider(new PriceQuote(10m, 1m, 5m, "first"));
        var second = new StubProvider(new PriceQuote(20m, 2m, 10m, "second"));
        var reads = 0;
        var pass = LootValuePricingPass.Capture(() => ++reads == 1 ? first : second);

        Assert.True(pass.TryPrice(Query("one"), 1, 2, out var firstResult));
        Assert.True(pass.TryPrice(Query("two"), 1, 2, out var secondResult));
        Assert.Equal(1, reads);
        Assert.Equal(2, first.CapturedQueries.Count);
        Assert.Empty(second.CapturedQueries);
        Assert.Equal(10m, firstResult.DisplayValue);
        Assert.Equal(10m, secondResult.DisplayValue);
    }

    [Fact]
    public void CompleteQueryIsPassedToProviderUnchanged()
    {
        var provider = new StubProvider(new PriceQuote(12.5m, 0.25m, 2.5m, "test"));
        var pass = LootValuePricingPass.Capture(() => provider);
        var query = new PriceQuery(
            "Unique Name",
            new[] { "+10 to Strength", "20% increased Armour" },
            "InternalBasename",
            "Metadata/Items/Armours/InternalBasename",
            "Unique Name\nItem Level: 80");

        Assert.True(pass.TryPrice(query, 1, 1, out _));
        Assert.Same(query, Assert.Single(provider.CapturedQueries));
        Assert.Equal("Unique Name", provider.CapturedQueries[0].ItemName);
        Assert.Equal(new[] { "+10 to Strength", "20% increased Armour" }, provider.CapturedQueries[0].ExplicitMods);
        Assert.Equal("InternalBasename", provider.CapturedQueries[0].InternalPathBasename);
        Assert.Equal("Metadata/Items/Armours/InternalBasename", provider.CapturedQueries[0].FullItemPath);
        Assert.Equal("Unique Name\nItem Level: 80", provider.CapturedQueries[0].ScoutText);
    }

    [Theory]
    [InlineData(0, 3, "0.38 div")]
    [InlineData(1, 3, "7.5 ex")]
    [InlineData(2, 3, "37.5 c")]
    public void StackTotalsUseProviderConversionsAndExistingRounding(int currency, int stack, string expectedText)
    {
        var provider = new StubProvider(new PriceQuote(12.5m, 0.125m, 2.5m, "test"));
        var pass = LootValuePricingPass.Capture(() => provider);

        Assert.True(pass.TryPrice(Query("stack"), stack, currency, out var result));
        Assert.Equal(7.5m, result.ExaltedValue);
        Assert.Equal(expectedText, result.Text);
    }

    [Fact]
    public void DisplayNameResolutionUsesArtKeyAsInternalBasename()
    {
        var provider = new StubProvider(new PriceQuote(1m, 1m, 1m, "test"), displayName: "Resolved Unique");
        var pass = LootValuePricingPass.Capture(() => provider);

        Assert.True(pass.TryResolveDisplayName("TheArtKey", out var displayName));
        Assert.Equal("Resolved Unique", displayName);
        var query = Assert.Single(provider.CapturedResolutionQueries);
        Assert.Equal("TheArtKey", query.ItemName);
        Assert.Equal("TheArtKey", query.InternalPathBasename);
    }

    [Fact]
    public void MissingFailedNullAndInvalidQuotesFailClosed()
    {
        Assert.False(LootValuePricingPass.Capture(() => null).TryPrice(Query("missing"), 1, 1, out _));
        Assert.False(LootValuePricingPass.Capture(() => new StubProvider(null, succeeds: false)).TryPrice(Query("failed"), 1, 1, out _));
        Assert.False(LootValuePricingPass.Capture(() => new StubProvider(null)).TryPrice(Query("null"), 1, 1, out _));

        foreach (var quote in new[]
                 {
                     new PriceQuote(0m, 1m, 1m, "zero"),
                     new PriceQuote(-1m, 1m, 1m, "negative"),
                     new PriceQuote(1m, 0m, 1m, "zero"),
                     new PriceQuote(1m, 1m, 0m, "zero"),
                     new PriceQuote(1m, 1m, 1m, string.Empty),
                 })
        {
            Assert.False(LootValuePricingPass.Capture(() => new StubProvider(quote)).TryPrice(Query("invalid"), 1, 1, out _));
        }
    }

    [Fact]
    public void RemovedLegacyPricingSettingsAreIgnoredWhileRemainingSettingsSurvive()
    {
        const string json = """
            {"PriceSource":1,"League":"Legacy","RefreshIntervalMin":99,"ShowOverlay":false,"MinValueEx":4.5}
            """;

        var settings = JsonConvert.DeserializeObject<LootValueSettings>(json);

        Assert.NotNull(settings);
        Assert.False(settings.ShowOverlay);
        Assert.Equal(4.5f, settings.MinValueEx);
    }

    private static PriceQuery Query(string name) => new(name, Array.Empty<string>(), string.Empty, string.Empty, name);

    private sealed class StubProvider : IPriceProvider
    {
        private readonly PriceQuote? quote;
        private readonly bool succeeds;
        private readonly string? displayName;

        public StubProvider(PriceQuote? quote, bool succeeds = true, string? displayName = null)
        {
            this.quote = quote;
            this.succeeds = succeeds;
            this.displayName = displayName;
        }

        public List<PriceQuery> CapturedQueries { get; } = new();

        public List<PriceQuery> CapturedResolutionQueries { get; } = new();

        public PriceProviderStatus Status { get; } = new("test", "test", "test", false, 1, DateTimeOffset.UtcNow);

        public bool TryGetPrice(PriceQuery query, out PriceQuote quote)
        {
            this.CapturedQueries.Add(query);
            quote = this.quote!;
            return this.succeeds;
        }

        public bool TryResolveDisplayName(PriceQuery query, out string displayName)
        {
            this.CapturedResolutionQueries.Add(query);
            displayName = this.displayName ?? string.Empty;
            return this.displayName != null;
        }

        public bool IsGenericLookupName(string itemName) => false;

        public bool HasPriceDataForName(string itemName) => false;

        public void RequestRefresh()
        {
        }
    }
}
