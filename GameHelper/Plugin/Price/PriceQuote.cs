// <copyright file="PriceQuote.cs" company="GameHelper">
// Copyright (c) GameHelper. All rights reserved.
// </copyright>

namespace GameHelper.Plugin.Price
{
    /// <summary>
    /// Represents a provider's immutable valuation of an item.
    /// </summary>
    public sealed record PriceQuote(
        decimal ChaosValue,
        decimal DivineValue,
        decimal ExaltedValue,
        string SourceName);
}
