#if PACKET_TRACING
using System.Diagnostics;
using MiaoNet.ClientShared;
using MiaoNet.Shared;

namespace Celeste.Mod.MiaoNet;

public sealed partial class MainComponent
{
    private const double WatchDiagnosticsIntervalMilliseconds = 5000d;
    private const double SlowWatchDrawGapMilliseconds = 20d;
    private const double SevereWatchDrawGapMilliseconds = 1000d / 30d;

    private long watchDiagnosticsWindowStartTimestamp;
    private long watchAllocatedBytesAtWindowStart;
    private int watchGen0CollectionsAtWindowStart;
    private int watchGen1CollectionsAtWindowStart;
    private int watchGen2CollectionsAtWindowStart;
    private int watchUpdateTickCount;
    private int watchDrawCount;
    private int watchDrawGapSampleCount;
    private int slowWatchDrawGapCount;
    private int severeWatchDrawGapCount;
    private double totalWatchDrawGapMilliseconds;
    private double maxWatchDrawGapMilliseconds;
    private double totalWatchDrawMilliseconds;
    private double maxWatchDrawMilliseconds;
    private long lastWatchDrawStartedAt;
    private int watchLevelRenderCount;
    private double totalWatchLevelRenderMilliseconds;
    private double maxWatchLevelRenderMilliseconds;
    private int watchLevelUpdateCount;
    private double totalWatchLevelUpdateMilliseconds;
    private double maxWatchLevelUpdateMilliseconds;
    private double totalWatchCameraUpdateMilliseconds;
    private double maxWatchCameraUpdateMilliseconds;
    private int sentWatchPlayerFrameCount;
    private int receivedWatchPlayerFrameCount;
    private int receivedWatchPlayerFrameGapCount;
    private int watchPlayerFrameGapOver20MillisecondsCount;
    private int watchPlayerFrameGapOver33MillisecondsCount;
    private int watchPlayerFrameGapOver50MillisecondsCount;
    private int watchPlayerFrameGapOver100MillisecondsCount;
    private double totalWatchPlayerFrameGapMilliseconds;
    private double maxWatchPlayerFrameGapMilliseconds;
    private long lastWatchPlayerFrameReceivedAt;
    private int maxWatchPlayerPlaybackBufferDepth;
    private int maxWatchScenePlaybackBufferDepth;
    private int maxWatchPlayerEventBufferDepth;
    private int watchPlaybackUnderflowCount;
    private bool watchPlaybackUnderflowActive;
    private int producedWatchDeltaCount;
    private int producedWatchStateCount;
    private int producedWatchEventCount;
    private readonly Dictionary<WatchEntityKind, int> producedWatchStateKinds = new();
    private int receivedWatchDeltaCount;
    private int receivedWatchStateCount;
    private int receivedWatchEventCount;
    private readonly Dictionary<WatchEntityKind, int> receivedWatchStateKinds = new();
    private int maxWatchStatesPerDelta;
    private int receivedWatchDeathRespawnCount;
    private int receivedWatchRoomReloadCount;
    private int receivedWatchRoomTransitionCount;
    private int watchCaptureCallCount;
    private int capturedWatchStateCount;
    private double totalWatchCaptureMilliseconds;
    private double maxWatchCaptureMilliseconds;
    private int watchApplyCallCount;
    private int appliedWatchStateCount;
    private double totalWatchApplyMilliseconds;
    private double maxWatchApplyMilliseconds;
    private long lastWatchMismatchLogTimestamp;
    private int suppressedWatchMismatchCount;
    private WatchDeathDiagnosticsSample? watchDeathDiagnostics;

    private bool WatchDiagnosticsActive => Watching || watchProducerSessions.Count > 0;
    private string WatchDiagnosticsRole => Watching
        ? "watcher"
        : watchProducerSessions.Count > 0
            ? "producer"
            : "inactive";

    private sealed class WatchDeathDiagnosticsSample
    {
        public WatchDeathDiagnosticsSample(PlayerLocation sourceLocation, string sceneBefore)
        {
            SourceLocation = sourceLocation;
            SceneBefore = sceneBefore;
            BeginTimestamp = Stopwatch.GetTimestamp();
        }

        public PlayerLocation SourceLocation { get; }
        public string SceneBefore { get; }
        public long BeginTimestamp { get; }
        public long WipeSignalTimestamp { get; set; }
        public long WipeStartTimestamp { get; set; }
        public long BlackFrameTimestamp { get; set; }
        public long StateReadyTimestamp { get; set; }
        public long RespawnReadyTimestamp { get; set; }
        public long CameraReadyTimestamp { get; set; }
        public bool CameraTimedOut { get; set; }
        public bool SessionPrepared { get; set; }
        public double SessionPreparationMilliseconds { get; set; }
        public bool ReloadAttempted { get; set; }
        public bool ReloadCompleted { get; set; }
        public WatchLevelReloadTiming ReloadTiming { get; set; }
        public double SnapshotApplyMilliseconds { get; set; }
        public double PresentationMilliseconds { get; set; }
        public string AdapterTimings { get; set; } = "none";
        public int SnapshotStateCount { get; set; }
        public int SnapshotKindCount { get; set; }
        public long ManagedBytesBeforeReload { get; set; }
        public long AllocatedBytesBeforeReload { get; set; }
        public int Gen0BeforeReload { get; set; }
        public int Gen1BeforeReload { get; set; }
        public int Gen2BeforeReload { get; set; }
    }

