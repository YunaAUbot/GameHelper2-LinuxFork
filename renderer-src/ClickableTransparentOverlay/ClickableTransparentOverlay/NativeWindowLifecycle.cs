namespace ClickableTransparentOverlay
{
    /// <summary>
    /// Separates the disposable Wine host window from the native compositor lifecycle.
    /// </summary>
    internal static class NativeWindowLifecycle
    {
        internal static bool ShouldCloseOnDestroy(bool useNativeGpu) => !useNativeGpu;
    }
}
