namespace NinjaPricer;

public static class NinjaPricerSettingsNormalizer
{
    public const string DefaultLeague = "Runes of Aldur";

    public static void Normalize(NinjaPricerSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (settings.PriceSource != PriceFetcher.SourcePoeNinja &&
            settings.PriceSource != PriceFetcher.SourcePoe2Scout)
        {
            settings.PriceSource = PriceFetcher.SourcePoe2Scout;
        }

        settings.League = string.IsNullOrWhiteSpace(settings.League)
            ? DefaultLeague
            : settings.League.Trim();
        settings.RefreshIntervalMin = Math.Max(1, settings.RefreshIntervalMin);
    }
}
