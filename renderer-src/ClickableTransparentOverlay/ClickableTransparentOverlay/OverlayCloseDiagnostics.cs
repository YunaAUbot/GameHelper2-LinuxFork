namespace ClickableTransparentOverlay
{
    using System;
    using System.Threading;

    /// <summary>
    /// Captures the first overlay shutdown request so lifecycle failures remain attributable.
    /// </summary>
    internal static class OverlayCloseDiagnostics
    {
        private static int captured;

        internal static string? CaptureOnce(string reason)
        {
            if (Interlocked.Exchange(ref captured, 1) != 0) return null;
            return $"reason={reason}{Environment.NewLine}stack={Environment.StackTrace}";
        }
    }
}
