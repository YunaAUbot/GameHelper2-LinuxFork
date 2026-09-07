// <copyright file="Probe.cs" company="None">
// Copyright (c) None. All rights reserved.
// </copyright>
// The test inserts the actual PManager reload/discovery methods at the marker.
// Lifecycle stubs deliberately retain every old ALC, modeling deferred unload.
using System.Reflection;
using System.Runtime.Loader;
namespace GameHelper.Plugin;
internal record PluginMetadata { public bool Enable { get; set; } }
internal record PluginWithName(string Name, Assembly Plugin, PluginAssemblyLoadContext Alc);
internal record PluginContainer(string Name, Assembly Plugin, PluginMetadata Metadata, PluginAssemblyLoadContext Alc);
internal class PluginAssemblyLoadContext(string path) : AssemblyLoadContext(isCollectible: true);
internal static class State { internal static DirectoryInfo PluginsDirectory = null!; }
internal static class PManager
{
    internal static readonly List<PluginContainer> Plugins = new();
    private static readonly Dictionary<string, PluginMetadata> PluginMetadataByName = new();
    private static readonly List<PluginAssemblyLoadContext> Retained = new();
    private static void SaveAndDisablePlugin(PluginContainer plugin) { }
    private static void QueuePluginAssemblyUnload(PluginContainer plugin) { Retained.Add(plugin.Alc); plugin.Alc.Unload(); }
    private static void ResolveStartupConflicts(PluginContainer[] plugins) { }
    private static void EnablePluginIfRequired(PluginContainer plugin) { }
    private static void SavePluginMetadata() { }
    private static void LoadPluginMetadata(IEnumerable<PluginWithName> plugins)
    {
        foreach (var plugin in plugins)
            Plugins.Add(new(plugin.Name, plugin.Plugin, new() { Enable = true }, plugin.Alc));
    }
    private static PluginWithName? LoadPlugin(DirectoryInfo directory)
    {
        var loaded = ReadPluginFiles(directory);
        return loaded is {} value ? new(directory.Name, value.assembly, value.alc) : null;
    }
    // PRODUCTION_METHODS
}
internal static class Program
{
    public static void Main(string[] args)
    {
        State.PluginsDirectory = Directory.CreateDirectory(args[0]);
        Console.WriteLine("ready");
        while (Console.ReadLine() == "reload")
        {
            var count = PManager.ReloadAllPlugins();
            var version = count == 0 ? 0 : PManager.Plugins.Single().Plugin.GetType("Fixture")!.GetField("Version")!.GetRawConstantValue();
            Console.WriteLine($"{count}:{version}");
        }
    }
}
