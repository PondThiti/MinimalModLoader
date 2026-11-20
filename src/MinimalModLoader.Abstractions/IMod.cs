namespace MinimalModLoader.Abstractions;

/// <summary>
/// Core contract implemented by every C# mod DLL.
/// </summary>
public interface IMod : IDisposable
{
    ModManifest Manifest { get; }
    IModHelper Helper { get; }
    IMonitor Monitor { get; }

    /// <summary>Called once by the Mod Loader after dependencies are resolved.</summary>
    void Entry(IModHelper helper);

    /// <summary>Optional hook for exposing an inter-mod API object to other mods.</summary>
    object? GetApi() => null;
}

/// <summary>
/// Convenient base class for C# mods. Subclass this and override Entry(IModHelper).
/// </summary>
public abstract class ModBase : IMod
{
    public ModManifest Manifest { get; private set; } = null!;
    public IModHelper Helper { get; private set; } = null!;
    public IMonitor Monitor { get; private set; } = null!;

    /// <summary>Internal initialization called by the loader before Entry().</summary>
    public void InitializeRuntime(ModManifest manifest, IModHelper helper, IMonitor monitor)
    {
        Manifest = manifest;
        Helper = helper;
        Monitor = monitor;
    }

    public abstract void Entry(IModHelper helper);

    public virtual object? GetApi() => null;

    public virtual void Dispose()
    {
        GC.SuppressFinalize(this);
    }
}
