namespace GameHelper.Plugin
{
    using System;
    using System.Collections.Generic;
    using System.Reflection;
    using System.IO;
    using System.Runtime.Loader;
    using GameOffsets;

    internal class PluginAssemblyLoadContext : AssemblyLoadContext
    {
        private static readonly IReadOnlyDictionary<string, Assembly> SharedAssemblies =
            new Dictionary<string, Assembly>(StringComparer.OrdinalIgnoreCase)
            {
                [typeof(IPCore).Assembly.GetName().Name!] = typeof(IPCore).Assembly,
                [typeof(GameProcessDetails).Assembly.GetName().Name!] = typeof(GameProcessDetails).Assembly,
            };

        private readonly AssemblyDependencyResolver resolver;

        public PluginAssemblyLoadContext(string assemblyLocation)
            : base(isCollectible: true)
        {
            this.resolver = new AssemblyDependencyResolver(assemblyLocation);
        }

        // Reading fresh bytes bypasses path-based PE image reuse while an older
        // collectible context is still alive. The resolver keeps the original
        // plugin directory for dependencies; plugin data uses SetPluginDllLocation.
        public Assembly LoadPluginAssembly(string path)
        {
            using var bytes = new MemoryStream(File.ReadAllBytes(path), writable: false);
            return this.LoadFromStream(bytes);
        }

        protected override Assembly? Load(AssemblyName assemblyName)
        {
            if (assemblyName.Name != null &&
                SharedAssemblies.TryGetValue(assemblyName.Name, out var sharedAssembly))
            {
                return sharedAssembly;
            }

            var path = this.resolver.ResolveAssemblyToPath(assemblyName);
            if (path != null)
            {
                return this.LoadPluginAssembly(path);
            }

            return null;
        }
    }
}
