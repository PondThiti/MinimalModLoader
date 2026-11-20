namespace MinimalModLoader.Abstractions;

/// <summary>
/// Represents a data-only Content Pack folder targeting a parent C# mod.
/// </summary>
public interface IContentPack
{
    ModManifest Manifest { get; }
    string DirectoryPath { get; }
    bool HasFile(string relativePath);
    T? ReadJsonFile<T>(string relativePath);
    string ReadTextFile(string relativePath);
}

public interface IContentPackHelper
{
    /// <summary>Returns all valid Content Packs whose ContentPackFor.UniqueID matches this mod.</summary>
    IEnumerable<IContentPack> GetOwned();
}
