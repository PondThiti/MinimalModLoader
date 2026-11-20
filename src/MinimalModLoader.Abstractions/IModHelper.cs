namespace MinimalModLoader.Abstractions;

public interface IContentHelper
{
    /// <summary>Loads a text or JSON file from inside this mod's folder.</summary>
    T LoadModFile<T>(string relativePath);

    /// <summary>Evicts an asset from the Asset Pipeline cache so AssetRequested fires again on next load.</summary>
    bool InvalidateCache(string assetName);

    /// <summary>Loads an asset through the game's Asset Pipeline (including mod overrides).</summary>
    T LoadGameAsset<T>(string assetName) where T : notnull;
}

public interface IModRegistry
{
    bool IsLoaded(string uniqueId);
    ModManifest? GetManifest(string uniqueId);
    IReadOnlyCollection<ModManifest> GetAllManifests();

    /// <summary>
    /// Retrieves another mod's public API object, automatically wrapping it in a collectible
    /// duck-typed proxy if the caller defines its own matching interface without referencing the provider DLL.
    /// </summary>
    TInterface? GetApi<TInterface>(string uniqueId) where TInterface : class;
}

/// <summary>
/// Central facade injected into every mod's Entry(IModHelper) method.
/// </summary>
public interface IModHelper
{
    ModManifest Manifest { get; }
    string DirectoryPath { get; }
    IModEvents Events { get; }
    IContentHelper Content { get; }
    IContentPackHelper ContentPacks { get; }
    IModRegistry Registry { get; }

    /// <summary>Reads config.json from the mod directory, creating a default file if missing.</summary>
    TConfig ReadConfig<TConfig>() where TConfig : class, new();

    /// <summary>Writes config.json back to the mod directory.</summary>
    void WriteConfig<TConfig>(TConfig config) where TConfig : class;
}
