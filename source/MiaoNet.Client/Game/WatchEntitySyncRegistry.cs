using MiaoNet.Shared;

namespace Celeste.Mod.MiaoNet;

[Flags]
internal enum WatchEntityApplyResult
{
    None = 0,
    SceneChanged = 1 << 0,
    RequiresRoomReload = 1 << 1,
}

internal readonly record struct WatchEntityApplySummary(
    WatchEntityApplyResult Result,
    IReadOnlyCollection<WatchEntityKind> RoomReloadRequestedKinds
);

internal interface IWatchEntityAdapter
{
    WatchEntityKind Kind { get; }

    IEnumerable<WatchEntityState> CaptureStates(Level level);

    WatchEntityApplyResult ApplyStates(
        Level level,
        IReadOnlyCollection<WatchEntityState> states,
        bool isCompleteState
    );

    void ApplyEvent(Level level, WatchEntityEvent entityEvent)
    {
    }
}

internal static class WatchEntitySyncRegistry
{
    private static readonly SortedDictionary<WatchEntityKind, IWatchEntityAdapter> adapters = new();
#if PACKET_TRACING
    private static readonly Dictionary<WatchEntityKind, int> debugCaptureExceptions = new();
    private static readonly Dictionary<WatchEntityKind, int> debugApplyExceptions = new();
    private static readonly Dictionary<WatchEntityKind, int> debugEventExceptions = new();
    private static readonly List<(WatchEntityKind Kind, double Milliseconds, int StateCount)>
        debugScopedApplyTimings = new();
    private static bool debugApplyTimingScopeActive;
#endif
    private static int remoteApplyDepth;
    private static int forceCurrentCaptureDepth;
    private static int lifecycleResetApplyDepth;

    public static event Action<Level, WatchEntityEvent>? EventProduced;

    public static bool IsApplyingRemoteState => remoteApplyDepth > 0;

    public static bool IsCapturingCurrentState => forceCurrentCaptureDepth > 0;

    public static bool IsApplyingLifecycleReset => lifecycleResetApplyDepth > 0;

    public static void Register(IWatchEntityAdapter adapter)
    {
        if (adapter.Kind == WatchEntityKind.None || !adapters.TryAdd(adapter.Kind, adapter))
            throw new InvalidOperationException($"Invalid or duplicate watch entity adapter: {adapter.Kind}.");
    }

    public static void Unregister(IWatchEntityAdapter adapter)
    {
        if (adapters.TryGetValue(adapter.Kind, out IWatchEntityAdapter? registered)
            && ReferenceEquals(adapter, registered))
            adapters.Remove(adapter.Kind);
    }

    public static HashSet<WatchEntityKind> CaptureStates(
        Level level,
        out Dictionary<WatchEntityKey, WatchEntityState> states,
        bool forceCurrent = false
    )
    {
        states = new();
        HashSet<WatchEntityKind> unavailableKinds = new();
        if (forceCurrent)
            forceCurrentCaptureDepth++;
        try
        {
            foreach (IWatchEntityAdapter adapter in adapters.Values)
            {
                Dictionary<WatchEntityKey, WatchEntityState>? adapterStates = null;
                try
                {
                    foreach (WatchEntityState state in adapter.CaptureStates(level))
                    {
                        if (state.Key.Kind != adapter.Kind)
                        {
                            throw new InvalidOperationException(
                                $"Watch entity adapter {adapter.Kind} produced an invalid key."
                            );
                        }
                        if (!WatchPacketValidator.IsValid(state))
                        {
                            throw new InvalidOperationException(
                                $"Watch entity adapter {adapter.Kind} produced an invalid state for " +
                                $"#{state.Key.EntityID}:{state.Key.SubID} ({state.Payload.Length} bytes)."
                            );
                        }

                        adapterStates ??= new();
                        if (!adapterStates.TryAdd(state.Key, state))
                        {
                            adapterStates[state.Key] = state;
                            Logger.Warn(
                                LT.MiaoNetWatch,
                                $"Collapsed a duplicate transient watch entity key: {state.Key.Kind} " +
                                $"#{state.Key.EntityID}:{state.Key.SubID}."
                            );
                        }
                    }

                    if (adapterStates is not null)
                        foreach ((WatchEntityKey key, WatchEntityState state) in adapterStates)
                            states.Add(key, state);
                }
                catch (Exception exception)
                {
                    unavailableKinds.Add(adapter.Kind);
#if PACKET_TRACING
                    IncrementDebugException(debugCaptureExceptions, adapter.Kind);
#endif
                    Logger.Error(
                        LT.MiaoNetWatch,
                        $"Quarantined an invalid local watch update for {adapter.Kind}; " +
                        "the watch session will continue with its last known state when available."
                    );
                    Logger.LogDetailed(exception, LT.MiaoNetWatch);
                }
            }
            return unavailableKinds;
        }
        finally
        {
            if (forceCurrent)
                forceCurrentCaptureDepth--;
        }
    }