    private void RecordWatchUpdateTick(Level level)
    {
        watchUpdateTickCount++;
        LogWatchPerformanceIfDue(level);
    }

    internal void RecordWatchDraw(long startedAt)
    {
        if (!WatchDiagnosticsActive)
        {
            lastWatchDrawStartedAt = 0;
            return;
        }

        long completedAt = Stopwatch.GetTimestamp();
        watchDrawCount++;
        double drawMilliseconds = GetElapsedMilliseconds(startedAt, completedAt);
        totalWatchDrawMilliseconds += drawMilliseconds;
        maxWatchDrawMilliseconds = Math.Max(maxWatchDrawMilliseconds, drawMilliseconds);

        if (lastWatchDrawStartedAt != 0)
        {
            double gapMilliseconds = GetElapsedMilliseconds(lastWatchDrawStartedAt, startedAt);
            watchDrawGapSampleCount++;
            totalWatchDrawGapMilliseconds += gapMilliseconds;
            maxWatchDrawGapMilliseconds = Math.Max(maxWatchDrawGapMilliseconds, gapMilliseconds);
            if (gapMilliseconds > SlowWatchDrawGapMilliseconds)
                slowWatchDrawGapCount++;
            if (gapMilliseconds > SevereWatchDrawGapMilliseconds)
                severeWatchDrawGapCount++;
        }

        lastWatchDrawStartedAt = startedAt;
        LogWatchPerformanceIfDue(Engine.Scene as Level);
    }

    internal void RecordWatchLevelRender(long startedAt)
    {
        if (!WatchDiagnosticsActive)
            return;

        double elapsedMilliseconds = GetElapsedMilliseconds(startedAt);
        watchLevelRenderCount++;
        totalWatchLevelRenderMilliseconds += elapsedMilliseconds;
        maxWatchLevelRenderMilliseconds = Math.Max(
            maxWatchLevelRenderMilliseconds,
            elapsedMilliseconds
        );
    }

    internal void RecordWatchLevelUpdate(long startedAt, long cameraUpdateStartedAt)
    {
        if (!WatchDiagnosticsActive)
            return;

        long completedAt = Stopwatch.GetTimestamp();
        double levelUpdateMilliseconds = GetElapsedMilliseconds(startedAt, cameraUpdateStartedAt);
        double cameraUpdateMilliseconds = GetElapsedMilliseconds(cameraUpdateStartedAt, completedAt);
        watchLevelUpdateCount++;
        totalWatchLevelUpdateMilliseconds += levelUpdateMilliseconds;
        maxWatchLevelUpdateMilliseconds = Math.Max(
            maxWatchLevelUpdateMilliseconds,
            levelUpdateMilliseconds
        );
        totalWatchCameraUpdateMilliseconds += cameraUpdateMilliseconds;
        maxWatchCameraUpdateMilliseconds = Math.Max(
            maxWatchCameraUpdateMilliseconds,
            cameraUpdateMilliseconds
        );
    }

    private void RecordWatchPlayerFrameSent()
    {
        sentWatchPlayerFrameCount++;
        LogWatchPerformanceIfDue();
    }

    private void RecordWatchPlayerFrameReceived()
    {
        long receivedAt = Stopwatch.GetTimestamp();
        receivedWatchPlayerFrameCount++;
        if (lastWatchPlayerFrameReceivedAt != 0)
        {
            double gapMilliseconds = GetElapsedMilliseconds(
                lastWatchPlayerFrameReceivedAt,
                receivedAt
            );
            receivedWatchPlayerFrameGapCount++;
            totalWatchPlayerFrameGapMilliseconds += gapMilliseconds;
            maxWatchPlayerFrameGapMilliseconds = Math.Max(
                maxWatchPlayerFrameGapMilliseconds,
                gapMilliseconds
            );
            if (gapMilliseconds > 20d)
                watchPlayerFrameGapOver20MillisecondsCount++;
            if (gapMilliseconds > 1000d / 30d)
                watchPlayerFrameGapOver33MillisecondsCount++;
            if (gapMilliseconds > 50d)
                watchPlayerFrameGapOver50MillisecondsCount++;
            if (gapMilliseconds > 100d)
                watchPlayerFrameGapOver100MillisecondsCount++;
        }

        lastWatchPlayerFrameReceivedAt = receivedAt;
        LogWatchPerformanceIfDue();
    }

    private void RecordProducedWatchDelta(WatchSceneDelta delta)
    {
        producedWatchDeltaCount++;
        producedWatchStateCount += delta.EntityStates.Count;
        producedWatchEventCount += delta.EntityEvents.Count;
        CountWatchStateKinds(delta.EntityStates, producedWatchStateKinds);
        LogWatchPerformanceIfDue();
    }

    private void RecordReceivedWatchDelta(WatchSceneDelta delta)
    {
        receivedWatchDeltaCount++;
        receivedWatchStateCount += delta.EntityStates.Count;
        receivedWatchEventCount += delta.EntityEvents.Count;
        CountWatchStateKinds(delta.EntityStates, receivedWatchStateKinds);
        maxWatchStatesPerDelta = Math.Max(maxWatchStatesPerDelta, delta.EntityStates.Count);
        if (delta.IsDeathRespawn)
            receivedWatchDeathRespawnCount++;
        if (delta.RequiresRoomReload)
            receivedWatchRoomReloadCount++;
        if (delta.RoomTransition.HasValue)
            receivedWatchRoomTransitionCount++;
        LogWatchPerformanceIfDue();
    }

