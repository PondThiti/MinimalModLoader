using System.Text.Json;
using System.Text.RegularExpressions;
using MinimalModLoader.Abstractions;

namespace MinimalModLoader.Core.GameLocator;

/// <summary>
/// Provider #1 (Priority 10): Inspects the explicit path supplied via CLI or API options.
/// </summary>
public sealed class ExplicitPathLocationProvider : IGameLocationProvider
{
    public string ProviderName => "ExplicitPath";
    public int Priority => 10;

    public IEnumerable<GameLocation> Discover(
        GameLocatorOptions options,
        ICollection<string> searchedLocations,
        ICollection<string> diagnostics)
    {
        if (string.IsNullOrWhiteSpace(options.ExplicitPath))
            yield break;

        searchedLocations.Add($"[CLI] {options.ExplicitPath}");
        var found = GamePathInspector.Inspect(options.ExplicitPath, GameDiscoveryMethod.CommandLineArgument, options, diagnostics);
        if (found is not null)
        {
            yield return found;
        }
        else
        {
            diagnostics.Add($"Explicit path '{options.ExplicitPath}' did not contain a verified game installation or package.");
        }
    }
}

/// <summary>
/// Provider #2 (Priority 20): Inspects configured environment variables (e.g. MINIMAL_MOD_LOADER_GAME_PATH).
/// </summary>
public sealed class EnvironmentVariableLocationProvider : IGameLocationProvider
{
    private readonly Func<string, string?> _envReader;

    public string ProviderName => "EnvironmentVariable";
    public int Priority => 20;

    public EnvironmentVariableLocationProvider(Func<string, string?>? envReader = null)
    {
        _envReader = envReader ?? Environment.GetEnvironmentVariable;
    }

    public IEnumerable<GameLocation> Discover(
        GameLocatorOptions options,
        ICollection<string> searchedLocations,
        ICollection<string> diagnostics)
    {
        var names = new List<string>();
        if (!string.IsNullOrWhiteSpace(options.EnvironmentVariableName))
            names.Add(options.EnvironmentVariableName);
        foreach (var fallback in options.FallbackEnvironmentVariables)
        {
            if (!string.IsNullOrWhiteSpace(fallback) && !names.Contains(fallback, StringComparer.OrdinalIgnoreCase))
                names.Add(fallback);
        }

        foreach (var name in names)
        {
            var value = _envReader(name);
            if (string.IsNullOrWhiteSpace(value))
                continue;

            searchedLocations.Add($"[ENV:{name}] {value}");
            var found = GamePathInspector.Inspect(value, GameDiscoveryMethod.EnvironmentVariable, options, diagnostics);
            if (found is not null)
                yield return found;
            else
                diagnostics.Add($"Environment variable {name}='{value}' did not resolve to a verified game.");
        }
    }
}

internal sealed class SavedGameLocatorConfig
{
    public string? GamePath { get; set; }
    public List<string>? ExtraSearchRoots { get; set; }
}

/// <summary>
/// Provider #3 (Priority 30): Reads a persisted JSON locator config file (e.g. game-locator.json).
/// </summary>
public sealed class ConfigFileLocationProvider : IGameLocationProvider
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        WriteIndented = true
    };

    public string ProviderName => "SavedConfig";
    public int Priority => 30;

    public IEnumerable<GameLocation> Discover(
        GameLocatorOptions options,
        ICollection<string> searchedLocations,
        ICollection<string> diagnostics)
    {
        if (string.IsNullOrWhiteSpace(options.ConfigFilePath) || !File.Exists(options.ConfigFilePath))
            yield break;

        searchedLocations.Add($"[Config] {options.ConfigFilePath}");

        SavedGameLocatorConfig? cfg;
        try
        {
            var json = File.ReadAllText(options.ConfigFilePath);
            cfg = JsonSerializer.Deserialize<SavedGameLocatorConfig>(json, JsonOptions);
        }
        catch (Exception ex)
        {
            diagnostics.Add($"Failed to parse config file '{options.ConfigFilePath}': {ex.Message}");
            yield break;
        }

        if (cfg is null || string.IsNullOrWhiteSpace(cfg.GamePath))
            yield break;

        var found = GamePathInspector.Inspect(cfg.GamePath, GameDiscoveryMethod.SavedConfiguration, options, diagnostics);
        if (found is not null)
            yield return found;
        else
            diagnostics.Add($"Saved game path '{cfg.GamePath}' is no longer valid or accessible.");
    }

    public static void SavePath(string configFilePath, string gamePath)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(configFilePath));
        if (!string.IsNullOrWhiteSpace(dir))
            Directory.CreateDirectory(dir);

        SavedGameLocatorConfig cfg = new();
        if (File.Exists(configFilePath))
        {
            try
            {
                cfg = JsonSerializer.Deserialize<SavedGameLocatorConfig>(File.ReadAllText(configFilePath), JsonOptions)
                    ?? new SavedGameLocatorConfig();
            }
            catch
            {
                cfg = new SavedGameLocatorConfig();
            }
        }

        cfg.GamePath = Path.GetFullPath(gamePath);
        File.WriteAllText(configFilePath, JsonSerializer.Serialize(cfg, JsonOptions));
    }
}

