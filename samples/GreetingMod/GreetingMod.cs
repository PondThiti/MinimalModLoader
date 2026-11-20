using MinimalModLoader.Abstractions;

namespace GreetingMod;

/// <summary>
/// Local duck-typed interface matching CropBonusMod's exposed API.
/// Notice GreetingMod has ZERO compile-time reference to CropBonusMod.dll!
/// </summary>
public interface ICropBonusApi
{
    double GetSellMultiplier();
    int GetContentPackCropCount();
}

public sealed class GreetingMod : ModBase
{
    private ICropBonusApi? _cropApi;

    public override void Entry(IModHelper helper)
    {
        helper.Events.GameLaunched += OnGameLaunched;
        helper.Events.AssetRequested += OnAssetRequested;
        helper.Events.DayStarted += OnDayStarted;
    }

    private void OnGameLaunched(object? sender, GameLaunchedEventArgs e)
    {
        _cropApi = Helper.Registry.GetApi<ICropBonusApi>("Minimal.CropBonusMod");
        if (_cropApi is not null)
        {
            Monitor.Log(
                $"Connected to CropBonusMod API via collectible proxy! " +
                $"Multiplier={_cropApi.GetSellMultiplier():0.##}x, ContentPackCrops={_cropApi.GetContentPackCropCount()}");
        }
        else
        {
            Monitor.Log("CropBonusMod API was not available.", LogLevel.Warn);
        }
    }

    private void OnAssetRequested(object? sender, AssetRequestedEventArgs e)
    {
        if (e.NameIs("Strings/Greeting"))
            e.LoadFromModFile<string>("assets/greeting.txt", AssetLoadPriority.High);
    }

    private void OnDayStarted(object? sender, DayStartedEventArgs e)
    {
        var greeting = Helper.Content.LoadGameAsset<string>("Strings/Greeting");
        Monitor.Log($"[Day {e.Day}] {greeting}");
    }
}