    private void RecordWatchCapture(long startTimestamp, int stateCount)
    {
        double elapsedMilliseconds = GetElapsedMilliseconds(startTimestamp);
        watchCaptureCallCount++;
        capturedWatchStateCount += stateCount;
        totalWatchCaptureMilliseconds += elapsedMilliseconds;
        maxWatchCaptureMilliseconds = Math.Max(maxWatchCaptureMilliseconds, elapsedMilliseconds);
        LogWatchPerformanceIfDue();
    }

    private void RecordWatchApply(long startTimestamp, int stateCount)
    {
        double elapsedMilliseconds = GetElapsedMilliseconds(startTimestamp);
        watchApplyCallCount++;
        appliedWatchStateCount += stateCount;
        totalWatchApplyMilliseconds += elapsedMilliseconds;
        maxWatchApplyMilliseconds = Math.Max(maxWatchApplyMilliseconds, elapsedMilliseconds);
        LogWatchPerformanceIfDue();
    }

    private void LogWatchPerformanceIfDue(Level? level = null)
    {
        if (watchDiagnosticsWindowStartTimestamp == 0)
        {
            BeginWatchDiagnosticsWindow();
            return;
        }

        double windowMilliseconds = GetElapsedMilliseconds(watchDiagnosticsWindowStartTimestamp);
        if (windowMilliseconds < WatchDiagnosticsIntervalMilliseconds)
            return;

        long allocatedBytes = GC.GetTotalAllocatedBytes();
        int gen0Collections = GC.CollectionCount(0);
        int gen1Collections = GC.CollectionCount(1);
        int gen2Collections = GC.CollectionCount(2);
        level ??= Engine.Scene as Level;
        string sceneSummary = level is null ? "scene=unavailable" : GetWatchSceneSummary(level);
        string adapterExceptions = WatchEntitySyncRegistry.ConsumeDebugExceptionSummary();
        MiaoTransportDebugSnapshot transport = context.ConsumeDebugTransportSnapshot();
        MiaoReceiveQueueDebugSnapshot receiveQueue = context.ConsumeDebugReceiveQueueSnapshot();
        Logger.Debug(
            LT.MiaoNetWatch,
            $"Watch performance over {windowMilliseconds / 1000d:F1}s: " +
            $"role={WatchDiagnosticsRole}; " +
            $"draw={watchDrawCount} ({watchDrawCount * 1000d / windowMilliseconds:F1}fps, " +
            $"gapAvg={GetAverageMilliseconds(totalWatchDrawGapMilliseconds, watchDrawGapSampleCount):F2}ms/" +
            $"max:{maxWatchDrawGapMilliseconds:F2}ms, " +
            $">20ms:{slowWatchDrawGapCount}/>33ms:{severeWatchDrawGapCount}, " +
            $"workAvg={GetAverageMilliseconds(totalWatchDrawMilliseconds, watchDrawCount):F3}ms/" +
            $"max:{maxWatchDrawMilliseconds:F3}ms); " +
            $"level=render:{watchLevelRenderCount} " +
            $"avg:{GetAverageMilliseconds(totalWatchLevelRenderMilliseconds, watchLevelRenderCount):F3}ms/" +
            $"max:{maxWatchLevelRenderMilliseconds:F3}ms, " +
            $"update:{watchLevelUpdateCount} " +
            $"avg:{GetAverageMilliseconds(totalWatchLevelUpdateMilliseconds, watchLevelUpdateCount):F3}ms/" +
            $"max:{maxWatchLevelUpdateMilliseconds:F3}ms, " +
            $"watchCameraAvg:{GetAverageMilliseconds(totalWatchCameraUpdateMilliseconds, watchLevelUpdateCount):F3}ms/" +
            $"max:{maxWatchCameraUpdateMilliseconds:F3}ms, ticks:{watchUpdateTickCount}; " +
            $"playerFrames=queued:{sentWatchPlayerFrameCount}/received:{receivedWatchPlayerFrameCount}, " +
            $"gapAvg={GetAverageMilliseconds(totalWatchPlayerFrameGapMilliseconds, receivedWatchPlayerFrameGapCount):F2}ms/" +
            $"max:{maxWatchPlayerFrameGapMilliseconds:F2}ms, " +
            $">20/33/50/100ms:{watchPlayerFrameGapOver20MillisecondsCount}/" +
            $"{watchPlayerFrameGapOver33MillisecondsCount}/" +
            $"{watchPlayerFrameGapOver50MillisecondsCount}/" +
            $"{watchPlayerFrameGapOver100MillisecondsCount}, " +
            $"silence:{FormatWatchAge(lastWatchPlayerFrameReceivedAt)}; " +
            $"playbackBuffers=max:{maxWatchPlayerPlaybackBufferDepth}/" +
            $"{maxWatchScenePlaybackBufferDepth}/{maxWatchPlayerEventBufferDepth}, " +
            $"underflows:{watchPlaybackUnderflowCount}; " +
            $"sceneDeltas=produced:{producedWatchDeltaCount}/{producedWatchStateCount}states/" +
            $"{producedWatchEventCount}events [{FormatWatchStateKinds(producedWatchStateKinds)}], " +
            $"received:{receivedWatchDeltaCount}/{receivedWatchStateCount}states/" +
            $"{receivedWatchEventCount}events maxStates:{maxWatchStatesPerDelta} " +
            $"[{FormatWatchStateKinds(receivedWatchStateKinds)}]; " +
            $"lifecycle=death:{receivedWatchDeathRespawnCount}/reload:{receivedWatchRoomReloadCount}/" +
            $"transition:{receivedWatchRoomTransitionCount}; " +
            $"capture={watchCaptureCallCount} calls/{capturedWatchStateCount} states " +
            $"(avg={GetAverageMilliseconds(totalWatchCaptureMilliseconds, watchCaptureCallCount):F3}ms, " +
            $"max={maxWatchCaptureMilliseconds:F3}ms); " +
            $"apply={watchApplyCallCount} calls/{appliedWatchStateCount} states " +
            $"(avg={GetAverageMilliseconds(totalWatchApplyMilliseconds, watchApplyCallCount):F3}ms, " +
            $"max={maxWatchApplyMilliseconds:F3}ms); " +
            $"sendQueue={transport.CurrentQueueDepth}/max:{transport.MaxQueueDepth}/" +
            $"oldest:{transport.OldestPacketAgeMilliseconds:F2}ms, " +
            $"sent:{transport.PacketsSent}/{transport.BytesSent / 1024d:F1}KiB, " +
            $"waitAvg:{transport.AverageQueueWaitMilliseconds:F3}ms/" +
            $"max:{transport.MaxQueueWaitMilliseconds:F3}ms, " +
            $"serializeMax:{transport.MaxSerializationMilliseconds:F3}ms/" +
            $"writeMax:{transport.MaxWriteMilliseconds:F3}ms, " +
            $"playerFrames:{transport.PlayerFramesSent}/{transport.PlayerFrameBytesSent / 1024d:F1}KiB, " +
            $"watchDeltas:{transport.WatchDeltasSent}/{transport.WatchDeltaBytesSent / 1024d:F1}KiB; " +
            $"receiveQueue={receiveQueue.CurrentQueueDepth}/max:{receiveQueue.MaxQueueDepth}/" +
            $"oldest:{receiveQueue.OldestPacketAgeMilliseconds:F2}ms, " +
            $"handled:{receiveQueue.PacketsHandled}/budgetHits:{receiveQueue.BudgetHits}/" +
            $"maxPerUpdate:{receiveQueue.MaxPacketsHandledPerUpdate}, " +
            $"waitAvg:{receiveQueue.AverageQueueWaitMilliseconds:F3}ms/" +
            $"max:{receiveQueue.MaxQueueWaitMilliseconds:F3}ms/" +
            $"drainMax:{receiveQueue.MaxDrainMilliseconds:F3}ms, " +
            $"playerFrames:{receiveQueue.PlayerFramesHandled} " +
            $"waitAvg:{receiveQueue.AveragePlayerFrameQueueWaitMilliseconds:F3}ms/" +
            $"max:{receiveQueue.MaxPlayerFrameQueueWaitMilliseconds:F3}ms, " +
            $"watchDeltas:{receiveQueue.WatchDeltasHandled} " +
            $"waitAvg:{receiveQueue.AverageWatchDeltaQueueWaitMilliseconds:F3}ms/" +
            $"max:{receiveQueue.MaxWatchDeltaQueueWaitMilliseconds:F3}ms; " +
            $"managed={GC.GetTotalMemory(false) / 1048576d:F1}MiB, " +
            $"allocated={(allocatedBytes - watchAllocatedBytesAtWindowStart) / 1048576d:F1}MiB, " +
            $"gc={gen0Collections - watchGen0CollectionsAtWindowStart}/" +
            $"{gen1Collections - watchGen1CollectionsAtWindowStart}/" +
            $"{gen2Collections - watchGen2CollectionsAtWindowStart}; " +
            $"{sceneSummary}; adapterExceptions={adapterExceptions}."
        );

        ResetWatchDiagnosticsCounters();
        BeginWatchDiagnosticsWindow();
    }

