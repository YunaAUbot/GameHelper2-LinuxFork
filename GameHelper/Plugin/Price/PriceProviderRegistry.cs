// <copyright file="PriceProviderRegistry.cs" company="GameHelper">
// Copyright (c) GameHelper. All rights reserved.
// </copyright>

namespace GameHelper.Plugin.Price
{
    using System;

    /// <summary>
    /// Holds the single price provider shared by all plugin load contexts.
    /// </summary>
    public static class PriceProviderRegistry
    {
        private static readonly object SyncRoot = new();
        private static object? owner;
        private static IPriceProvider? current;

        public static IPriceProvider? Current
        {
            get
            {
                lock (SyncRoot)
                {
                    return current;
                }
            }
        }

        public static bool TryRegister(object providerOwner, IPriceProvider provider)
        {
            ArgumentNullException.ThrowIfNull(providerOwner);
            ArgumentNullException.ThrowIfNull(provider);

            lock (SyncRoot)
            {
                if (owner is not null && !ReferenceEquals(owner, providerOwner))
                {
                    return false;
                }

                owner = providerOwner;
                current = provider;
                return true;
            }
        }

        public static bool Unregister(object providerOwner)
        {
            ArgumentNullException.ThrowIfNull(providerOwner);

            lock (SyncRoot)
            {
                if (!ReferenceEquals(owner, providerOwner))
                {
                    return false;
                }

                owner = null;
                current = null;
                return true;
            }
        }
    }
}
