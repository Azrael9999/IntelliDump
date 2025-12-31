using System.Collections.ObjectModel;

namespace IntelliDump.Diagnostics;

public sealed record ThreadSnapshot(
    int ManagedId,
    string State,
    int LockCount,
    string? CurrentException,
    bool IsFinalizer,
    bool IsGcThread,
    IReadOnlyList<string> Stack);

public sealed record NotableString(
    int ThreadId,
    string Value,
    int TotalLength,
    bool WasTruncated);

public sealed record GcSnapshot(
    ulong TotalHeapBytes,
    ulong LargeObjectHeapBytes,
    int SegmentCount,
    bool IsServerGc,
    ulong Gen0Bytes,
    ulong Gen1Bytes,
    ulong Gen2Bytes,
    ulong PinnedBytes);

public sealed record BlockingSummary(int SyncBlockCount, int WaitingThreadCount);

public sealed record DeadlockCandidate(
    int? OwnerThreadId,
    int WaitingThreads,
    ulong ObjectAddress);

public sealed record HeapTypeStat(string TypeName, ulong TotalSize, int Count);

public sealed record ModuleInfo(string Name, ulong Size);

public sealed record DumpSnapshot(
    string DumpPath,
    string? RuntimeDescription,
    IReadOnlyList<ThreadSnapshot> Threads,
    GcSnapshot Gc,
    BlockingSummary Blocking,
    IReadOnlyList<NotableString> NotableStrings,
    IReadOnlyList<DeadlockCandidate> Deadlocks,
    IReadOnlyList<HeapTypeStat> HeapHistogram,
    IReadOnlyList<ModuleInfo> Modules,
    IReadOnlyList<string> Warnings)
{
    public ThreadSnapshot? FaultingThread =>
        Threads.FirstOrDefault(t => !string.IsNullOrWhiteSpace(t.CurrentException));

    public IReadOnlyList<ThreadSnapshot> FinalizerThreads =>
        new ReadOnlyCollection<ThreadSnapshot>(Threads.Where(t => t.IsFinalizer).ToList());

    public IReadOnlyList<NotableString> Strings =>
        new ReadOnlyCollection<NotableString>(NotableStrings.ToList());

    public IReadOnlyList<DeadlockCandidate> DeadlockCandidates =>
        new ReadOnlyCollection<DeadlockCandidate>(Deadlocks.ToList());

    public IReadOnlyList<HeapTypeStat> HeapTypes =>
        new ReadOnlyCollection<HeapTypeStat>(HeapHistogram.ToList());

    public IReadOnlyList<ModuleInfo> LoadedModules =>
        new ReadOnlyCollection<ModuleInfo>(Modules.ToList());
}
