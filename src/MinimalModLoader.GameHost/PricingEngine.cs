using System.Runtime.CompilerServices;

namespace MinimalModLoader.GameHost;

/// <summary>
/// Example core game logic class. Marked NoInlining so runtime Harmony patches
/// reliably intercept calls even under Release JIT optimization.
/// </summary>
public static class PricingEngine
{
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static int CalculateSellPrice(string cropId, int basePrice, int quantity)
    {
        if (basePrice < 0 || quantity <= 0)
            return 0;

        return basePrice * quantity;
    }
}
