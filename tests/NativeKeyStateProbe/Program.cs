using System;
using System.Reflection;
using ClickableTransparentOverlay;

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

Console.WriteLine("PASS: native key state preserves down state and pending one-shot press edges");
