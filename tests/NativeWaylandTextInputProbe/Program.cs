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

// Run only under the private nested KWin/Xwayland created by test_linux_wayland_text_input.sh.
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
foreach(var value in new[] {"0","0","800","600","90",heartbeat,port.ToString(),Convert.ToHexString(token)}) start.ArgumentList.Add(value);
using var helper = Process.Start(start)!;
var inner = X.XOpenDisplay(IntPtr.Zero);
var innerAuthority=Environment.GetEnvironmentVariable("XAUTHORITY");
Environment.SetEnvironmentVariable("XAUTHORITY",Environment.GetEnvironmentVariable("GH_OUTER_XAUTHORITY"));
var display = X.XOpenDisplayName(Environment.GetEnvironmentVariable("GH_OUTER_DISPLAY")!);
Environment.SetEnvironmentVariable("XAUTHORITY",innerAuthority);
var root = X.XDefaultRootWindow(inner);
var background = X.XCreateSimpleWindow(inner,root,0,0,800,600,0,0,0x224466);
X.XStoreName(inner,background,"Path of Exile 2");
if(Environment.GetEnvironmentVariable("GH_FIXTURE_FULLSCREEN")=="1")
{
    var state=X.XInternAtom(inner,"_NET_WM_STATE",0);
    var fullscreen=X.XInternAtom(inner,"_NET_WM_STATE_FULLSCREEN",0);
    X.XChangeProperty(inner,background,state,4,32,0,new[]{fullscreen},1);
    Console.WriteLine("stand-in fullscreen requested");
}
if(Environment.GetEnvironmentVariable("GH_WAYLAND_GAME")!="1")
{
X.XMapRaised(inner,background); X.XSync(inner,0); Thread.Sleep(1500);
X.XSetInputFocus(inner,background,1,0); X.XSync(inner,0);
}
else Console.WriteLine("native Wayland stand-in active; X11 stand-in remains unmapped");
Console.WriteLine($"inner={Environment.GetEnvironmentVariable("DISPLAY")} outer={Environment.GetEnvironmentVariable("GH_OUTER_DISPLAY")}"); Check(display != IntPtr.Zero, "Xvfb unavailable");
object? transport = null;
try
{
    transport = Create("NativeGpuTransport", port, token);
    Check((bool)Call(transport,"WaitForReady",TimeSpan.FromSeconds(5))!, "authenticated readiness");
    Type("NativeGpuProbe").GetField("transport",flags)!.SetValue(null,transport);
    var renderer = Create("ImGuiRenderer", null,null,800,600,true);
    var input = Create("ImGuiInputHandler",IntPtr.Zero);
    var io = ImGui.GetIO(); io.ConfigFlags |= ImGuiConfigFlags.NoMouseCursorChange;
    unsafe { io.NativePtr->IniFilename = null; }
    string url = ""; bool active = false; bool shown = true; Vector2 field = default;
    void Frame()
    {
        Probe("PollInput",input,renderer);
        bool mouse = (bool)Call(input,"Update",true)!;
        Call(renderer,"Update",1f/60,(Action)(() => {
            if((bool)Type("NativeKeyState").GetMethod("ConsumePressed",flags)!.Invoke(null,new object[]{0x7B})!) shown=!shown;
            if(!shown){active=false;return;}
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
    X.XTestFakeMotionEvent(display,-1,(int)field.X-100,(int)field.Y,0); X.XSync(display,0); Frames();
    X.XTestFakeButtonEvent(display,1,1,0); X.XSync(display,0); Frames(4);
    X.XTestFakeButtonEvent(display,1,0,0); X.XSync(display,0); Frames();
    X.XGetInputFocus(inner,out var focused,out var revert);
    Console.WriteLine($"focus after click: standin={background} focused={focused} matches={focused==background}");
    Console.WriteLine($"clicked: active={active} WantTextInput={io.WantTextInput} capture={Call(input,"WantsKeyboardCapture")}");
    Check(active,"native click did not activate URL field");
    Check((bool)Call(input,"WantsKeyboardCapture")!,"URL field did not request keyboard capture");
    Tap("a"); Tap("b"); Tap("c");
    X.XGetInputFocus(inner,out focused,out revert);
    Console.WriteLine($"focus after typing: standin={background} focused={focused} matches={focused==background}");
    Check(url=="abc",$"ordinary typing: expected abc, got '{url}'");
    Console.WriteLine("PASS: native click -> WantTextInput -> XGrabKeyboard -> key/text packets -> ImGui URL value abc");
    var lifecycle=Environment.GetEnvironmentVariable("GH_LIFECYCLE")??"f12";
    if(lifecycle=="shutdown")
    {
        Probe("Stop"); Check(helper.WaitForExit(3000)&&helper.ExitCode==0,"active authenticated shutdown failed");
        Frames(20);
    }
    else if(lifecycle=="close")
    {
        Check(X.XGetWMProtocols(inner,focused,out var protocols,out var count)!=0,"focus proxy missing WM_PROTOCOLS");
        var delete=X.XInternAtom(inner,"WM_DELETE_WINDOW",0); bool found=false;
        for(int index=0;index<count;index++) if((nuint)Marshal.ReadIntPtr(protocols,index*IntPtr.Size)==delete)found=true;
        X.XFree(protocols); Check(found,"focus proxy missing WM_DELETE_WINDOW");
        var close=new X.ClientMessage {type=33,display=inner,window=focused,messageType=X.XInternAtom(inner,"WM_PROTOCOLS",0),format=32,data0=delete};
        X.XSendEvent(inner,focused,0,0,ref close);X.XSync(inner,0);Frames(30);
        Check(!helper.HasExited,"closing focus proxy killed native helper");
        Check((bool)Call(input,"WantsKeyboardCapture")!,"close fixture lost text demand before suspension check");
    }
    else if(lifecycle=="focusloss")
    {
        X.XMapRaised(inner,background);X.XSync(inner,0);Thread.Sleep(300);
        X.XSetInputFocus(inner,background,1,0);X.XSync(inner,0);Frames(20);
        X.XUnmapWindow(inner,background);X.XSync(inner,0);Frames(30);
        Check((bool)Call(input,"WantsKeyboardCapture")!,"focus-loss fixture lost ImGui text demand");
        Console.WriteLine("fixture app focus changed while ImGui still requests text");
    }
    else if(lifecycle=="disconnect")
    {
        ((IDisposable)transport).Dispose(); Type("NativeGpuProbe").GetField("transport",flags)!.SetValue(null,null);
        Frames(20); Check(!helper.HasExited,"disconnect incorrectly exited helper before reconnect grace");
    }
    else
    {
    Key("F12",true); Check(!shown,"F12 down did not hide menu");
    Frames(20); Check(!shown,"F12 held toggled menu");
    Key("F12",false); Frames(20);
    Check(!(bool)Call(input,"WantsKeyboardCapture")!,"capture stayed active");
    }
    var grabbed=X.XGrabKeyboard(inner,X.XDefaultRootWindow(inner),0,1,1,0);
    Check(grabbed==0,"capture-off did not release keyboard"); X.XUngrabKeyboard(inner,0);X.XSync(inner,0);
    Tap("d"); Frames(20);
    if(Environment.GetEnvironmentVariable("GH_WAYLAND_GAME")=="1")
       Check(File.ReadAllText(Environment.GetEnvironmentVariable("GH_FIXTURE_WAYLAND_LOG")!).Contains("PUBLIC FIXTURE KEY 32 1"),"Wayland did not recover d key on capture-off");
    Console.WriteLine($"PASS: {lifecycle} -> ungrab -> Wayland d key restored");
    if(lifecycle=="focusloss")
    {
        X.XTestFakeMotionEvent(display,-1,(int)field.X-100,(int)field.Y,0);X.XSync(display,0);Frames(10);
        X.XTestFakeButtonEvent(display,1,1,0);X.XSync(display,0);Frames(4);
        X.XTestFakeButtonEvent(display,1,0,0);X.XSync(display,0);Frames(20);
        Tap("e");Check(url.Length==4&&url.Contains('e'),"fresh click did not resume text input");
        Console.WriteLine("PASS: fresh overlay click resumes suspended text entry");
    }
    if(lifecycle=="disconnect") { helper.Kill(); helper.WaitForExit(); }
    Probe("Stop");
    Check(helper.WaitForExit(3000),"authenticated stop did not exit");
    if(lifecycle!="disconnect")Check(helper.ExitCode==0,"native helper failed rather than clean stop");
}
finally
{
    if(transport is IDisposable disposable) disposable.Dispose();
    X.XCloseDisplay(display); X.XDestroyWindow(inner,background); X.XCloseDisplay(inner);
    if(!helper.HasExited) { helper.Kill(); helper.WaitForExit(); }
    File.Delete(heartbeat);
}
static class X
{
    [StructLayout(LayoutKind.Explicit,Size=192)] internal struct ClientMessage
    {
        [FieldOffset(0)] internal int type;
        [FieldOffset(24)] internal IntPtr display;
        [FieldOffset(32)] internal nuint window;
        [FieldOffset(40)] internal nuint messageType;
        [FieldOffset(48)] internal int format;
        [FieldOffset(56)] internal nuint data0;
    }
    [DllImport("libX11.so.6")] internal static extern int XGetWMProtocols(IntPtr d,nuint w,out IntPtr protocols,out int count);
    [DllImport("libX11.so.6")] internal static extern int XFree(IntPtr p);
    [DllImport("libX11.so.6")] internal static extern int XSendEvent(IntPtr d,nuint w,int propagate,nint mask,ref ClientMessage e);
    [DllImport("libX11.so.6",EntryPoint="XOpenDisplay")] internal static extern IntPtr XOpenDisplayName(string name);
    [DllImport("libX11.so.6")] internal static extern nuint XDefaultRootWindow(IntPtr d);
    [DllImport("libX11.so.6")] internal static extern nuint XCreateSimpleWindow(IntPtr d,nuint parent,int x,int y,uint width,uint height,uint border,nuint borderColor,nuint background);
    [DllImport("libX11.so.6")] internal static extern int XStoreName(IntPtr d,nuint w,string name);
    [DllImport("libX11.so.6")] internal static extern int XUnmapWindow(IntPtr d,nuint w);
    [DllImport("libX11.so.6")] internal static extern int XMapRaised(IntPtr d,nuint w);
    [DllImport("libX11.so.6")] internal static extern int XSetInputFocus(IntPtr d,nuint w,int revert,nuint time);
    [DllImport("libX11.so.6")] internal static extern int XGrabKeyboard(IntPtr d,nuint w,int owner,int pointerMode,int keyboardMode,nuint time);
    [DllImport("libX11.so.6")] internal static extern int XUngrabKeyboard(IntPtr d,nuint time);
    [DllImport("libX11.so.6")] internal static extern nuint XInternAtom(IntPtr d,string name,int only);
    [DllImport("libX11.so.6")] internal static extern int XChangeProperty(IntPtr d,nuint w,nuint prop,nuint type,int format,int mode,nuint[] data,int count);
    [DllImport("libX11.so.6")] internal static extern int XGetInputFocus(IntPtr d,out nuint focused,out int revert);
    [DllImport("libX11.so.6")] internal static extern int XDestroyWindow(IntPtr d,nuint w);
    [DllImport("libX11.so.6")] internal static extern IntPtr XOpenDisplay(IntPtr name);
    [DllImport("libX11.so.6")] internal static extern int XCloseDisplay(IntPtr d);
    [DllImport("libX11.so.6")] internal static extern int XSync(IntPtr d,int discard);
    [DllImport("libX11.so.6")] internal static extern nuint XStringToKeysym(string name);
    [DllImport("libX11.so.6")] internal static extern byte XKeysymToKeycode(IntPtr d,nuint key);
    [DllImport("libXtst.so.6")] internal static extern int XTestFakeKeyEvent(IntPtr d,uint key,int down,nuint delay);
    [DllImport("libXtst.so.6")] internal static extern int XTestFakeButtonEvent(IntPtr d,uint button,int down,nuint delay);
    [DllImport("libXtst.so.6")] internal static extern int XTestFakeMotionEvent(IntPtr d,int screen,int x,int y,nuint delay);
}
