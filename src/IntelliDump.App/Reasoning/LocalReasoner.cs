using System;
using System.Linq;
using System.Text;
using IntelliDump.Diagnostics;

namespace IntelliDump.Reasoning;

/// <summary>
/// Applies local heuristic reasoning to a dump snapshot to detect hotspots such as crashes,
/// high CPU/memory usage, blocking/locking, and thread starvation.
/// </summary>
public sealed class LocalReasoner
{
    private const ulong Gigabyte = 1_073_741_824;
    private const double Gen2DominanceThreshold = 0.8;
    private const double PinnedPressureThreshold = 0.1;

    public IReadOnlyList<AnalysisIssue> Analyze(DumpSnapshot snapshot)
    {
        var issues = new List<AnalysisIssue>();

        AddCrashSignals(snapshot, issues);
        AddMemorySignals(snapshot, issues);
        AddGcNuanceSignals(snapshot, issues);
        AddBlockingSignals(snapshot, issues);
        AddCpuSignals(snapshot, issues);
        AddStringSignals(snapshot, issues);
        AddFinalizerSignals(snapshot, issues);
        AddThreadpoolSignals(snapshot, issues);
        AddWaitClassificationSignals(snapshot, issues);
        AddNonMonitorBlockingSignals(snapshot, issues);
        AddHeapLeakSignals(snapshot, issues);
        AddModuleAnomalies(snapshot, issues);
        AddNativeSignals(snapshot, issues);
        AddCoverageSignals(snapshot, issues);
        AddDataAvailabilitySignals(snapshot, issues);
        AddDeadlockSignals(snapshot, issues);

        if (issues.Count == 0)
        {
            issues.Add(new AnalysisIssue(
                "No critical signals detected",
                IssueSeverity.Info,
                "The heuristic engine did not detect obvious failures, deadlocks, or resource saturation.",
                "Inspect thread stacks and GC statistics manually to confirm health."));
        }

        return issues;
    }

    private static void AddDataAvailabilitySignals(DumpSnapshot snapshot, ICollection<AnalysisIssue> issues)
    {
        if (snapshot.Warnings.Count > 0)
        {
            issues.Add(new AnalysisIssue(
                "Data availability warning",
                IssueSeverity.Warning,
                string.Join(Environment.NewLine, snapshot.Warnings.Select(w => $"{w.Category}: {w.Message}")),
                "Consider capturing a full memory dump to improve heap-related analysis."));
        }
    }

    private static void AddCrashSignals(DumpSnapshot snapshot, ICollection<AnalysisIssue> issues)
    {
        if (snapshot.FaultingThread is not null)
        {
            var evidence = new StringBuilder();
            evidence.AppendLine($"Thread {snapshot.FaultingThread.ManagedId} reported an exception:");
            evidence.Append(snapshot.FaultingThread.CurrentException);

            issues.Add(new AnalysisIssue(
                "Application crash or unhandled exception",
                IssueSeverity.Critical,
                evidence.ToString().Trim(),
                "Inspect this thread's call stack and surrounding threads for cascading failures. Validate null checks and recent deployments."));
        }
    }

    private static void AddMemorySignals(DumpSnapshot snapshot, ICollection<AnalysisIssue> issues)
    {
        var totalMb = snapshot.Gc.TotalHeapBytes / (1024 * 1024);
        var lohMb = snapshot.Gc.LargeObjectHeapBytes / (1024 * 1024);

        if (snapshot.Gc.TotalHeapBytes > 2 * Gigabyte)
        {
            issues.Add(new AnalysisIssue(
                "High managed memory pressure",
                IssueSeverity.Critical,
                $"Total managed heap is {totalMb:N0} MB across {snapshot.Gc.SegmentCount} segments (LOH {lohMb:N0} MB).",
                "Capture allocation patterns with ETW in a similar workload, review caches for eviction, and confirm GC configuration for the IIS pool."));
        }
        else if (lohMb > 512)
        {
            issues.Add(new AnalysisIssue(
                "Large Object Heap growth",
                IssueSeverity.Warning,
                $"LOH is {lohMb:N0} MB; large buffers or array-based payloads may fragment the heap.",
                "Investigate buffering strategies, ensure pooled arrays are reused, and consider Span/Memory usage to avoid oversized allocations."));
        }
    }

