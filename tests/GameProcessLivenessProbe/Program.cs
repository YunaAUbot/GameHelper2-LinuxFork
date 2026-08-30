using System;
using GameHelper.Utils;

static void Expect(bool expected, bool actual, string message)
{
    if (expected != actual) throw new InvalidOperationException(message);
}

Expect(false, GameProcessLiveness.ShouldClose(false, false, 0, false, true),
    "native backend treated an alt-tab window-handle gap as game exit");
Expect(true, GameProcessLiveness.ShouldClose(false, false, 0, false, false),
    "Windows backend stopped treating a missing game window as closure");
Expect(true, GameProcessLiveness.ShouldClose(true, false, 1, false, true),
    "missing process information did not close the helper");
Expect(true, GameProcessLiveness.ShouldClose(false, true, 1, false, true),
    "exited game process did not close the helper");
Expect(true, GameProcessLiveness.ShouldClose(false, false, 1, true, true),
    "force-close no longer closes the helper");
Expect(false, GameProcessLiveness.ShouldClose(false, false, 1, false, false),
    "healthy Windows game process was classified as closed");

Console.WriteLine("PASS: native backend tolerates missing window handles while real exits still close");
