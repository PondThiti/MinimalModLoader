using MinimalModLoader.Abstractions;
using MinimalModLoader.Core;
using MinimalModLoader.GameHost;
using Xunit;

namespace MinimalModLoader.Tests;

public sealed class ModLoaderTests
{
    public interface ITestCropBonusApi
    {
        double GetSellMultiplier();
        int GetContentPackCropCount();
        void SetSellMultiplier(double newMultiplier);
    }

    [Fact]
    public void SemVer_ParsesAndComparesCorrectly()
    {
        Assert.True(SemVer.TryParse("1.2.3", out var v123));
        Assert.True(SemVer.TryParse("1.2", out var v120));
        Assert.True(SemVer.TryParse("1.2.3-beta.1", out var v123Beta));

        Assert.Equal(new SemVer(1, 2, 0), v120);
        Assert.True(v123 > v120);
        Assert.True(v123 > v123Beta);
        Assert.False(SemVer.TryParse("not-a-version", out _));
    }

    [Fact]
    public void DependencyResolver_SortsTopologically_AndDetectsCyclesAndMissingDeps()
    {
        var resolver = new DependencyResolver(new SemVer(1, 0, 0));

        var modA = MakeDiscovered("Mod.A", "1.0.0", deps: [new() { UniqueID = "Mod.Base", MinimumVersion = "1.0.0" }]);
        var modBase = MakeDiscovered("Mod.Base", "1.2.0");
        var modBroken = MakeDiscovered("Mod.Broken", "1.0.0", deps: [new() { UniqueID = "Mod.DoesNotExist" }]);
        var modCycle1 = MakeDiscovered("Mod.Cycle1", "1.0.0", deps: [new() { UniqueID = "Mod.Cycle2" }]);
        var modCycle2 = MakeDiscovered("Mod.Cycle2", "1.0.0", deps: [new() { UniqueID = "Mod.Cycle1" }]);

        var result = resolver.Resolve([modA, modBase, modBroken, modCycle1, modCycle2]);

        Assert.Equal(["Mod.Base", "Mod.A"], result.OrderedCodeMods.Select(m => m.Manifest.UniqueID).ToArray());
        Assert.Equal(3, result.SkippedMods.Count);
        Assert.Contains(result.SkippedMods, s => s.DisplayName == "Mod.Broken" && s.Reason.Contains("Missing required dependency"));
        Assert.Contains(result.SkippedMods, s => s.DisplayName == "Mod.Cycle1" && s.Reason.Contains("Circular"));
        Assert.Contains(result.SkippedMods, s => s.DisplayName == "Mod.Cycle2" && s.Reason.Contains("Circular"));
    }

    [Fact]
    public void DependencyResolver_AllowsMutualOptionalDependenciesWithoutFalseCycle()
    {
        var resolver = new DependencyResolver(new SemVer(1, 0, 0));

        var modX = MakeDiscovered("Mod.X", "1.0.0", deps: [new() { UniqueID = "Mod.Y", IsRequired = false }]);
        var modY = MakeDiscovered("Mod.Y", "1.0.0", deps: [new() { UniqueID = "Mod.X", IsRequired = false }]);

        var result = resolver.Resolve([modX, modY]);

        Assert.Equal(2, result.OrderedCodeMods.Count);
        Assert.Empty(result.SkippedMods);
    }

    [Fact]
    public void AssetManager_AppliesReplacementAndOrderedEdits_AndSupportsInvalidation()
    {
        var game = new DemoGame();
        var logWriter = new StringWriter();
        var monitor = new ConsoleMonitor("Test", logWriter, LogLevel.Debug, useColors: false);
        var bus = new ModEventBus();
        var assets = new AssetManager(game, bus, monitor);

        var mod1Events = bus.CreateScopedEvents("Mod.One", monitor, (_, _) => throw new NotSupportedException());
        var mod2Events = bus.CreateScopedEvents("Mod.Two", monitor, (_, _) => throw new NotSupportedException());

        var editSuffix = "v1";
        mod1Events.AssetRequested += (_, e) =>
        {
            if (e.NameIs("Strings/Greeting"))
            {
                e.LoadFrom(() => "BaseReplaced", AssetLoadPriority.Medium);
                e.Edit(a => a.ReplaceWith(a.GetData<string>() + "-Late-" + editSuffix), AssetEditPriority.Late);
            }
        };

        mod2Events.AssetRequested += (_, e) =>
        {
            if (e.NameIs("Strings/Greeting"))
                e.Edit(a => a.ReplaceWith(a.GetData<string>() + "-Early"), AssetEditPriority.Early);
        };

        var first = assets.Load<string>("Strings/Greeting");
        Assert.Equal("BaseReplaced-Early-Late-v1", first);

        // Cached value stays stable until invalidated
        editSuffix = "v2";
        Assert.Equal("BaseReplaced-Early-Late-v1", assets.Load<string>("Strings/Greeting"));

        Assert.True(assets.InvalidateCache("Strings/Greeting"));
        Assert.Equal("BaseReplaced-Early-Late-v2", assets.Load<string>("Strings/Greeting"));
    }

