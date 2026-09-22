using System.Reflection;
using GameHelper.Plugin;

var directory = Path.Combine(Path.GetTempPath(), "plugin-reload-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(directory);
try
{
    var target = Path.Combine(directory, "ReloadFixture.dll");
    File.Copy(args[0], target);
    var oldContext = new PluginAssemblyLoadContext(target);
    var old = oldContext.LoadPluginAssembly(target);
    if (Marker(old) != "first") throw new Exception("Invalid first fixture");
    // Keep the old context and assembly alive: actual plugins can retain static references.
    var replacement = target + ".new";
    File.Copy(args[1], replacement);
    File.Move(replacement, target, true);
    var nextContext = new PluginAssemblyLoadContext(target);
    var next = nextContext.LoadPluginAssembly(target);
    if (Marker(next) != "second") throw new Exception("Reload reused old bytes at the same path");
    if (Marker(old) != "first") throw new Exception("Existing assembly changed");
    if (ReferenceEquals(old, next)) throw new Exception("Contexts share plugin assembly");
    // The host contract must remain shared, not duplicated into the plugin context.
    if (!ReferenceEquals(typeof(IPCore).Assembly, nextContext.LoadFromAssemblyName(typeof(IPCore).Assembly.GetName())))
        throw new Exception("Host assembly identity was not preserved");
    Console.WriteLine("PASS: changed DLL with unchanged identity/path loads new code while old code remains live; host contracts shared.");
    oldContext.Unload(); nextContext.Unload();
}
finally { Directory.Delete(directory, true); }
static string Marker(Assembly assembly) => (string)assembly.GetType("VersionMarker")!.GetProperty("Value")!.GetValue(null)!;

namespace GameHelper.Plugin { public interface IPCore {} }
namespace GameOffsets { public sealed class GameProcessDetails {} }
