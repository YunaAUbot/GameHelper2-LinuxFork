using System;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using ClickableTransparentOverlay;

var assembly = typeof(Overlay).Assembly;
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
