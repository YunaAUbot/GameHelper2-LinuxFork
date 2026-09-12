using Atlas2;
using GameHelper.Plugin.Price;

static void Check(bool condition, string message)
{
    if (!condition) throw new Exception(message);
}

var cases = new (string Label, string Name, int Quantity, bool UnitOnly)[]
{
    ("Divine Orbs x5", "Divine Orb", 5, false),
    ("Divine Orb x1", "Divine Orb", 1, false),
    ("Exalted Orbs x8", "Exalted Orb", 8, false),
    ("Chaos Orbs x4", "Chaos Orb", 4, false),
    ("Greater Orbs of Augmentation x8", "Greater Orb of Augmentation", 8, false),
    ("Greater Orbs of Transmutation x2", "Greater Orb of Transmutation", 2, false),
    ("Perfect Orbs of Transmutation", "Perfect Orb of Transmutation", 1, true),
    ("Orbs of Annulment", "Orb of Annulment", 1, true),
    ("Orbs of Chance", "Orb of Chance", 1, true),
    ("Greater Chaos Orbs", "Greater Chaos Orb", 1, true),
    ("Perfect Exalted Orbs", "Perfect Exalted Orb", 1, true),
    ("Omen: the Blessed", "Omen of the Blessed", 1, false),
    ("Omen: Sinistral Annulment", "Omen of Sinistral Annulment", 1, false),
    ("Kalandra's Touch", "Kalandra's Touch", 1, false),
    ("Mageblood", "Mageblood", 1, false),
};
foreach (var c in cases)
    Check(RitualRewardPricing.TryMap(c.Label, out var name, out var count, out var unit) &&
        name == c.Name && count == c.Quantity && unit == c.UnitOnly, "Mapping: " + c.Label);
foreach (var label in new[] { "+25% Tribute", "Very Rare Unique", "[2 mods]", "+Monster Packs", "Chaos Orbs x0", "Chaos Orbs x-2", "Chaos Orbs x999999999999999" })
    Check(!RitualRewardPricing.TryMap(label, out _, out _, out _), "Must not invent a price: " + label);

var provider = new FakeProvider();
provider.Values["Divine Orb"] = 243.25m;
provider.Values["Perfect Exalted Orb"] = 0.125m;
var labels = new[] { "Divine Orbs x5", "Divine Orb x1", "Perfect Exalted Orbs", "+25% Tribute", "Mageblood" };
var manual = new Dictionary<string, int> { ["+25% Tribute"] = -3, ["Mageblood"] = 20, ["Divine Orb x1"] = 999 };
var pricing = new RitualRewardPricing();
var now = DateTime.UtcNow;
Check(pricing.Refresh(provider, true, labels, now), "Initial prices change weights");
Check(provider.Calls == 3, "One quote per item name, including misses");
Check(pricing.GetWeight("Divine Orbs x5", manual) == 1216.25m, "Multiply quantities");
Check(pricing.GetWeight("Divine Orb x1", manual) == 243.25m, "Price replaces manual when available");
Check(pricing.GetWeight("Perfect Exalted Orbs", manual) == 0.125m, "Do not truncate fractional prices");
Check(pricing.GetWeight("+25% Tribute", manual) == -3 && pricing.GetWeight("Mageblood", manual) == 20, "Manual fallback");
Check(pricing.GetWeight(null, manual) == 0, "Missing second mod");
Check(!pricing.Refresh(provider, true, labels, now.AddSeconds(1)) && provider.Calls == 3, "No per-frame requotes");
Check(!pricing.Refresh(provider, true, labels, now.AddSeconds(5)), "Unchanged quotes do not trigger route sorting");
provider.Values["Divine Orb"] = 300m;
Check(pricing.Refresh(provider, true, labels, now.AddSeconds(10)) && pricing.GetWeight("Divine Orbs x5", manual) == 1500m, "Refresh changes ranking");
Check(pricing.Refresh(provider, false, labels, now.AddSeconds(11)) && pricing.GetWeight("Divine Orb x1", manual) == 999, "Disable restores manual immediately");
pricing.Refresh(provider, true, labels, now.AddSeconds(12));
Check(pricing.Refresh(null, true, labels, now.AddSeconds(13)) && pricing.GetWeight("Divine Orb x1", manual) == 999, "Unload removes stale prices");
provider.Throw = true;
Check(!pricing.Refresh(provider, true, labels, now.AddSeconds(14)), "Throwing provider cannot break rendering");
provider.Throw = false;
provider.Values["Divine Orb"] = -5;
provider.Values["Perfect Exalted Orb"] = decimal.MaxValue;
Check(!pricing.Refresh(provider, true, labels, now.AddSeconds(20)), "Reject negative/overflowing prices");
Console.WriteLine("PASS: reward mappings, quantities, precision, cache cadence, provider failures/reload, manual fallback.");

sealed class FakeProvider : IPriceProvider
{
    public Dictionary<string, decimal> Values = new();
    public int Calls;
    public bool Throw;
    public PriceProviderStatus Status => throw new NotSupportedException();
    public bool TryGetPrice(PriceQuery query, out PriceQuote quote)
    {
        Calls++;
        if (Throw) throw new InvalidOperationException();
        if (Values.TryGetValue(query.ItemName, out var value)) { quote = new(value, value, value, "test"); return true; }
        quote = null; return false;
    }
    public bool TryResolveDisplayName(PriceQuery query, out string name) => throw new NotSupportedException();
    public bool IsGenericLookupName(string name) => throw new NotSupportedException();
    public bool HasPriceDataForName(string name) => throw new NotSupportedException();
    public void RequestRefresh() => throw new Exception("Consumer must not request network refreshes");
}