    private string GetWatchSceneSummary(Level level)
    {
        int coroutineCount = 0;
        int activeCoroutineCount = 0;
        foreach (Entity entity in level.Entities)
        {
            foreach (Coroutine coroutine in entity.Components.GetAll<Coroutine>())
            {
                coroutineCount++;
                if (coroutine.Active)
                    activeCoroutineCount++;
            }
        }

        return $"scene={level.Session.Level}, entities={level.Entities.Count}/" +
            $"active:{level.Entities.Count(entity => entity.Active)}/toAdd:{level.Entities.ToAdd.Count}, " +
            $"coroutines={coroutineCount}/active:{activeCoroutineCount}, " +
            $"watchCache={watchEntityStates?.Count ?? 0}/pending:{watchPendingEntityStateKeys.Count}/" +
            $"events:{watchPendingEntityEvents.Count}, " +
            $"playback={WatchPlaybackTiming.DelayFrames}f/" +
            $"{WatchPlaybackDelayTicks * 1000d / Stopwatch.Frequency:F0}ms, " +
            $"buffers=player:{watchPlayerFrameBuffer.Count}/scene:{watchSceneDeltaBuffer.Count}/" +
            $"events:{watchPlayerEventBuffer.Count}, " +
            $"sequence=applied:{lastWatchSequence}/received:{lastWatchReceivedSequence}, " +
            $"key={CountEntities<Key>()}, glider={CountEntities<Glider>()}, " +
            $"theo={CountEntities<TheoCrystal>()}, touchSwitch={CountEntities<TouchSwitch>()}, " +
            $"switchGate={CountEntities<SwitchGate>()}, crystalSpinner={CountEntities<CrystalStaticSpinner>()}, " +
            $"dustSpinner={CountEntities<DustStaticSpinner>()}, trackSpinner={CountEntities<TrackSpinner>()}";

        int CountEntities<TEntity>() where TEntity : Entity
            => level.Entities.OfType<TEntity>().Count();
    }

