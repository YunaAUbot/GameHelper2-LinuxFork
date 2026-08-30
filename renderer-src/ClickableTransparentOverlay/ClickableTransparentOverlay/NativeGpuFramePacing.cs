using System;
using System.Globalization;

namespace ClickableTransparentOverlay
{
    internal static class NativeGpuFramePacing
    {
        internal static int ResolveFrameLimit(string? configuredValue, int fallback)
        {
            if (int.TryParse(configuredValue, NumberStyles.None, CultureInfo.InvariantCulture, out var configured) &&
                configured is >= 0 and <= 240)
            {
                return configured;
            }

            return fallback is >= 0 and <= 240 ? fallback : 60;
        }

        internal static int RemainingSleepMilliseconds(int framesPerSecond, double elapsedMilliseconds)
        {
            if (framesPerSecond <= 0 || !double.IsFinite(elapsedMilliseconds) || elapsedMilliseconds < 0)
            {
                return 0;
            }

            var remaining = (1000.0 / framesPerSecond) - elapsedMilliseconds;
            return remaining > 1 ? (int)remaining : 0;
        }
    }
}
