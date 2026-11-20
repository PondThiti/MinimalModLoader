using MinimalModLoader.Abstractions;

namespace MinimalModLoader.Core.GameLocator;

/// <summary>
/// Cross-platform game discovery engine combining explicit CLI paths, environment variables,
/// persisted JSON configuration, standard OS install locations, bounded filesystem scanning,
/// and pluggable platform providers (such as Android PackageManager / SAF).
/// </summary>
public sealed class GameLocator : IGameLocator
{
    private readonly List<IGameLocationProvider> _providers;

    public GameLocator(IEnumerable<IGameLocationProvider>? providers = null)
    {
        _providers = providers is not null
            ? providers.OrderBy(p => p.Priority).ToList()
            : CreateDefaultProviders();
    }

    public IReadOnlyList<IGameLocationProvider> Providers => _providers;

    /// <summary>
    /// Registers an additional platform-specific provider (e.g. Android PackageManager or SAF).
    /// </summary>
    public GameLocator AddProvider(IGameLocationProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        _providers.Add(provider);
        _providers.Sort((a, b) => a.Priority.CompareTo(b.Priority));
        return this;
    }

    public GameDiscoveryReport LocateAll(GameLocatorOptions? options = null)
    {
        var opts = options ?? new GameLocatorOptions();
        var searched = new List<string>();
        var diagnostics = new List<string>();
        var candidates = new List<GameLocation>();
        var seenCanonicalPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var provider in _providers)
        {
            IEnumerable<GameLocation> discovered;
            try
            {
                discovered = provider.Discover(opts, searched, diagnostics).ToList();
            }
            catch (Exception ex)
            {
                diagnostics.Add($"Provider '{provider.ProviderName}' failed: {ex.Message}");
                continue;
            }

            foreach (var loc in discovered)
            {
                var key = NormalizeDedupeKey(loc.LocationPath);
                if (seenCanonicalPaths.Add(key))
                    candidates.Add(loc);
            }
        }

        var selected = SelectBestCandidate(candidates);
        return new GameDiscoveryReport(selected, candidates, searched, diagnostics);
    }

    public GameLocation? LocateBest(GameLocatorOptions? options = null)
        => LocateAll(options).Selected;

    public GameLocation? InspectPath(string path, GameDiscoveryMethod method, GameLocatorOptions? options = null)
        => GamePathInspector.Inspect(path, method, options ?? new GameLocatorOptions());

    /// <summary>
    /// Validates and persists a user-chosen game path into a JSON configuration file.
    /// </summary>
    public GameLocation SaveConfiguredPath(string configFilePath, string gamePath, GameLocatorOptions? options = null)
    {
        var opts = options ?? new GameLocatorOptions();
        var inspected = GamePathInspector.Inspect(gamePath, GameDiscoveryMethod.SavedConfiguration, opts)
            ?? throw new InvalidOperationException(
                $"'{gamePath}' is not a verified game installation or matching Android package archive.");

        ConfigFileLocationProvider.SavePath(configFilePath, inspected.LocationPath);
        return inspected;
    }

    public static List<IGameLocationProvider> CreateDefaultProviders(Func<string, string?>? envReader = null)
    {
        return
        [
            new ExplicitPathLocationProvider(),
            new EnvironmentVariableLocationProvider(envReader),
            new ConfigFileLocationProvider(),
            new StandardInstallPathsProvider(),
            new FileSystemScanLocationProvider()
        ];
    }

    private static GameLocation? SelectBestCandidate(List<GameLocation> candidates)
    {
        if (candidates.Count == 0)
            return null;

        return candidates
            .OrderBy(c => MethodRank(c.DiscoveryMethod))
            .ThenBy(c => SourceRank(c.SourceKind))
            .ThenByDescending(c => c.VersionCode ?? 0)
            .ThenByDescending(c => c.SizeBytes)
            .First();
    }

    private static int MethodRank(GameDiscoveryMethod method) => method switch
    {
        GameDiscoveryMethod.CommandLineArgument => 0,
        GameDiscoveryMethod.EnvironmentVariable => 1,
        GameDiscoveryMethod.SavedConfiguration => 2,
        GameDiscoveryMethod.CustomProvider => 3,
        GameDiscoveryMethod.StandardInstallPath => 4,
        GameDiscoveryMethod.FileSystemScan => 5,
        GameDiscoveryMethod.BuiltInFallback => 6,
        _ => 99
    };

    private static int SourceRank(GameSourceKind kind) => kind switch
    {
        GameSourceKind.InstalledDirectory => 0,
        GameSourceKind.AndroidApk => 1,
        GameSourceKind.AndroidBundle => 2,
        GameSourceKind.DemoBuiltIn => 3,
        _ => 99
    };

    private static string NormalizeDedupeKey(string locationPath)
    {
        // Preserve URI schemes (e.g. content:// on Android SAF) untouched
        if (locationPath.Contains("://", StringComparison.Ordinal))
            return locationPath;

        try
        {
            return Path.GetFullPath(locationPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch
        {
            return locationPath;
        }
    }
}
