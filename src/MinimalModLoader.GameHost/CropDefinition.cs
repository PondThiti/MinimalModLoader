namespace MinimalModLoader.GameHost;

public sealed class CropDefinition
{
    public string Id { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public int BasePrice { get; set; }
    public int DaysToGrow { get; set; }

    public CropDefinition Clone() => new()
    {
        Id = Id,
        DisplayName = DisplayName,
        BasePrice = BasePrice,
        DaysToGrow = DaysToGrow
    };
}
