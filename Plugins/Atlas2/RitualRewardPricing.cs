// <copyright file="RitualRewardPricing.cs" company="GameHelper">
// Copyright (c) GameHelper. All rights reserved.
// </copyright>

namespace Atlas2
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using GameHelper.Plugin.Price;

    /// <summary>Prices named rewards through the shared provider, without owning network requests.</summary>
    internal sealed class RitualRewardPricing
    {
        private readonly Dictionary<string, RewardPrice> prices = new(StringComparer.Ordinal);
        private IPriceProvider lastProvider;
        private DateTime nextRefresh;
        private bool wasEnabled;

        internal readonly record struct RewardPrice(decimal Weight, bool UnitOnly);

        internal bool TryGet(string label, out RewardPrice price) => this.prices.TryGetValue(label, out price);

        /// <summary>Returns true only when effective prices change; failed/missing quotes clear old values.</summary>
        internal bool Refresh(IPriceProvider provider, bool enabled, IEnumerable<string> labels, DateTime now)
        {
            if (enabled == this.wasEnabled && ReferenceEquals(provider, this.lastProvider) && now < this.nextRefresh)
                return false;
            this.wasEnabled = enabled;
            this.lastProvider = provider;
            this.nextRefresh = now.AddSeconds(5);
            var updated = new Dictionary<string, RewardPrice>(StringComparer.Ordinal);
            var quotes = new Dictionary<string, PriceQuote>(StringComparer.Ordinal);
            if (enabled && provider != null)
            {
                foreach (var label in labels)
                {
                    if (!TryMap(label, out var name, out var quantity, out var unitOnly)) continue;
                    try
                    {
                        if (!quotes.TryGetValue(name, out var quote))
                        {
                            var query = new PriceQuery(name, Array.Empty<string>(), string.Empty, string.Empty, name);
                            if (!provider.TryGetPrice(query, out quote)) quote = null;
                            quotes[name] = quote;
                        }

                        if (quote == null || quote.ExaltedValue <= 0 || string.IsNullOrWhiteSpace(quote.SourceName)) continue;
                        // Reject implausible scores too large to safely sum across a route.
                        var weight = checked(quote.ExaltedValue * quantity);
                        if (weight <= decimal.MaxValue / 1000000m)
                            updated[label] = new RewardPrice(weight, unitOnly);
                    }
                    catch (Exception)
                    {
                        // A stopped/reloading provider must never break Atlas rendering.
                        quotes[name] = null;
                    }
                }
            }

            bool changed = updated.Count != this.prices.Count;
            foreach (var pair in updated)
                if (!this.prices.TryGetValue(pair.Key, out var old) || old != pair.Value) changed = true;
            this.prices.Clear();
            foreach (var pair in updated) this.prices.Add(pair.Key, pair.Value);
            return changed;
        }

        internal decimal GetWeight(string label, IReadOnlyDictionary<string, int> manual) =>
            label == null ? 0m : this.prices.TryGetValue(label, out var price) ? price.Weight :
            manual.TryGetValue(label, out var weight) ? weight : 0m;

        /// <summary>Maps planner labels, preserving explicit quantities and marking unnamed stack sizes.</summary>
        internal static bool TryMap(string label, out string name, out int quantity, out bool unitOnly)
        {
            name = string.Empty;
            quantity = 1;
            unitOnly = false;
            if (string.IsNullOrWhiteSpace(label)) return false;
            if (label.StartsWith("Omen: ", StringComparison.Ordinal))
            {
                name = "Omen of " + label[6..];
                return true;
            }

            if (label is "Mageblood" or "Headhunter" or "Kalandra's Touch" or "Queen of the Forest" or
                "Alpha's Howl" or "Defiance of Destiny" or "Yoke of Suffering" or "Astramentis" or
                "Dream Fragments" or "Original Sin")
            {
                name = label;
                return true;
            }

            int separator = label.LastIndexOf(" x", StringComparison.Ordinal);
            var currency = label;
            if (separator >= 0)
            {
                if (!int.TryParse(label[(separator + 2)..], NumberStyles.None, CultureInfo.InvariantCulture, out quantity) || quantity <= 0)
                    return false;
                currency = label[..separator];
            }
            name = currency.Replace("Orbs", "Orb", StringComparison.Ordinal);
            if (name is not ("Divine Orb" or "Exalted Orb" or "Chaos Orb" or "Orb of Annulment" or "Orb of Chance" or
                "Greater Orb of Augmentation" or "Greater Orb of Transmutation" or "Greater Chaos Orb" or
                "Greater Regal Orb" or "Greater Exalted Orb" or "Perfect Orb of Augmentation" or
                "Perfect Orb of Transmutation" or "Perfect Chaos Orb" or "Perfect Regal Orb" or "Perfect Exalted Orb"))
                return false;
            unitOnly = separator < 0 && currency.Contains("Orbs", StringComparison.Ordinal);
            return true;
        }
    }
}
