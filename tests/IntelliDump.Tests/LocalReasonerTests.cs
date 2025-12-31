using System.Collections.Generic;
using System.Linq;
using IntelliDump.Diagnostics;
using IntelliDump.Reasoning;

namespace IntelliDump.Tests;

public class LocalReasonerTests
{
    [Fact]
    public void DetectsCrashWhenExceptionPresent()
    {
        var snapshot = CreateSnapshot(threadException: "System.NullReferenceException: boom");
        var reasoner = new LocalReasoner();

        var issues = reasoner.Analyze(snapshot);

        Assert.Contains(issues, i => i.Severity == IssueSeverity.Critical && i.Title.Contains("crash", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void FlagsHighMemoryPressure()
    {
        var snapshot = CreateSnapshot(totalHeapBytes: 3UL * 1024 * 1024 * 1024);
        var reasoner = new LocalReasoner();

        var issues = reasoner.Analyze(snapshot);

        Assert.Contains(issues, i => i.Title.Contains("memory", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void FlagsBlockingThreads()
    {
        var snapshot = CreateSnapshot(syncBlocks: 6, waitingThreads: 12, lockCount: 6);
        var reasoner = new LocalReasoner();

        var issues = reasoner.Analyze(snapshot);

        Assert.Contains(issues, i => i.Title.Contains("Synchronization contention", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void FlagsSyncOverAsyncTaskWaits()
    {
        var threads = new List<ThreadSnapshot>
        {
            new(1, "Wait", 0, null, false, false, new List<string>{ "System.Threading.Tasks.Task.Wait()" }, 1, 10, null),
            new(2, "Wait", 0, null, false, false, new List<string>{ "System.Threading.Tasks.Task`1.GetResult()" }, 1, 10, null),
            new(3, "Wait", 0, null, false, false, new List<string>{ "GetAwaiter().GetResult" }, 1, 10, null)
        };

        var gc = new GcSnapshot(100 * 1024 * 1024, 10 * 1024 * 1024, 2, true, 10, 10, 10, 0);
        var blocking = new BlockingSummary(0, 0);

        var snapshot = new DumpSnapshot(
            "fake.dmp",
            ".NET",
            threads.Count,
            threads,
            gc,
            blocking,
            Array.Empty<NotableString>(),
            Array.Empty<DeadlockCandidate>(),
            Array.Empty<HeapTypeStat>(),
            Array.Empty<ModuleInfo>(),
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            Array.Empty<DataWarning>());

        var issues = new LocalReasoner().Analyze(snapshot);

        Assert.Contains(issues, i => i.Title.Contains("Sync-over-async", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void FlagsHighDuplicateStrings()
    {
        var threads = new List<ThreadSnapshot>
        {
            new(1, "Running", 0, null, false, false, Array.Empty<string>(), 0, 0, null)
        };

        var gc = new GcSnapshot(100 * 1024 * 1024, 10 * 1024 * 1024, 2, true, 10, 10, 10, 0);
        var blocking = new BlockingSummary(0, 0);
        var strings = new List<NotableString>
        {
            new(new List<int>{1}, "SQL", 3, false, StringSource.Stack, 40)
        };

        var snapshot = new DumpSnapshot(
            "fake.dmp",
            ".NET",
            threads.Count,
            threads,
            gc,
            blocking,
            strings,
            Array.Empty<DeadlockCandidate>(),
            Array.Empty<HeapTypeStat>(),
            Array.Empty<ModuleInfo>(),
            0,
            0,
            0,
            strings.Count,
            strings.Sum(s => s.Occurrences),
            strings.Sum(s => s.Source is StringSource.Stack or StringSource.StackAndHeap ? s.Occurrences : 0),
            strings.Sum(s => s.Source is StringSource.Heap or StringSource.StackAndHeap ? s.Occurrences : 0),
            0,
            0,
            Array.Empty<DataWarning>());

        var issues = new LocalReasoner().Analyze(snapshot);

        Assert.Contains(issues, i => i.Title.Contains("duplicate string", StringComparison.OrdinalIgnoreCase));
    }

    private static DumpSnapshot CreateSnapshot(
        string? threadException = null,
        ulong totalHeapBytes = 100 * 1024 * 1024,
        int syncBlocks = 0,
        int waitingThreads = 0,
        int lockCount = 0)
    {
        var threads = new List<ThreadSnapshot>
        {
            new(1, "Running", lockCount, threadException, false, false, Array.Empty<string>(), 0, 0, null),
            new(2, "Running", 0, null, false, false, Array.Empty<string>(), 0, 0, null)
        };

        var gc = new GcSnapshot(totalHeapBytes, 10 * 1024 * 1024, 2, true, 10, 10, 10, 0);
        var blocking = new BlockingSummary(syncBlocks, waitingThreads);

        return new DumpSnapshot(
            "fake.dmp",
            ".NET",
            threads.Count,
            threads,
            gc,
            blocking,
            Array.Empty<NotableString>(),
            Array.Empty<DeadlockCandidate>(),
            Array.Empty<HeapTypeStat>(),
            Array.Empty<ModuleInfo>(),
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            Array.Empty<DataWarning>());
    }
}
