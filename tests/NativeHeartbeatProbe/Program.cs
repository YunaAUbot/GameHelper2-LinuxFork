using System;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using ClickableTransparentOverlay;

var assembly = typeof(Overlay).Assembly;
var restartPolicyType = assembly.GetType("ClickableTransparentOverlay.NativeGpuRestartPolicy")
    ?? throw new InvalidOperationException("NativeGpuRestartPolicy missing");
var shouldRestartCompositor = restartPolicyType.GetMethod("ShouldRestart", BindingFlags.Static | BindingFlags.NonPublic)
    ?? throw new InvalidOperationException("NativeGpuRestartPolicy.ShouldRestart missing");
if (!(bool)(shouldRestartCompositor.Invoke(null, new object[] { true, true, false, false, TimeSpan.FromSeconds(3) }) ?? false) ||
    (bool)(shouldRestartCompositor.Invoke(null, new object[] { true, true, false, false, TimeSpan.FromSeconds(1) }) ?? true) ||
    (bool)(shouldRestartCompositor.Invoke(null, new object[] { true, true, false, true, TimeSpan.FromSeconds(3) }) ?? true) ||
    (bool)(shouldRestartCompositor.Invoke(null, new object[] { true, true, true, false, TimeSpan.FromSeconds(3) }) ?? true))
    throw new InvalidOperationException("native compositor restart policy is not delayed and single-shot");

var probeType = assembly.GetType("ClickableTransparentOverlay.NativeGpuProbe")
    ?? throw new InvalidOperationException("NativeGpuProbe missing");
var prepareReconnect = probeType.GetMethod("PrepareReconnect", BindingFlags.Static | BindingFlags.NonPublic)
    ?? throw new InvalidOperationException("NativeGpuProbe.PrepareReconnect missing");
var interactiveField = probeType.GetField("interactive", BindingFlags.Static | BindingFlags.NonPublic)
    ?? throw new InvalidOperationException("NativeGpuProbe.interactive missing");
var keyboardCaptureField = probeType.GetField("keyboardCapture", BindingFlags.Static | BindingFlags.NonPublic)
    ?? throw new InvalidOperationException("NativeGpuProbe.keyboardCapture missing");
interactiveField.SetValue(null, true);
keyboardCaptureField.SetValue(null, true);
prepareReconnect.Invoke(null, null);
if (interactiveField.GetValue(null) != null || keyboardCaptureField.GetValue(null) != null)
    throw new InvalidOperationException("connection-scoped input state remained cached across reconnect");

var disconnectPolicyType = assembly.GetType("ClickableTransparentOverlay.NativeGpuDisconnectPolicy")
    ?? throw new InvalidOperationException("NativeGpuDisconnectPolicy missing");
var shouldWaitForReconnect = disconnectPolicyType.GetMethod("ShouldWaitForReconnect", BindingFlags.Static | BindingFlags.NonPublic)
    ?? throw new InvalidOperationException("NativeGpuDisconnectPolicy.ShouldWaitForReconnect missing");
if (!(bool)(shouldWaitForReconnect.Invoke(null, new object[] { true, true, false }) ?? false) ||
    (bool)(shouldWaitForReconnect.Invoke(null, new object[] { false, true, false }) ?? true) ||
    (bool)(shouldWaitForReconnect.Invoke(null, new object[] { true, false, false }) ?? true) ||
    (bool)(shouldWaitForReconnect.Invoke(null, new object[] { true, true, true }) ?? true))
    throw new InvalidOperationException("native disconnect policy does not isolate a started disconnected native backend");

var transportType = assembly.GetType("ClickableTransparentOverlay.NativeGpuTransport")
    ?? throw new InvalidOperationException("NativeGpuTransport missing");
var transportConstructor = transportType.GetConstructor(
    BindingFlags.Instance | BindingFlags.NonPublic,
    binder: null,
    new[] { typeof(int), typeof(byte[]) },
    modifiers: null)
    ?? throw new InvalidOperationException("NativeGpuTransport constructor missing");
var waitForReady = transportType.GetMethod("WaitForReady", BindingFlags.Instance | BindingFlags.NonPublic)
    ?? throw new InvalidOperationException("NativeGpuTransport.WaitForReady missing");
var trySend = transportType.GetMethod("TrySend", BindingFlags.Instance | BindingFlags.NonPublic)
    ?? throw new InvalidOperationException("NativeGpuTransport.TrySend missing");
