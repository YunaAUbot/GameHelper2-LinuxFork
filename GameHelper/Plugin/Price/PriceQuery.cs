// <copyright file="PriceQuery.cs" company="GameHelper">
// Copyright (c) GameHelper. All rights reserved.
// </copyright>

namespace GameHelper.Plugin.Price
{
    using System;
    using System.Collections.Generic;
    using System.Collections.ObjectModel;

    /// <summary>
    /// Describes an item to look up without exposing plugin-specific item types.
    /// </summary>
    public sealed class PriceQuery
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="PriceQuery"/> class.
        /// </summary>
        public PriceQuery(
            string itemName,
            IEnumerable<string> explicitMods,
            string internalPathBasename,
            string fullItemPath,
            string scoutText)
        {
            ArgumentNullException.ThrowIfNull(itemName);
            ArgumentNullException.ThrowIfNull(explicitMods);
            ArgumentNullException.ThrowIfNull(internalPathBasename);
            ArgumentNullException.ThrowIfNull(fullItemPath);
            ArgumentNullException.ThrowIfNull(scoutText);

            this.ItemName = itemName;
            this.ExplicitMods = new ReadOnlyCollection<string>([.. explicitMods]);
            this.InternalPathBasename = internalPathBasename;
            this.FullItemPath = fullItemPath;
            this.ScoutText = scoutText;
        }

        public string ItemName { get; }

        public IReadOnlyList<string> ExplicitMods { get; }

        public string InternalPathBasename { get; }

        public string FullItemPath { get; }

        public string ScoutText { get; }
    }
}
