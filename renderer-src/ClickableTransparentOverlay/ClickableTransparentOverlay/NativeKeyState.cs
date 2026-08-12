namespace ClickableTransparentOverlay
{
    using System;

    internal static class NativeKeyState
    {
        private static readonly bool[] Down = new bool[256];
        private static readonly bool[] Pressed = new bool[256];
        private static readonly bool[] Observed = new bool[256];

        internal static void Update(int virtualKey, bool down)
        {
            if ((uint)virtualKey >= Down.Length) return;
            Observed[virtualKey] = true;
            if (down && !Down[virtualKey]) Pressed[virtualKey] = true;
            Down[virtualKey] = down;
        }

        internal static bool IsDown(int virtualKey) =>
            (uint)virtualKey < Down.Length && Down[virtualKey];

        internal static bool HasPressed(int virtualKey) =>
            (uint)virtualKey < Pressed.Length && Pressed[virtualKey];

        internal static bool HasObserved(int virtualKey) =>
            (uint)virtualKey < Observed.Length && Observed[virtualKey];

        internal static bool ConsumePressed(int virtualKey)
        {
            if ((uint)virtualKey >= Pressed.Length || !Pressed[virtualKey]) return false;
            Pressed[virtualKey] = false;
            return true;
        }

        internal static void Reset()
        {
            Array.Clear(Down);
            Array.Clear(Pressed);
            Array.Clear(Observed);
        }
    }
}