    internal void MarkWatchPerformance(string? label)
    {
        string markerLabel = string.IsNullOrWhiteSpace(label)
            ? "manual"
            : new(label.Where(character => !char.IsControl(character)).Take(64).ToArray());
        (MiaoQueueDebugState sendQueue, MiaoQueueDebugState receiveQueue) =
            context.GetDebugQueueStates();
        Level? level = Engine.Scene as Level;
        Logger.Info(
            LT.MiaoNetWatch,
            $"Watch performance marker '{markerLabel}': " +
            $"active={WatchDiagnosticsActive}, role={WatchDiagnosticsRole}, " +
            $"drawSilence={FormatWatchAge(lastWatchDrawStartedAt)}, " +
            $"playerFrameSilence={FormatWatchAge(lastWatchPlayerFrameReceivedAt)}, " +
            $"sendQueue={sendQueue.Depth}/oldest:{sendQueue.OldestPacketAgeMilliseconds:F2}ms, " +
            $"receiveQueue={receiveQueue.Depth}/oldest:{receiveQueue.OldestPacketAgeMilliseconds:F2}ms; " +
            $"{(level is null ? "scene=unavailable" : GetWatchSceneSummary(level))}."
        );
        LogWatchPerformanceIfDue(level);
    }

    private static void CountWatchStateKinds(
        IReadOnlyCollection<WatchEntityState> states,
        Dictionary<WatchEntityKind, int> counts
    )
    {
        foreach (WatchEntityState state in states)
        {
            counts.TryGetValue(state.Key.Kind, out int count);
            counts[state.Key.Kind] = count + 1;
        }
    }

    private static string FormatWatchStateKinds(Dictionary<WatchEntityKind, int> counts)
        => counts.Count == 0
            ? "none"
            : string.Join(
                ",",
                counts
                    .OrderByDescending(pair => pair.Value)
                    .ThenBy(pair => pair.Key)
                    .Take(8)
                    .Select(pair => $"{pair.Key}:{pair.Value}")
            );

    private static string FormatWatchAge(long timestamp)
        => timestamp == 0 ? "n/a" : $"{GetElapsedMilliseconds(timestamp):F2}ms";

    private void RecordWatchPlaybackBufferDepths()
    {
        maxWatchPlayerPlaybackBufferDepth = Math.Max(
            maxWatchPlayerPlaybackBufferDepth,
            watchPlayerFrameBuffer.Count
        );
        maxWatchScenePlaybackBufferDepth = Math.Max(
            maxWatchScenePlaybackBufferDepth,
            watchSceneDeltaBuffer.Count
        );
        maxWatchPlayerEventBufferDepth = Math.Max(
            maxWatchPlayerEventBufferDepth,
            watchPlayerEventBuffer.Count
        );
    }

    private void RecordWatchPlaybackUnderflow(bool underflow)
    {
        if (underflow && !watchPlaybackUnderflowActive)
            watchPlaybackUnderflowCount++;
        watchPlaybackUnderflowActive = underflow;
    }

    private void ResetWatchDiagnostics()
    {
        watchDiagnosticsWindowStartTimestamp = 0;
        ResetWatchDiagnosticsCounters();
        lastWatchDrawStartedAt = 0;
        lastWatchPlayerFrameReceivedAt = 0;
        context.ConsumeDebugTransportSnapshot();
        context.ConsumeDebugReceiveQueueSnapshot();
        CancelWatchDeathDiagnostics();
        WatchEntitySyncRegistry.ConsumeDebugExceptionSummary();
    }

    private void BeginWatchDiagnosticsWindow()
    {
        watchDiagnosticsWindowStartTimestamp = Stopwatch.GetTimestamp();
        watchAllocatedBytesAtWindowStart = GC.GetTotalAllocatedBytes();
        watchGen0CollectionsAtWindowStart = GC.CollectionCount(0);
        watchGen1CollectionsAtWindowStart = GC.CollectionCount(1);
        watchGen2CollectionsAtWindowStart = GC.CollectionCount(2);
    }

