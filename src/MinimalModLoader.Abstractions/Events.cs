namespace MinimalModLoader.Abstractions;

public enum AssetLoadPriority
{
    Low = -100,
    Medium = 0,
    High = 100
}

public enum AssetEditPriority
{
    Early = -100,
    Default = 0,
    Late = 100
}

/// <summary>
/// Wraps a loaded asset so mods can inspect or mutate it cleanly during AssetRequested.
/// </summary>
public interface IAssetData
{
    string AssetName { get; }
    Type DataType { get; }
    object Data { get; }
    T GetData<T>();
    void ReplaceWith<T>(T value) where T : notnull;
}

/// <summary>
/// Fired when the host requests an asset through the Asset Pipeline.
/// Each subscribed mod receives an instance bound to its own directory and UniqueID.
/// </summary>
public sealed class AssetRequestedEventArgs : EventArgs
{
    public string AssetName { get; }
    public Type DataType { get; }

    private readonly string _ownerModId;
    private readonly Func<string, Type, object> _modFileLoader;
    private readonly List<AssetLoadOperation> _loads;
    private readonly List<AssetEditOperation> _edits;

    public AssetRequestedEventArgs(
        string assetName,
        Type dataType,
        string ownerModId,
        Func<string, Type, object> modFileLoader,
        List<AssetLoadOperation> sharedLoads,
        List<AssetEditOperation> sharedEdits)
    {
        AssetName = assetName;
        DataType = dataType;
        _ownerModId = ownerModId;
        _modFileLoader = modFileLoader;
        _loads = sharedLoads;
        _edits = sharedEdits;
    }

    public bool NameIs(string targetName)
        => string.Equals(NormalizeAssetName(AssetName), NormalizeAssetName(targetName), StringComparison.OrdinalIgnoreCase);

    /// <summary>Provide a brand-new asset instance replacing the base game asset.</summary>
    public void LoadFrom<T>(Func<T> loader, AssetLoadPriority priority = AssetLoadPriority.Medium)
        where T : notnull
    {
        _loads.Add(new AssetLoadOperation(() => loader(), priority, _ownerModId));
    }

    /// <summary>Replace the asset by reading a file relative to this mod's directory.</summary>
    public void LoadFromModFile<T>(string relativePath, AssetLoadPriority priority = AssetLoadPriority.Medium)
        where T : notnull
    {
        _loads.Add(new AssetLoadOperation(() => _modFileLoader(relativePath, typeof(T)), priority, _ownerModId));
    }

    /// <summary>Mutate or transform the asset after it has been loaded.</summary>
    public void Edit(Action<IAssetData> editor, AssetEditPriority priority = AssetEditPriority.Default)
    {
        _edits.Add(new AssetEditOperation(editor, priority, _ownerModId));
    }

    public static string NormalizeAssetName(string assetName)
        => assetName.Replace('\\', '/').Trim().Trim('/');
}

public sealed record AssetLoadOperation(Func<object> Loader, AssetLoadPriority Priority, string OwnerModId);
public sealed record AssetEditOperation(Action<IAssetData> Editor, AssetEditPriority Priority, string OwnerModId);

public sealed class GameLaunchedEventArgs : EventArgs
{
    public IReadOnlyList<ModManifest> LoadedMods { get; }
    public GameLaunchedEventArgs(IReadOnlyList<ModManifest> loadedMods) => LoadedMods = loadedMods;
}

public sealed class UpdateTickedEventArgs : EventArgs
{
    public long Tick { get; }
    public UpdateTickedEventArgs(long tick) => Tick = tick;
}

public sealed class DayStartedEventArgs : EventArgs
{
    public int Day { get; }
    public DayStartedEventArgs(int day) => Day = day;
}

public interface IModEvents
{
    event EventHandler<GameLaunchedEventArgs>? GameLaunched;
    event EventHandler<UpdateTickedEventArgs>? UpdateTicked;
    event EventHandler<DayStartedEventArgs>? DayStarted;
    event EventHandler<AssetRequestedEventArgs>? AssetRequested;
}
