namespace MinimalModLoader.Abstractions;

/// <summary>
/// Declarative metadata loaded from a mod's manifest.json.
/// Supports both C# DLL mods (EntryDll) and JSON Content Packs (ContentPackFor).
/// </summary>
public sealed class ModManifest
{
    public string UniqueID { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Author { get; set; } = "Unknown";
    public string Version { get; set; } = "1.0.0";
    public string Description { get; set; } = string.Empty;
    public string? MinimumLoaderVersion { get; set; }

    /// <summary>Relative DLL filename for C# code mods (e.g., "MyMod.dll").</summary>
    public string? EntryDll { get; set; }

    /// <summary>If set, this folder is a data-only Content Pack owned by another C# mod.</summary>
    public ContentPackTarget? ContentPackFor { get; set; }

    public List<ModDependency> Dependencies { get; set; } = [];
    public bool Enabled { get; set; } = true;
}

public sealed class ContentPackTarget
{
    public string UniqueID { get; set; } = string.Empty;
    public string? MinimumVersion { get; set; }
}

public sealed class ModDependency
{
    public string UniqueID { get; set; } = string.Empty;
    public string? MinimumVersion { get; set; }
    public bool IsRequired { get; set; } = true;
}
