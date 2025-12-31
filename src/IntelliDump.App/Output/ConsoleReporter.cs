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
        PrintGcOverview(snapshot.Gc);
        PrintNotableStrings(snapshot.Strings);
    }

    private static void PrintHeader(DumpSnapshot snapshot)
    {
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine("IntelliDump offline IIS analyzer");
        Console.ResetColor();
        Console.WriteLine($"Dump: {snapshot.DumpPath}");
        Console.WriteLine($"Runtime: {snapshot.RuntimeDescription}");
        Console.WriteLine($"Threads: {snapshot.Threads.Count}");
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
    }

    private static void PrintNotableStrings(IReadOnlyList<NotableString> strings)
    {
        if (strings.Count == 0)
        {
            return;
        }

        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine();
        Console.WriteLine("Thread-captured strings (SQL/XML etc.)");
        Console.ResetColor();

        foreach (var s in strings)
        {
            Console.WriteLine($"  [T{s.ThreadId}] length={s.TotalLength:N0}{(s.WasTruncated ? " (truncated)" : string.Empty)}");
            Console.WriteLine($"  {s.Value}");
            Console.WriteLine();
        }
    }
}
