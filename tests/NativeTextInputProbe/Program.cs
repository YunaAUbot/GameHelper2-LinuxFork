// <copyright file="Program.cs" company="None">
// Copyright (c) None. All rights reserved.
// </copyright>
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Numerics;
using System.Reflection;
using System.Runtime.InteropServices;
using ImGuiNET;

// Run only under the private Xvfb created by test_linux_gpu_text_input.sh.
if (Environment.GetEnvironmentVariable("GH_TEXT_FIXTURE") != "1") throw new Exception("private display required");
var assembly = typeof(ClickableTransparentOverlay.Overlay).Assembly;
const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
Type Type(string name) => assembly.GetType("ClickableTransparentOverlay." + name)!;
object Create(string name, params object?[] values) => Activator.CreateInstance(Type(name), flags, null, values, null)!;
object? Call(object obj, string name, params object?[] values) => obj.GetType().GetMethod(name, flags)!.Invoke(obj, values);
object? Probe(string name, params object?[] values) => Type("NativeGpuProbe").GetMethod(name, flags)!.Invoke(null, values);
void Check(bool test, string detail) { if (!test) throw new Exception(detail); }
using var reservation = new TcpListener(IPAddress.Loopback, 0);
reservation.Start(); int port = ((IPEndPoint)reservation.LocalEndpoint).Port; reservation.Stop();
var token = Enumerable.Repeat((byte)0xAB,32).ToArray(); // Fixture-only public token, never a live launch token.
var heartbeat = Path.Combine(Path.GetTempPath(), "gh-text-" + Guid.NewGuid() + ".alive");
File.WriteAllText(heartbeat, $"{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()} 0 0 0 800 600\n");
var start = new ProcessStartInfo(args[0]) { UseShellExecute = false };
foreach(var value in new[] {"0","0","800","600","30",heartbeat,port.ToString(),Convert.ToHexString(token)}) start.ArgumentList.Add(value);
using var helper = Process.Start(start)!;
var display = X.XOpenDisplay(IntPtr.Zero); Check(display != IntPtr.Zero, "Xvfb unavailable");
object? transport = null;
try
{
    transport = Create("NativeGpuTransport", port, token);
    Check((bool)Call(transport,"WaitForReady",TimeSpan.FromSeconds(5))!, "authenticated readiness");
    Type("NativeGpuProbe").GetField("transport",flags)!.SetValue(null,transport);
    var renderer = Create("ImGuiRenderer", null,null,800,600,true);
    var input = Create("ImGuiInputHandler",IntPtr.Zero);
    Type("NativeKeyState").GetMethod("Update",flags)!.Invoke(null,new object[] { 0x7B, false }); // Avoid Win32 fallback before the first native hotkey.
    var io = ImGui.GetIO(); io.ConfigFlags |= ImGuiConfigFlags.NoMouseCursorChange;
    unsafe { io.NativePtr->IniFilename = null; }
    string url = ""; bool active = false; bool shown = false; Vector2 field = default;
    void Frame()
    {
        Probe("PollInput",input,renderer);
        bool mouse = (bool)Call(input,"Update",true)!;
        Call(renderer,"Update",1f/60,(Action)(() => {
            if (ClickableTransparentOverlay.Win32.Utils.IsKeyPressedAndNotTimeout(ClickableTransparentOverlay.Win32.VK.F12, 1)) shown = !shown;
            if (!shown) { active = false; return; }
            ImGui.SetNextWindowPos(Vector2.Zero); ImGui.SetNextWindowSize(new(800,600));
            ImGui.Begin("Settings");
            ImGui.InputText("Repository HTTPS URL##GitUrl",ref url,512);
            active=ImGui.IsItemActive(); field=(ImGui.GetItemRectMin()+ImGui.GetItemRectMax())/2;
            ImGui.End();
        }));
        Probe("SetInteractive",mouse);
        Probe("SetKeyboardCapture",Call(input,"WantsKeyboardCapture"));
        Probe("Present",ImGui.GetDrawData(),renderer);
        Thread.Sleep(17);
    }
    void Frames(int count=15) { for(int i=0;i<count;i++) Frame(); }
    void Key(string name, bool down)
    {
        var sym=X.XStringToKeysym(name); var code=X.XKeysymToKeycode(display,sym);
        Check(code!=0,"test key missing: "+name);
        X.XTestFakeKeyEvent(display,code,down?1:0,0); X.XSync(display,0); Frames(4);
    }
    void Tap(string name) { Key(name,true);Key(name,false); }
    X.XTestFakeMotionEvent(display,-1,100,40,0); X.XSync(display,0); Frames();
    Key("F12",true); Check(shown,"passive F12 down did not open menu");
    Frames(15); Check(shown,"held F12 toggled repeatedly");
    Key("F12",false); Check(shown,"F12 up toggled menu");
    X.XTestFakeMotionEvent(display,-1,(int)field.X-100,(int)field.Y,0); X.XSync(display,0); Frames();
    X.XTestFakeButtonEvent(display,1,1,0); X.XSync(display,0); Frames(4);
    X.XTestFakeButtonEvent(display,1,0,0); X.XSync(display,0); Frames();
    Console.WriteLine($"clicked: active={active} WantTextInput={io.WantTextInput} capture={Call(input,"WantsKeyboardCapture")}");
    Check(active,"native click did not activate URL field");
    Check((bool)Call(input,"WantsKeyboardCapture")!,"URL field did not request keyboard capture");
    Tap("a"); Tap("b"); Tap("c");
    Check(url=="abc",$"ordinary typing: expected abc, got '{url}'");
    Console.WriteLine("PASS: native click -> WantTextInput -> XGrabKeyboard -> key/text packets -> ImGui URL value abc");
    ImGui.SetClipboardText("https://github.com/example/public-fixture"); // Internal clipboard on this Linux-only probe.
    Key("Control_L",true); Tap("a"); Tap("v"); Key("Control_L",false); Frames();
    Check(url=="https://github.com/example/public-fixture", "synthetic paste failed: " + url);
    Console.WriteLine("PASS: native Ctrl+A/Ctrl+V -> ImGui clipboard callback -> exact public URL");
    Tap("BackSpace"); Check(url=="https://github.com/example/public-fixtur", "editing after paste failed");
    Key("F12",true); Check(!shown,"captured F12 did not hide menu");
    Frames(); Key("F12",false); Frames();
    Check(!(bool)Call(input,"WantsKeyboardCapture")!,"hidden field retained keyboard capture");
    var grab = X.XGrabKeyboard(display,X.XDefaultRootWindow(display),0,1,1,0);
    Check(grab==0,"hiding menu did not ungrab keyboard"); X.XUngrabKeyboard(display,0); X.XSync(display,0);
    Console.WriteLine("PASS: F12 down/up and hold, captured F12 hide, keyboard ungrab");
    Probe("Stop");
    Check(helper.WaitForExit(3000),"authenticated stop did not exit");
}
finally
{
    if(transport is IDisposable disposable) disposable.Dispose();
    X.XCloseDisplay(display);
    if(!helper.HasExited) { helper.Kill(); helper.WaitForExit(); }
    File.Delete(heartbeat);
}
static class X
{
    [DllImport("libX11.so.6")] internal static extern IntPtr XOpenDisplay(IntPtr name);
    [DllImport("libX11.so.6")] internal static extern nuint XDefaultRootWindow(IntPtr d);
    [DllImport("libX11.so.6")] internal static extern int XGrabKeyboard(IntPtr d,nuint w,int owner,int pointerMode,int keyboardMode,nuint time);
    [DllImport("libX11.so.6")] internal static extern int XUngrabKeyboard(IntPtr d,nuint time);
    [DllImport("libX11.so.6")] internal static extern int XCloseDisplay(IntPtr d);
    [DllImport("libX11.so.6")] internal static extern int XSync(IntPtr d,int discard);
    [DllImport("libX11.so.6")] internal static extern nuint XStringToKeysym(string name);
    [DllImport("libX11.so.6")] internal static extern byte XKeysymToKeycode(IntPtr d,nuint key);
    [DllImport("libXtst.so.6")] internal static extern int XTestFakeKeyEvent(IntPtr d,uint key,int down,nuint delay);
    [DllImport("libXtst.so.6")] internal static extern int XTestFakeButtonEvent(IntPtr d,uint button,int down,nuint delay);
    [DllImport("libXtst.so.6")] internal static extern int XTestFakeMotionEvent(IntPtr d,int screen,int x,int y,nuint delay);
}
