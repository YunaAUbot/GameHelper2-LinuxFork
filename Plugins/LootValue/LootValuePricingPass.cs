// <copyright file="LootValuePricingPass.cs" company="None">
// Copyright (c) None. All rights reserved.
// </copyright>

namespace LootValue
{
    using System;
    using System.Globalization;
    using GameHelper.Plugin.Price;

    /// <summary>Immutable provider snapshot used for one LootValue draw/pricing pass.</summary>
    internal sealed class LootValuePricingPass
    {
        private readonly IPriceProvider? provider;

        private LootValuePricingPass(IPriceProvider? provider)
        {
            this.provider = provider;
        }

        public static LootValuePricingPass Capture(Func<IPriceProvider?> getProvider)
        {
            ArgumentNullException.ThrowIfNull(getProvider);
            return new LootValuePricingPass(getProvider());
        }

        public bool TryPrice(PriceQuery query, long stack, int displayCurrency, out LootValuePrice result)
        {
            result = default;
            if (this.provider == null || stack <= 0) return false;

            try
            {
                if (!this.provider.TryGetPrice(query, out var quote) || quote == null || !IsValid(quote)) return false;

                var chaos = checked(quote.ChaosValue * stack);
                var divine = checked(quote.DivineValue * stack);
                var exalted = checked(quote.ExaltedValue * stack);
                var (displayValue, currency) = displayCurrency switch
                {
                    2 => (decimal.Round(chaos, 1), "chaos"),
                    1 => (decimal.Round(exalted, 1), "ex"),
                    _ => (decimal.Round(divine, 3), "divine"),
                };

                result = new LootValuePrice(exalted, displayValue, FormatValue(displayValue, currency));
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        public bool TryResolveDisplayName(string lookupKey, out string displayName)
        {
            displayName = string.Empty;
            if (this.provider == null || string.IsNullOrWhiteSpace(lookupKey)) return false;

            try
            {
                var query = new PriceQuery(
                    lookupKey,
                    Array.Empty<string>(),
                    lookupKey,
                    string.Empty,
                    lookupKey);
                return this.provider.TryResolveDisplayName(query, out displayName) && !string.IsNullOrWhiteSpace(displayName);
            }
            catch (Exception)
            {
                displayName = string.Empty;
                return false;
            }
        }

        public bool IsGenericLookupName(string itemName)
        {
            try
            {
                return this.provider?.IsGenericLookupName(itemName) ?? true;
            }
            catch (Exception)
            {
                return true;
            }
        }

        public bool HasPriceDataForName(string itemName)
        {
            try
            {
                return this.provider?.HasPriceDataForName(itemName) ?? false;
            }
            catch (Exception)
            {
                return false;
            }
        }

        public bool TryGetStatus(out PriceProviderStatus status)
        {
            status = default!;
            if (this.provider == null) return false;

            try
            {
                status = this.provider.Status;
                return status != null;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static bool IsValid(PriceQuote quote) =>
            quote.ChaosValue > 0m && quote.DivineValue > 0m && quote.ExaltedValue > 0m &&
            !string.IsNullOrWhiteSpace(quote.SourceName);

        private static string FormatValue(decimal value, string currency) => currency switch
        {
            "divine" => value.ToString("0.00", CultureInfo.InvariantCulture) + " div",
            "chaos" => value.ToString("0.#", CultureInfo.InvariantCulture) + " c",
            _ => value.ToString("0.#", CultureInfo.InvariantCulture) + " ex",
        };
    }

    internal readonly record struct LootValuePrice(decimal ExaltedValue, decimal DisplayValue, string Text);
}
