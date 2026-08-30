namespace GameHelper.Utils
{
    /// <summary>
    /// Classifies process-monitor observations without confusing transient window loss with process exit.
    /// </summary>
    internal static class GameProcessLiveness
    {
        internal static bool ShouldClose(
            bool processInformationMissing,
            bool processExited,
            long mainWindowHandle,
            bool closeForcefully,
            bool tolerateMissingWindowHandle)
        {
            return processInformationMissing ||
                   processExited ||
                   closeForcefully ||
                   (mainWindowHandle <= 0 && !tolerateMissingWindowHandle);
        }
    }
}
