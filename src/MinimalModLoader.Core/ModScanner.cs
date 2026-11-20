using System.Text.Json;
using MinimalModLoader.Abstractions;

namespace MinimalModLoader.Core;

public sealed record DiscoveredMod(
    ModManifest Manifest,
    SemVer ParsedVersion,
    string DirectoryPath,
    bool IsContentPack);

public sealed record SkippedMod(
    string DisplayName,
    string DirectoryPath,
    string Reason);

public sealed record ModScanResult(
    IReadOnlyList<DiscoveredMod> ValidManifests,
    IReadOnlyList<SkippedMod> InvalidMods);

/// <summary>
/// Recursively discovers mods by locating manifest.json files while skipping dot-prefixed folders.
/// </summary>
public sealed class ModScanner
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    public ModScanResult Scan(string modsRoot)
    {
        var valid = new List<DiscoveredMod>();
        var invalid = new List<SkippedMod>();

        if (!Directory.Exists(modsRoot))
            return new ModScanResult(valid, invalid);

        ScanDirectory(modsRoot, valid, invalid);
        return new ModScanResult(valid, invalid);
    }

    private static void ScanDirectory(string directory, List<DiscoveredMod> valid, List<SkippedMod> invalid)
    {
        var manifestPath = Path.Combine(directory, "manifest.json");
        if (File.Exists(manifestPath))
        {
            ParseManifest(directory, manifestPath, valid, invalid);
            // Stop descending once a mod folder is identified (a mod's subfolders are assets/libs)
            return;
        }

        foreach (var subDir in Directory.EnumerateDirectories(directory).OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
        {
            var folderName = Path.GetFileName(subDir);
            // Ignore hidden or disabled folders like ".disabled" or ".git"
            if (folderName.StartsWith('.'))
                continue;

            ScanDirectory(subDir, valid, invalid);
        }
    }

    private static void ParseManifest(
        string directory,
        string manifestPath,
        List<DiscoveredMod> valid,
        List<SkippedMod> invalid)
    {
        var fallbackName = Path.GetFileName(directory);
        ModManifest? manifest;
        try
        {
            var json = File.ReadAllText(manifestPath);
            manifest = JsonSerializer.Deserialize<ModManifest>(json, JsonOptions);
        }
        catch (Exception ex)
        {
            invalid.Add(new SkippedMod(fallbackName, directory, $"Failed to parse manifest.json: {ex.Message}"));
            return;
        }

        if (manifest is null)
        {
            invalid.Add(new SkippedMod(fallbackName, directory, "manifest.json deserialized to null."));
            return;
        }

        var displayName = string.IsNullOrWhiteSpace(manifest.Name) ? fallbackName : manifest.Name;

        if (!manifest.Enabled)
        {
            invalid.Add(new SkippedMod(displayName, directory, "Disabled in manifest.json (Enabled = false)."));
            return;
        }

        if (string.IsNullOrWhiteSpace(manifest.UniqueID))
        {
            invalid.Add(new SkippedMod(displayName, directory, "Missing required 'UniqueID' in manifest.json."));
            return;
        }

        if (!SemVer.TryParse(manifest.Version, out var parsedVersion))
        {
            invalid.Add(new SkippedMod(displayName, directory, $"Invalid semantic Version '{manifest.Version}'."));
            return;
        }

        var hasDll = !string.IsNullOrWhiteSpace(manifest.EntryDll);
        var hasContentPackTarget = manifest.ContentPackFor is not null && !string.IsNullOrWhiteSpace(manifest.ContentPackFor.UniqueID);

        if (!hasDll && !hasContentPackTarget)
        {
            invalid.Add(new SkippedMod(displayName, directory, "Must specify either 'EntryDll' (C# mod) or 'ContentPackFor' (Content Pack)."));
            return;
        }

        if (hasDll && hasContentPackTarget)
        {
            invalid.Add(new SkippedMod(displayName, directory, "Cannot specify both 'EntryDll' and 'ContentPackFor'."));
            return;
        }

        if (hasDll)
        {
            var dllPath = Path.Combine(directory, manifest.EntryDll!);
            if (!File.Exists(dllPath))
            {
                invalid.Add(new SkippedMod(displayName, directory, $"EntryDll '{manifest.EntryDll}' not found in mod directory."));
                return;
            }
        }

        valid.Add(new DiscoveredMod(manifest, parsedVersion, directory, IsContentPack: hasContentPackTarget));
    }
}
