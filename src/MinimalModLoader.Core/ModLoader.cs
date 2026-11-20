using System.Reflection;
using MinimalModLoader.Abstractions;

namespace MinimalModLoader.Core;

public sealed record LoadedModEntry(
    ModManifest Manifest,
    string DirectoryPath,
    IMod Instance,
    ModAssemblyLoadContext LoadContext);

public sealed record ModLoadReport(
    IReadOnlyList<ModManifest> LoadedCodeMods,
    IReadOnlyList<ModManifest> LoadedContentPacks,
    IReadOnlyList<SkippedMod> SkippedMods,
    IReadOnlyList<string> Warnings);

/// <summary>
/// Main Mod Loader engine orchestrating:
/// 1) Manifest discovery
/// 2) SemVer & dependency topological resolution
/// 3) Isolated AssemblyLoadContext loading
/// 4) Content Pack attachment & Inter-Mod API registration
/// 5) Entry() execution, Asset Pipeline, Event Bus, and graceful Unload/Dispose.
/// </summary>
public sealed class ModLoader : IDisposable
{
    public static readonly SemVer CurrentVersion = new(1, 0, 0);

    private readonly IMonitor _loaderMonitor;
    private readonly Func<string, IMonitor> _modMonitorFactory;
    private readonly List<LoadedModEntry> _loadedMods = [];
    private readonly List<IContentPack> _loadedContentPacks = [];
    private readonly IReadOnlyCollection<string>? _additionalSharedAssemblies;
    private bool _disposed;

    public ModEventBus Events { get; }
    public AssetManager Assets { get; }
    public ModRegistryImpl Registry { get; }

    public IReadOnlyList<LoadedModEntry> LoadedMods => _loadedMods;
    public IReadOnlyList<IContentPack> LoadedContentPacks => _loadedContentPacks;

    public ModLoader(
        IBaseAssetProvider baseAssetProvider,
        TextWriter? logWriter = null,
        LogLevel minLogLevel = LogLevel.Info,
        IEnumerable<string>? additionalSharedAssemblies = null)
    {
        _loaderMonitor = new ConsoleMonitor("Loader", logWriter, minLogLevel);
        _modMonitorFactory = modName => new ConsoleMonitor(modName, logWriter, minLogLevel);
        _additionalSharedAssemblies = additionalSharedAssemblies?.ToArray();

        Events = new ModEventBus();
        Assets = new AssetManager(baseAssetProvider, Events, _loaderMonitor);
        Registry = new ModRegistryImpl();
    }

    public ModLoadReport LoadMods(string modsDirectory)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        _loaderMonitor.Log($"MinimalModLoader v{CurrentVersion} scanning '{modsDirectory}'...");

        var scanner = new ModScanner();
        var scanResult = scanner.Scan(modsDirectory);

        var resolver = new DependencyResolver(CurrentVersion);
        var resolved = resolver.Resolve(scanResult.ValidManifests);

        var allSkipped = new List<SkippedMod>(scanResult.InvalidMods);
        allSkipped.AddRange(resolved.SkippedMods);

        foreach (var warning in resolved.Warnings)
            _loaderMonitor.Log(warning, LogLevel.Warn);

        // Prepare content packs grouped by their target C# mod UniqueID
        var packsByOwner = new Dictionary<string, List<IContentPack>>(StringComparer.OrdinalIgnoreCase);
        foreach (var packMeta in resolved.ValidContentPacks)
        {
            var pack = new ContentPackImpl(packMeta.Manifest, packMeta.DirectoryPath);
            _loadedContentPacks.Add(pack);
            Registry.RegisterMod(pack.Manifest);

            var ownerId = pack.Manifest.ContentPackFor!.UniqueID;
            if (!packsByOwner.TryGetValue(ownerId, out var list))
            {
                list = [];
                packsByOwner[ownerId] = list;
            }
            list.Add(pack);
        }

        // Load C# code mods in resolved dependency order
        var activatedEntries = new List<(LoadedModEntry Entry, ModHelperImpl Helper)>();