/// <summary>
/// Provider #4 (Priority 40): Checks OS-standard installation directories (Steam libraryfolders.vdf,
/// GOG, Xbox GamePass, Linux/macOS Steam, Android shared storage folders).
/// </summary>
public sealed partial class StandardInstallPathsProvider : IGameLocationProvider
{
    public string ProviderName => "StandardInstallPaths";
    public int Priority => 40;

    [GeneratedRegex("\"path\"\\s+\"([^\"]+)\"", RegexOptions.IgnoreCase)]
    private static partial Regex SteamLibraryPathRegex();

    public IEnumerable<GameLocation> Discover(
        GameLocatorOptions options,
        ICollection<string> searchedLocations,
        ICollection<string> diagnostics)
    {
        if (!options.IncludeStandardInstallPaths)
            yield break;

        var candidateDirs = BuildStandardCandidateDirectories();
        foreach (var dir in candidateDirs)
        {
            if (!Directory.Exists(dir))
                continue;

            searchedLocations.Add($"[Standard] {dir}");
            var found = GamePathInspector.Inspect(dir, GameDiscoveryMethod.StandardInstallPath, options, diagnostics);
            if (found is not null)
                yield return found;
        }
    }

    private static IReadOnlyList<string> BuildStandardCandidateDirectories()
    {
        var dirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var steamRoots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (OperatingSystem.IsWindows())
        {
            var pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);

            if (!string.IsNullOrWhiteSpace(pf86))
            {
                steamRoots.Add(Path.Combine(pf86, "Steam"));
                dirs.Add(Path.Combine(pf86, "GOG Galaxy", "Games", "Stardew Valley"));
                dirs.Add(Path.Combine(pf86, "GalaxyClient", "Games", "Stardew Valley"));
            }

            if (!string.IsNullOrWhiteSpace(pf))
            {
                steamRoots.Add(Path.Combine(pf, "Steam"));
                dirs.Add(Path.Combine(pf, "GOG Galaxy", "Games", "Stardew Valley"));
            }

            // Check every fixed drive for SteamLibrary, GOG Games, and XboxGames
            foreach (var drive in SafeGetFixedDrives())
            {
                var root = drive.RootDirectory.FullName;
                steamRoots.Add(Path.Combine(root, "Steam"));
                steamRoots.Add(Path.Combine(root, "SteamLibrary"));
                steamRoots.Add(Path.Combine(root, "Games", "Steam"));
                dirs.Add(Path.Combine(root, "GOG Games", "Stardew Valley"));
                dirs.Add(Path.Combine(root, "Games", "Stardew Valley"));
                dirs.Add(Path.Combine(root, "XboxGames", "Stardew Valley", "Content"));
            }
        }
        else if (OperatingSystem.IsMacOS())
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            steamRoots.Add(Path.Combine(home, "Library", "Application Support", "Steam"));
            dirs.Add("/Applications/Stardew Valley.app/Contents/MacOS");
        }
        else
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            steamRoots.Add(Path.Combine(home, ".local", "share", "Steam"));
            steamRoots.Add(Path.Combine(home, ".steam", "steam"));
            dirs.Add(Path.Combine(home, "GOG Games", "Stardew Valley", "game"));
        }

        // Expand Steam libraryfolders.vdf to discover games installed on custom library drives
        var expandedSteamLibraries = new HashSet<string>(steamRoots, StringComparer.OrdinalIgnoreCase);
        foreach (var steamRoot in steamRoots)
        {
            var vdf = Path.Combine(steamRoot, "steamapps", "libraryfolders.vdf");
            if (!File.Exists(vdf))
                continue;

            try
            {
                var text = File.ReadAllText(vdf);
                foreach (Match match in SteamLibraryPathRegex().Matches(text))
                {
                    var libPath = match.Groups[1].Value.Replace(@"\\", @"\");
                    if (!string.IsNullOrWhiteSpace(libPath))
                        expandedSteamLibraries.Add(libPath);
                }
            }
            catch
            {
                // Ignore unreadable VDF
            }
        }

        foreach (var lib in expandedSteamLibraries)
        {
            dirs.Add(Path.Combine(lib, "steamapps", "common", "Stardew Valley"));
            dirs.Add(Path.Combine(lib, "steamapps", "common", "Stardew Valley", "Contents", "MacOS"));
        }

        return dirs.ToList();
    }

    private static IEnumerable<DriveInfo> SafeGetFixedDrives()
    {
        try
        {
            return DriveInfo.GetDrives()
                .Where(d => d.IsReady && (d.DriveType == DriveType.Fixed || d.DriveType == DriveType.Removable))
                .ToArray();
        }
        catch
        {
            return [];
        }
    }
}

