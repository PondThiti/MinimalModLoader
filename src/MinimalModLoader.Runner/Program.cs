using MinimalModLoader.Abstractions;
using MinimalModLoader.Core;
using MinimalModLoader.Core.GameLocator;
using MinimalModLoader.GameHost;

var modsDirectory = Path.Combine(AppContext.BaseDirectory, "Mods");
var configPath = Path.Combine(AppContext.BaseDirectory, "game-locator.json");
string? explicitGamePath = null;
string? saveGamePath = null;
var extraSearchRoots = new List<string>();
var locateOnly = false;
var includeDriveScan = true;

for (var i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--mods-dir" when i + 1 < args.Length:
            modsDirectory = Path.GetFullPath(args[++i]);
            break;
        case "--game-path" when i + 1 < args.Length:
            explicitGamePath = args[++i];
            break;
        case "--save-game-path" when i + 1 < args.Length:
            saveGamePath = args[++i];
            break;
        case "--search-root" when i + 1 < args.Length:
            extraSearchRoots.Add(args[++i]);
            break;
        case "--config" when i + 1 < args.Length:
            configPath = Path.GetFullPath(args[++i]);
            break;
        case "--locate-only":
            locateOnly = true;
            break;
        case "--no-drive-scan":
            includeDriveScan = false;
            break;
        case "-h":
        case "--help":
            PrintUsage();
            return;
    }
}

Console.WriteLine("============================================================");
Console.WriteLine("  MinimalModLoader (C# / .NET 8) — From-Scratch Demo Host");
Console.WriteLine("============================================================");
Console.WriteLine();

// 0) Locate external game installation or verified Android APK/bundle anywhere on the system
var locator = new GameLocator();
var locatorOptions = new GameLocatorOptions
{
    ExplicitPath = explicitGamePath,
    ConfigFilePath = configPath,
    AdditionalSearchRoots = extraSearchRoots,
    IncludeDriveRoots = includeDriveScan
};

if (!string.IsNullOrWhiteSpace(saveGamePath))
{
    var saved = locator.SaveConfiguredPath(configPath, saveGamePath, locatorOptions);
    Console.WriteLine($"[GameLocator] Saved verified game path to '{configPath}':");
    Console.WriteLine($"              {saved.DisplayName} ({saved.SourceKind}) -> {saved.LocationPath}");
    Console.WriteLine();
}

var discovery = locator.LocateAll(locatorOptions);
Console.WriteLine($"[GameLocator] Searched {discovery.SearchedLocations.Count} root(s), found {discovery.Candidates.Count} verified game source(s):");
if (discovery.Selected is not null)
{
    var sel = discovery.Selected;
    var sizeMb = sel.SizeBytes > 0 ? $" | {sel.SizeBytes / (1024.0 * 1024.0):F1} MB" : "";
    var ver = string.IsNullOrWhiteSpace(sel.VersionName) ? "unknown" : $"v{sel.VersionName}";
    var code = sel.VersionCode is > 0 ? $" (code {sel.VersionCode})" : "";
    Console.WriteLine($"  * ACTIVE   : [{sel.DiscoveryMethod}] {sel.DisplayName} ({sel.SourceKind})");
    Console.WriteLine($"               Package/Id : {sel.PackageOrAssemblyId ?? "n/a"} | Version: {ver}{code}{sizeMb}");
    Console.WriteLine($"               Location   : {sel.LocationPath}");
    if (discovery.Candidates.Count > 1)
    {
        Console.WriteLine("  * OTHERS   :");
        foreach (var other in discovery.Candidates.Where(c => c.LocationPath != sel.LocationPath))
            Console.WriteLine($"               - [{other.DiscoveryMethod}] {other.DisplayName} ({other.SourceKind}) @ {other.LocationPath}");
    }
}
else
{
    Console.WriteLine("  * ACTIVE   : None found on disk — falling back to built-in DemoGame host.");
    Console.WriteLine("               Tip: pass --game-path <dir-or-apk> or set MINIMAL_MOD_LOADER_GAME_PATH.");
}
Console.WriteLine();

