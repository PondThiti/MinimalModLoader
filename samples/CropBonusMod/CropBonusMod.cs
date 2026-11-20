using HarmonyLib;
using MinimalModLoader.Abstractions;
using MinimalModLoader.GameHost;

namespace CropBonusMod;

public sealed class CropBonusMod : ModBase
{
    private static double _activeMultiplier = 1.0;
    private static IMonitor? _staticMonitor;

    private CropBonusConfig _config = new();
    private Harmony? _harmony;
    private readonly List<CropEntry> _packCrops = [];

    public override void Entry(IModHelper helper)
    {
        _config = helper.ReadConfig<CropBonusConfig>();
        _activeMultiplier = _config.SellPriceMultiplier;
        _staticMonitor = Monitor;

        Monitor.Log($"Loaded config: SellPriceMultiplier = {_config.SellPriceMultiplier:0.##}x");

        // 1) Read any JSON Content Packs targeting this mod
        foreach (var pack in helper.ContentPacks.GetOwned())
        {
            if (!pack.HasFile("content.json"))
            {
                Monitor.Log($"Content pack '{pack.Manifest.Name}' has no content.json; skipping.", LogLevel.Warn);
                continue;
            }

            var data = pack.ReadJsonFile<CropContentPackModel>("content.json");
            if (data?.Crops is { Count: > 0 })
            {
                _packCrops.AddRange(data.Crops);
                Monitor.Log($"Loaded {data.Crops.Count} crop(s) from content pack '{pack.Manifest.Name}' v{pack.Manifest.Version}.");
            }
        }

        // 2) Patch PricingEngine.CalculateSellPrice at runtime via Harmony
        _harmony = new Harmony(Manifest.UniqueID);
        var original = AccessTools.Method(typeof(PricingEngine), nameof(PricingEngine.CalculateSellPrice));
        var postfix = AccessTools.Method(typeof(CropBonusMod), nameof(CalculateSellPrice_Postfix));
        _harmony.Patch(original, postfix: new HarmonyMethod(postfix));
        Monitor.Log("Applied Harmony Postfix to PricingEngine.CalculateSellPrice.");

        // 3) Hook into the Asset Pipeline to inject new crops into Data/Crops
        helper.Events.AssetRequested += OnAssetRequested;
        helper.Events.DayStarted += OnDayStarted;
    }

    public override object GetApi() => new CropBonusApi(this);

    private void OnAssetRequested(object? sender, AssetRequestedEventArgs e)
    {
        if (!e.NameIs("Data/Crops"))
            return;

        e.Edit(asset =>
        {
            var crops = asset.GetData<Dictionary<string, CropDefinition>>();

            // Inject built-in mod crop
            crops["Starfruit"] = new CropDefinition
            {
                Id = "Starfruit",
                DisplayName = "Starfruit (Modded)",
                BasePrice = _config.StarfruitBasePrice,
                DaysToGrow = 13
            };

            // Inject crops contributed by JSON Content Packs
            foreach (var entry in _packCrops)
            {
                crops[entry.Id] = new CropDefinition
                {
                    Id = entry.Id,
                    DisplayName = entry.DisplayName,
                    BasePrice = entry.BasePrice,
                    DaysToGrow = entry.DaysToGrow
                };
            }
        }, AssetEditPriority.Default);
    }

    private void OnDayStarted(object? sender, DayStartedEventArgs e)
    {
        Monitor.Log($"Day {e.Day} started — active sell bonus is {_activeMultiplier:0.##}x.");
    }

    private static void CalculateSellPrice_Postfix(string cropId, ref int __result)
    {
        var boosted = (int)Math.Round(__result * _activeMultiplier, MidpointRounding.AwayFromZero);
        _staticMonitor?.Log($"[Harmony] CalculateSellPrice('{cropId}'): {__result}g -> {boosted}g", LogLevel.Debug);
        __result = boosted;
    }

    public override void Dispose()
    {
        _harmony?.UnpatchAll(Manifest.UniqueID);
        _activeMultiplier = 1.0;
        _staticMonitor = null;
        base.Dispose();
    }

    /// <summary>
    /// Public API object exposed to other mods via ModRegistry.GetApi (duck-typed via DispatchProxy).
    /// </summary>
    public sealed class CropBonusApi
    {
        private readonly CropBonusMod _mod;
        public CropBonusApi(CropBonusMod mod) => _mod = mod;

        public double GetSellMultiplier() => _mod._config.SellPriceMultiplier;

        public int GetContentPackCropCount() => _mod._packCrops.Count;

        public void SetSellMultiplier(double newMultiplier)
        {
            if (newMultiplier <= 0)
                throw new ArgumentOutOfRangeException(nameof(newMultiplier), "Multiplier must be positive.");

            _mod._config.SellPriceMultiplier = newMultiplier;
            _activeMultiplier = newMultiplier;
        }
    }
}
