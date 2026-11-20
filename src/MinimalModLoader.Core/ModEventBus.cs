using MinimalModLoader.Abstractions;

namespace MinimalModLoader.Core;

/// <summary>
/// Fault-isolated central event bus. Every mod subscribes via a scoped proxy so exceptions
/// are attributed to the failing mod and never crash the game or other mods.
/// </summary>
public sealed class ModEventBus
{
    private readonly List<ModSubscription<GameLaunchedEventArgs>> _gameLaunched = [];
    private readonly List<ModSubscription<UpdateTickedEventArgs>> _updateTicked = [];
    private readonly List<ModSubscription<DayStartedEventArgs>> _dayStarted = [];
    private readonly List<AssetRequestedSubscription> _assetRequested = [];

    public IModEvents CreateScopedEvents(
        string modId,
        IMonitor monitor,
        Func<string, Type, object> modFileLoader)
        => new ScopedModEvents(this, modId, monitor, modFileLoader);

    public void UnsubscribeAll(string modId)
    {
        _gameLaunched.RemoveAll(s => string.Equals(s.ModId, modId, StringComparison.OrdinalIgnoreCase));
        _updateTicked.RemoveAll(s => string.Equals(s.ModId, modId, StringComparison.OrdinalIgnoreCase));
        _dayStarted.RemoveAll(s => string.Equals(s.ModId, modId, StringComparison.OrdinalIgnoreCase));
        _assetRequested.RemoveAll(s => string.Equals(s.ModId, modId, StringComparison.OrdinalIgnoreCase));
    }

    public void RaiseGameLaunched(object sender, GameLaunchedEventArgs args)
        => Dispatch(_gameLaunched, sender, args, nameof(IModEvents.GameLaunched));

    public void RaiseUpdateTicked(object sender, UpdateTickedEventArgs args)
        => Dispatch(_updateTicked, sender, args, nameof(IModEvents.UpdateTicked));

    public void RaiseDayStarted(object sender, DayStartedEventArgs args)
        => Dispatch(_dayStarted, sender, args, nameof(IModEvents.DayStarted));

    internal void RaiseAssetRequested(
        object sender,
        string normalizedAssetName,
        Type dataType,
        List<AssetLoadOperation> sharedLoads,
        List<AssetEditOperation> sharedEdits)
    {
        foreach (var sub in _assetRequested.ToArray())
        {
            var args = new AssetRequestedEventArgs(
                normalizedAssetName,
                dataType,
                sub.ModId,
                sub.ModFileLoader,
                sharedLoads,
                sharedEdits);

            try
            {
                sub.Handler(sender, args);
            }
            catch (Exception ex)
            {
                sub.Monitor.LogError($"Unhandled exception in AssetRequested for '{normalizedAssetName}'.", ex);
            }
        }
    }

    private static void Dispatch<TEventArgs>(
        List<ModSubscription<TEventArgs>> subscriptions,
        object sender,
        TEventArgs args,
        string eventName)
    {
        foreach (var sub in subscriptions.ToArray())
        {
            try
            {
                sub.Handler(sender, args);
            }
            catch (Exception ex)
            {
                sub.Monitor.LogError($"Unhandled exception in {eventName} handler.", ex);
            }
        }
    }

    private sealed record ModSubscription<TEventArgs>(
        string ModId,
        IMonitor Monitor,
        EventHandler<TEventArgs> Handler);

    private sealed record AssetRequestedSubscription(
        string ModId,
        IMonitor Monitor,
        Func<string, Type, object> ModFileLoader,
        EventHandler<AssetRequestedEventArgs> Handler);

    private sealed class ScopedModEvents : IModEvents
    {
        private readonly ModEventBus _bus;
        private readonly string _modId;
        private readonly IMonitor _monitor;
        private readonly Func<string, Type, object> _modFileLoader;

        public ScopedModEvents(
            ModEventBus bus,
            string modId,
            IMonitor monitor,
            Func<string, Type, object> modFileLoader)
        {
            _bus = bus;
            _modId = modId;
            _monitor = monitor;
            _modFileLoader = modFileLoader;
        }

        public event EventHandler<GameLaunchedEventArgs>? GameLaunched
        {
            add { if (value is not null) _bus._gameLaunched.Add(new(_modId, _monitor, value)); }
            remove { if (value is not null) _bus._gameLaunched.RemoveAll(s => s.ModId == _modId && s.Handler == value); }
        }

        public event EventHandler<UpdateTickedEventArgs>? UpdateTicked
        {
            add { if (value is not null) _bus._updateTicked.Add(new(_modId, _monitor, value)); }
            remove { if (value is not null) _bus._updateTicked.RemoveAll(s => s.ModId == _modId && s.Handler == value); }
        }

        public event EventHandler<DayStartedEventArgs>? DayStarted
        {
            add { if (value is not null) _bus._dayStarted.Add(new(_modId, _monitor, value)); }
            remove { if (value is not null) _bus._dayStarted.RemoveAll(s => s.ModId == _modId && s.Handler == value); }
        }

        public event EventHandler<AssetRequestedEventArgs>? AssetRequested
        {
            add { if (value is not null) _bus._assetRequested.Add(new(_modId, _monitor, _modFileLoader, value)); }
            remove { if (value is not null) _bus._assetRequested.RemoveAll(s => s.ModId == _modId && s.Handler == value); }
        }
    }
}
