using MinimalModLoader.Abstractions;

namespace MinimalModLoader.GameHost;

/// <summary>
/// Self-contained demo game host that supplies base assets and exposes lifecycle hooks
/// (similar to how a MonoGame/Stardew-style game loop interacts with a Mod Loader).
/// </summary>
public sealed class DemoGame : IBaseAssetProvider
{
    public int CurrentDay { get; private set; } = 1;
    public long CurrentTick { get; private set; }
    public int Gold { get; set; } = 500;

    public T LoadBaseAsset<T>(string normalizedAssetName) where T : notnull
    {
        if (string.Equals(normalizedAssetName, "Data/Crops", StringComparison.OrdinalIgnoreCase))
        {
            var crops = new Dictionary<string, CropDefinition>(StringComparer.OrdinalIgnoreCase)
            {
                ["Parsnip"] = new() { Id = "Parsnip", DisplayName = "Parsnip", BasePrice = 35, DaysToGrow = 4 },
                ["Cauliflower"] = new() { Id = "Cauliflower", DisplayName = "Cauliflower", BasePrice = 175, DaysToGrow = 12 },
                ["Potato"] = new() { Id = "Potato", DisplayName = "Potato", BasePrice = 80, DaysToGrow = 6 }
            };

            if (crops is T typed)
                return typed;
        }

        if (string.Equals(normalizedAssetName, "Strings/Greeting", StringComparison.OrdinalIgnoreCase))
        {
            object greeting = "Welcome to Minimal Valley! (Base Game Asset)";
            if (greeting is T typed)
                return typed;
        }

        throw new KeyNotFoundException($"Base asset '{normalizedAssetName}' (type {typeof(T).Name}) was not found.");
    }

    public long StepTick() => ++CurrentTick;

    public int NextDay() => ++CurrentDay;

    public int SellCrop(Dictionary<string, CropDefinition> crops, string cropId, int quantity)
    {
        if (!crops.TryGetValue(cropId, out var crop))
            throw new KeyNotFoundException($"Unknown crop '{cropId}'.");

        var revenue = PricingEngine.CalculateSellPrice(crop.Id, crop.BasePrice, quantity);
        Gold += revenue;
        return revenue;
    }
}