    private void ResetWatchDiagnosticsCounters()
    {
        watchUpdateTickCount = 0;
        watchDrawCount = 0;
        watchDrawGapSampleCount = 0;
        slowWatchDrawGapCount = 0;
        severeWatchDrawGapCount = 0;
        totalWatchDrawGapMilliseconds = 0d;
        maxWatchDrawGapMilliseconds = 0d;
        totalWatchDrawMilliseconds = 0d;
        maxWatchDrawMilliseconds = 0d;
        watchLevelRenderCount = 0;
        totalWatchLevelRenderMilliseconds = 0d;
        maxWatchLevelRenderMilliseconds = 0d;
        watchLevelUpdateCount = 0;
        totalWatchLevelUpdateMilliseconds = 0d;
        maxWatchLevelUpdateMilliseconds = 0d;
        totalWatchCameraUpdateMilliseconds = 0d;
        maxWatchCameraUpdateMilliseconds = 0d;
        sentWatchPlayerFrameCount = 0;
        receivedWatchPlayerFrameCount = 0;
        receivedWatchPlayerFrameGapCount = 0;
        watchPlayerFrameGapOver20MillisecondsCount = 0;
        watchPlayerFrameGapOver33MillisecondsCount = 0;
        watchPlayerFrameGapOver50MillisecondsCount = 0;
        watchPlayerFrameGapOver100MillisecondsCount = 0;
        totalWatchPlayerFrameGapMilliseconds = 0d;
        maxWatchPlayerFrameGapMilliseconds = 0d;
        maxWatchPlayerPlaybackBufferDepth = 0;
        maxWatchScenePlaybackBufferDepth = 0;
        maxWatchPlayerEventBufferDepth = 0;
        watchPlaybackUnderflowCount = 0;
        watchPlaybackUnderflowActive = false;
        producedWatchDeltaCount = 0;
        producedWatchStateCount = 0;
        producedWatchEventCount = 0;
        producedWatchStateKinds.Clear();
        receivedWatchDeltaCount = 0;
        receivedWatchStateCount = 0;
        receivedWatchEventCount = 0;
        receivedWatchStateKinds.Clear();
        maxWatchStatesPerDelta = 0;
        receivedWatchDeathRespawnCount = 0;
        receivedWatchRoomReloadCount = 0;
        receivedWatchRoomTransitionCount = 0;
        watchCaptureCallCount = 0;
        capturedWatchStateCount = 0;
        totalWatchCaptureMilliseconds = 0d;
        maxWatchCaptureMilliseconds = 0d;
        watchApplyCallCount = 0;
        appliedWatchStateCount = 0;
        totalWatchApplyMilliseconds = 0d;
        maxWatchApplyMilliseconds = 0d;
    }

    private void LogWatchEntityMismatch(
        PlayerLocation watchLocation,
        IReadOnlyCollection<WatchEntityKind> entityKinds
    )
    {
        long currentTimestamp = Stopwatch.GetTimestamp();
        if (lastWatchMismatchLogTimestamp != 0
            && GetElapsedMilliseconds(lastWatchMismatchLogTimestamp) < WatchDiagnosticsIntervalMilliseconds)
        {
            suppressedWatchMismatchCount++;
            return;
        }

        string suppressedCountSuffix = suppressedWatchMismatchCount == 0
            ? string.Empty
            : $"; suppressed={suppressedWatchMismatchCount}";
        Logger.Warn(
            LT.MiaoNetWatch,
            $"Applied watch state for room {watchLocation.Room} without promoting an entity mismatch " +
            $"to a room reload; kinds={string.Join(",", entityKinds)}{suppressedCountSuffix}."
        );
        lastWatchMismatchLogTimestamp = currentTimestamp;
        suppressedWatchMismatchCount = 0;
    }

    private void BeginWatchDeathDiagnostics(Level level)
    {
        watchDeathDiagnostics = new(
            PlayerLocation.FetchFrom(level.Session),
            GetWatchSceneSummary(level)
        );
    }

    private void RecordWatchDeathWipeSignal()
        => RecordWatchDeathTimestamp(
            static sample => sample.WipeSignalTimestamp,
            static (sample, timestamp) => sample.WipeSignalTimestamp = timestamp
        );

    private void RecordWatchDeathWipeStart()
        => RecordWatchDeathTimestamp(
            static sample => sample.WipeStartTimestamp,
            static (sample, timestamp) => sample.WipeStartTimestamp = timestamp
        );

    private void RecordWatchDeathBlackFrame()
        => RecordWatchDeathTimestamp(
            static sample => sample.BlackFrameTimestamp,
            static (sample, timestamp) => sample.BlackFrameTimestamp = timestamp
        );

    private void RecordWatchDeathStateReady()
        => RecordWatchDeathTimestamp(
            static sample => sample.StateReadyTimestamp,
            static (sample, timestamp) => sample.StateReadyTimestamp = timestamp
        );

    private void RecordWatchDeathRespawnReady()
        => RecordWatchDeathTimestamp(
            static sample => sample.RespawnReadyTimestamp,
            static (sample, timestamp) => sample.RespawnReadyTimestamp = timestamp
        );

    private void RecordWatchDeathCameraReady(bool timedOut)
    {
        if (watchDeathDiagnostics is not { CameraReadyTimestamp: 0 } sample)
            return;

        sample.CameraReadyTimestamp = Stopwatch.GetTimestamp();
        sample.CameraTimedOut = timedOut;
    }

    private void BeginWatchDeathReloadDiagnostics()
    {
        if (watchDeathDiagnostics is not { } sample)
            return;

        sample.ManagedBytesBeforeReload = GC.GetTotalMemory(false);
        sample.AllocatedBytesBeforeReload = GC.GetTotalAllocatedBytes();
        sample.Gen0BeforeReload = GC.CollectionCount(0);
        sample.Gen1BeforeReload = GC.CollectionCount(1);
        sample.Gen2BeforeReload = GC.CollectionCount(2);
    }

    private void RecordWatchDeathReloadSnapshot(IReadOnlyCollection<WatchEntityState> states)
    {
        if (watchDeathDiagnostics is not { } sample)
            return;

        sample.SnapshotStateCount = states.Count;
        sample.SnapshotKindCount = states.Select(state => state.Key.Kind).Distinct().Count();
    }

