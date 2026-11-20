using System.Text.Json;
using MinimalModLoader.Abstractions;

namespace MinimalModLoader.Core;

public sealed class ModHelperImpl : IModHelper
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    public ModManifest Manifest { get; }
    public string DirectoryPath { get; }
    public IModEvents Events { get; }
    public IContentHelper Content { get; }
    public IContentPackHelper ContentPacks { get; }
    public IModRegistry Registry { get; }

    public ModHelperImpl(
        ModManifest manifest,
        string directoryPath,
        ModEventBus eventBus,
        AssetManager assetManager,
        IModRegistry registry,
        IReadOnlyList<IContentPack> ownedContentPacks,
        IMonitor monitor)
    {
        Manifest = manifest;
        DirectoryPath = directoryPath;
        Registry = registry;
        ContentPacks = new ContentPackHelperImpl(ownedContentPacks);
        Content = new ContentHelperImpl(directoryPath, assetManager);
        Events = eventBus.CreateScopedEvents(
            manifest.UniqueID,
            monitor,
            (relativePath, targetType) => LoadFileByType(directoryPath, relativePath, targetType));
    }

    public TConfig ReadConfig<TConfig>() where TConfig : class, new()
    {
        var configPath = Path.Combine(DirectoryPath, "config.json");
        if (!File.Exists(configPath))
        {
            var defaultConfig = new TConfig();
            WriteConfig(defaultConfig);
            return defaultConfig;
        }

        var json = File.ReadAllText(configPath);
        return JsonSerializer.Deserialize<TConfig>(json, JsonOptions) ?? new TConfig();
    }

    public void WriteConfig<TConfig>(TConfig config) where TConfig : class
    {
        ArgumentNullException.ThrowIfNull(config);
        var configPath = Path.Combine(DirectoryPath, "config.json");
        var json = JsonSerializer.Serialize(config, JsonOptions);
        File.WriteAllText(configPath, json);
    }

    internal static object LoadFileByType(string modDirectory, string relativePath, Type targetType)
    {
        var fullPath = ModPathGuard.ResolveSafePath(modDirectory, relativePath);
        if (!File.Exists(fullPath))
            throw new FileNotFoundException($"Mod file '{relativePath}' was not found.", fullPath);

        if (targetType == typeof(string))
            return File.ReadAllText(fullPath).TrimEnd('\r', '\n');

        if (targetType == typeof(byte[]))
            return File.ReadAllBytes(fullPath);

        var json = File.ReadAllText(fullPath);
        var deserialized = JsonSerializer.Deserialize(json, targetType, JsonOptions);
        return deserialized ?? throw new InvalidOperationException(
            $"Deserializing '{relativePath}' into '{targetType.Name}' returned null.");
    }

    private sealed class ContentHelperImpl : IContentHelper
    {
        private readonly string _modDirectory;
        private readonly AssetManager _assetManager;

        public ContentHelperImpl(string modDirectory, AssetManager assetManager)
        {
            _modDirectory = modDirectory;
            _assetManager = assetManager;
        }

        public T LoadModFile<T>(string relativePath)
            => (T)LoadFileByType(_modDirectory, relativePath, typeof(T));

        public bool InvalidateCache(string assetName)
            => _assetManager.InvalidateCache(assetName);

        public T LoadGameAsset<T>(string assetName) where T : notnull
            => _assetManager.Load<T>(assetName);
    }

    private sealed class ContentPackHelperImpl : IContentPackHelper
    {
        private readonly IReadOnlyList<IContentPack> _packs;
        public ContentPackHelperImpl(IReadOnlyList<IContentPack> packs) => _packs = packs;
        public IEnumerable<IContentPack> GetOwned() => _packs;
    }
}
