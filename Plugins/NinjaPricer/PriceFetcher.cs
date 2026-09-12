using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace NinjaPricer
{
    public class PoeNinjaPrice
    {
        public double Price { get; set; }
        public double PriceChaos { get; set; }
        public string Currency { get; set; } = "ex";
        public double? MaxVolumeRate { get; set; }
        public string MaxVolumeCurrency { get; set; } = string.Empty;
        public double? TotalChange { get; set; }
        public double? Volume { get; set; }
        public string ExchangeRateDisplay { get; set; } = string.Empty;
        public string ChangePercentDisplay { get; set; } = string.Empty;
        public string VolumeDisplay { get; set; } = string.Empty;
    }

    internal sealed class UniquePriceListing
    {
        public string Name { get; set; } = string.Empty;
        public string Text { get; set; } = string.Empty;
        public string BaseType { get; set; } = string.Empty;
        public double PriceChaos { get; set; }
        public List<string> ExplicitMods { get; set; } = new();
    }

    internal sealed class PriceCacheSnapshot
    {
        /// <summary>Schema/semantics version. A cache written by a different version is discarded
        /// (deleted + refetched) rather than trusted. Bump when the cache shape or how it's built changes.</summary>
        public int CacheVersion { get; set; }
        public int PriceSource { get; set; }
        public int? PreferredPriceSource { get; set; }
        public string League { get; set; } = string.Empty;
        public DateTime LastFetchUtc { get; set; }
        public double ChaosPerDivine { get; set; }
        public double ChaosPerExalted { get; set; }
        public Dictionary<string, double> FlatPricesChaos { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, List<UniquePriceListing>> UniqueListings { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, string> PathBasenameToItemName { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    public static class PriceFetcher
    {
        public const int MaxResponseBytes = 2 * 1024 * 1024;
        public const int MaxCacheBytes = 8 * 1024 * 1024;
        private const int MaxPages = 20;
        private const int MaxItems = 25000;
        private const int MaxKeys = 50000;
        private const int MaxStringLength = 1024;
        public const int SourcePoeNinja = 0;
        public const int SourcePoe2Scout = 1;

        // Bump whenever the cache shape or how the art->name index is built changes, so caches written
        // by an older plugin version are discarded instead of trusted. (v2: art index now built from
        // both poe.ninja + poe2scout icons; v3: canonical poe.ninja rate conversion and complete
        // provider-family coverage.)
        private const int CacheSchemaVersion = 3;

        private static readonly string[] ScoutCurrencyCategories =
        {
            "currency", "ritual", "runes", "idol", "essences", "fragments", "abyss", "breach",
            "delirium", "expedition", "incursion", "ultimatum", "vaal", "vaultkeys", "verisium",
            "uncutgems", "lineagesupportgems",
        };

        private static readonly string[] ScoutUniqueCategories =
        {
            "weapon", "armour", "accessory", "flask", "jewel", "map", "sanctum",
        };

        private static readonly string[] NinjaExchangeTypes =
        {
            // Currency must come first: its canonical Chaos/Exalted/Divine rows seed conversion
            // rates for every subsequently fetched category.
            "Currency", "Ritual", "Runes", "Idols", "Verisium", "Essences", "Fragments", "Abyss", "Breach",
            "Delirium", "Expedition", "Incursion", "Ultimatum", "Vaal", "VaultKeys", "UncutGems",
            "LineageSupportGems", "SoulCores",
        };

        private static readonly string[] NinjaStashTypes =
        {
            "UniqueArmours", "UniqueAccessories", "UniqueCharms", "UniqueWeapons", "UniqueFlasks", "UniqueJewels",
        };

        private static readonly HashSet<string> GenericLookupNames = new(StringComparer.OrdinalIgnoreCase)
        {
            "Charm", "Ring", "Belt", "Wand", "Staff", "Bow", "Spear", "Gloves", "Boots", "Helmet",
            "Shield", "Quiver", "Amulet", "Focus", "Body Armour", "Quarterstaff", "Sceptre", "Mace",
            "Map", "Idol", "Omen", "Gem", "Flask", "Currency", "Rune",
        };

        private static readonly Dictionary<string, string> DefaultPathBasenames = new(StringComparer.OrdinalIgnoreCase)
        {
            ["goldenuniquecharm"] = "Rite of Passage",
            ["silveruniquecharm"] = "The Fall of the Axe",
            ["stoneuniquecharm"] = "For Utopia",
            ["thawinguniquecharm"] = "Nascent Hope",
            ["dousinguniquecharm"] = "Beira's Anguish",
            ["topazuniquecharm"] = "Valako's Roar",
            ["staunchinguniquecharm"] = "Sanguis Heroum",
            ["groundinguniquecharm"] = "The Black Cat",
            ["rubyuniquecharm"] = "Ngamahu's Chosen",
            ["chaosuniquecharm"] = "Forsaken Bangle",
            ["antidoteuniquecharm"] = "Arakaali's Gift",
            ["sapphireuniquecharm"] = "Breath of the Mountains",
        };

        private static HttpClient Http = CreateHttpClient();

        private static readonly object Gate = new();
        private static Dictionary<string, double> flatPricesChaos = new(StringComparer.OrdinalIgnoreCase);
        private static Dictionary<string, List<UniquePriceListing>> uniqueListingsByName = new(StringComparer.OrdinalIgnoreCase);
        private static Dictionary<string, string> pathBasenameToItemName = new(StringComparer.OrdinalIgnoreCase);

        private static bool isFetching;
        private static string pluginDir = string.Empty;
        private static string cacheFilePath = string.Empty;
        private static DateTime lastFetchTime = DateTime.MinValue;
        private static string lastFetchError = string.Empty;
        private static int consecutiveFailureCount;
        private static DateTime nextRetryUtc = DateTime.MinValue;
        private static int configuredSource = SourcePoe2Scout;
        private static int activeSource = SourcePoe2Scout;
        private static int fetchingSource = SourcePoe2Scout;
        private static bool isFailingOver;
        internal static TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);
        private static string configuredLeague = "Runes of Aldur";
        private static int configuredRefreshMinutes = 5;
        private static double chaosPerDivine = 12.0;
        private static double chaosPerExalted = 0.1;
        private static long activationGeneration;
        private static CancellationTokenSource? activeCancellation;
        private static Task<bool>? activeTask;
        private static bool pendingRefresh;
        private static bool enabled;

        private sealed record FetchSettings(int Source, string League, int RefreshMinutes, string PluginDirectory, string CachePath, int PreferredSource);

        public static double DivineToExaltedRate { get; private set; } = 80.0;
        public static int LoadedItemCount { get; private set; }
        public static DateTime LastFetchUtc => lastFetchTime;
        public static bool IsFetching => isFetching;
        public static bool IsFailingOver { get { lock (Gate) return isFailingOver; } }
        public static bool IsUsingFallback { get { lock (Gate) return activeSource != configuredSource; } }
        public static string ActiveSourceName { get { lock (Gate) return SourceName(activeSource); } }
        public static string FetchingSourceName { get { lock (Gate) return SourceName(fetchingSource); } }
        private static string SourceName(int source) => source == SourcePoeNinja ? "poe.ninja" : "poe2scout";
        internal static string LastFetchError { get { lock (Gate) return lastFetchError; } }
        public static int ConsecutiveFailureCount { get { lock (Gate) return consecutiveFailureCount; } }
        public static DateTime NextRetryUtc { get { lock (Gate) return nextRetryUtc; } }
        public static bool ShouldShowFailureWarning { get { lock (Gate) return consecutiveFailureCount >= 3; } }

        public static string GetFailureWarningText(DateTime utcNow)
        {
            lock (Gate)
            {
                if (consecutiveFailureCount < 3) return string.Empty;
                var remaining = nextRetryUtc > utcNow ? nextRetryUtc - utcNow : TimeSpan.Zero;
                var retryText = remaining.TotalMinutes >= 1
                    ? $"{Math.Ceiling(remaining.TotalMinutes):0} min"
                    : $"{Math.Max(1, Math.Ceiling(remaining.TotalSeconds)):0} sec";
                var error = lastFetchError.Length <= 240 ? lastFetchError : lastFetchError[..240] + "...";
                return $"Price refresh failed {consecutiveFailureCount} consecutive times. Next retry in {retryText}.\n{error}";
            }
        }

        private static HttpClient CreateHttpClient()
        {
            var client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
            client.DefaultRequestHeaders.Add("User-Agent", "NinjaPricer-GameHelper-Plugin");
            return client;
        }

        public static void Configure(int priceSource, string league, int refreshIntervalMinutes)
        {
            lock (Gate)
            {
                var source = priceSource == SourcePoeNinja ? SourcePoeNinja : SourcePoe2Scout;
                var normalizedLeague = BoundString(string.IsNullOrWhiteSpace(league) ? "Runes of Aldur" : league.Trim());
                var refresh = Math.Clamp(refreshIntervalMinutes, 1, 120);
                var identityChanged = source != configuredSource ||
                    !string.Equals(normalizedLeague, configuredLeague, StringComparison.Ordinal);
                configuredSource = source;
                configuredLeague = normalizedLeague;
                configuredRefreshMinutes = refresh;
                if (identityChanged)
                {
                    activeSource = source;
                    isFailingOver = false;
                    activationGeneration++;
                    ClearPublishedDataLocked();
                    ResetFailureHealthLocked();
                    if (isFetching)
                    {
                        pendingRefresh = true;
                    }
                    else if (enabled && !string.IsNullOrEmpty(pluginDir))
                    {
                        StartFetchLocked();
                    }
                }
            }
        }

        public static void Initialize(string pluginDirectory)
        {
            lock (Gate)
            {
                enabled = true;
                activationGeneration++;
                pluginDir = pluginDirectory;
                cacheFilePath = Path.Combine(pluginDirectory, "price_cache.json");
            }

            if (TryLoadCacheFromDisk())
            {
                if (NeedsPathIndexRebuild() ||
                    DateTime.UtcNow - lastFetchTime >= TimeSpan.FromMinutes(configuredRefreshMinutes))
                {
                    StartFetch();
                }

                return;
            }

            StartFetch();
        }

        public static void RefreshIfNeeded()
        {
            lock (Gate)
            {
                if (!enabled || isFetching || string.IsNullOrEmpty(pluginDir)) return;
                var now = DateTime.UtcNow;
                if (consecutiveFailureCount > 0)
                {
                    if (now < nextRetryUtc) return;
                }
                else if (now - lastFetchTime < TimeSpan.FromMinutes(configuredRefreshMinutes))
                {
                    return;
                }
                StartFetchLocked();
            }
        }

        private static TimeSpan CalculateRetryDelay(int consecutiveFailures, int source, string league, int refreshMinutes)
        {
            var exponent = Math.Clamp(consecutiveFailures - 1, 0, 10);
            var capSeconds = TimeSpan.FromMinutes(Math.Clamp(refreshMinutes, 1, 30)).TotalSeconds;
            var baseSeconds = Math.Min(30.0 * Math.Pow(2, exponent), capSeconds);

            // Stable per provider/league/attempt jitter avoids synchronized retry spikes without flaky tests.
            uint hash = 2166136261;
            hash = (hash ^ (uint)source) * 16777619;
            foreach (var character in league)
                hash = (hash ^ character) * 16777619;
            hash = (hash ^ (uint)Math.Max(1, consecutiveFailures)) * 16777619;
            var jitter = 0.8 + (hash % 4001) / 10000.0;
            return TimeSpan.FromSeconds(baseSeconds * jitter);
        }

        public static void ForceRefresh(string pluginDirectory, bool ignoreCooldown = false)
        {
            lock (Gate)
            {
                if (!enabled) return;
                // Configure() already starts (or queues) the one fetch needed for a source/league change.
                // Its UI call uses ignoreCooldown=true; do not turn that same active fetch into a duplicate.
                if (isFetching)
                {
                    if (!ignoreCooldown) pendingRefresh = true;
                    return;
                }
                if (!ignoreCooldown && DateTime.UtcNow - lastFetchTime < TimeSpan.FromSeconds(30)) return;
                pluginDir = pluginDirectory;
                cacheFilePath = Path.Combine(pluginDirectory, "price_cache.json");
                StartFetchLocked();
            }
        }

        public static bool TryResolveDisplayName(string internalPathBasename, out string displayName)
        {
            lock (Gate)
            {
                return TryResolveDisplayNameCore(internalPathBasename, out displayName);
            }
        }

        private static bool TryResolveDisplayNameCore(string internalPathBasename, out string displayName)
        {
            displayName = string.Empty;
            if (string.IsNullOrWhiteSpace(internalPathBasename)) return false;

            if (DefaultPathBasenames.TryGetValue(NormalizeKey(internalPathBasename), out var defaultName))
            {
                displayName = defaultName;
                return true;
            }

            if (pathBasenameToItemName.TryGetValue(NormalizeKey(internalPathBasename), out var resolvedName) &&
                !string.IsNullOrWhiteSpace(resolvedName))
            {
                displayName = resolvedName;
                return true;
            }

            return false;
        }

        public static bool IsGenericLookupName(string? name)
        {
            if (string.IsNullOrWhiteSpace(name)) return true;
            var trimmed = name.Trim();
            if (trimmed.Length < 4) return true;
            if (GenericLookupNames.Contains(trimmed)) return true;
            if (trimmed.StartsWith("Item ", StringComparison.Ordinal)) return true;
            return false;
        }

        public static bool HasPriceDataForName(string? name)
        {
            lock (Gate)
            {
                return HasPriceDataForNameUnlocked(name);
            }
        }

        private static bool HasPriceDataForNameUnlocked(string? name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return false;
            }

            var key = NormalizeKey(name);
            if (key.Length == 0)
            {
                return false;
            }

            return flatPricesChaos.ContainsKey(key) || uniqueListingsByName.ContainsKey(key);
        }

        public static double GetDivineValue(PoeNinjaPrice price)
        {
            if (price == null) return 0;
            if (chaosPerDivine <= 0) return 0;
            return price.PriceChaos / chaosPerDivine;
        }

        public static double GetChaosPerDivine()
        {
            lock (Gate)
            {
                return chaosPerDivine;
            }
        }

        public static double GetChaosPerExalted()
        {
            lock (Gate)
            {
                return chaosPerExalted;
            }
        }

        public static (double Value, string Currency) GetDisplayPrice(PoeNinjaPrice price, int displayCurrency)
        {
            if (price == null) return (0, "divine");

            if (displayCurrency == 2)
                return (Math.Round(price.PriceChaos, 1), "chaos");

            if (displayCurrency == 1)
            {
                var ex = chaosPerExalted > 0 ? price.PriceChaos / chaosPerExalted : price.Price;
                return (Math.Round(ex, 1), "ex");
            }

            var div = chaosPerDivine > 0 ? price.PriceChaos / chaosPerDivine : price.Price;
            return (Math.Round(div, 3), "divine");
        }

        public static PoeNinjaPrice? GetPrice(
            string itemName,
            IReadOnlyList<string>? mods = null,
            string? internalPathBasename = null,
            string? fullItemPath = null,
            string? scoutText = null)
        {
            if (string.IsNullOrWhiteSpace(itemName) &&
                string.IsNullOrWhiteSpace(internalPathBasename) &&
                string.IsNullOrWhiteSpace(scoutText) &&
                (mods == null || mods.Count == 0))
            {
                return null;
            }

            lock (Gate)
            {
                foreach (var candidate in BuildNameCandidates(itemName, internalPathBasename, fullItemPath, scoutText))
                {
                    if (!HasPriceDataForNameUnlocked(candidate))
                    {
                        continue;
                    }

                    var direct = LookupPrice(candidate, mods);
                    if (direct != null) return direct;

                    if (mods != null && mods.Count > 0)
                    {
                        var byMods = LookupByModsForName(candidate, mods);
                        if (byMods != null) return byMods;
                    }
                }

                return null;
            }
        }

        private static List<string> BuildNameCandidates(
            string itemName,
            string? internalPathBasename,
            string? fullItemPath,
            string? scoutText)
        {
            var candidates = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            void add(string? value)
            {
                if (string.IsNullOrWhiteSpace(value)) return;
                var trimmed = value.Trim();
                if (IsGenericLookupName(trimmed)) return;
                if (seen.Add(trimmed)) candidates.Add(trimmed);
            }

            void addPathBasename(string? basename)
            {
                if (string.IsNullOrWhiteSpace(basename)) return;
                if (TryResolveDisplayNameCore(basename, out var mapped))
                    add(mapped);
                add(basename);
            }

            add(scoutText);
            addPathBasename(internalPathBasename);

            if (!string.IsNullOrWhiteSpace(fullItemPath))
            {
                foreach (var segment in fullItemPath.Split('/', StringSplitOptions.RemoveEmptyEntries))
                    addPathBasename(segment);
            }

            add(itemName);
            return candidates;
        }

        private static PoeNinjaPrice? LookupPrice(string itemName, IReadOnlyList<string>? mods)
        {
            if (string.IsNullOrWhiteSpace(itemName)) return null;

            var chaosFromUnique = 0.0;
            if (mods != null && mods.Count > 0 &&
                uniqueListingsByName.TryGetValue(NormalizeKey(itemName), out var listings) && listings.Count > 0)
            {
                chaosFromUnique = ResolveUniquePrice(listings, mods);
            }

            flatPricesChaos.TryGetValue(NormalizeKey(itemName), out var chaosFromFlat);

            // Scout unique rows can be far below poe.ninja / trade (e.g. cheap uniques). Prefer the higher estimate.
            var chaos = Math.Max(chaosFromUnique, chaosFromFlat);
            if (chaos > 0) return FromChaos(chaos);

            return null;
        }

        private static PoeNinjaPrice? LookupByModsForName(string itemName, IReadOnlyList<string> mods)
        {
            if (string.IsNullOrWhiteSpace(itemName) || mods == null || mods.Count == 0) return null;
            if (!uniqueListingsByName.TryGetValue(NormalizeKey(itemName), out var listings) || listings.Count == 0)
                return null;

            var best = PickBestListingByMods(listings, mods);
            if (best == null) return null;

            flatPricesChaos.TryGetValue(NormalizeKey(itemName), out var chaosFromFlat);
            var chaos = Math.Max(best.PriceChaos, chaosFromFlat);
            return chaos > 0 ? FromChaos(chaos) : null;
        }

        private static double ResolveUniquePrice(List<UniquePriceListing> listings, IReadOnlyList<string>? mods)
        {
            if (listings == null || listings.Count == 0) return 0;

            if (mods != null && mods.Count > 0)
            {
                var best = PickBestListingByMods(listings, mods);
                if (best != null)
                    return best.PriceChaos;

                return GetMedianListingPrice(listings);
            }

            return GetMedianListingPrice(listings);
        }

        private static double GetMedianListingPrice(IEnumerable<UniquePriceListing> listings)
        {
            var prices = new List<double>();
            foreach (var listing in listings)
            {
                if (listing.PriceChaos > 0)
                    prices.Add(listing.PriceChaos);
            }

            if (prices.Count == 0)
                return 0;

            prices.Sort();
            return prices[prices.Count / 2];
        }

        private static UniquePriceListing? PickBestListingByMods(
            IEnumerable<UniquePriceListing> listings,
            IReadOnlyList<string> mods)
        {
            UniquePriceListing? best = null;
            var bestScore = 0;
            foreach (var listing in listings)
            {
                var score = ScoreModMatch(mods, listing.ExplicitMods);
                if (score > bestScore)
                {
                    bestScore = score;
                    best = listing;
                }
            }

            var threshold = mods.Count >= 4 ? 2 : 3;
            return best != null && bestScore >= threshold ? best : null;
        }

        private static int ScoreModMatch(IReadOnlyList<string> itemMods, IReadOnlyList<string> listingMods)
        {
            if (itemMods == null || listingMods == null || itemMods.Count == 0 || listingMods.Count == 0)
                return 0;

            var score = 0;
            foreach (var itemMod in itemMods)
            {
                var itemNorm = NormalizeMod(itemMod);
                if (itemNorm.Length < 4) continue;

                foreach (var listingMod in listingMods)
                {
                    var listingNorm = NormalizeMod(listingMod);
                    if (listingNorm == itemNorm)
                    {
                        score += 3;
                        break;
                    }

                    if (listingNorm.Contains(itemNorm) || itemNorm.Contains(listingNorm))
                    {
                        score += 2;
                        break;
                    }

                    var itemNums = ExtractNumbers(itemMod);
                    var listNums = ExtractNumbers(listingMod);
                    if (itemNums.Count > 0 && itemNums.SequenceEqual(listNums))
                    {
                        score += 1;
                        break;
                    }
                }
            }

            return score;
        }

        private static List<int> ExtractNumbers(string text)
        {
            var nums = new List<int>();
            foreach (Match m in Regex.Matches(text ?? string.Empty, @"-?\d+"))
            {
                if (int.TryParse(m.Value, out var n)) nums.Add(n);
            }
            return nums;
        }

        private static string NormalizeMod(string mod)
        {
            if (string.IsNullOrWhiteSpace(mod)) return string.Empty;
            var s = mod.ToLowerInvariant();
            s = Regex.Replace(s, @"\s+", " ").Trim();
            return s;
        }

        private static PoeNinjaPrice? FromChaos(double chaosValue)
        {
            if (chaosValue <= 0) return null;

            var price = new PoeNinjaPrice { PriceChaos = chaosValue };

            if (chaosPerDivine > 0 && chaosValue >= chaosPerDivine)
            {
                price.Price = chaosValue / chaosPerDivine;
                price.Currency = "divine";
                return price;
            }

            if (chaosPerExalted > 0 && chaosValue >= chaosPerExalted)
            {
                price.Price = chaosValue / chaosPerExalted;
                price.Currency = "ex";
                return price;
            }

            price.Price = chaosValue;
            price.Currency = "chaos";
            return price;
        }

        private static bool NeedsPathIndexRebuild()
        {
            lock (Gate)
            {
                return pathBasenameToItemName.Count == 0 &&
                       (flatPricesChaos.Count > 0 || uniqueListingsByName.Count > 0);
            }
        }

        private static void ClearPublishedDataLocked()
        {
            flatPricesChaos = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            uniqueListingsByName = new Dictionary<string, List<UniquePriceListing>>(StringComparer.OrdinalIgnoreCase);
            pathBasenameToItemName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            lastFetchTime = DateTime.MinValue;
            LoadedItemCount = 0;
            chaosPerDivine = 12.0;
            chaosPerExalted = 0.1;
            DivineToExaltedRate = 80.0;
        }

        private static void ResetFailureHealthLocked()
        {
            lastFetchError = string.Empty;
            consecutiveFailureCount = 0;
            nextRetryUtc = DateTime.MinValue;
        }

        private static void StartFetch()
        {
            lock (Gate)
            {
                enabled = true;
                if (isFetching) { pendingRefresh = true; return; }
                StartFetchLocked();
            }
        }

        private static void StartFetchLocked()
        {
            isFetching = true;
            activeCancellation?.Dispose();
            activeCancellation = new CancellationTokenSource();
            var generation = activationGeneration;
            var settings = new FetchSettings(activeSource, configuredLeague, configuredRefreshMinutes, pluginDir, cacheFilePath, configuredSource);
            fetchingSource = settings.Source;
            isFailingOver = false;
            var token = activeCancellation.Token;
            activeTask = Task.Run(() => FetchPricesAsync(settings, generation, token));
        }

        public static void Shutdown()
        {
            lock (Gate)
            {
                enabled = false;
                pendingRefresh = false;
                activationGeneration++;
                activeCancellation?.Cancel();
            }
        }

        private static async Task<bool> FetchPricesAsync(FetchSettings settings, long generation, CancellationToken token)
        {
            var success = false;
            try
            {
                var flat = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
                var uniques = new Dictionary<string, List<UniquePriceListing>>(StringComparer.OrdinalIgnoreCase);
                var pathNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                double divChaos = chaosPerDivine;
                double exChaos = chaosPerExalted;

                async Task<RatePair> FetchProvider(FetchSettings attempt, bool enrichScout = true)
                {
                    RatePair rates;
                    if (attempt.Source == SourcePoe2Scout)
                    {
                        rates = await FetchFromScoutAsync(attempt, flat, uniques, pathNames, divChaos, exChaos, token).ConfigureAwait(false);
                        ValidateFetched(flat, uniques, pathNames, rates.DivChaos, rates.ExChaos);
                        if (!enrichScout) return rates; // Ninja already failed earlier in this refresh.
                        // Optional Ninja enrichment must not discard a complete Scout result during a Ninja outage.
                        var enriched = new Dictionary<string, double>(flat, StringComparer.OrdinalIgnoreCase);
                        var enrichedPaths = new Dictionary<string, string>(pathNames, StringComparer.OrdinalIgnoreCase);
                        try
                        {
                            var enrichedRates = await FetchNinjaStashOverviewsAsync(attempt, enriched, enrichedPaths,
                                rates.DivChaos, rates.ExChaos, token).ConfigureAwait(false);
                            ValidateFetched(enriched, uniques, enrichedPaths, enrichedRates.DivChaos, enrichedRates.ExChaos);
                            flat = enriched;
                            pathNames = enrichedPaths;
                            rates = enrichedRates;
                        }
                        catch (Exception ex) when (!token.IsCancellationRequested && IsProviderFailure(ex))
                        {
                            Console.WriteLine($"[NinjaPricer] Optional Ninja enrichment unavailable: {ex.Message}");
                        }
                    }
                    else
                    {
                        rates = await FetchFromNinjaAsync(attempt, flat, pathNames, divChaos, exChaos, token).ConfigureAwait(false);
                    }
                    ValidateFetched(flat, uniques, pathNames, rates.DivChaos, rates.ExChaos);
                    return rates;
                }

                RatePair result;
                try
                {
                    result = await FetchProvider(settings).ConfigureAwait(false);
                }
                catch (Exception ex) when (!token.IsCancellationRequested && IsProviderFailure(ex))
                {
                    lock (Gate)
                    {
                        if (!enabled || generation != activationGeneration) return false;
                        settings = settings with { Source = settings.Source == SourcePoeNinja ? SourcePoe2Scout : SourcePoeNinja };
                        fetchingSource = settings.Source;
                        isFailingOver = true;
                    }
                    Console.WriteLine($"[NinjaPricer] Switching to {SourceName(settings.Source)}: {ex.Message}");
                    // Never mix an incomplete failed provider with the fallback result.
                    flat.Clear();
                    uniques.Clear();
                    pathNames.Clear();
                    result = await FetchProvider(settings, enrichScout: false).ConfigureAwait(false);
                }
                divChaos = result.DivChaos;
                exChaos = result.ExChaos;

                var fetchedAt = DateTime.UtcNow;
                lock (Gate)
                {
                    token.ThrowIfCancellationRequested();
                    if (!enabled || generation != activationGeneration) return false;
                    ValidateFetched(flat, uniques, pathNames, divChaos, exChaos);
                    if (!SaveCacheToDisk(settings, generation, token, flat, uniques, pathNames, divChaos, exChaos, fetchedAt)) return false;
                    flatPricesChaos = flat;
                    uniqueListingsByName = uniques;
                    pathBasenameToItemName = pathNames;
                    chaosPerDivine = divChaos > 0 ? divChaos : chaosPerDivine;
                    chaosPerExalted = exChaos > 0 ? exChaos : chaosPerExalted;
                    if (chaosPerExalted > 0)
                        DivineToExaltedRate = chaosPerDivine / chaosPerExalted;
                    LoadedItemCount = flat.Count + uniques.Values.Sum(v => v.Count);
                    lastFetchTime = fetchedAt;
                    activeSource = settings.Source;
                    ResetFailureHealthLocked();
                }
                success = true;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                // Expected lifecycle/configuration cancellation is not a provider failure.
            }
            catch (Exception ex)
            {
                lock (Gate)
                {
                    if (enabled && generation == activationGeneration)
                    {
                        consecutiveFailureCount++;
                        lastFetchError = $"{ex.GetType().Name}: {ex.Message}";
                        nextRetryUtc = DateTime.UtcNow + CalculateRetryDelay(
                            consecutiveFailureCount,
                            settings.Source,
                            settings.League,
                            settings.RefreshMinutes);
                    }
                }
                Console.WriteLine($"[NinjaPricer] Price refresh failed: {ex.GetType().Name}: {ex.Message}");
            }
            finally
            {
                lock (Gate)
                {
                    isFetching = false;
                    isFailingOver = false;
                    if (enabled && pendingRefresh)
                    {
                        pendingRefresh = false;
                        StartFetchLocked();
                    }
                }
            }
            return success;
        }

        private readonly struct RatePair
        {
            public RatePair(double divChaos, double exChaos)
            {
                DivChaos = divChaos;
                ExChaos = exChaos;
            }

            public double DivChaos { get; }
            public double ExChaos { get; }
        }

        private static async Task<RatePair> FetchFromScoutAsync(
            FetchSettings settings,
            Dictionary<string, double> flat,
            Dictionary<string, List<UniquePriceListing>> uniques,
            Dictionary<string, string> pathNames,
            double divChaos,
            double exChaos,
            CancellationToken token)
        {
            var league = Uri.EscapeDataString(settings.League);
            var rates = await UpdateScoutRatesAsync(settings, league, divChaos, exChaos, token).ConfigureAwait(false);
            divChaos = rates.DivChaos;
            exChaos = rates.ExChaos;

            foreach (var category in ScoutCurrencyCategories)
            {
                await FetchScoutCurrencyCategoryAsync(league, category, flat, pathNames, token).ConfigureAwait(false);
            }

            foreach (var category in ScoutUniqueCategories)
            {
                await FetchScoutUniqueCategoryAsync(league, category, uniques, pathNames, token).ConfigureAwait(false);
            }

            return new RatePair(divChaos, exChaos);
        }

        private static async Task<RatePair> UpdateScoutRatesAsync(FetchSettings settings, string leagueEscaped, double divChaos, double exChaos, CancellationToken token)
        {
                var json = await GetJsonAsync("https://api.poe2scout.com/poe2/Leagues", token).ConfigureAwait(false);
                var leagues = ScoutLeaguePayload.ParseLeagueArray(json, 100);

                foreach (var league in leagues)
                {
                    if (!string.Equals(league["Value"]?.ToString(), settings.League, StringComparison.OrdinalIgnoreCase))
                        continue;

                    var chaosDiv = league["ChaosDivinePrice"]?.Value<double?>() ?? 0;
                    if (IsValidPrice(chaosDiv)) divChaos = chaosDiv;

                    var divEx = league["DivinePrice"]?.Value<double?>() ?? 0;
                    if (IsValidPrice(divEx) && IsValidPrice(chaosDiv))
                        exChaos = chaosDiv / divEx;
                    break;
                }
                var url = $"https://api.poe2scout.com/poe2/Leagues/{leagueEscaped}/Currencies/ByCategory?Category=currency&ReferenceCurrency=chaos&PerPage=250&Page=1";
                json = await GetJsonAsync(url, token).ConfigureAwait(false);
                var items = ScoutLeaguePayload.RequireObjectArray(
                    JObject.Parse(json)["Items"],
                    250,
                    "currency items");
                if (items.Count > 0)
                {
                    foreach (var item in items)
                    {
                        var text = item["Text"]?.ToString();
                        var price = item["CurrentPrice"]?.Value<double?>() ?? 0;
                        if (!IsBounded(text) || !IsValidPrice(price)) continue;

                        if (text!.Contains("Divine Orb", StringComparison.OrdinalIgnoreCase))
                            divChaos = price;
                        if (text.Contains("Exalted Orb", StringComparison.OrdinalIgnoreCase))
                            exChaos = price;
                    }
                }
            return new RatePair(divChaos, exChaos);
        }

        private static async Task FetchScoutCurrencyCategoryAsync(
            string leagueEscaped,
            string category,
            Dictionary<string, double> flat,
            Dictionary<string, string> pathNames,
            CancellationToken token)
        {
            var page = 1;
            var pages = 1;
            while (page <= pages)
            {
                token.ThrowIfCancellationRequested();
                    var url = $"https://api.poe2scout.com/poe2/Leagues/{leagueEscaped}/Currencies/ByCategory?Category={category}&ReferenceCurrency=chaos&PerPage=250&Page={page}";
                    var json = await GetJsonAsync(url, token).ConfigureAwait(false);
                    var data = JObject.Parse(json);
                    pages = data["Pages"]?.Value<int?>() ?? 1;
                    if (pages < 0 || pages > MaxPages) throw new InvalidDataException("Invalid page count.");

                    var items = ScoutLeaguePayload.RequireObjectArray(data["Items"], 250, "currency items");
                    if (pages == 0)
                    {
                        if (page != 1 || items.Count != 0)
                            throw new InvalidDataException("Contradictory zero-page category response.");
                        break;
                    }

                    foreach (var item in items)
                    {
                        var price = item["CurrentPrice"]?.Value<double?>() ?? 0;
                        if (!IsValidPrice(price)) continue;

                        var text = item["Text"]?.ToString();
                        AddFlatPrice(flat, text, price);
                        AddFlatPrice(flat, item["ApiId"]?.ToString(), price);
                        AddFlatPrice(flat, item["ItemMetadata"]?["name"]?.ToString(), price);
                        AddFlatPrice(flat, item["ItemMetadata"]?["base_type"]?.ToString(), price);
                        IndexPathName(pathNames, item["ApiId"]?.ToString(), text);
                        IndexPathName(pathNames, ExtractIconBasename(item["IconUrl"]?.ToString()), text);
                    }
                page++;
            }
        }

        private static async Task FetchScoutUniqueCategoryAsync(
            string leagueEscaped,
            string category,
            Dictionary<string, List<UniquePriceListing>> uniques,
            Dictionary<string, string> pathNames,
            CancellationToken token)
        {
            var page = 1;
            var pages = 1;
            while (page <= pages)
            {
                token.ThrowIfCancellationRequested();
                    var url = $"https://api.poe2scout.com/poe2/Leagues/{leagueEscaped}/Uniques/ByCategory?Category={category}&ReferenceCurrency=chaos&PerPage=250&Page={page}";
                    var json = await GetJsonAsync(url, token).ConfigureAwait(false);
                    var data = JObject.Parse(json);
                    pages = data["Pages"]?.Value<int?>() ?? 1;
                    if (pages < 0 || pages > MaxPages) throw new InvalidDataException("Invalid page count.");
                    var items = ScoutLeaguePayload.RequireObjectArray(data["Items"], 250, "unique items");
                    if (pages == 0)
                    {
                        if (page != 1 || items.Count != 0)
                            throw new InvalidDataException("Contradictory zero-page category response.");
                        break;
                    }

                    foreach (var item in items)
                    {
                        var price = item["CurrentPrice"]?.Value<double?>() ?? 0;
                        if (!IsValidPrice(price)) continue;

                        var listing = new UniquePriceListing
                        {
                            Name = item["Name"]?.ToString() ?? string.Empty,
                            Text = item["Text"]?.ToString() ?? string.Empty,
                            BaseType = item["Type"]?.ToString() ?? item["ItemMetadata"]?["base_type"]?.ToString() ?? string.Empty,
                            PriceChaos = price,
                            ExplicitMods = CombineModLists(
                                ParseScoutModifiers(item["ItemMetadata"]?["implicit_mods"]),
                                ParseScoutModifiers(item["ItemMetadata"]?["explicit_mods"])),
                        };

                        AddUniqueListing(uniques, listing);
                        IndexPathName(pathNames, ExtractIconBasename(item["IconUrl"]?.ToString()), listing.Name);
                        IndexPathName(pathNames, listing.Name, listing.Name);
                    }
                page++;
            }
        }

        private static List<string> CombineModLists(IReadOnlyList<string>? first, IReadOnlyList<string>? second)
        {
            var mods = new List<string>();
            if ((first?.Count ?? 0) + (second?.Count ?? 0) > 100) throw new InvalidDataException("Too many modifiers.");
            if (first != null) mods.AddRange(first.Where(IsBounded));
            if (second != null) mods.AddRange(second.Where(IsBounded));
            return mods;
        }

        private static IReadOnlyList<string> ParseScoutModifiers(JToken? token)
        {
            if (token == null || token.Type == JTokenType.Null) return Array.Empty<string>();
            if (token is not JArray values) throw new InvalidDataException("Invalid modifier list.");
            if (values.Count > 100) throw new InvalidDataException("Too many modifiers.");

            var modifiers = new List<string>(values.Count);
            foreach (var value in values)
            {
                var description = value.Type == JTokenType.String
                    ? value.ToString()
                    : value is JObject obj && obj["description"]?.Type == JTokenType.String
                        ? obj["description"]!.ToString()
                        : null;
                if (IsBounded(description)) modifiers.Add(description!);
            }
            return modifiers;
        }

        private static void IndexPathName(Dictionary<string, string> pathNames, string? pathBasename, string? displayName)
        {
            if (!IsBounded(pathBasename) || !IsBounded(displayName)) return;
            var key = NormalizeKey(pathBasename!);
            if (!pathNames.ContainsKey(key) && pathNames.Count >= MaxKeys) throw new InvalidDataException("Too many path keys.");
            pathNames[key] = displayName!.Trim();
        }

        private static string ExtractIconBasename(string? iconUrl)
        {
            if (string.IsNullOrWhiteSpace(iconUrl)) return string.Empty;

            var withoutQuery = iconUrl.Split('?')[0];
            var file = withoutQuery.Split('/').LastOrDefault();
            if (string.IsNullOrWhiteSpace(file)) return string.Empty;

            var dot = file.LastIndexOf('.');
            return dot > 0 ? file[..dot] : file;
        }

        private static void AddFlatPrice(Dictionary<string, double> flat, string? key, double price)
        {
            if (!IsBounded(key) || !IsValidPrice(price)) return;
            var norm = NormalizeKey(key!);
            if (norm.Length == 0 || norm.Length > MaxStringLength) return;
            if (!flat.ContainsKey(norm) && flat.Count >= MaxKeys) throw new InvalidDataException("Too many price keys.");
            if (!flat.ContainsKey(norm) || flat[norm] < price)
                flat[norm] = price;
        }

        private static void AddUniqueListing(Dictionary<string, List<UniquePriceListing>> uniques, UniquePriceListing listing)
        {
            if (!IsBounded(listing.Name) || !IsValidPrice(listing.PriceChaos)) return;
            if (uniques.Values.Sum(x => x.Count) >= MaxItems) throw new InvalidDataException("Too many unique listings.");

            void add(string key)
            {
                if (string.IsNullOrWhiteSpace(key)) return;
                var norm = NormalizeKey(key);
                if (!uniques.TryGetValue(norm, out var list))
                {
                    if (uniques.Count >= MaxKeys) throw new InvalidDataException("Too many unique keys.");
                    list = new List<UniquePriceListing>();
                    uniques[norm] = list;
                }
                list.Add(listing);
            }

            add(listing.Name);
            add(listing.Text);
            if (!string.IsNullOrWhiteSpace(listing.BaseType))
                add($"{listing.Name} {listing.BaseType}");
        }

        private static async Task<RatePair> FetchFromNinjaAsync(FetchSettings settings, Dictionary<string, double> flat, Dictionary<string, string> pathNames, double divChaos, double exChaos, CancellationToken token)
        {
            var leagueParam = Uri.EscapeDataString(settings.League).Replace("%20", "+");

            foreach (var type in NinjaExchangeTypes)
            {
                var url = $"https://poe.ninja/poe2/api/economy/exchange/current/overview?league={leagueParam}&type={type}";
                var rates = await FetchNinjaExchangeApi(url, flat, pathNames, divChaos, exChaos, token).ConfigureAwait(false);
                divChaos = rates.DivChaos;
                exChaos = rates.ExChaos;
            }

            return await FetchNinjaStashOverviewsAsync(settings, flat, pathNames, divChaos, exChaos, token).ConfigureAwait(false);
        }

        private static async Task<RatePair> FetchNinjaStashOverviewsAsync(
            FetchSettings settings,
            Dictionary<string, double> flat,
            Dictionary<string, string> pathNames,
            double divChaos,
            double exChaos,
            CancellationToken token)
        {
            var leagueParam = Uri.EscapeDataString(settings.League).Replace("%20", "+");

            foreach (var type in NinjaStashTypes)
            {
                var url = $"https://poe.ninja/poe2/api/economy/stash/current/item/overview?league={leagueParam}&type={type}";
                exChaos = await FetchNinjaStashApi(url, flat, pathNames, divChaos, exChaos, token).ConfigureAwait(false);
            }

            return new RatePair(divChaos, exChaos);
        }

        private static async Task<RatePair> FetchNinjaExchangeApi(string url, Dictionary<string, double> flat, Dictionary<string, string> pathNames, double divChaos, double exChaos, CancellationToken token)
        {
                var response = await GetJsonAsync(url, token).ConfigureAwait(false);
                var data = JObject.Parse(response);

                var primaryCurrency = data["core"]?["primary"]?.ToString() ?? "divine";
                var rateToken = data["core"]?["rates"]?["exalted"];
                if (rateToken != null)
                {
                    var r = rateToken.Value<double>();
                    if (!IsValidPrice(r)) throw new InvalidDataException("Invalid rate.");
                }

                var idToName = new Dictionary<string, string>();
                var idToIcon = new Dictionary<string, string>();
                if (data["items"] is not JArray itemsArray)
                    throw new InvalidDataException("Missing items.");
                if (itemsArray.Count > MaxItems) throw new InvalidDataException("Too many items.");
                foreach (var item in itemsArray)
                {
                    var id = item["id"]?.ToString();
                    if (!IsBounded(id)) continue;
                    var name = item["name"]?.ToString();
                    if (IsBounded(name)) idToName[id!] = name!;
                    var icon = item["image"]?.ToString() ?? item["icon"]?.ToString();
                    if (IsBounded(icon)) idToIcon[id!] = icon!;
                }

                if (data["lines"] is not JArray lines)
                    throw new InvalidDataException("Missing lines.");
                if (lines.Count > MaxItems) throw new InvalidDataException("Too many lines.");

                // primaryValue is denominated in the response's primary currency. Establish
                // primary-to-chaos from the exact Chaos Orb row before converting any rows;
                // carrying the preceding refresh's rate compounds every subsequent refresh.
                if (!primaryCurrency.Equals("chaos", StringComparison.OrdinalIgnoreCase))
                {
                    var chaosPrimaryValue = 0.0;
                    foreach (var line in lines)
                    {
                        var id = line["id"]?.ToString();
                        if (id == null || !idToName.TryGetValue(id, out var rowName) ||
                            !rowName.Equals("Chaos Orb", StringComparison.OrdinalIgnoreCase)) continue;
                        chaosPrimaryValue = line["primaryValue"]?.Value<double>() ?? 0.0;
                        break;
                    }

                    if (IsValidPrice(chaosPrimaryValue))
                    {
                        if (primaryCurrency.Equals("exalted", StringComparison.OrdinalIgnoreCase))
                            exChaos = 1.0 / chaosPrimaryValue;
                        else if (primaryCurrency.Equals("divine", StringComparison.OrdinalIgnoreCase))
                            divChaos = 1.0 / chaosPrimaryValue;
                    }
                }

                foreach (var line in lines)
                {
                    var id = line["id"]?.ToString();
                    if (id == null || !idToName.TryGetValue(id, out var name)) continue;

                    var pval = line["primaryValue"]?.Value<double>() ?? 0.0;
                    if (!IsValidPrice(pval)) continue;

                    var chaos = PrimaryValueToChaos(pval, primaryCurrency, divChaos, exChaos);
                    AddFlatPrice(flat, name, chaos);
                    if (idToIcon.TryGetValue(id, out var iconUrl))
                        IndexPathName(pathNames, ExtractIconBasename(iconUrl), name);

                    if (name.Equals("Divine Orb", StringComparison.OrdinalIgnoreCase))
                        divChaos = chaos;
                    if (name.Equals("Exalted Orb", StringComparison.OrdinalIgnoreCase))
                        exChaos = chaos;
                }
            return new RatePair(divChaos, exChaos);
        }

        private static async Task<double> FetchNinjaStashApi(string url, Dictionary<string, double> flat, Dictionary<string, string> pathNames, double divChaos, double exChaos, CancellationToken token)
        {
                var response = await GetJsonAsync(url, token).ConfigureAwait(false);
                var data = JObject.Parse(response);

                var primaryCurrency = data["core"]?["primary"]?.ToString() ?? "exalted";
                var rateToken = data["core"]?["rates"]?["exalted"];
                if (rateToken != null)
                {
                    var r = rateToken.Value<double>();
                    if (!IsValidPrice(r)) throw new InvalidDataException("Invalid rate.");
                }

                if (data["lines"] is not JArray lines)
                    throw new InvalidDataException("Missing lines.");
                if (lines.Count > MaxItems) throw new InvalidDataException("Too many lines.");
                foreach (var line in lines)
                {
                    var name = line["name"]?.ToString();
                    var baseType = line["baseType"]?.ToString() ?? string.Empty;
                    var pval = line["primaryValue"]?.Value<double>() ?? 0.0;
                    if (!IsBounded(name) || (!string.IsNullOrEmpty(baseType) && !IsBounded(baseType)) || !IsValidPrice(pval)) continue;

                    var chaos = PrimaryValueToChaos(pval, primaryCurrency, divChaos, exChaos);
                    var cacheKey = BuildStashCacheKey(name!, baseType);
                    AddFlatPrice(flat, cacheKey, chaos);
                    var icon = line["icon"]?.ToString() ?? line["image"]?.ToString();
                    IndexPathName(pathNames, ExtractIconBasename(icon), name);
                }
            return exChaos;
        }

        private static double PrimaryValueToChaos(double value, string primaryCurrency, double divChaos, double exChaos)
        {
            if (primaryCurrency.Equals("divine", StringComparison.OrdinalIgnoreCase))
                return value * (divChaos > 0 ? divChaos : 1.0);

            return value * (exChaos > 0 ? exChaos : 0.1);
        }

        private static string BuildStashCacheKey(string name, string baseType)
        {
            if (baseType.Contains("Runeforged", StringComparison.OrdinalIgnoreCase))
                return $"{name} Runeforged";

            if (baseType.Contains("Runemastered", StringComparison.OrdinalIgnoreCase))
                return $"{name} Runemastered";

            return name;
        }

        private static string NormalizeKey(string key)
        {
            if (string.IsNullOrWhiteSpace(key)) return string.Empty;
            return Regex.Replace(key.Trim().ToLowerInvariant(), @"[^a-z0-9]+", "");
        }

        private static bool TryLoadCacheFromDisk()
        {
            if (string.IsNullOrEmpty(cacheFilePath) || !File.Exists(cacheFilePath)) return false;

            try
            {
                var snapshot = JsonConvert.DeserializeObject<PriceCacheSnapshot>(ReadBoundedCacheFile(cacheFilePath));

                // Written by a different (older) plugin version, or missing required data: discard it
                // entirely so we never trust a stale-schema cache. A fresh fetch rebuilds it.
                if (snapshot == null || snapshot.CacheVersion != CacheSchemaVersion || snapshot.FlatPricesChaos == null)
                {
                    return false;
                }

                // Valid cache, but for a different source/league than currently selected — leave the file
                // (the next fetch overwrites it) and just fall through to refetch.
                if ((snapshot.PreferredPriceSource ?? snapshot.PriceSource) != configuredSource) return false;
                if (snapshot.PriceSource is not (SourcePoeNinja or SourcePoe2Scout)) return false;
                if (!string.Equals(snapshot.League, configuredLeague, StringComparison.OrdinalIgnoreCase)) return false;
                ValidateCacheSnapshot(snapshot);

                lock (Gate)
                {
                    flatPricesChaos = new Dictionary<string, double>(snapshot.FlatPricesChaos, StringComparer.OrdinalIgnoreCase);
                    uniqueListingsByName = snapshot.UniqueListings != null
                        ? new Dictionary<string, List<UniquePriceListing>>(snapshot.UniqueListings, StringComparer.OrdinalIgnoreCase)
                        : new Dictionary<string, List<UniquePriceListing>>(StringComparer.OrdinalIgnoreCase);
                    pathBasenameToItemName = snapshot.PathBasenameToItemName != null
                        ? new Dictionary<string, string>(snapshot.PathBasenameToItemName, StringComparer.OrdinalIgnoreCase)
                        : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    chaosPerDivine = snapshot.ChaosPerDivine > 0 ? snapshot.ChaosPerDivine : chaosPerDivine;
                    chaosPerExalted = snapshot.ChaosPerExalted > 0 ? snapshot.ChaosPerExalted : chaosPerExalted;
                    if (chaosPerExalted > 0)
                        DivineToExaltedRate = chaosPerDivine / chaosPerExalted;
                    lastFetchTime = snapshot.LastFetchUtc;
                    activeSource = snapshot.PriceSource;
                    LoadedItemCount = flatPricesChaos.Count + uniqueListingsByName.Values.Sum(v => v.Count);
                }

                return true;
            }
            catch
            {
                return false;
            }
        }

        private static string ReadBoundedCacheFile(string path)
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length > MaxCacheBytes) throw new InvalidDataException("Cache exceeds size limit.");
            using var buffer = new MemoryStream(Math.Min(MaxCacheBytes, 81920));
            var chunk = new byte[81920];
            while (true)
            {
                var read = stream.Read(chunk, 0, chunk.Length);
                if (read == 0) break;
                if (buffer.Length + read > MaxCacheBytes) throw new InvalidDataException("Cache exceeds size limit.");
                buffer.Write(chunk, 0, read);
            }
            return System.Text.Encoding.UTF8.GetString(buffer.GetBuffer(), 0, checked((int)buffer.Length));
        }

        private static bool SaveCacheToDisk(FetchSettings settings, long generation, CancellationToken token,
            Dictionary<string, double> flat, Dictionary<string, List<UniquePriceListing>> uniques,
            Dictionary<string, string> paths, double div, double ex, DateTime fetchedAt)
        {
            if (string.IsNullOrEmpty(settings.CachePath)) return false;

            var snapshot = new PriceCacheSnapshot
            {
                CacheVersion = CacheSchemaVersion, PriceSource = settings.Source, PreferredPriceSource = settings.PreferredSource, League = settings.League,
                LastFetchUtc = fetchedAt, ChaosPerDivine = div, ChaosPerExalted = ex,
                FlatPricesChaos = new(flat, StringComparer.OrdinalIgnoreCase),
                UniqueListings = new(uniques, StringComparer.OrdinalIgnoreCase),
                PathBasenameToItemName = new(paths, StringComparer.OrdinalIgnoreCase),
            };

            token.ThrowIfCancellationRequested();
            lock (Gate) if (!enabled || generation != activationGeneration) return false;
            var json = JsonConvert.SerializeObject(snapshot, Formatting.Indented);
            if (System.Text.Encoding.UTF8.GetByteCount(json) > MaxCacheBytes) throw new InvalidDataException("Cache exceeds size limit.");
            Directory.CreateDirectory(Path.GetDirectoryName(settings.CachePath) ?? settings.PluginDirectory);
            var temp = settings.CachePath + ".tmp-" + Guid.NewGuid().ToString("N");
            try
            {
                File.WriteAllText(temp, json);
                token.ThrowIfCancellationRequested();
                lock (Gate) if (!enabled || generation != activationGeneration) return false;
                File.Move(temp, settings.CachePath, true);
                return true;
            }
            finally { try { if (File.Exists(temp)) File.Delete(temp); } catch { } }
        }

        private static bool IsProviderFailure(Exception ex) =>
            ex is HttpRequestException or TimeoutException or InvalidDataException or JsonException;

        private static async Task<string> GetJsonAsync(string url, CancellationToken token)
        {
            // Two consecutive failures at an endpoint trigger the provider fallback. Each attempt
            // bounds both headers and body; shutdown/configuration cancellation never triggers fallback.
            for (int attempt = 0; ; attempt++)
            {
                token.ThrowIfCancellationRequested();
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
                deadline.CancelAfter(RequestTimeout);
                try
                {
                    return await BoundedHttp.GetStringAsync(Http, url, MaxResponseBytes, deadline.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException ex) when (!token.IsCancellationRequested)
                {
                    if (attempt >= 1) throw new TimeoutException($"{new Uri(url).Host} request timed out twice after {RequestTimeout.TotalSeconds:0}s each.", ex);
                }
                catch (HttpRequestException) when (attempt == 0 && !token.IsCancellationRequested)
                {
                    // Retry once; the next failure is handled by the single provider fallback.
                }
            }
        }

        private static bool IsValidPrice(double value) => value > 0 && double.IsFinite(value);
        private static bool IsBounded(string? value) => !string.IsNullOrWhiteSpace(value) && value.Length <= MaxStringLength;
        private static string BoundString(string value) => value.Length <= MaxStringLength ? value : value[..MaxStringLength];

        private static void ValidateFetched(Dictionary<string, double> flat, Dictionary<string, List<UniquePriceListing>> uniques, Dictionary<string, string> paths, double div, double ex)
        {
            if (flat.Count == 0 && uniques.Count == 0) throw new InvalidDataException("Source returned no price data.");
            if (flat.Count > MaxKeys || uniques.Count > MaxKeys || paths.Count > MaxKeys) throw new InvalidDataException("Excessive collection.");
            if (!IsValidPrice(div) || !IsValidPrice(ex) || flat.Any(x => !IsBounded(x.Key) || !IsValidPrice(x.Value)))
                throw new InvalidDataException("Invalid price data.");
        }

        private static void ValidateCacheSnapshot(PriceCacheSnapshot snapshot)
        {
            if (!IsBounded(snapshot.League) || snapshot.FlatPricesChaos.Count > MaxKeys ||
                (snapshot.UniqueListings?.Count ?? 0) > MaxKeys || (snapshot.PathBasenameToItemName?.Count ?? 0) > MaxKeys ||
                !IsValidPrice(snapshot.ChaosPerDivine) || !IsValidPrice(snapshot.ChaosPerExalted) ||
                snapshot.FlatPricesChaos.Any(x => !IsBounded(x.Key) || !IsValidPrice(x.Value)))
                throw new InvalidDataException("Invalid cache.");
            var listingCount = snapshot.UniqueListings?.Values.Sum(x => x?.Count ?? 0) ?? 0;
            if (listingCount > MaxItems) throw new InvalidDataException("Excessive cache listings.");
            if (snapshot.PathBasenameToItemName?.Any(x => !IsBounded(x.Key) || !IsBounded(x.Value)) == true ||
                snapshot.UniqueListings?.Any(x => !IsBounded(x.Key) || x.Value == null || x.Value.Any(v =>
                    v == null || !IsBounded(v.Name) || (!string.IsNullOrEmpty(v.Text) && !IsBounded(v.Text)) ||
                    (!string.IsNullOrEmpty(v.BaseType) && !IsBounded(v.BaseType)) || !IsValidPrice(v.PriceChaos) ||
                    v.ExplicitMods == null || v.ExplicitMods.Count > 100 || v.ExplicitMods.Any(m => !IsBounded(m)))) == true)
                throw new InvalidDataException("Invalid cache strings or listings.");
        }

        internal static IReadOnlyList<string> ParseScoutModifiersForTests(JToken? token)
            => ParseScoutModifiers(token);

        internal static TimeSpan CalculateRetryDelayForTests(int consecutiveFailures, int source, string league, int refreshMinutes)
            => CalculateRetryDelay(consecutiveFailures, source, league, refreshMinutes);

        internal static async Task<string> ReadBoundedJsonForTests(HttpMessageHandler handler, CancellationToken token)
        {
            using var client = new HttpClient(handler);
            return await BoundedHttp.GetStringAsync(client, "https://test.invalid/", MaxResponseBytes, token);
        }

        internal static void ResetForTests(HttpMessageHandler handler)
        {
            Shutdown();
            lock (Gate)
            {
                Http.Dispose(); Http = new HttpClient(handler);
                RequestTimeout = TimeSpan.FromSeconds(10);
                activeSource = configuredSource; fetchingSource = configuredSource; isFailingOver = false;
                flatPricesChaos = new(StringComparer.OrdinalIgnoreCase); uniqueListingsByName = new(StringComparer.OrdinalIgnoreCase); pathBasenameToItemName = new(StringComparer.OrdinalIgnoreCase);
                lastFetchTime = DateTime.MinValue; ResetFailureHealthLocked();
                LoadedItemCount = 0; chaosPerDivine = 12; chaosPerExalted = .1;
                enabled = true; pendingRefresh = false; isFetching = false; activeTask = null; activationGeneration++;
                pluginDir = string.Empty; cacheFilePath = string.Empty;
            }
        }

        internal static Task<bool> RunFetchForTests(string directory)
        {
            lock (Gate)
            {
                pluginDir = directory; cacheFilePath = Path.Combine(directory, "price_cache.json");
                if (isFetching) { pendingRefresh = true; return activeTask!; }
                StartFetchLocked(); return activeTask!;
            }
        }

        internal static bool TryLoadCacheForTests(string path)
        {
            lock (Gate) cacheFilePath = path;
            return TryLoadCacheFromDisk();
        }

        internal static async Task WaitForIdleForTests()
        {
            while (true)
            {
                Task? task;
                lock (Gate) { if (!isFetching) return; task = activeTask; }
                if (task != null) await task.ConfigureAwait(false);
            }
        }
    }
}
