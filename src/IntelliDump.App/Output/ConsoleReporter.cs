using System.Linq;
using IntelliDump.Diagnostics;
using IntelliDump.Reasoning;

namespace IntelliDump.Output;

public sealed class ConsoleReporter
{
    public void Render(DumpSnapshot snapshot, IReadOnlyList<AnalysisIssue> issues)
    {
        PrintHeader(snapshot);
        PrintIssues(issues);
        PrintThreadOverview(snapshot.Threads);
        PrintTopStacks(snapshot.Threads);
        PrintGcOverview(snapshot.Gc);
        PrintNotableStrings(snapshot.Strings);
        PrintHeapHistogram(snapshot.HeapTypes);
        PrintDeadlocks(snapshot.DeadlockCandidates);
        PrintModules(snapshot.LoadedModules, snapshot.TotalModuleCount);
        PrintWarnings(snapshot.Warnings);
        Console.WriteLine("PDF tip: use the GUI to export a hyperlinked report.");
    }

    private static void PrintHeader(DumpSnapshot snapshot)
    {
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine("IntelliDump offline IIS analyzer");
        Console.ResetColor();
        Console.WriteLine($"Dump: {snapshot.DumpPath}");
        Console.WriteLine($"Runtime: {snapshot.RuntimeDescription}");
        Console.WriteLine($"Threads shown: {snapshot.Threads.Count} of {snapshot.TotalThreadCount}");
        Console.WriteLine($"Strings: {snapshot.UniqueStringCount} unique / {snapshot.TotalStringOccurrences} occurrences (stack {snapshot.StackStringOccurrences}, heap {snapshot.HeapStringOccurrences})");
        Console.WriteLine($"Heap types: {snapshot.HeapTypes.Count} of {snapshot.TotalHeapTypeCount} shown ({snapshot.HeapHistogramCoverage * 100:N1}% of {snapshot.Gc.TotalHeapBytes / (1024 * 1024):N0} MB, {snapshot.TotalHeapObjectCount:N0} objects); modules: {Math.Min(15, snapshot.LoadedModules.Count)} of {snapshot.TotalModuleCount} shown ({snapshot.ModuleCoverageShown * 100:N1}% of {snapshot.TotalModuleBytes / (1024 * 1024):N0} MB)");
        if (snapshot.DeadlockCandidates.Count > 0)
        {
            var first = snapshot.DeadlockCandidates.First();
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"*** DEADLOCK candidates: {snapshot.DeadlockCandidates.Count} (e.g., object 0x{first.ObjectAddress:X} owner={first.OwnerThreadId?.ToString() ?? "unknown"} waiting={first.WaitingThreads}) ***");
            Console.ResetColor();
        }
        Console.WriteLine();
    }

    private static void PrintIssues(IReadOnlyList<AnalysisIssue> issues)
    {
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine("Findings");
        Console.ResetColor();

        foreach (var issue in issues)
        {
            Console.ForegroundColor = issue.Severity switch
            {
                IssueSeverity.Critical => ConsoleColor.Red,
                IssueSeverity.Warning => ConsoleColor.Yellow,
                _ => ConsoleColor.Green
            };

            Console.WriteLine($"- [{issue.Severity}] {issue.Title}");
            Console.ResetColor();
            Console.WriteLine($"  Evidence: {issue.Evidence}");
            Console.WriteLine($"  Recommendation: {issue.Recommendation}");
            Console.WriteLine();
        }
    }

    private static void PrintThreadOverview(IReadOnlyCollection<ThreadSnapshot> threads)
    {
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine("Thread overview");
        Console.ResetColor();

        var blocked = threads.Count(t => t.LockCount > 0);
        var withExceptions = threads.Count(t => !string.IsNullOrWhiteSpace(t.CurrentException));
        var running = threads.Count(t => t.State.Contains("Running", StringComparison.OrdinalIgnoreCase));
        var finalizers = threads.Count(t => t.IsFinalizer);
        var gcThreads = threads.Count(t => t.IsGcThread);

        Console.WriteLine($"  Running: {running}");
        Console.WriteLine($"  Blocked/Waiting: {blocked}");
        Console.WriteLine($"  Threads with exceptions: {withExceptions}");
        Console.WriteLine($"  Finalizer threads: {finalizers}");
        Console.WriteLine($"  GC threads: {gcThreads}");
        Console.WriteLine();
    }

    private static void PrintGcOverview(GcSnapshot gc)
    {
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine("GC overview");
        Console.ResetColor();

        Console.WriteLine($"  Total heap: {gc.TotalHeapBytes / (1024 * 1024):N0} MB");
        Console.WriteLine($"  LOH: {gc.LargeObjectHeapBytes / (1024 * 1024):N0} MB");
        Console.WriteLine($"  Segments: {gc.SegmentCount}");
        Console.WriteLine($"  GC mode: {(gc.IsServerGc ? "Server" : "Workstation")}");
        Console.WriteLine($"  Gen0: {gc.Gen0Bytes / (1024 * 1024):N0} MB | Gen1: {gc.Gen1Bytes / (1024 * 1024):N0} MB | Gen2: {gc.Gen2Bytes / (1024 * 1024):N0} MB | Pinned: {gc.PinnedBytes / (1024 * 1024):N0} MB");
    }

    private static void PrintNotableStrings(IReadOnlyList<NotableString> strings)
    {
        if (strings.Count == 0)
        {
            return;
        }

        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine();
        Console.WriteLine("Captured strings (SQL/XML etc.)");
        Console.ResetColor();

        const int stringDisplay = 10;
        foreach (var s in strings.Take(stringDisplay))
        {
            var threadInfo = s.ThreadIds.Count == 0 ? "heap" : string.Join(", ", s.ThreadIds.Take(5));
            if (s.ThreadIds.Count > 5)
            {
                threadInfo += " ...";
            }

            Console.WriteLine($"  [{s.Source}] count={s.Occurrences:N0} threads={threadInfo} length={s.TotalLength:N0}{(s.WasTruncated ? " (truncated)" : string.Empty)}");
            Console.WriteLine($"  {s.Value}");
            Console.WriteLine();
        }

        var hotDuplicates = strings.Where(s => s.Occurrences > 1).OrderByDescending(s => s.Occurrences).Take(5).ToList();
        if (hotDuplicates.Count > 0)
        {
            Console.WriteLine("  Top duplicate strings:");
            foreach (var dup in hotDuplicates)
            {
                Console.WriteLine($"    count={dup.Occurrences:N0} len={dup.TotalLength:N0} sample: {dup.Value}");
            }
            Console.WriteLine();
        }
    }

    private static void PrintHeapHistogram(IReadOnlyList<HeapTypeStat> types)
    {
        if (types.Count == 0)
        {
            return;
        }

        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine($"Heap top types (showing {types.Count})");
        Console.ResetColor();

        foreach (var entry in types)
        {
            Console.WriteLine($"  {entry.TypeName} - {entry.TotalSize / (1024 * 1024):N0} MB across {entry.Count:N0} objects");
        }
        Console.WriteLine();
    }

    private static void PrintDeadlocks(IReadOnlyList<DeadlockCandidate> deadlocks)
    {
        if (deadlocks.Count == 0)
        {
            return;
        }

        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine("Potential lock contention / deadlocks");
        Console.ResetColor();

        foreach (var d in deadlocks)
        {
            var owner = d.OwnerThreadId?.ToString() ?? "unknown";
            Console.WriteLine($"  Object 0x{d.ObjectAddress:X} held by {owner}, waiting threads: {d.WaitingThreads}");
        }
        Console.WriteLine();
    }

    private static void PrintModules(IReadOnlyList<ModuleInfo> modules, int totalModuleCount)
    {
        if (modules.Count == 0)
        {
            return;
        }

        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine($"Loaded modules (managed) - showing top {Math.Min(15, modules.Count)} of {totalModuleCount}");
        Console.ResetColor();

        foreach (var m in modules.OrderByDescending(m => m.Size).Take(15))
        {
            Console.WriteLine($"  {m.Name} - {m.Size / 1024:N0} KB");
        }
        Console.WriteLine();
    }

    private static void PrintTopStacks(IReadOnlyList<ThreadSnapshot> threads)
    {
        var withStacks = threads.Where(t => t.Stack.Count > 0).Take(5).ToList();
        if (withStacks.Count == 0)
        {
            return;
        }

        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine("Top thread stacks");
        Console.ResetColor();

        foreach (var t in withStacks)
        {
            var truncated = t.CapturedStackFrames < t.RequestedStackFrames ? " (truncated)" : string.Empty;
            Console.WriteLine($"  Thread {t.ManagedId} ({t.State}, locks: {t.LockCount}, exception: {t.CurrentException ?? "none"}, frames {t.CapturedStackFrames}/{t.RequestedStackFrames}{truncated}, CPUms:{t.CpuTimeMs?.ToString("N0") ?? "n/a"})");
            foreach (var frame in t.Stack)
            {
                Console.WriteLine($"    {frame}");
            }
            Console.WriteLine();
        }
    }

    private static void PrintWarnings(IReadOnlyList<DataWarning> warnings)
    {
        if (warnings.Count == 0)
        {
            return;
        }

        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine("Warnings");
        Console.ResetColor();
        foreach (var group in warnings.GroupBy(w => w.Category))
        {
            Console.WriteLine($"  {group.Key}:");
            foreach (var warning in group)
            {
                Console.WriteLine($"    - {warning.Message}");
            }
        }
        Console.WriteLine();
    }
}
