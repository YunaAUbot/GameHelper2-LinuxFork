// <copyright file="DevBridgeSettings.cs" company="None">
// Copyright (c) None. All rights reserved.
// </copyright>

namespace DevBridge
{
    using GameHelper.Plugin;

    /// <summary>Settings for the bounded read-only bridge.</summary>
    public sealed class DevBridgeSettings : IPSettings
    {
        /// <summary>Minimum status refresh interval.</summary>
        public int StatusIntervalMilliseconds { get; set; } = 1000;
    }
}
