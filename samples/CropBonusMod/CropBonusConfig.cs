namespace CropBonusMod;

public sealed class CropBonusConfig
{
    public double SellPriceMultiplier { get; set; } = 1.5;
    public int StarfruitBasePrice { get; set; } = 750;
}

public sealed class CropContentPackModel
{
    public List<CropEntry> Crops { get; set; } = [];
}

public sealed class CropEntry
{
    public string Id { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public int BasePrice { get; set; }
    public int DaysToGrow { get; set; }
}
