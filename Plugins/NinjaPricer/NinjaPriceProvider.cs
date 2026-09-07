namespace NinjaPricer;

using GameHelper.Plugin.Price;

public sealed class NinjaPriceProvider : IPriceProvider
{
    private readonly Func<NinjaPricerSettings> settings;
    private readonly string pluginDirectory;

    public NinjaPriceProvider(Func<NinjaPricerSettings> settings, string pluginDirectory)
    {
        this.settings = settings ?? throw new ArgumentNullException(nameof(settings));
        this.pluginDirectory = pluginDirectory ?? throw new ArgumentNullException(nameof(pluginDirectory));
    }

    public PriceProviderStatus Status
    {
        get
        {
            var current = this.settings();
            return new PriceProviderStatus(
                "NinjaPricer",
                current.PriceSource == PriceFetcher.SourcePoeNinja ? "poe.ninja" : "poe2scout",
                current.League,
                PriceFetcher.IsFetching,
                PriceFetcher.LoadedItemCount,
                new DateTimeOffset(PriceFetcher.LastFetchUtc, TimeSpan.Zero));
        }
    }

    public bool TryGetPrice(PriceQuery query, out PriceQuote quote)
    {
        ArgumentNullException.ThrowIfNull(query);
        var price = PriceFetcher.GetPrice(
            query.ItemName,
            query.ExplicitMods,
            query.InternalPathBasename,
            query.FullItemPath,
            query.ScoutText);
        if (price is null)
        {
            quote = null!;
            return false;
        }

        var converted = PriceConversion.FromChaos(
            (decimal)price.PriceChaos,
            (decimal)PriceFetcher.GetChaosPerDivine(),
            (decimal)PriceFetcher.GetChaosPerExalted());
        quote = new PriceQuote(converted.Chaos, converted.Divine, converted.Exalted, this.Status.Source);
        return true;
    }

    public bool TryResolveDisplayName(PriceQuery query, out string displayName)
    {
        ArgumentNullException.ThrowIfNull(query);
        return PriceFetcher.TryResolveDisplayName(query.InternalPathBasename, out displayName);
    }

    public bool IsGenericLookupName(string itemName) => PriceFetcher.IsGenericLookupName(itemName);

    public bool HasPriceDataForName(string itemName) => PriceFetcher.HasPriceDataForName(itemName);

    public void RequestRefresh() => PriceFetcher.ForceRefresh(this.pluginDirectory);
}
