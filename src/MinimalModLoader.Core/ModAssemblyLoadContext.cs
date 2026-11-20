using System.Reflection;
using System.Runtime.Loader;

namespace MinimalModLoader.Core;

/// <summary>
/// Collectible, isolated AssemblyLoadContext per mod.
/// Shares host/API/Harmony assemblies with the default context so interface types match,
/// while resolving mod-private dependencies from the mod's own directory.
/// </summary>
public sealed class ModAssemblyLoadContext : AssemblyLoadContext
{
    private readonly AssemblyDependencyResolver _resolver;
    private readonly string _modDirectory;
    private readonly HashSet<string> _sharedAssemblyNames;

    public ModAssemblyLoadContext(
        string modId,
        string entryDllPath,
        IEnumerable<string>? additionalSharedAssemblies = null)
        : base(name: $"ModALC:{modId}", isCollectible: true)
    {
        _resolver = new AssemblyDependencyResolver(entryDllPath);
        _modDirectory = Path.GetDirectoryName(entryDllPath)
            ?? throw new ArgumentException("Entry DLL path must include a directory.", nameof(entryDllPath));

        _sharedAssemblyNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "MinimalModLoader.Abstractions",
            "MinimalModLoader.Core",
            "MinimalModLoader.GameHost",
            "0Harmony"
        };

        if (additionalSharedAssemblies is not null)
        {
            foreach (var name in additionalSharedAssemblies)
                _sharedAssemblyNames.Add(name);
        }
    }

    /// <summary>
    /// Loads the mod assembly from memory streams so Windows does not hold a file lock on the DLL.
    /// </summary>
    public Assembly LoadModAssemblyFromDisk(string dllPath)
    {
        using var dllStream = new MemoryStream(File.ReadAllBytes(dllPath));
        var pdbPath = Path.ChangeExtension(dllPath, ".pdb");
        if (File.Exists(pdbPath))
        {
            using var pdbStream = new MemoryStream(File.ReadAllBytes(pdbPath));
            return LoadFromStream(dllStream, pdbStream);
        }

        return LoadFromStream(dllStream);
    }

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        var simpleName = assemblyName.Name;
        if (string.IsNullOrEmpty(simpleName))
            return null;

        // Returning null delegates resolution to AssemblyLoadContext.Default
        if (_sharedAssemblyNames.Contains(simpleName))
            return null;

        // First try standard .deps.json dependency resolution inside the mod folder
        var resolvedPath = _resolver.ResolveAssemblyToPath(assemblyName);
        if (resolvedPath is not null && File.Exists(resolvedPath))
            return LoadModAssemblyFromDisk(resolvedPath);

        // Fallback: probe a sibling DLL directly inside the mod folder
        var localDll = Path.Combine(_modDirectory, $"{simpleName}.dll");
        if (File.Exists(localDll))
            return LoadModAssemblyFromDisk(localDll);

        return null;
    }

    protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
    {
        var libraryPath = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
        return libraryPath is not null ? LoadUnmanagedDllFromPath(libraryPath) : IntPtr.Zero;
    }
}