    private void RecordWatchDeathSessionPreparation(long startTimestamp, bool prepared)
    {
        if (watchDeathDiagnostics is not { } sample)
            return;

        sample.SessionPrepared = prepared;
        sample.SessionPreparationMilliseconds = GetElapsedMilliseconds(startTimestamp);
    }

    private void RecordWatchDeathReload(WatchLevelReloadTiming timing, bool completed)
    {
        if (watchDeathDiagnostics is { } sample)
        {
            sample.ReloadAttempted = true;
            sample.ReloadCompleted = completed;
            sample.ReloadTiming = timing;
        }
    }

    private void RecordWatchDeathSnapshotApply(long startTimestamp, string adapterTimings)
    {
        if (watchDeathDiagnostics is not { } sample)
            return;

        sample.SnapshotApplyMilliseconds = GetElapsedMilliseconds(startTimestamp);
        sample.AdapterTimings = adapterTimings;
    }

    private void RecordWatchDeathPresentation(long startTimestamp)
    {
        if (watchDeathDiagnostics is { } sample)
            sample.PresentationMilliseconds = GetElapsedMilliseconds(startTimestamp);
    }

    private void CompleteWatchDeathDiagnostics(Level level, float cameraWaitSeconds)
    {
        if (watchDeathDiagnostics is not { } sample)
            return;

        long completedTimestamp = Stopwatch.GetTimestamp();
        long allocatedBytes = GC.GetTotalAllocatedBytes();
        int gen0Collections = GC.CollectionCount(0);
        int gen1Collections = GC.CollectionCount(1);
        int gen2Collections = GC.CollectionCount(2);
        WatchLevelReloadTiming reload = sample.ReloadTiming;
        string reloadTotal = sample.ReloadCompleted
            ? $"{reload.TotalMilliseconds:F3}ms"
            : sample.ReloadAttempted ? "incomplete" : "skipped";
        Logger.Debug(
            LT.MiaoNetWatch,
            $"Watcher death reload profile: room={sample.SourceLocation.Room}; " +
            $"events=t+wipeSignal:{FormatWatchDeathOffset(sample, sample.WipeSignalTimestamp)}/" +
            $"wipeStart:{FormatWatchDeathOffset(sample, sample.WipeStartTimestamp)}/" +
            $"black:{FormatWatchDeathOffset(sample, sample.BlackFrameTimestamp)}/" +
            $"state:{FormatWatchDeathOffset(sample, sample.StateReadyTimestamp)}/" +
            $"respawn:{FormatWatchDeathOffset(sample, sample.RespawnReadyTimestamp)}/" +
            $"camera:{FormatWatchDeathOffset(sample, sample.CameraReadyTimestamp)}" +
            $"({(sample.CameraTimedOut ? "timeout" : "fresh")},gameWait={cameraWaitSeconds * 1000f:F1}ms); " +
            $"work=session:{sample.SessionPreparationMilliseconds:F3}ms/prepared:{sample.SessionPrepared}, " +
            $"reload:{reloadTotal} " +
            $"[preUnload:{FormatOptionalMilliseconds(reload.PreUnloadMilliseconds)}," +
            $"unload:{FormatOptionalMilliseconds(reload.UnloadMilliseconds)}," +
            $"gc:{FormatOptionalMilliseconds(reload.GcAndFinalizersMilliseconds)}," +
            $"snapshotWait:{FormatOptionalMilliseconds(reload.SnapshotWaitMilliseconds)}," +
            $"load:{FormatOptionalMilliseconds(reload.LoadMilliseconds)}," +
            $"tail:{FormatOptionalMilliseconds(reload.TailMilliseconds)}], " +
            $"snapshotApply:{sample.SnapshotApplyMilliseconds:F3}ms, " +
            $"presentation:{sample.PresentationMilliseconds:F3}ms; " +
            $"total={GetElapsedMilliseconds(sample.BeginTimestamp, completedTimestamp):F3}ms, " +
            $"blackHold={GetOptionalElapsedMilliseconds(sample.BlackFrameTimestamp, completedTimestamp)}; " +
            $"resources=managed:{sample.ManagedBytesBeforeReload / 1048576d:F1}->" +
            $"{GC.GetTotalMemory(false) / 1048576d:F1}MiB, " +
            $"allocated:{(allocatedBytes - sample.AllocatedBytesBeforeReload) / 1048576d:F1}MiB, " +
            $"gc:{gen0Collections - sample.Gen0BeforeReload}/" +
            $"{gen1Collections - sample.Gen1BeforeReload}/" +
            $"{gen2Collections - sample.Gen2BeforeReload}, " +
            $"snapshot:{sample.SnapshotStateCount}states/{sample.SnapshotKindCount}kinds; " +
            $"sceneBefore=[{sample.SceneBefore}]; sceneAfter=[{GetWatchSceneSummary(level)}]; " +
            $"slowAdapters=[{sample.AdapterTimings}]."
        );
        watchDeathDiagnostics = null;
    }

    private void CancelWatchDeathDiagnostics()
    {
        watchDeathDiagnostics = null;
        WatchLevelReloadDiagnostics.Cancel();
    }

    private void RecordWatchDeathTimestamp(
        Func<WatchDeathDiagnosticsSample, long> selector,
        Action<WatchDeathDiagnosticsSample, long> assign
    )
    {
        if (watchDeathDiagnostics is not { } sample || selector(sample) != 0)
            return;

        assign(sample, Stopwatch.GetTimestamp());
    }

