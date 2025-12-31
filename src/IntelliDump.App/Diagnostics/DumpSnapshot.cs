using System.Collections.ObjectModel;

namespace IntelliDump.Diagnostics;

public sealed record ThreadSnapshot(
    int ManagedId,
    string State,
    int LockCount,
    string? CurrentException,
    bool IsFinalizer,
    bool IsGcThread);

public sealed record NotableString(
    int ThreadId,
    string Value,
    int TotalLength,
    bool WasTruncated);

public sealed record GcSnapshot(
    ulong TotalHeapBytes,
    ulong LargeObjectHeapBytes,
    int SegmentCount,
    bool IsServerGc);

public sealed record BlockingSummary(int SyncBlockCount, int WaitingThreadCount);

public sealed record DumpSnapshot(
    string DumpPath,
    string? RuntimeDescription,
    IReadOnlyList<ThreadSnapshot> Threads,
    GcSnapshot Gc,
    BlockingSummary Blocking,
    IReadOnlyList<NotableString> NotableStrings)
{
    public ThreadSnapshot? FaultingThread =>
        Threads.FirstOrDefault(t => !string.IsNullOrWhiteSpace(t.CurrentException));

    public IReadOnlyList<ThreadSnapshot> FinalizerThreads =>
        new ReadOnlyCollection<ThreadSnapshot>(Threads.Where(t => t.IsFinalizer).ToList());

    public IReadOnlyList<NotableString> Strings =>
        new ReadOnlyCollection<NotableString>(NotableStrings.ToList());
}