    private static void AddGcNuanceSignals(DumpSnapshot snapshot, ICollection<AnalysisIssue> issues)
    {
        if (snapshot.Gc.TotalHeapBytes == 0)
        {
            return;
        }

        var gen2Share = (double)snapshot.Gc.Gen2Bytes / snapshot.Gc.TotalHeapBytes;
        var gen0Share = (double)snapshot.Gc.Gen0Bytes / snapshot.Gc.TotalHeapBytes;
        var pinnedShare = (double)snapshot.Gc.PinnedBytes / snapshot.Gc.TotalHeapBytes;

        if (gen2Share >= Gen2DominanceThreshold && gen0Share < 0.1)
        {
            issues.Add(new AnalysisIssue(
                "Gen2 dominant managed heap",
                IssueSeverity.Warning,
                $"Gen2 holds {(gen2Share * 100):N0}% of the managed heap while Gen0 is {(gen0Share * 100):N0}%. This usually indicates long-lived objects or fragmentation.",
                "Inspect caches and singletons for growth, review LOH/Gen2 allocation patterns, and consider forcing periodic trimming of oversized caches."));
        }

        if (pinnedShare >= PinnedPressureThreshold)
        {
            issues.Add(new AnalysisIssue(
                "High pinned object pressure",
                IssueSeverity.Warning,
                $"Pinned objects account for {(pinnedShare * 100):N1}% of the heap; this can prevent compaction and increase fragmentation.",
                "Audit pinned handles (GCHandle.Alloc), large native interop buffers, and long-lived spans to reduce pinning duration."));
        }

        if (!snapshot.Gc.IsServerGc && Environment.ProcessorCount >= 4)
        {
            issues.Add(new AnalysisIssue(
                "Workstation GC on multi-core host",
                IssueSeverity.Info,
                $"Process is using workstation GC on a {Environment.ProcessorCount}-core machine; throughput can suffer for server workloads.",
                "Consider enabling server GC for IIS worker processes handling parallel requests."));
        }
    }

    private static void AddBlockingSignals(DumpSnapshot snapshot, ICollection<AnalysisIssue> issues)
    {
        var locksHeld = snapshot.Threads.Count(t => t.LockCount > 0);
        if (snapshot.Blocking.SyncBlockCount > 0)
        {
            var severity = snapshot.Blocking.SyncBlockCount > 10 || snapshot.Blocking.WaitingThreadCount > 5
                ? IssueSeverity.Critical
                : IssueSeverity.Warning;

            issues.Add(new AnalysisIssue(
                "Synchronization contention",
                severity,
                $"{snapshot.Blocking.SyncBlockCount} sync blocks observed with {snapshot.Blocking.WaitingThreadCount} waiting threads; {locksHeld} managed threads hold locks.",
                "Inspect lock owners and waiting stacks to confirm whether Monitor/lock usage or coarse critical sections are causing stalls. Consider async I/O to reduce blocking."));
        }
        else if (locksHeld > 0)
        {
            issues.Add(new AnalysisIssue(
                "Locks held by managed threads",
                IssueSeverity.Warning,
                $"{locksHeld} threads hold CLR locks even though no sync blocks are waiting.",
                "Validate these locks are short-lived; review thread stacks to ensure no long-running work is done while holding locks."));
        }
    }

    private static void AddCpuSignals(DumpSnapshot snapshot, ICollection<AnalysisIssue> issues)
    {
        var runningThreads = snapshot.Threads.Count(t => t.State.Contains("Running", StringComparison.OrdinalIgnoreCase));

        if (runningThreads > Environment.ProcessorCount * 4)
        {
            issues.Add(new AnalysisIssue(
                "High CPU suspicion",
                IssueSeverity.Warning,
                $"{runningThreads} managed threads were running when the dump was captured; this is elevated for a machine with {Environment.ProcessorCount} logical cores.",
                "Correlate with IIS worker process counters. Review hot stacks for tight loops or CPU-bound JSON/XML parsing."));
        }

        var gcThreads = snapshot.Threads.Count(t => t.IsGcThread);
        if (gcThreads > Math.Max(2, Environment.ProcessorCount / 2))
        {
            issues.Add(new AnalysisIssue(
                "GC threads elevated",
                IssueSeverity.Warning,
                $"{gcThreads} GC threads were present, which can accompany memory pressure or GC-induced pauses.",
                "Inspect GC settings (server vs workstation), review allocation spikes, and consider enabling GC ETW events to confirm pause contributors."));
        }
    }

