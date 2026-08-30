// <copyright file="PriceProviderStatus.cs" company="GameHelper">
// Copyright (c) GameHelper. All rights reserved.
// </copyright>

namespace GameHelper.Plugin.Price
{
    using System;

    /// <summary>
    /// Represents an immutable snapshot of price-provider state.
    /// </summary>
    public sealed record PriceProviderStatus(
        string ProviderName,
        string Source,
        string League,
        bool IsFetching,
        int ItemCount,
        DateTimeOffset LastFetchUtc);
}