    private static string FormatWatchDeathOffset(
        WatchDeathDiagnosticsSample sample,
        long timestamp
    ) => timestamp == 0
        ? "n/a"
        : $"{GetElapsedMilliseconds(sample.BeginTimestamp, timestamp):F1}ms";

    private static string FormatOptionalMilliseconds(double milliseconds)
        => milliseconds < 0d ? "n/a" : $"{milliseconds:F3}ms";

    private static string GetOptionalElapsedMilliseconds(long startTimestamp, long endTimestamp)
        => startTimestamp == 0
            ? "n/a"
            : $"{GetElapsedMilliseconds(startTimestamp, endTimestamp):F3}ms";

    private static double GetElapsedMilliseconds(long startTimestamp)
        => (Stopwatch.GetTimestamp() - startTimestamp) * 1000d / Stopwatch.Frequency;

    private static double GetElapsedMilliseconds(long startTimestamp, long endTimestamp)
        => (endTimestamp - startTimestamp) * 1000d / Stopwatch.Frequency;

    private static double GetAverageMilliseconds(double totalMilliseconds, int sampleCount)
        => sampleCount == 0 ? 0d : totalMilliseconds / sampleCount;
}

internal readonly record struct WatchLevelReloadTiming(
    double TotalMilliseconds,
    double PreUnloadMilliseconds,
    double UnloadMilliseconds,
    double GcAndFinalizersMilliseconds,
    double SnapshotWaitMilliseconds,
    double LoadMilliseconds,
    double TailMilliseconds
);

internal static class WatchLevelReloadDiagnostics
{
    private static Level? activeLevel;
    private static long reloadStartTimestamp;
    private static long unloadStartTimestamp;
    private static long unloadEndTimestamp;
    private static long garbageCollectionEndTimestamp;
    private static long loadStartTimestamp;
    private static long loadEndTimestamp;

    public static bool IsActive => activeLevel is not null;

    public static void Load()
    {
        On.Celeste.Level.UnloadLevel += Level_UnloadLevel;
        On.Celeste.Level.LoadLevel += Level_LoadLevel;
    }

    public static void Unload()
    {
        On.Celeste.Level.LoadLevel -= Level_LoadLevel;
        On.Celeste.Level.UnloadLevel -= Level_UnloadLevel;
        Cancel();
    }

    public static void Begin(Level level)
    {
        activeLevel = level;
        reloadStartTimestamp = Stopwatch.GetTimestamp();
        unloadStartTimestamp = 0;
        unloadEndTimestamp = 0;
        garbageCollectionEndTimestamp = 0;
        loadStartTimestamp = 0;
        loadEndTimestamp = 0;
    }

    public static WatchLevelReloadTiming End()
    {
        long reloadEndTimestamp = Stopwatch.GetTimestamp();
        WatchLevelReloadTiming timing = new(
            GetOptionalElapsedMilliseconds(reloadStartTimestamp, reloadEndTimestamp),
            GetOptionalElapsedMilliseconds(reloadStartTimestamp, unloadStartTimestamp),
            GetOptionalElapsedMilliseconds(unloadStartTimestamp, unloadEndTimestamp),
            GetOptionalElapsedMilliseconds(unloadEndTimestamp, garbageCollectionEndTimestamp),
            GetOptionalElapsedMilliseconds(garbageCollectionEndTimestamp, loadStartTimestamp),
            GetOptionalElapsedMilliseconds(loadStartTimestamp, loadEndTimestamp),
            GetOptionalElapsedMilliseconds(loadEndTimestamp, reloadEndTimestamp)
        );
        Cancel();
        return timing;
    }

    public static void Cancel()
    {
        activeLevel = null;
        reloadStartTimestamp = 0;
        unloadStartTimestamp = 0;
        unloadEndTimestamp = 0;
        garbageCollectionEndTimestamp = 0;
        loadStartTimestamp = 0;
        loadEndTimestamp = 0;
    }

    public static void RecordGarbageCollectionCompleted()
    {
        if (activeLevel is not null && garbageCollectionEndTimestamp == 0)
            garbageCollectionEndTimestamp = Stopwatch.GetTimestamp();
    }

    private static void Level_UnloadLevel(On.Celeste.Level.orig_UnloadLevel orig, Level self)
    {
        if (!ReferenceEquals(activeLevel, self))
        {
            orig(self);
            return;
        }

        unloadStartTimestamp = Stopwatch.GetTimestamp();
        try
        {
            orig(self);
        }
        finally
        {
            unloadEndTimestamp = Stopwatch.GetTimestamp();
        }
    }

    private static void Level_LoadLevel(
        On.Celeste.Level.orig_LoadLevel orig,
        Level self,
        Player.IntroTypes playerIntro,
        bool isFromLoader
    )
    {
        if (!ReferenceEquals(activeLevel, self))
        {
            orig(self, playerIntro, isFromLoader);
            return;
        }

        loadStartTimestamp = Stopwatch.GetTimestamp();
        try
        {
            orig(self, playerIntro, isFromLoader);
        }
        finally
        {
            loadEndTimestamp = Stopwatch.GetTimestamp();
        }
    }

    private static double GetOptionalElapsedMilliseconds(long startTimestamp, long endTimestamp)
        => startTimestamp == 0 || endTimestamp == 0
            ? -1d
            : (endTimestamp - startTimestamp) * 1000d / Stopwatch.Frequency;
}
#endif