    public static WatchEntityApplySummary ApplyStates(
        Level level,
        IReadOnlyCollection<WatchEntityState> states,
        bool isCompleteState,
        bool isLifecycleReset = false
    )
    {
        remoteApplyDepth++;
        if (isLifecycleReset)
            lifecycleResetApplyDepth++;
        IDisposable? audioSuppression = MiaoNetModule.Settings.PlayerAudioSyncMode.HasReceive
            ? null
            : WatchSceneAudioSuppression.Begin();
        try
        {
            WatchEntityApplyResult result = WatchEntityApplyResult.None;
            HashSet<WatchEntityKind> roomReloadRequestedKinds = new();
            Dictionary<WatchEntityKind, List<WatchEntityState>> statesByKind = new();
            foreach (WatchEntityState state in states)
            {
                if (!statesByKind.TryGetValue(state.Key.Kind, out List<WatchEntityState>? kindStates))
                {
                    kindStates = new();
                    statesByKind.Add(state.Key.Kind, kindStates);
                }
                kindStates.Add(state);
            }
            IEnumerable<IWatchEntityAdapter> targetAdapters = isCompleteState
                ? adapters.Values
                : statesByKind.Keys.Select(kind => adapters.GetValueOrDefault(kind))
                    .OfType<IWatchEntityAdapter>();

            foreach (IWatchEntityAdapter adapter in targetAdapters)
            {
#if PACKET_TRACING
                long debugApplyStartTimestamp = debugApplyTimingScopeActive
                    ? System.Diagnostics.Stopwatch.GetTimestamp()
                    : 0;
#endif
                try
                {
                    WatchEntityApplyResult adapterResult = adapter.ApplyStates(
                        level,
                        statesByKind.GetValueOrDefault(adapter.Kind) ?? [],
                        isCompleteState
                    );
                    result |= adapterResult;
                    if (adapterResult.HasFlag(WatchEntityApplyResult.RequiresRoomReload))
                        roomReloadRequestedKinds.Add(adapter.Kind);
                }
                catch (Exception exception)
                {
                    roomReloadRequestedKinds.Add(adapter.Kind);
#if PACKET_TRACING
                    IncrementDebugException(debugApplyExceptions, adapter.Kind);
#endif
                    Logger.Error(
                        LT.MiaoNetWatch,
                        $"Failed to apply remote watch state for {adapter.Kind}; ignored this adapter update."
                    );
                    Logger.LogDetailed(exception, LT.MiaoNetWatch);
                }
#if PACKET_TRACING
                finally
                {
                    if (debugApplyStartTimestamp != 0)
                    {
                        debugScopedApplyTimings.Add((
                            adapter.Kind,
                            GetDebugElapsedMilliseconds(debugApplyStartTimestamp),
                            statesByKind.GetValueOrDefault(adapter.Kind)?.Count ?? 0
                        ));
                    }
                }
#endif
            }
            return new(
                result,
                roomReloadRequestedKinds.Order().ToArray()
            );
        }
        finally
        {
            audioSuppression?.Dispose();
            if (isLifecycleReset)
                lifecycleResetApplyDepth--;
            remoteApplyDepth--;
        }
    }

    public static bool ApplyEvent(Level level, WatchEntityEvent entityEvent)
    {
        if (!adapters.TryGetValue(entityEvent.Key.Kind, out IWatchEntityAdapter? adapter))
            return false;

        remoteApplyDepth++;
        IDisposable? audioSuppression = MiaoNetModule.Settings.PlayerAudioSyncMode.HasReceive
            ? null
            : WatchSceneAudioSuppression.Begin();
        try
        {
            try
            {
                adapter.ApplyEvent(level, entityEvent);
                return true;
            }
            catch (Exception exception)
            {
#if PACKET_TRACING
                IncrementDebugException(debugEventExceptions, entityEvent.Key.Kind);
#endif
                Logger.Error(
                    LT.MiaoNetWatch,
                    $"Failed to apply remote watch event for {entityEvent.Key.Kind} " +
                    $"#{entityEvent.Key.EntityID}:{entityEvent.Key.SubID}; ignored this event."
                );
                Logger.LogDetailed(exception, LT.MiaoNetWatch);
                return false;
            }
        }
        finally
        {
            audioSuppression?.Dispose();
            remoteApplyDepth--;
        }
    }

    public static void PublishEvent(Level level, WatchEntityEvent entityEvent)
        => EventProduced?.Invoke(level, entityEvent);

#if PACKET_TRACING
    internal static void BeginDebugApplyTimingScope()
    {
        debugScopedApplyTimings.Clear();
        debugApplyTimingScopeActive = true;
    }

    internal static string EndDebugApplyTimingScope()
    {
        debugApplyTimingScopeActive = false;
        string summary = string.Join(
            ",",
            debugScopedApplyTimings
                .OrderByDescending(timing => timing.Milliseconds)
                .Take(8)
                .Select(timing =>
                    $"{timing.Kind}={timing.Milliseconds:F3}ms/{timing.StateCount}states"
                )
        );
        debugScopedApplyTimings.Clear();
        return summary.Length == 0 ? "none" : summary;
    }

    internal static string ConsumeDebugExceptionSummary()
    {
        string summary = string.Join(
            ",",
            FormatDebugExceptions("capture", debugCaptureExceptions)
                .Concat(FormatDebugExceptions("apply", debugApplyExceptions))
                .Concat(FormatDebugExceptions("event", debugEventExceptions))
        );
        debugCaptureExceptions.Clear();
        debugApplyExceptions.Clear();
        debugEventExceptions.Clear();
        return summary.Length == 0 ? "none" : summary;
    }

    private static IEnumerable<string> FormatDebugExceptions(
        string operation,
        IReadOnlyDictionary<WatchEntityKind, int> exceptions
    ) => exceptions.OrderBy(pair => pair.Key)
        .Select(pair => $"{operation}:{pair.Key}={pair.Value}");

    private static void IncrementDebugException(
        Dictionary<WatchEntityKind, int> exceptions,
        WatchEntityKind kind
    ) => exceptions[kind] = exceptions.GetValueOrDefault(kind) + 1;

    private static double GetDebugElapsedMilliseconds(long startTimestamp)
        => (System.Diagnostics.Stopwatch.GetTimestamp() - startTimestamp)
            * 1000d / System.Diagnostics.Stopwatch.Frequency;
#endif
}