if (locateOnly)
{
    if (discovery.Diagnostics.Count > 0)
    {
        Console.WriteLine("[GameLocator] Diagnostics:");
        foreach (var line in discovery.Diagnostics)
            Console.WriteLine($"  - {line}");
    }
    return;
}

var game = new DemoGame();

// 1) Baseline check BEFORE loading any mods
var baselineParsnip = PricingEngine.CalculateSellPrice("Parsnip", 35, 10);
Console.WriteLine($"[Vanilla] Greeting           : {game.LoadBaseAsset<string>("Strings/Greeting")}");
Console.WriteLine($"[Vanilla] Sell 10x Parsnip   : {baselineParsnip}g");
Console.WriteLine();

// 2) Boot ModLoader, discover manifests, resolve dependencies, load isolated ALCs + Harmony patches
using (var loader = new ModLoader(game, minLogLevel: LogLevel.Debug))
{
    var report = loader.LoadMods(modsDirectory);
    Console.WriteLine();

    // 3) Fire GameLaunched (mods connect to inter-mod APIs here)
    loader.RaiseGameLaunched();
    Console.WriteLine();

    // 4) Load assets through the Asset Pipeline (AssetRequested -> LoadFrom + Edit)
    var greeting = loader.Assets.Load<string>("Strings/Greeting");
    var crops = loader.Assets.Load<Dictionary<string, CropDefinition>>("Data/Crops");

    Console.WriteLine($"[Modded]  Greeting           : {greeting}");
    Console.WriteLine($"[Modded]  Available Crops ({crops.Count}):");
    foreach (var crop in crops.Values.OrderBy(c => c.BasePrice))
        Console.WriteLine($"          - {crop.Id,-15} | {crop.DisplayName,-30} | {crop.BasePrice,4}g | {crop.DaysToGrow,2}d");
    Console.WriteLine();

    // 5) Simulate game loop + Harmony-patched PricingEngine
    loader.RaiseDayStarted(game.CurrentDay);
    for (var i = 0; i < 3; i++)
        loader.RaiseUpdateTicked(game.StepTick());

    var parsnipRevenue = game.SellCrop(crops, "Parsnip", 10);
    var ancientFruitRevenue = game.SellCrop(crops, "AncientFruit", 2);

    Console.WriteLine();
    Console.WriteLine($"[Modded]  Sell 10x Parsnip      : {parsnipRevenue}g (was {baselineParsnip}g vanilla — Harmony 1.5x!)");
    Console.WriteLine($"[Modded]  Sell  2x AncientFruit : {ancientFruitRevenue}g (from JSON Content Pack + Harmony 1.5x!)");
    Console.WriteLine($"[Modded]  Player Gold           : {game.Gold}g");
    Console.WriteLine();

    // 6) Advance day and trigger events
    loader.RaiseDayStarted(game.NextDay());
    Console.WriteLine();
}

// 7) Verify clean teardown & Harmony unpatch after ModLoader.Dispose()
var postUnloadParsnip = PricingEngine.CalculateSellPrice("Parsnip", 35, 10);
Console.WriteLine();
Console.WriteLine($"[Post-Unload] Sell 10x Parsnip  : {postUnloadParsnip}g (Harmony cleanly unpatched!)");

static void PrintUsage()
{
    Console.WriteLine("MinimalModLoader.Runner options:");
    Console.WriteLine("  --game-path <path>       Explicitly specify game directory or .apk/.apks/.xapk");
    Console.WriteLine("  --save-game-path <path>  Verify and save game path into game-locator.json");
    Console.WriteLine("  --search-root <dir>      Additional directory to scan (repeatable)");
    Console.WriteLine("  --config <file>          Path to game-locator.json config file");
    Console.WriteLine("  --locate-only            Print discovered game locations and exit");
    Console.WriteLine("  --no-drive-scan          Skip scanning mounted drive roots");
    Console.WriteLine("  --mods-dir <dir>         Directory containing mod folders");
}
