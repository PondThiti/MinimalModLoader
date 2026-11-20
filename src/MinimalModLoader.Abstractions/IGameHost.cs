namespace MinimalModLoader.Abstractions;

/// <summary>
/// Minimal abstraction over the host game's asset storage, allowing the Mod Loader Core
/// to stay decoupled from any specific game engine (Console demo, MonoGame, .NET for Android).
/// </summary>
public interface IBaseAssetProvider
{
    T LoadBaseAsset<T>(string normalizedAssetName) where T : notnull;
}