/// <summary>
/// Provider #5 (Priority 50): Bounded recursive filesystem scanner across additional search roots,
/// workspace ancestor directories, common user directories, and optional drive roots.
/// </summary>
public sealed class FileSystemScanLocationProvider : IGameLocationProvider
{
    private static readonly HashSet<string> SkippedDirectoryNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Windows",
        "ProgramData",
        "System Volume Information",
        "$Recycle.Bin",
        "$WinREAgent",
        "Recovery",
        "PerfLogs",
        "MSOCache",
        "AppData",
        "node_modules",
        ".git",
        ".vs",
        ".gradle",
        ".idea",
        ".kotlin",
        "bin",
        "obj",
        "build",
        "Android"
    };

    public string ProviderName => "FileSystemScan";
    public int Priority => 50;

    public IEnumerable<GameLocation> Discover(
        GameLocatorOptions options,
        ICollection<string> searchedLocations,
        ICollection<string> diagnostics)
    {
        var scanQueue = new Queue<(string Dir, int Depth, int MaxDepth)>();
        var enqueuedRoots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void EnqueueRoot(string? dir, int maxDepth)
        {
            if (string.IsNullOrWhiteSpace(dir))
                return;

            string full;
            try
            {
                full = Path.GetFullPath(dir);
            }
            catch
            {
                return;
            }

            if (Directory.Exists(full) && enqueuedRoots.Add(full))
            {
                searchedLocations.Add($"[Scan(d<={maxDepth})] {full}");
                scanQueue.Enqueue((full, 0, Math.Max(0, maxDepth)));
            }
        }

        // 1. Caller-supplied search roots (highest scan priority)
        foreach (var root in options.AdditionalSearchRoots)
            EnqueueRoot(root, options.MaxScanDepth);

        if (options.ScanDrivesAndCommonFolders)
        {
            // 2. Current working directory and up to 3 ancestor folders (shallow scan to catch workspace APKs)
            var current = Directory.GetCurrentDirectory();
            EnqueueRoot(current, Math.Min(options.MaxScanDepth, 3));
            var parent = Directory.GetParent(current);
            for (var level = 0; level < 3 && parent is not null; level++)
            {
                EnqueueRoot(parent.FullName, 1);
                parent = parent.Parent;
            }

            // 3. Common user folders (Downloads, Desktop, Documents, Games)
            var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!string.IsNullOrWhiteSpace(userProfile))
            {
                EnqueueRoot(Path.Combine(userProfile, "Downloads"), Math.Min(options.MaxScanDepth, 3));
                EnqueueRoot(Path.Combine(userProfile, "Desktop"), Math.Min(options.MaxScanDepth, 2));
                EnqueueRoot(Path.Combine(userProfile, "Documents"), Math.Min(options.MaxScanDepth, 2));
                EnqueueRoot(Path.Combine(userProfile, "Games"), Math.Min(options.MaxScanDepth, 3));
            }

            // 4. Optional shallow scan on mounted fixed/removable drives (depth <= 2)
            if (options.IncludeDriveRoots)
            {
                try
                {
                    foreach (var drive in DriveInfo.GetDrives())
                    {
                        if (!drive.IsReady)
                            continue;
                        if (drive.DriveType is not (DriveType.Fixed or DriveType.Removable))
                            continue;
                        EnqueueRoot(drive.RootDirectory.FullName, Math.Min(options.MaxScanDepth, 2));
                    }
                }
                catch (Exception ex)
                {
                    diagnostics.Add($"Drive enumeration skipped: {ex.Message}");
                }
            }
        }

        var visitedDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var results = new List<GameLocation>();

        while (scanQueue.Count > 0 && visitedDirs.Count < options.MaxVisitedDirectories)
        {
            var (dir, depth, maxDepth) = scanQueue.Dequeue();
            if (!visitedDirs.Add(dir))
                continue;

            // Check if this directory itself is an unpacked desktop game installation
            var dirMatch = GamePathInspector.Inspect(dir, GameDiscoveryMethod.FileSystemScan, options);
            if (dirMatch is not null)
            {
                results.Add(dirMatch);
                continue;
            }

            // Inspect candidate .apk / .apks / .xapk files directly in this directory
            IEnumerable<string> files;
            try
            {
                files = Directory.EnumerateFiles(dir);
            }
            catch
            {
                continue;
            }

            foreach (var file in files)
            {
                if (!GamePathInspector.IsSupportedArchiveFile(file))
                    continue;

                var fileMatch = GamePathInspector.Inspect(file, GameDiscoveryMethod.FileSystemScan, options, diagnostics);
                if (fileMatch is not null)
                    results.Add(fileMatch);
            }

            if (depth >= maxDepth)
                continue;

            IEnumerable<string> subDirs;
            try
            {
                subDirs = Directory.EnumerateDirectories(dir);
            }
            catch
            {
                continue;
            }

            foreach (var subDir in subDirs)
            {
                var name = Path.GetFileName(subDir);
                if (string.IsNullOrEmpty(name) || name.StartsWith('.') || SkippedDirectoryNames.Contains(name))
                    continue;

                // Avoid following reparse points / symlinks that could cause infinite loops
                try
                {
                    var attrs = File.GetAttributes(subDir);
                    if ((attrs & FileAttributes.ReparsePoint) != 0)
                        continue;
                }
                catch
                {
                    continue;
                }

                if (!visitedDirs.Contains(subDir))
                    scanQueue.Enqueue((subDir, depth + 1, maxDepth));
            }
        }

        return results;
    }
}
