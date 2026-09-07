namespace NinjaPricer;

using GameHelper.Plugin;

public sealed class NinjaPricerSettings : IPSettings
{
    public int PriceSource = PriceFetcher.SourcePoe2Scout;
    public string League = "Runes of Aldur";
    public int RefreshIntervalMin = 5;
}
