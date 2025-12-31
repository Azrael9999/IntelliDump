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

    private static DumpSnapshot CreateSnapshot(
        string? threadException = null,
        ulong totalHeapBytes = 100 * 1024 * 1024,
        int syncBlocks = 0,
        int waitingThreads = 0,
        int lockCount = 0)
    {
        var threads = new List<ThreadSnapshot>
        {
            new(1, "Running", lockCount, threadException, false, false, Array.Empty<string>()),
            new(2, "Running", 0, null, false, false, Array.Empty<string>())
        };

        var gc = new GcSnapshot(totalHeapBytes, 10 * 1024 * 1024, 2, true, 10, 10, 10, 0);
        var blocking = new BlockingSummary(syncBlocks, waitingThreads);

        return new DumpSnapshot(
            "fake.dmp",
            ".NET",
            threads,
            gc,
            blocking,
            Array.Empty<NotableString>(),
            Array.Empty<DeadlockCandidate>(),
            Array.Empty<HeapTypeStat>(),
            Array.Empty<ModuleInfo>(),
            Array.Empty<string>());
    }
}
