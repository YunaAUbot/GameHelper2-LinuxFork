namespace ClickableTransparentOverlay
{
    using System;

    /// <summary>
    /// Bounds native compositor replacement after authenticated reconnect attempts fail.
    /// </summary>
    internal static class NativeGpuRestartPolicy
    {
        internal static bool ShouldRestart(
            bool useNativeGpu,
            bool nativeGpuStarted,
            bool connected,
            bool restartAttempted,
            TimeSpan disconnectedFor) =>
            useNativeGpu && nativeGpuStarted && !connected && !restartAttempted && disconnectedFor >= TimeSpan.FromSeconds(2);
    }
}