var authBytes = new byte[32];
for (var i = 0; i < authBytes.Length; i++) authBytes[i] = (byte)(i + 1);
using var listener = new TcpListener(IPAddress.Loopback, 0);
listener.Start();
var reconnectPort = ((IPEndPoint)listener.LocalEndpoint).Port;
var firstConnectionClosed = new ManualResetEventSlim(false);
var secondFrameReceived = new ManualResetEventSlim(false);
var serverTask = Task.Run(() =>
{
    for (var connectionNumber = 0; connectionNumber < 2; connectionNumber++)
    {
        using var accepted = listener.AcceptTcpClient();
        using var stream = accepted.GetStream();
        var authLengthBytes = new byte[4];
        stream.ReadExactly(authLengthBytes);
        var authLength = BitConverter.ToInt32(authLengthBytes, 0);
        if (authLength != 36) throw new InvalidOperationException("reconnected transport skipped authentication length");
        var authPayload = new byte[authLength];
        stream.ReadExactly(authPayload);
        if (BitConverter.ToUInt32(authPayload, 0) != 0x31485541)
            throw new InvalidOperationException("reconnected transport skipped authentication magic");
        for (var i = 0; i < authBytes.Length; i++)
            if (authPayload[i + 4] != authBytes[i]) throw new InvalidOperationException("reconnected transport used the wrong token");
        stream.Write(BitConverter.GetBytes(0x31594452u));

        var frameLengthBytes = new byte[4];
        stream.ReadExactly(frameLengthBytes);
        var frameLength = BitConverter.ToInt32(frameLengthBytes, 0);
        var frame = new byte[frameLength];
        stream.ReadExactly(frame);
        if (connectionNumber == 0)
        {
            accepted.Client.LingerState = new LingerOption(true, 0);
            firstConnectionClosed.Set();
        }
        else
        {
            secondFrameReceived.Set();
        }
    }
});
var reconnectTransport = transportConstructor.Invoke(new object[] { reconnectPort, authBytes });
if (!(bool)(waitForReady.Invoke(reconnectTransport, new object[] { TimeSpan.FromSeconds(2) }) ?? false))
    throw new InvalidOperationException("initial native transport authentication failed");
if (!(bool)(trySend.Invoke(reconnectTransport, new object[] { new byte[] { 1, 2, 3, 4 } }) ?? false))
    throw new InvalidOperationException("initial native transport frame failed");
if (!firstConnectionClosed.Wait(TimeSpan.FromSeconds(2)))
    throw new InvalidOperationException("test server did not close the first authenticated connection");
var reconnectDeadline = DateTime.UtcNow.AddSeconds(3);
while (DateTime.UtcNow < reconnectDeadline && !secondFrameReceived.IsSet)
{
    _ = trySend.Invoke(reconnectTransport, new object[] { new byte[] { 5, 6, 7, 8 } });
    Thread.Sleep(25);
}
if (!secondFrameReceived.Wait(TimeSpan.FromSeconds(1)))
    throw new InvalidOperationException("native transport did not reconnect and re-authenticate after connection loss");
((IDisposable)reconnectTransport).Dispose();
if (!serverTask.Wait(TimeSpan.FromSeconds(2)))
    throw new InvalidOperationException("reconnect test server did not finish");
firstConnectionClosed.Dispose();
secondFrameReceived.Dispose();

var closeDiagnosticsType = assembly.GetType("ClickableTransparentOverlay.OverlayCloseDiagnostics")
    ?? throw new InvalidOperationException("OverlayCloseDiagnostics missing");
var captureClose = closeDiagnosticsType.GetMethod("CaptureOnce", BindingFlags.Static | BindingFlags.NonPublic)
    ?? throw new InvalidOperationException("OverlayCloseDiagnostics.CaptureOnce missing");
var firstClose = captureClose.Invoke(null, new object[] { "probe" }) as string;
if (firstClose == null || !firstClose.Contains("reason=probe", StringComparison.Ordinal) || !firstClose.Contains("stack=", StringComparison.Ordinal))
    throw new InvalidOperationException("first overlay close did not capture its reason and stack");
if (captureClose.Invoke(null, new object[] { "duplicate" }) != null)
    throw new InvalidOperationException("duplicate overlay close diagnostics were not suppressed");

var windowLifecycleType = assembly.GetType("ClickableTransparentOverlay.NativeWindowLifecycle")
    ?? throw new InvalidOperationException("NativeWindowLifecycle missing");
var shouldCloseOnDestroy = windowLifecycleType.GetMethod("ShouldCloseOnDestroy", BindingFlags.Static | BindingFlags.NonPublic)
    ?? throw new InvalidOperationException("NativeWindowLifecycle.ShouldCloseOnDestroy missing");
if ((bool)(shouldCloseOnDestroy.Invoke(null, new object[] { true }) ?? true))
    throw new InvalidOperationException("native backend treated destruction of its disposable Wine host window as overlay shutdown");
if (!(bool)(shouldCloseOnDestroy.Invoke(null, new object[] { false }) ?? false))
    throw new InvalidOperationException("Windows backend stopped closing when its presenter window is destroyed");

var type = assembly.GetType("ClickableTransparentOverlay.NativeGpuHeartbeat")
    ?? throw new InvalidOperationException("NativeGpuHeartbeat missing");
