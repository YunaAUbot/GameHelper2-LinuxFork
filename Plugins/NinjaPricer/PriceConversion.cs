namespace NinjaPricer;

public readonly record struct ConvertedPrice(decimal Chaos, decimal Divine, decimal Exalted);

public static class PriceConversion
{
    public static ConvertedPrice FromChaos(decimal chaos, decimal chaosPerDivine, decimal chaosPerExalted) =>
        new(
            chaos,
            chaosPerDivine > 0m ? chaos / chaosPerDivine : 0m,
            chaosPerExalted > 0m ? chaos / chaosPerExalted : 0m);
}
