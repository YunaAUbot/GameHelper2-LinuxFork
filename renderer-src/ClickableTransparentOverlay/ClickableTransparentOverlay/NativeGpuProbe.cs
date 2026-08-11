namespace ClickableTransparentOverlay
{
    using System;
    using System.Diagnostics;
    using System.Drawing;
    using System.IO;
    using System.Net;
    using System.Net.Sockets;
    using System.Security.Cryptography;

    // Lifecycle bridge for the default Linux renderer. The visible ARGB GLX
    // surface receives compact ImGui geometry and replaces Wine's host HWND.
    internal sealed class NativeGpuProbe
    {
        private static string heartbeatWindowsPath;
        private static NativeGpuTransport transport;
        private static bool fontSent;
        private static bool? interactive;
        private static bool? keyboardCapture;
        private static bool menuInput;
        private NativeGpuProbe() { }

        internal static bool TryStart(Rectangle bounds)
        {
            try
            {
                var baseDirectory = AppContext.BaseDirectory;
                if (!baseDirectory.StartsWith("Z:\\", StringComparison.OrdinalIgnoreCase)) return false;
                var helper = Path.Combine(baseDirectory, "gamehelper2-gpu-overlay");
                if (!File.Exists(helper)) return false;
                var unixHelper = "/" + helper.Substring(3).Replace('\\', '/');
                heartbeatWindowsPath = $@"Z:\tmp\gamehelper2-gpu-overlay-{Environment.ProcessId}.alive";
                File.WriteAllText(heartbeatWindowsPath, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString());
                var unixHeartbeat = "/tmp/" + Path.GetFileName(heartbeatWindowsPath);
                using var reservation = new TcpListener(IPAddress.Loopback, 0);
                reservation.Start();
                var port = ((IPEndPoint)reservation.LocalEndpoint).Port;
                // The listener only selects a collision-free port.  Release it
                // before the native helper binds the same loopback endpoint.
                reservation.Stop();
                var token = RandomNumberGenerator.GetBytes(32);
                transport = new NativeGpuTransport(port, token);
                _ = Process.Start(new ProcessStartInfo
                {
                    FileName = @"C:\\windows\\system32\\start.exe",
                    Arguments = $"/unix \"{unixHelper}\" {bounds.X} {bounds.Y} {bounds.Width} {bounds.Height} 3600 \"{unixHeartbeat}\" {port} {Convert.ToHexString(token)}",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                });
                if (transport.WaitForReady(TimeSpan.FromSeconds(3))) return true;
                Stop();
                return false;
            }
            catch { Stop(); return false; }
        }

        internal static void Pulse(Rectangle bounds)
        {
            if (heartbeatWindowsPath is null) return;
            try { File.WriteAllText(heartbeatWindowsPath, $"{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()} {(menuInput ? 1 : 0)} {bounds.X} {bounds.Y} {bounds.Width} {bounds.Height}"); }
            catch { }
        }

        internal static void ToggleMenuInput() => menuInput = !menuInput;

        internal static void Present(ImGuiNET.ImDrawDataPtr drawData, ImGuiRenderer renderer)
        {
            if (transport is null) return;
            try
            {
                var font = renderer.GetNativeFontAtlas();
                if (!fontSent && font.Pixels.Length > 0)
                {
                    if (transport.TrySend(NativeGpuFrameProtocol.SerializeFont(font.Pixels, font.Width, font.Height))) fontSent = true;
                    else return;
                }
                transport.TrySend(NativeGpuFrameProtocol.Serialize(drawData, font.TextureId));
            }
            catch { fontSent = false; }
        }

        internal static void SetInteractive(bool wantsInput)
        {
            if (transport is null || interactive == wantsInput) return;
            if (transport.TrySend(NativeGpuFrameProtocol.SerializeInputMode(wantsInput))) interactive = wantsInput;
        }

        internal static void SetKeyboardCapture(bool wantsInput)
        {
            if (transport is null || keyboardCapture == wantsInput) return;
            if (transport.TrySend(NativeGpuFrameProtocol.SerializeKeyboardMode(wantsInput))) keyboardCapture = wantsInput;
        }

        internal static void PollInput(ImGuiInputHandler input)
        {
            while (transport is not null && transport.TryReadEvent(out var kind, out var code, out var down, out var value, out var value2))
            {
                if (kind == NativeGpuFrameProtocol.KeyInputMagic)
                {
                    input.AddNativeKey((uint)code, down, value);
                    continue;
                }
                input.AddNativeMousePosition(BitConverter.Int32BitsToSingle((int)value), BitConverter.Int32BitsToSingle((int)value2));
                switch (code)
                {
                    case -2: if (down) input.AddNativeMouseWheel(0, 1); break;
                    case -3: if (down) input.AddNativeMouseWheel(0, -1); break;
                    case -4: if (down) input.AddNativeMouseWheel(-1, 0); break;
                    case -5: if (down) input.AddNativeMouseWheel(1, 0); break;
                    default: input.AddNativeMouseButton(code, down); break;
                }
            }
        }

        internal static bool IsConnected => transport?.IsConnected == true;

        internal static void Stop()
        {
            transport?.Dispose(); transport = null; fontSent = false; interactive = null; keyboardCapture = null; menuInput = false;
            if (heartbeatWindowsPath is not null) { try { File.Delete(heartbeatWindowsPath); } catch { } }
            heartbeatWindowsPath = null;
        }
    }
}