    [Fact]
    public void ModEventBus_IsolatesThrowingHandlerFromOtherMods()
    {
        var logWriter = new StringWriter();
        var monitor = new ConsoleMonitor("FaultyMod", logWriter, LogLevel.Info, useColors: false);
        var bus = new ModEventBus();

        var faulty = bus.CreateScopedEvents("Mod.Faulty", monitor, (_, _) => throw new NotSupportedException());
        var healthy = bus.CreateScopedEvents("Mod.Healthy", monitor, (_, _) => throw new NotSupportedException());

        var healthyCalled = false;
        faulty.DayStarted += (_, _) => throw new InvalidOperationException("Boom!");
        healthy.DayStarted += (_, e) => healthyCalled = e.Day == 2;

        bus.RaiseDayStarted(this, new DayStartedEventArgs(2));

        Assert.True(healthyCalled);
        Assert.Contains("Boom!", logWriter.ToString());
    }

    [Fact]
    public void EndToEnd_LoadsSampleMods_HarmonyPatch_ContentPack_AssetPipeline_DispatchProxy_AndCleanUnload()
    {
        var testModsDir = Path.Combine(AppContext.BaseDirectory, "TestMods");
        Assert.True(Directory.Exists(testModsDir), $"Expected staged test mods at '{testModsDir}'.");

        // Ensure clean vanilla pricing before loading
        Assert.Equal(350, PricingEngine.CalculateSellPrice("Parsnip", 35, 10));

        var game = new DemoGame();
        var logs = new StringWriter();

        using (var loader = new ModLoader(game, logs, LogLevel.Info))
        {
            var report = loader.LoadMods(testModsDir);

            // Dependency order: CropBonusMod must load before GreetingMod
            Assert.Equal(["Minimal.CropBonusMod", "Minimal.GreetingMod"],
                report.LoadedCodeMods.Select(m => m.UniqueID).ToArray());
            Assert.Single(report.LoadedContentPacks);
            Assert.Equal("Minimal.ExtraCropsPack", report.LoadedContentPacks[0].UniqueID);
            Assert.Empty(report.SkippedMods);

            loader.RaiseGameLaunched();

            // Verify cross-collectible-ALC inter-mod API call inside GreetingMod succeeded with zero errors
            var logOutput = logs.ToString();
            Assert.Contains("Connected to CropBonusMod API via collectible proxy!", logOutput);
            Assert.DoesNotContain("ERROR", logOutput);

            // Asset Pipeline: Data/Crops contains vanilla + Starfruit (C# mod) + AncientFruit & SweetGemBerry (Content Pack)
            var crops = loader.Assets.Load<Dictionary<string, CropDefinition>>("Data/Crops");
            Assert.True(crops.ContainsKey("Parsnip"));
            Assert.True(crops.ContainsKey("Starfruit"));
            Assert.True(crops.ContainsKey("AncientFruit"));
            Assert.True(crops.ContainsKey("SweetGemBerry"));

            // Asset Pipeline: Strings/Greeting replaced from GreetingMod/assets/greeting.txt
            var greeting = loader.Assets.Load<string>("Strings/Greeting");
            Assert.Contains("GreetingMod/assets/greeting.txt", greeting);

            // Harmony runtime patch: 35 * 10 = 350 -> 1.5x = 525
            Assert.Equal(525, PricingEngine.CalculateSellPrice("Parsnip", 35, 10));

            // Duck-typed DispatchProxy inter-mod API
            var api = loader.Registry.GetApi<ITestCropBonusApi>("Minimal.CropBonusMod");
            Assert.NotNull(api);
            Assert.Equal(1.5, api!.GetSellMultiplier());
            Assert.Equal(2, api.GetContentPackCropCount());

            // Live mutation through duck-typed API updates Harmony patch behavior immediately
            api.SetSellMultiplier(2.0);
            Assert.Equal(700, PricingEngine.CalculateSellPrice("Parsnip", 35, 10));
        }

        // After Dispose(), Harmony must be cleanly unpatched back to vanilla behavior
        Assert.Equal(350, PricingEngine.CalculateSellPrice("Parsnip", 35, 10));
    }

    private static DiscoveredMod MakeDiscovered(
        string id,
        string version,
        List<ModDependency>? deps = null,
        bool isContentPack = false)
    {
        var manifest = new ModManifest
        {
            UniqueID = id,
            Name = id,
            Version = version,
            EntryDll = isContentPack ? null : $"{id}.dll",
            Dependencies = deps ?? []
        };
        return new DiscoveredMod(manifest, SemVer.Parse(version), $"C:\\FakeMods\\{id}", isContentPack);
    }
}
