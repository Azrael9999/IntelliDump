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

    public IReadOnlyList<AnalysisIssue> Analyze(DumpSnapshot snapshot)
    {
        var issues = new List<AnalysisIssue>();

        AddCrashSignals(snapshot, issues);
        AddMemorySignals(snapshot, issues);
        AddBlockingSignals(snapshot, issues);
        AddCpuSignals(snapshot, issues);
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
                string.Join(Environment.NewLine, snapshot.Warnings),
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
