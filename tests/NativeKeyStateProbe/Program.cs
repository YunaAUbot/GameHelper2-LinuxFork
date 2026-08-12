using System;
using System.Reflection;
using System.Threading;
using ClickableTransparentOverlay;
using ClickableTransparentOverlay.Win32;

static MethodInfo Method(Type type, string name) =>
    type.GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic)
    ?? throw new InvalidOperationException($"NativeKeyState.{name} missing");

var assembly = typeof(Overlay).Assembly;
var state = assembly.GetType("ClickableTransparentOverlay.NativeKeyState")
    ?? throw new InvalidOperationException("NativeKeyState missing");
var reset = Method(state, "Reset");
var update = Method(state, "Update");
var isDown = Method(state, "IsDown");
var hasPressed = Method(state, "HasPressed");
var consumePressed = Method(state, "ConsumePressed");
const int f12 = 0x7B;

bool Bool(MethodInfo method, params object[] args) => (bool)(method.Invoke(null, args) ?? false);
void Update(bool down) => update.Invoke(null, new object[] { f12, down });

reset.Invoke(null, null);
Update(true);
if (!Bool(isDown, f12)) throw new InvalidOperationException("key-down state was not retained");
if (!Bool(consumePressed, f12)) throw new InvalidOperationException("press edge was not observable");
if (Bool(consumePressed, f12)) throw new InvalidOperationException("press edge was not one-shot");
Update(false);
if (Bool(isDown, f12)) throw new InvalidOperationException("key-up state was not retained");

reset.Invoke(null, null);
Update(true);
Update(false);
if (!Bool(hasPressed, f12)) throw new InvalidOperationException("quick key tap was not pending");
if (!Bool(hasPressed, f12)) throw new InvalidOperationException("observing a pending edge consumed it");
if (!Bool(consumePressed, f12)) throw new InvalidOperationException("quick key tap was lost before consumption");
if (Bool(hasPressed, f12)) throw new InvalidOperationException("consumed edge remained pending");

// A native hotkey is edge-triggered. Holding F12 beyond the timeout must not
// toggle the menu a second time; a release followed by another press must.
reset.Invoke(null, null);
_ = typeof(Utils).GetField("sw", BindingFlags.Static | BindingFlags.NonPublic)?.GetValue(null)
    ?? throw new InvalidOperationException("hotkey stopwatch missing");
Thread.Sleep(5);
Update(true);
if (!Utils.IsKeyPressedAndNotTimeout(VK.F12, 1)) throw new InvalidOperationException("initial native hotkey edge was missed");
Thread.Sleep(5);
if (Utils.IsKeyPressedAndNotTimeout(VK.F12, 1)) throw new InvalidOperationException("held native hotkey repeated after timeout");
Update(false);
Update(true);
if (!Utils.IsKeyPressedAndNotTimeout(VK.F12, 1)) throw new InvalidOperationException("second native hotkey edge was missed");

Console.WriteLine("PASS: native key state preserves down state and pending one-shot press edges");