    private static void AddFinalizerSignals(DumpSnapshot snapshot, ICollection<AnalysisIssue> issues)
    {
        var finalizers = snapshot.FinalizerThreads;
        if (finalizers.Count == 0)
        {
            return;
        }

        var blockedFinalizers = finalizers
            .Where(t => t.State.Contains("Wait", StringComparison.OrdinalIgnoreCase)
                        || t.State.Contains("Block", StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (blockedFinalizers.Count > 0)
        {
            issues.Add(new AnalysisIssue(
                "Finalizer thread may be blocked",
                IssueSeverity.Critical,
                $"Finalizer thread(s) are waiting/blocking: {string.Join(", ", blockedFinalizers.Select(f => f.ManagedId))}.",
                "Inspect finalizer thread stacks for waits on locks or external resources; a blocked finalizer stalls IDisposable cleanup and can grow memory usage."));
        }

        var finalizeFrames = snapshot.Threads
            .SelectMany(t => t.Stack)
            .Count(frame => frame.Contains("Finalize", StringComparison.OrdinalIgnoreCase));
        if (finalizeFrames > 50)
        {
            issues.Add(new AnalysisIssue(
                "Heavy finalization activity",
                IssueSeverity.Warning,
                $"Detected {finalizeFrames} frames containing 'Finalize'; there may be many objects awaiting finalization.",
                "Profile allocations of finalizable types and ensure IDisposable objects are disposed promptly to avoid finalizer backlogs."));
        }
    }

    private static void AddStringSignals(DumpSnapshot snapshot, ICollection<AnalysisIssue> issues)
    {
        if (snapshot.TotalStringOccurrences <= 0)
        {
            return;
        }

        var duplicationRatio = snapshot.TotalStringOccurrences == 0
            ? 0
            : 1.0 - ((double)snapshot.UniqueStringCount / snapshot.TotalStringOccurrences);

        if (duplicationRatio >= 0.75 && snapshot.TotalStringOccurrences >= 20)
        {
            issues.Add(new AnalysisIssue(
                "High duplicate string frequency",
                IssueSeverity.Warning,
                $"Detected {snapshot.TotalStringOccurrences:N0} string occurrences with only {snapshot.UniqueStringCount:N0} unique values (duplication {(duplicationRatio * 100):N0}%).",
                "Investigate repeated SQL/XML payloads or hot request paths generating identical data; consider caching results or reducing per-request allocations."));
        }

        if (snapshot.StackStringOccurrences > snapshot.HeapStringOccurrences * 2 && snapshot.StackStringOccurrences >= 20)
        {
            issues.Add(new AnalysisIssue(
                "Strings concentrated on stacks",
                IssueSeverity.Info,
                $"Stack-captured strings ({snapshot.StackStringOccurrences:N0}) significantly exceed heap strings ({snapshot.HeapStringOccurrences:N0}); requests may be constructing large payloads synchronously.",
                "Inspect thread stacks for serialization/parsing hot spots and consider streaming or chunking payload processing."));
        }
    }

    private static void AddThreadpoolSignals(DumpSnapshot snapshot, ICollection<AnalysisIssue> issues)
    {
        var runningThreads = snapshot.Threads.Count(t => t.State.Contains("Running", StringComparison.OrdinalIgnoreCase));
        var waitingThreads = snapshot.Threads.Count(t => t.State.Contains("Wait", StringComparison.OrdinalIgnoreCase));

        if (runningThreads <= Math.Max(1, Environment.ProcessorCount / 2) && waitingThreads > runningThreads * 4 && waitingThreads >= 8)
        {
            issues.Add(new AnalysisIssue(
                "ThreadPool starvation or queue backlog",
                IssueSeverity.Warning,
                $"{waitingThreads} threads are waiting while only {runningThreads} are running; work may be queued behind blocked ThreadPool threads.",
                "Inspect worker thread stacks for blocking calls (I/O, locks) and consider increasing minimum ThreadPool threads or reducing synchronous waits."));
        }

        var gateFrames = snapshot.Threads
            .SelectMany(t => t.Stack.Take(5))
            .Count(f => f.Contains("ThreadPoolWorkQueue", StringComparison.OrdinalIgnoreCase) ||
                        f.Contains("PortableThreadPool", StringComparison.OrdinalIgnoreCase));

        if (gateFrames >= 5)
        {
            issues.Add(new AnalysisIssue(
                "ThreadPool gate congestion",
                IssueSeverity.Warning,
                $"Multiple threads are in ThreadPool gate/dispatch frames ({gateFrames} occurrences in top-of-stack frames).",
                "Review synchronous work on ThreadPool threads and reduce long-running blocking operations to free the queue."));
        }
    }

    private static void AddWaitClassificationSignals(DumpSnapshot snapshot, ICollection<AnalysisIssue> issues)
    {
        var httpWaits = snapshot.Threads.Count(t =>
            t.Stack.Any(f => f.Contains("HttpClient", StringComparison.OrdinalIgnoreCase)
                             || f.Contains("System.Net.Http", StringComparison.OrdinalIgnoreCase)
                             || f.Contains("HttpConnection", StringComparison.OrdinalIgnoreCase)));

        if (httpWaits >= 3)
        {
            issues.Add(new AnalysisIssue(
                "HTTP I/O waits observed",
                IssueSeverity.Info,
                $"{httpWaits} threads show HttpClient/HTTP send/receive frames, suggesting outbound HTTP I/O is a bottleneck.",
                "Check network/endpoint latency and ensure async HTTP is not blocked by sync waits (ConfigureAwait, Task.Wait())."));
        }

        var sqlWaits = snapshot.Threads.Count(t =>
            t.Stack.Any(f => f.Contains("SqlClient", StringComparison.OrdinalIgnoreCase)
                             || f.Contains("Microsoft.Data.SqlClient", StringComparison.OrdinalIgnoreCase)
                             || f.Contains("System.Data.SqlClient", StringComparison.OrdinalIgnoreCase)));

        if (sqlWaits >= 3)
        {
            issues.Add(new AnalysisIssue(
                "SQL I/O waits observed",
                IssueSeverity.Info,
                $"{sqlWaits} threads show SqlClient frames; database calls may be stalling threads.",
                "Check DB latency/locks, parameterization, and ensure async DB APIs are used without sync-over-async waits."));
        }

        var syncTaskWaits = snapshot.Threads.Count(t =>
            t.Stack.Any(f => f.Contains("Task.Wait", StringComparison.OrdinalIgnoreCase)
                             || f.Contains("Task`1.GetResult", StringComparison.OrdinalIgnoreCase)
                             || f.Contains("GetAwaiter().GetResult", StringComparison.OrdinalIgnoreCase)));

        if (syncTaskWaits >= 3)
        {
            issues.Add(new AnalysisIssue(
                "Sync-over-async / Task waits detected",
                IssueSeverity.Warning,
                $"{syncTaskWaits} threads are blocked on Task.Wait/Result; this can deadlock async flows or starve the ThreadPool.",
                "Remove sync waits on Tasks (use async all the way, ConfigureAwait(false) where safe) to avoid async deadlocks and starvation."));
        }
    }

    private static void AddNonMonitorBlockingSignals(DumpSnapshot snapshot, ICollection<AnalysisIssue> issues)
    {
        var waitingThreads = snapshot.Threads
            .Where(t => t.State.Contains("Wait", StringComparison.OrdinalIgnoreCase)
                        || t.State.Contains("Sleep", StringComparison.OrdinalIgnoreCase)
                        || t.State.Contains("Block", StringComparison.OrdinalIgnoreCase))
            .ToList();

        var commonFrames = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var thread in waitingThreads)
        {
            var topFrame = thread.Stack.FirstOrDefault(f => !string.IsNullOrWhiteSpace(f));
            if (string.IsNullOrWhiteSpace(topFrame))
            {
                continue;
            }

            if (topFrame.Contains("Monitor", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            commonFrames[topFrame] = commonFrames.TryGetValue(topFrame, out var count) ? count + 1 : 1;
        }

        var hotspots = commonFrames
            .Where(kvp => kvp.Value >= 5)
            .OrderByDescending(kvp => kvp.Value)
            .Take(3)
            .ToList();

        if (hotspots.Count > 0)
        {
            var evidence = string.Join(Environment.NewLine, hotspots.Select(h => $"{h.Value} threads blocked at: {h.Key}"));
            issues.Add(new AnalysisIssue(
                "Non-monitor blocking hotspot",
                IssueSeverity.Warning,
                evidence,
                "Investigate these shared frames for I/O waits (HTTP/SQL), async-over-sync patterns, or external resource throttling."));
        }
    }

    private static void AddHeapLeakSignals(DumpSnapshot snapshot, ICollection<AnalysisIssue> issues)
    {
        if (snapshot.Gc.TotalHeapBytes == 0 || snapshot.HeapTypes.Count == 0)
        {
            return;
        }

        var top = snapshot.HeapTypes.First();
        var topShare = (double)top.TotalSize / snapshot.Gc.TotalHeapBytes;
        if (topShare >= 0.5)
        {
            issues.Add(new AnalysisIssue(
                "Dominant heap type detected",
                IssueSeverity.Warning,
                $"{top.TypeName} occupies {(topShare * 100):N1}% of the managed heap across {top.Count:N0} instances.",
                "Review allocation paths for this type, ensure caches are bounded, and consider trimming or pooling strategies to prevent leaks."));
        }
    }

    private static void AddModuleAnomalies(DumpSnapshot snapshot, ICollection<AnalysisIssue> issues)
    {
        if (snapshot.LoadedModules.Count == 0)
        {
            return;
        }

        var largeModules = snapshot.LoadedModules
            .Where(m => m.Size >= 200UL * 1024 * 1024)
            .OrderByDescending(m => m.Size)
            .Take(5)
            .ToList();

        if (largeModules.Count > 0)
        {
            var evidence = string.Join(Environment.NewLine, largeModules.Select(m => $"{m.Name} ({m.Size / (1024 * 1024)} MB)"));
            issues.Add(new AnalysisIssue(
                "Unusually large modules loaded",
                IssueSeverity.Warning,
                evidence,
                "Validate whether these modules (profilers, native extensions, debuggers) are expected; oversized modules can affect memory footprint and stability."));
        }

        var profilerModules = snapshot.LoadedModules
            .Where(m => m.Name.Contains("profiler", StringComparison.OrdinalIgnoreCase)
                        || m.Name.Contains("instrumentation", StringComparison.OrdinalIgnoreCase)
                        || m.Name.Contains("agent", StringComparison.OrdinalIgnoreCase))
            .Take(5)
            .ToList();

        if (profilerModules.Count > 0)
        {
            var evidence = string.Join(Environment.NewLine, profilerModules.Select(m => m.Name));
            issues.Add(new AnalysisIssue(
                "Profiler/instrumentation modules detected",
                IssueSeverity.Info,
                evidence,
                "Ensure these extensions are required for the workload; instrumentation can change thread scheduling, GC behavior, and memory usage."));
        }
    }

    private static void AddCoverageSignals(DumpSnapshot snapshot, ICollection<AnalysisIssue> issues)
    {
        if (snapshot.HeapHistogramCoverage < 0.5 && snapshot.HeapTypes.Count > 0)
        {
            issues.Add(new AnalysisIssue(
                "Heap type coverage limited",
                IssueSeverity.Info,
                $"Top heap types cover only {(snapshot.HeapHistogramCoverage * 100):N1}% of the managed heap; {snapshot.TotalHeapTypeCount - snapshot.HeapTypes.Count} types are not shown.",
                "Increase heap histogram depth or collect a full heap walk to identify additional large types."));
        }

        if (snapshot.ModuleCoverageShown < 0.9 && snapshot.TotalModuleCount > 0)
        {
            issues.Add(new AnalysisIssue(
                "Module list truncated",
                IssueSeverity.Info,
                $"Module section shows top {snapshot.LoadedModules.Count} of {snapshot.TotalModuleCount} modules (~{snapshot.ModuleCoverageShown * 100:N1}% of size).",
                "Review the full module list in a debugger if unexpected extensions could affect stability."));
        }
    }

    private static void AddNativeSignals(DumpSnapshot snapshot, ICollection<AnalysisIssue> issues)
    {
        if (snapshot.TotalModuleBytes > 1_000_000_000 && snapshot.Gc.TotalHeapBytes < 512UL * 1024 * 1024)
        {
            issues.Add(new AnalysisIssue(
                "Native footprint elevated",
                IssueSeverity.Info,
                $"Managed heap is {snapshot.Gc.TotalHeapBytes / (1024 * 1024):N0} MB while loaded modules total {snapshot.TotalModuleBytes / (1024 * 1024):N0} MB; native allocations or large modules may dominate memory.",
                "Inspect native modules and consider native heap/handle investigations (UMDH, ProcDump with -ma, VMMap) to confirm native memory usage."));
        }
    }

    private static void AddDeadlockSignals(DumpSnapshot snapshot, ICollection<AnalysisIssue> issues)
    {
        var candidates = snapshot.DeadlockCandidates.Where(d => d.WaitingThreads > 0).ToList();
        if (candidates.Count == 0)
        {
            return;
        }

        var evidence = string.Join(Environment.NewLine, candidates.Select(c =>
            $"Object 0x{c.ObjectAddress:X} owner={c.OwnerThreadId?.ToString() ?? "unknown"} waiting={c.WaitingThreads}"));

        issues.Add(new AnalysisIssue(
            "Potential deadlock/monitor contention",
            IssueSeverity.Critical,
            evidence,
            "Inspect the owning and waiting thread stacks for shared monitors and blocking resources (DB/IO). Consider capturing contention ETW events in reproduction scenarios."));
    }
}
