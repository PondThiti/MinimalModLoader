namespace MinimalModLoader.Abstractions;

/// <summary>
/// Describes the type of game installation or package archive discovered by <see cref="IGameLocator"/>.
/// </summary>
public enum GameSourceKind
{
    /// <summary>An unpacked desktop/host game installation directory (e.g. containing Stardew Valley.dll + Content/).</summary>
    InstalledDirectory,

    /// <summary>A standalone Android APK package verified via its binary AndroidManifest.xml.</summary>
    AndroidApk,

    /// <summary>A split Android bundle archive (.apks or .xapk) verified via bundle metadata or nested base APK.</summary>
    AndroidBundle,

    /// <summary>Fallback built-in demo host when no external game installation is configured.</summary>
    DemoBuiltIn
}

/// <summary>
/// Indicates how a <see cref="GameLocation"/> was discovered.
/// </summary>
public enum GameDiscoveryMethod
{
    CommandLineArgument,
    EnvironmentVariable,
    SavedConfiguration,
    StandardInstallPath,
    FileSystemScan,
    CustomProvider,
    BuiltInFallback
}

/// <summary>
/// Metadata for a discovered game installation or verified game archive.
/// </summary>
public sealed record GameLocation(
    string LocationPath,
    GameSourceKind SourceKind,
    GameDiscoveryMethod DiscoveryMethod,
    string DisplayName,
    string? PackageOrAssemblyId = null,
    string? VersionName = null,
    long? VersionCode = null,
    string? ContentDirectory = null,
    long SizeBytes = 0,
    bool IsVerified = true);

/// <summary>
/// Configuration controlling where and how <see cref="IGameLocator"/> searches for the game.
/// </summary>
public sealed record GameLocatorOptions
{
    public const string DefaultEnvironmentVariable = "MINIMAL_MOD_LOADER_GAME_PATH";
    public const string SecondaryEnvironmentVariable = "STARDEW_GAME_PATH";
    public const string DefaultAndroidPackageId = "com.chucklefish.stardewvalley";

    /// <summary>Optional explicit game file or directory path (e.g. passed via --game-path).</summary>
    public string? ExplicitPath { get; init; }

    /// <summary>Primary environment variable name to inspect for a game path.</summary>
    public string EnvironmentVariableName { get; init; } = DefaultEnvironmentVariable;

    /// <summary>Additional environment variable names checked after <see cref="EnvironmentVariableName"/>.</summary>
    public IReadOnlyList<string> FallbackEnvironmentVariables { get; init; } = [SecondaryEnvironmentVariable];

    /// <summary>Optional path to a JSON file persisting the user's preferred game path.</summary>
    public string? ConfigFilePath { get; init; }

    /// <summary>Additional directories to scan recursively (e.g. workspace root, downloads).</summary>
    public IReadOnlyList<string> AdditionalSearchRoots { get; init; } = [];

    /// <summary>Whether to inspect OS-standard install locations (Steam/GOG/Xbox/Android shared folders).</summary>
    public bool IncludeStandardInstallPaths { get; init; } = true;

    /// <summary>Whether to scan common user folders and mounted drive roots with bounded depth.</summary>
    public bool ScanDrivesAndCommonFolders { get; init; } = true;

    /// <summary>Whether to include mounted filesystem drive roots when scanning.</summary>
    public bool IncludeDriveRoots { get; init; } = true;

    /// <summary>Maximum directory traversal depth during recursive scans.</summary>
    public int MaxScanDepth { get; init; } = 4;

    /// <summary>Safety cap on the number of directories visited during a scan.</summary>
    public int MaxVisitedDirectories { get; init; } = 1500;

    /// <summary>
    /// Expected Android package name when verifying .apk/.apks/.xapk files.
    /// Set to null or empty to accept any valid APK package.
    /// </summary>
    public string? TargetAndroidPackageId { get; init; } = DefaultAndroidPackageId;

    /// <summary>
    /// Filenames that identify an unpacked desktop game installation directory.
    /// </summary>
    public IReadOnlyList<string> ExpectedDesktopMarkers { get; init; } =
    [
        "Stardew Valley.dll",
        "StardewValley.dll",
        "Stardew Valley.exe",
        "StardewValley.exe"
    ];
}

/// <summary>
/// Full result of running <see cref="IGameLocator.LocateAll"/> across all registered providers.
/// </summary>
public sealed record GameDiscoveryReport(
    GameLocation? Selected,
    IReadOnlyList<GameLocation> Candidates,
    IReadOnlyList<string> SearchedLocations,
    IReadOnlyList<string> Diagnostics);

/// <summary>
/// Extensible discovery source for <see cref="IGameLocator"/>.
/// Platform hosts (such as a future .NET for Android runtime) can register custom providers
/// backed by Android PackageManager, StorageManager, or Storage Access Framework (SAF).
/// </summary>
public interface IGameLocationProvider
{
    string ProviderName { get; }

    /// <summary>Lower values are queried earlier and preferred during selection.</summary>
    int Priority { get; }

    IEnumerable<GameLocation> Discover(
        GameLocatorOptions options,
        ICollection<string> searchedLocations,
        ICollection<string> diagnostics);
}

/// <summary>
/// Discovers game installations or verified package archives regardless of where they reside on disk
/// (or on platform-specific providers such as Android PackageManager/SAF).
/// </summary>
public interface IGameLocator
{
    GameDiscoveryReport LocateAll(GameLocatorOptions? options = null);
    GameLocation? LocateBest(GameLocatorOptions? options = null);
    GameLocation? InspectPath(string path, GameDiscoveryMethod method, GameLocatorOptions? options = null);
}