        foreach (var discovered in resolved.OrderedCodeMods)
        {
            var manifest = discovered.Manifest;
            var dllPath = Path.Combine(discovered.DirectoryPath, manifest.EntryDll!);
            ModAssemblyLoadContext? alc = null;

            try
            {
                alc = new ModAssemblyLoadContext(manifest.UniqueID, dllPath, _additionalSharedAssemblies);
                var assembly = alc.LoadModAssemblyFromDisk(dllPath);
                var modType = FindModEntryType(assembly);

                if (modType is null)
                {
                    alc.Unload();
                    allSkipped.Add(new SkippedMod(manifest.Name, discovered.DirectoryPath,
                        $"Assembly '{manifest.EntryDll}' does not contain a concrete public class implementing IMod."));
                    continue;
                }

                var instance = (IMod?)Activator.CreateInstance(modType)
                    ?? throw new InvalidOperationException($"Failed to instantiate '{modType.FullName}'.");

                var modMonitor = _modMonitorFactory(manifest.Name);
                var ownedPacks = packsByOwner.TryGetValue(manifest.UniqueID, out var list)
                    ? (IReadOnlyList<IContentPack>)list
                    : Array.Empty<IContentPack>();

                var helper = new ModHelperImpl(
                    manifest,
                    discovered.DirectoryPath,
                    Events,
                    Assets,
                    Registry,
                    ownedPacks,
                    modMonitor);

                if (instance is ModBase modBase)
                    modBase.InitializeRuntime(manifest, helper, modMonitor);

                var entry = new LoadedModEntry(manifest, discovered.DirectoryPath, instance, alc);
                _loadedMods.Add(entry);
                Registry.RegisterMod(manifest, instance.GetApi);
                activatedEntries.Add((entry, helper));
            }
            catch (Exception ex)
            {
                alc?.Unload();
                _loaderMonitor.LogError($"Failed to load mod '{manifest.Name}' ({manifest.UniqueID}).", ex);
                allSkipped.Add(new SkippedMod(manifest.Name, discovered.DirectoryPath, $"Load exception: {ex.Message}"));
            }
        }

        // Invoke Entry(helper) after all loaded mods & APIs are registered in ModRegistry
        foreach (var (entry, helper) in activatedEntries)
        {
            try
            {
                _loaderMonitor.Log($"Initializing {entry.Manifest.Name} v{entry.Manifest.Version} by {entry.Manifest.Author}...");
                entry.Instance.Entry(helper);
            }
            catch (Exception ex)
            {
                _loaderMonitor.LogError($"Mod '{entry.Manifest.Name}' threw during Entry(). Unsubscribing its events.", ex);
                Events.UnsubscribeAll(entry.Manifest.UniqueID);
            }
        }

        LogLoadSummary(_loadedMods, _loadedContentPacks, allSkipped);

        return new ModLoadReport(
            _loadedMods.Select(m => m.Manifest).ToArray(),
            _loadedContentPacks.Select(p => p.Manifest).ToArray(),
            allSkipped,
            resolved.Warnings);
    }

    public void RaiseGameLaunched()
    {
        var allManifests = _loadedMods.Select(m => m.Manifest)
            .Concat(_loadedContentPacks.Select(p => p.Manifest))
            .ToArray();

        Events.RaiseGameLaunched(this, new GameLaunchedEventArgs(allManifests));
    }

    public void RaiseUpdateTicked(long tick)
        => Events.RaiseUpdateTicked(this, new UpdateTickedEventArgs(tick));

    public void RaiseDayStarted(int day)
        => Events.RaiseDayStarted(this, new DayStartedEventArgs(day));

    private static Type? FindModEntryType(Assembly assembly)
    {
        return assembly.GetExportedTypes()
            .FirstOrDefault(t =>
                !t.IsAbstract &&
                !t.IsInterface &&
                typeof(IMod).IsAssignableFrom(t) &&
                t.GetConstructor(Type.EmptyTypes) is not null);
    }

    private void LogLoadSummary(
        List<LoadedModEntry> codeMods,
        List<IContentPack> contentPacks,
        List<SkippedMod> skipped)
    {
        _loaderMonitor.Log($"Loaded {codeMods.Count} C# mod(s) and {contentPacks.Count} content pack(s).");

        foreach (var mod in codeMods)
            _loaderMonitor.Log($"   + {mod.Manifest.Name} {mod.Manifest.Version} [{mod.Manifest.UniqueID}]");

        foreach (var pack in contentPacks)
            _loaderMonitor.Log($"   + {pack.Manifest.Name} {pack.Manifest.Version} (content pack for {pack.Manifest.ContentPackFor!.UniqueID})");

        if (skipped.Count > 0)
        {
            _loaderMonitor.Log($"Skipped {skipped.Count} mod(s):", LogLevel.Warn);
            foreach (var s in skipped)
                _loaderMonitor.Log($"   - {s.DisplayName}: {s.Reason}", LogLevel.Warn);
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _loaderMonitor.Log("Shutting down ModLoader and unloading mod contexts...");

        // Dispose in reverse load order so dependent mods tear down before their providers
        for (var i = _loadedMods.Count - 1; i >= 0; i--)
        {
            var entry = _loadedMods[i];
            Events.UnsubscribeAll(entry.Manifest.UniqueID);

            try
            {
                entry.Instance.Dispose();
            }
            catch (Exception ex)
            {
                _loaderMonitor.LogError($"Error disposing mod '{entry.Manifest.Name}'.", ex);
            }

            try
            {
                entry.LoadContext.Unload();
            }
            catch (Exception ex)
            {
                _loaderMonitor.LogError($"Error unloading ALC for mod '{entry.Manifest.Name}'.", ex);
            }
        }

        _loadedMods.Clear();
        _loadedContentPacks.Clear();
        Registry.Clear();
        Assets.ClearCache();
    }
}
