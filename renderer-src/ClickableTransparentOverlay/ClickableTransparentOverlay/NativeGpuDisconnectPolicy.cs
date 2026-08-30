namespace ClickableTransparentOverlay
{
    /// <summary>
    /// Keeps transient native transport loss from terminating the managed overlay.
    /// </summary>
    internal static class NativeGpuDisconnectPolicy
    {
        internal static bool ShouldWaitForReconnect(bool useNativeGpu, bool nativeGpuStarted, bool connected) =>
            useNativeGpu && nativeGpuStarted && !connected;
    }
}
