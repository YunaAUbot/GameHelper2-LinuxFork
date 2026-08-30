// <copyright file="IPriceProvider.cs" company="GameHelper">
// Copyright (c) GameHelper. All rights reserved.
// </copyright>

namespace GameHelper.Plugin.Price
{
    /// <summary>
    /// Provides host-wide item price lookups across plugin load contexts.
    /// </summary>
    public interface IPriceProvider
    {
        PriceProviderStatus Status { get; }

        bool TryGetPrice(PriceQuery query, out PriceQuote quote);

        bool TryResolveDisplayName(PriceQuery query, out string displayName);

        bool IsGenericLookupName(string itemName);

        bool HasPriceDataForName(string itemName);

        void RequestRefresh();
    }
}
