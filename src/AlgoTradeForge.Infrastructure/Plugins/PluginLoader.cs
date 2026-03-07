using System.Reflection;
using System.Runtime.Loader;
using Microsoft.Extensions.Logging;

namespace AlgoTradeForge.Infrastructure.Plugins;

public static class PluginLoader
{
    public static Assembly[] LoadFrom(IEnumerable<string> paths, ILogger logger, string? basePath = null)
    {
        basePath ??= AppContext.BaseDirectory;
        var loaded = new List<Assembly>();

        foreach (var raw in paths)
        {
            var dir = Path.IsPathRooted(raw)
                ? raw
                : Path.Combine(basePath, raw);

            if (!Directory.Exists(dir))
            {
                logger.LogInformation("Plugin directory does not exist, skipping: {Path}", dir);
                continue;
            }

            foreach (var dll in Directory.EnumerateFiles(dir, "*.dll"))
            {
                var assemblyName = Path.GetFileNameWithoutExtension(dll);
                if (AssemblyLoadContext.Default.Assemblies.Any(a => a.GetName().Name == assemblyName))
                {
                    logger.LogDebug("Assembly already loaded, skipping: {Name}", assemblyName);
                    continue;
                }

                try
                {
                    var resolver = new AssemblyDependencyResolver(dll);
                    AssemblyLoadContext.Default.Resolving += (ctx, name) =>
                    {
                        var resolved = resolver.ResolveAssemblyToPath(name);
                        return resolved is not null ? ctx.LoadFromAssemblyPath(resolved) : null;
                    };

                    var assembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(dll);
                    loaded.Add(assembly);
                    logger.LogInformation("Loaded plugin assembly: {Name} from {Path}", assemblyName, dll);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Failed to load plugin assembly: {Path}", dll);
                }
            }
        }

        return [.. loaded];
    }
}
