using System;
using System.Linq;
using GameHelper.Plugin.Price;

var mods = new[] { "+10 to Strength" };
var query = new PriceQuery("Gold Ring", mods, "GoldRing", "Metadata/Items/Rings/GoldRing", "Gold Ring\nItem Level: 80");
mods[0] = "mutated";
if (query.ItemName != "Gold Ring" || query.ExplicitMods.Count != 1 || query.ExplicitMods[0] != "+10 to Strength" ||
    query.InternalPathBasename != "GoldRing" || query.FullItemPath != "Metadata/Items/Rings/GoldRing" ||
    query.ScoutText != "Gold Ring\nItem Level: 80")
    throw new InvalidOperationException("PriceQuery is not an immutable value snapshot");

var quote = new PriceQuote(12.5m, 0.1m, 3m, "probe");
var status = new PriceProviderStatus("Probe", "fixture", "Standard", false, 42, DateTimeOffset.UtcNow);
var first = new ProbeProvider(quote, status);
var replacement = new ProbeProvider(quote with { ChaosValue = 13m }, status);
var competitor = new ProbeProvider(quote, status);
var firstOwner = new object();
var equalButDistinctOwner = new EqualOwner();
var otherEqualButDistinctOwner = new EqualOwner();

if (PriceProviderRegistry.Current is not null)
    throw new InvalidOperationException("registry did not start empty");
if (!PriceProviderRegistry.TryRegister(firstOwner, first) || !ReferenceEquals(PriceProviderRegistry.Current, first))
    throw new InvalidOperationException("first provider registration failed");
if (PriceProviderRegistry.TryRegister(equalButDistinctOwner, competitor) || !ReferenceEquals(PriceProviderRegistry.Current, first))
    throw new InvalidOperationException("competing owner replaced the provider");
if (!PriceProviderRegistry.TryRegister(firstOwner, replacement) || !ReferenceEquals(PriceProviderRegistry.Current, replacement))
    throw new InvalidOperationException("same-owner registration did not update idempotently");
if (PriceProviderRegistry.Unregister(otherEqualButDistinctOwner) || PriceProviderRegistry.Current is null)
    throw new InvalidOperationException("unregister used value equality instead of owner identity");
if (PriceProviderRegistry.Unregister(equalButDistinctOwner) || PriceProviderRegistry.Current is null)
    throw new InvalidOperationException("non-owner unregistered the provider");
if (!PriceProviderRegistry.Unregister(firstOwner) || PriceProviderRegistry.Current is not null)
    throw new InvalidOperationException("owner could not unregister its provider");

var contenders = Enumerable.Range(0, 32)
    .Select(_ => (Owner: new object(), Provider: (IPriceProvider)new ProbeProvider(quote, status)))
    .ToArray();
var winners = contenders.AsParallel().Where(x => PriceProviderRegistry.TryRegister(x.Owner, x.Provider)).ToArray();
if (winners.Length != 1 || !ReferenceEquals(PriceProviderRegistry.Current, winners[0].Provider))
    throw new InvalidOperationException($"concurrent registration admitted {winners.Length} providers");
if (!PriceProviderRegistry.Unregister(winners[0].Owner) || PriceProviderRegistry.Current is not null)
    throw new InvalidOperationException("concurrent winner cleanup failed");

if (!first.TryGetPrice(query, out var found) || found != quote ||
    !first.TryResolveDisplayName(query, out var displayName) || displayName != query.ItemName ||
    first.IsGenericLookupName(query.ItemName) || !first.HasPriceDataForName(query.ItemName))
    throw new InvalidOperationException("IPriceProvider contract could not be consumed");
first.RequestRefresh();
if (first.RefreshRequests != 1 || first.Status != status)
    throw new InvalidOperationException("provider status/refresh contract failed");

Console.WriteLine("PASS: host price provider contracts are immutable and registry ownership is thread-safe");

sealed class ProbeProvider(PriceQuote quote, PriceProviderStatus status) : IPriceProvider
{
    public int RefreshRequests { get; private set; }
    public PriceProviderStatus Status => status;
    public bool TryGetPrice(PriceQuery query, out PriceQuote found) { found = quote; return true; }
    public bool TryResolveDisplayName(PriceQuery query, out string displayName) { displayName = query.ItemName; return true; }
    public bool IsGenericLookupName(string itemName) => false;
    public bool HasPriceDataForName(string itemName) => true;
    public void RequestRefresh() => this.RefreshRequests++;
}

sealed class EqualOwner
{
    public override bool Equals(object? obj) => obj is EqualOwner;
    public override int GetHashCode() => 0;
}
