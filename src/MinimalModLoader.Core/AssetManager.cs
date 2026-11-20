using System.Collections.Concurrent;
using MinimalModLoader.Abstractions;

namespace MinimalModLoader.Core;

/// <summary>
/// Interceptable Asset Pipeline supporting caching, full replacement (LoadFrom / LoadFromModFile),
/// prioritized edits (Early -> Default -> Late), and cache invalidation.
/// </summary>
public sealed class AssetManager
{
    private readonly IBaseAssetProvider _baseProvider;
    private readonly ModEventBus _eventBus;
    private readonly IMonitor _loaderMonitor;
    private readonly ConcurrentDictionary<string, object> _cache = new(StringComparer.OrdinalIgnoreCase);

    public AssetManager(IBaseAssetProvider baseProvider, ModEventBus eventBus, IMonitor loaderMonitor)
    {
        _baseProvider = baseProvider;
        _eventBus = eventBus;
        _loaderMonitor = loaderMonitor;
    }

    public T Load<T>(string assetName) where T : notnull
    {
        var normalized = AssetRequestedEventArgs.NormalizeAssetName(assetName);

        if (_cache.TryGetValue(normalized, out var cached) && cached is T typedCached)
            return typedCached;

        var loads = new List<AssetLoadOperation>();
        var edits = new List<AssetEditOperation>();

        _eventBus.RaiseAssetRequested(this, normalized, typeof(T), loads, edits);

        object value = ResolveInitialAsset<T>(normalized, loads);

        var assetData = new AssetDataImpl(normalized, value);

        // Apply edits in ascending priority: Early (-100) -> Default (0) -> Late (100)
        foreach (var edit in edits.OrderBy(e => (int)e.Priority))
        {
            try
            {
                edit.Editor(assetData);
            }
            catch (Exception ex)
            {
                _loaderMonitor.LogError(
                    $"Mod '{edit.OwnerModId}' failed while editing asset '{normalized}'. Skipping edit.", ex);
            }
        }

        if (assetData.Data is not T finalTyped)
        {
            throw new InvalidCastException(
                $"Asset '{normalized}' resolved to '{assetData.Data.GetType().Name}', expected '{typeof(T).Name}'.");
        }

        _cache[normalized] = finalTyped;
        return finalTyped;
    }

    public bool InvalidateCache(string assetName)
    {
        var normalized = AssetRequestedEventArgs.NormalizeAssetName(assetName);
        return _cache.TryRemove(normalized, out _);
    }

    public void ClearCache() => _cache.Clear();

    private object ResolveInitialAsset<T>(string normalized, List<AssetLoadOperation> loads) where T : notnull
    {
        if (loads.Count > 0)
        {
            var orderedLoads = loads.OrderByDescending(l => (int)l.Priority).ToList();
            if (orderedLoads.Count > 1 && orderedLoads[0].Priority == orderedLoads[1].Priority)
            {
                _loaderMonitor.Log(
                    $"Multiple mods attempted to replace asset '{normalized}' at priority {orderedLoads[0].Priority} " +
                    $"('{orderedLoads[0].OwnerModId}' vs '{orderedLoads[1].OwnerModId}'). Using '{orderedLoads[0].OwnerModId}'.",
                    LogLevel.Warn);
            }

            foreach (var loadOp in orderedLoads)
            {
                try
                {
                    var loaded = loadOp.Loader();
                    _loaderMonitor.Log(
                        $"Asset '{normalized}' replaced by mod '{loadOp.OwnerModId}' (priority {loadOp.Priority}).",
                        LogLevel.Debug);
                    return loaded;
                }
                catch (Exception ex)
                {
                    _loaderMonitor.LogError(
                        $"Mod '{loadOp.OwnerModId}' failed to load replacement for asset '{normalized}'. Falling back.", ex);
                }
            }
        }

        return _baseProvider.LoadBaseAsset<T>(normalized);
    }

    private sealed class AssetDataImpl : IAssetData
    {
        public string AssetName { get; }
        public object Data { get; private set; }
        public Type DataType => Data.GetType();

        public AssetDataImpl(string assetName, object initialData)
        {
            AssetName = assetName;
            Data = initialData;
        }

        public T GetData<T>()
        {
            if (Data is T typed)
                return typed;

            throw new InvalidCastException(
                $"Asset '{AssetName}' has type '{Data.GetType().Name}', cannot cast to '{typeof(T).Name}'.");
        }

        public void ReplaceWith<T>(T value) where T : notnull
        {
            ArgumentNullException.ThrowIfNull(value);
            Data = value;
        }
    }
}