var deterministicConstructor = type.GetConstructor(
    BindingFlags.Instance | BindingFlags.NonPublic,
    binder: null,
    new[] { typeof(string), typeof(Rectangle), typeof(TimeSpan), typeof(Action<string, string>) },
    modifiers: null)
    ?? throw new InvalidOperationException("NativeGpuHeartbeat deterministic test constructor missing");
var productionConstructor = type.GetConstructor(
    BindingFlags.Instance | BindingFlags.NonPublic,
    binder: null,
    new[] { typeof(string), typeof(Rectangle), typeof(TimeSpan) },
    modifiers: null)
    ?? throw new InvalidOperationException("NativeGpuHeartbeat production constructor missing");
var update = type.GetMethod("Update", BindingFlags.Instance | BindingFlags.NonPublic)
    ?? throw new InvalidOperationException("NativeGpuHeartbeat.Update missing");
var path = Path.Combine(Path.GetTempPath(), $"gamehelper2-heartbeat-probe-{Guid.NewGuid():N}.alive");
var oldTimerWriteEntered = new ManualResetEventSlim(false);
var releaseOldTimerWrite = new ManualResetEventSlim(false);
var latestStateWritten = new ManualResetEventSlim(false);
var writeGate = new object();
var writeCount = 0;
var lastContent = string.Empty;

void DeterministicWrite(string _, string content)
{
    var currentWrite = Interlocked.Increment(ref writeCount);
    if (currentWrite == 2)
    {
        oldTimerWriteEntered.Set();
        if (!releaseOldTimerWrite.Wait(TimeSpan.FromSeconds(5)))
            throw new TimeoutException("test did not release the blocked timer write");
    }

    lock (writeGate)
    {
        lastContent = content;
    }

    if (content.Contains(" 1 3 4 1024 768", StringComparison.Ordinal))
        latestStateWritten.Set();
}

string[] LastState()
{
    lock (writeGate)
    {
        return lastContent.Split(' ', StringSplitOptions.RemoveEmptyEntries);
    }
}

try
{
    var heartbeat = deterministicConstructor.Invoke(new object[]
    {
        path,
        new Rectangle(1, 2, 800, 600),
        TimeSpan.FromMilliseconds(20),
        (Action<string, string>)DeterministicWrite,
    });
    var first = long.Parse(LastState()[0], CultureInfo.InvariantCulture);
    if (!oldTimerWriteEntered.Wait(TimeSpan.FromSeconds(2)))
        throw new InvalidOperationException("timer did not refresh the heartbeat without render calls");

    var updateTask = Task.Run(() =>
        update.Invoke(heartbeat, new object[] { new Rectangle(3, 4, 1024, 768), true }));

    // A broken implementation can write the new state while the older timer write
    // is still blocked; releasing afterward then deterministically overwrites it.
    _ = latestStateWritten.Wait(TimeSpan.FromSeconds(1));
    releaseOldTimerWrite.Set();
    if (!updateTask.Wait(TimeSpan.FromSeconds(2)))
        throw new InvalidOperationException("state update remained blocked after the timer write completed");

    var state = LastState();
    if (long.Parse(state[0], CultureInfo.InvariantCulture) <= first)
        throw new InvalidOperationException("heartbeat timestamp did not advance independently of render frames");
    if (state.Length != 6 || state[1] != "1" || state[2] != "3" || state[3] != "4" || state[4] != "1024" || state[5] != "768")
        throw new InvalidOperationException("an older timer snapshot overwrote the latest input mode or bounds");

    ((IDisposable)heartbeat).Dispose();
    var stoppedWrites = Volatile.Read(ref writeCount);
    update.Invoke(heartbeat, new object[] { new Rectangle(5, 6, 640, 480), false });
    if (Volatile.Read(ref writeCount) != stoppedWrites)
        throw new InvalidOperationException("an update wrote after heartbeat disposal");

    var productionHeartbeat = productionConstructor.Invoke(new object[]
    {
        path,
        new Rectangle(7, 8, 1280, 720),
        TimeSpan.FromDays(1),
    });
    ((IDisposable)productionHeartbeat).Dispose();
    var persisted = File.ReadAllText(path).Split(' ', StringSplitOptions.RemoveEmptyEntries);
    if (persisted.Length != 6 || persisted[2] != "7" || persisted[3] != "8" || persisted[4] != "1280" || persisted[5] != "720")
        throw new InvalidOperationException("production heartbeat writer emitted an invalid snapshot");
}
finally
{
    releaseOldTimerWrite.Set();
    oldTimerWriteEntered.Dispose();
    releaseOldTimerWrite.Dispose();
    latestStateWritten.Dispose();
    File.Delete(path);
}

Console.WriteLine("PASS: managed heartbeat serializes timer/update writes and stops cleanly after disposal");
